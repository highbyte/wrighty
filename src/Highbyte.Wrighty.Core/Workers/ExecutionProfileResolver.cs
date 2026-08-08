using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Settings;

namespace Highbyte.Wrighty.Workers;

/// <summary>Where a resolved profile name came from, for diagnostics and JSON output.</summary>
public enum ExecutionProfileSource
{
    /// <summary>No profile applies; the vendor CLI's own defaults stand.</summary>
    None,
    CommandLine,
    WorkItem,
    RepositoryDefault
}

/// <summary>Whether a mapping came from Wrighty's shipped defaults or the operator's settings.</summary>
public enum ExecutionMappingSource
{
    BuiltIn,
    UserSettings
}

/// <summary>
/// What a fresh launch will actually ask the vendor for. Recorded beside the session so a resumed
/// run can be proven to have kept its original selection rather than silently re-resolving.
/// </summary>
/// <param name="Profile">Null when no profile applied.</param>
/// <param name="Model">Null means no model argument is passed and the vendor default applies.</param>
/// <param name="Effort">Null means no effort argument is passed.</param>
/// <param name="CliVersion">
/// The vendor CLI version observed at resolution, when known. Recorded because a mapping that was
/// valid under one CLI version can stop being valid under the next, and the failure is otherwise
/// indistinguishable from a bad mapping.
/// </param>
public sealed record ExecutionSelection(
    string? Profile,
    string Agent,
    string? Model = null,
    ExecutionEffort? Effort = null,
    ExecutionProfileSource Source = ExecutionProfileSource.None,
    string? CliVersion = null,
    DateTimeOffset? ResolvedAt = null,
    ExecutionMappingSource MappingSource = ExecutionMappingSource.BuiltIn)
{
    /// <summary>True when the launch carries no model or effort argument at all. Computed, and
    /// never persisted: this record is written into the session sidecar.</summary>
    [JsonIgnore]
    public bool IsVendorDefault => Model is null && Effort is null;

    public static ExecutionSelection VendorDefault(string agent) => new(null, agent);
}

public sealed record ExecutionProfileResolution(
    ExecutionSelection? Selection,
    string? FailureCode = null,
    string? FailureReason = null)
{
    public bool Succeeded => FailureCode is null;

    public static ExecutionProfileResolution Ok(ExecutionSelection selection) => new(selection);

    public static ExecutionProfileResolution Unavailable(string reason) =>
        new(null, ExecutionProfileResolver.UnavailableCode, reason);
}

/// <summary>
/// Resolves a work item's execution profile to one machine's concrete vendor selection.
///
/// The single rule this type exists to enforce: resolution either produces exactly what was asked
/// for, or it fails. There is no substitution in either direction — Wrighty never quietly drops to
/// a cheaper profile to conserve credits, and never escalates to a more capable one to rescue a
/// failing run. Both would spend the operator's money or degrade their results based on a decision
/// they never made.
/// </summary>
public static class ExecutionProfileResolver
{
    public const string UnavailableCode = "AGENT_PROFILE_UNAVAILABLE";

    /// <summary>
    /// Lowercase, dash-separated, starting and ending with an alphanumeric. Deliberately narrow:
    /// the name appears in repository config, item front matter, and a GitHub Project option, and a
    /// permissive syntax would let those three disagree about the same profile.
    /// </summary>
    private static readonly Regex NamePattern = new(
        "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Names that describe a ranking rather than an operator's intent. Rejected because they invite
    /// exactly the automatic substitution this feature forbids: "best" implies something to fall
    /// back from, and "cheapest" implies Wrighty knows a price, which it does not.
    /// </summary>
    private static readonly IReadOnlySet<string> ReservedNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "best", "latest", "cheapest", "fastest", "default", "auto", "none"
        };

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        NamePattern.IsMatch(name) &&
        !ReservedNames.Contains(name);

    /// <summary>
    /// Picks the profile name that applies, without consulting any machine-local mapping. Kept
    /// separate from <see cref="Resolve"/> so precedence can be reasoned about and tested without a
    /// settings file.
    /// </summary>
    public static (string? Profile, ExecutionProfileSource Source) SelectProfile(
        string? commandLineProfile,
        string? itemProfile,
        string? repositoryDefault)
    {
        if (!string.IsNullOrWhiteSpace(commandLineProfile))
        {
            return (commandLineProfile.Trim(), ExecutionProfileSource.CommandLine);
        }

        if (!string.IsNullOrWhiteSpace(itemProfile))
        {
            return (itemProfile.Trim(), ExecutionProfileSource.WorkItem);
        }

        return !string.IsNullOrWhiteSpace(repositoryDefault)
            ? (repositoryDefault.Trim(), ExecutionProfileSource.RepositoryDefault)
            : (null, ExecutionProfileSource.None);
    }

    /// <summary>
    /// Resolves the effective selection for a fresh launch of <paramref name="agent"/>.
    /// </summary>
    public static ExecutionProfileResolution Resolve(
        string agent,
        string? commandLineProfile,
        string? itemProfile,
        WorkerConfig? worker,
        UserSettings settings,
        AgentExecutionCapability capability,
        string? cliVersion = null,
        DateTimeOffset? resolvedAt = null)
    {
        // An unconfigured repository still recognizes the shipped names, so a project can set a
        // default profile without every machine having to declare the vocabulary first.
        var configured = worker?.EffectiveExecutionProfiles ?? [];
        var vocabulary = configured.Count > 0 ? configured : BuiltInExecutionProfiles.Names;
        var (profile, source) = SelectProfile(
            commandLineProfile, itemProfile, worker?.DefaultExecutionProfile);

        if (profile is null)
        {
            // No profile in play anywhere: preserve the pre-feature behaviour exactly.
            return ExecutionProfileResolution.Ok(ExecutionSelection.VendorDefault(agent));
        }

        if (!IsValidName(profile))
        {
            return ExecutionProfileResolution.Unavailable(
                $"'{profile}' is not a valid execution profile name. Use lowercase words separated " +
                "by dashes, and not a ranking word such as 'best' or 'cheapest'.");
        }

        if (!vocabulary.Contains(profile, StringComparer.OrdinalIgnoreCase))
        {
            return ExecutionProfileResolution.Unavailable(
                $"Execution profile '{profile}' ({Describe(source)}) is not one of this " +
                $"repository's configured profiles ({Join(vocabulary)}).");
        }

        var userMapping = settings.FindMapping(profile, agent);
        var mappingSource = userMapping is { IsEmpty: false }
            ? ExecutionMappingSource.UserSettings
            : ExecutionMappingSource.BuiltIn;
        var mapping = userMapping is { IsEmpty: false }
            ? userMapping
            : BuiltInExecutionProfiles.Find(profile, capability);
        if (mapping is null || mapping.IsEmpty)
        {
            // Reachable only for a repository-defined name Wrighty does not ship, which is exactly
            // the case where guessing would be wrong.
            return ExecutionProfileResolution.Unavailable(
                $"This machine has no '{profile}' mapping for agent '{agent}', and it is not one of " +
                $"Wrighty's built-in profiles ({string.Join(", ", BuiltInExecutionProfiles.Names)}). " +
                $"Add one with 'wrighty config profile set {profile} --agent {agent} ...'. Wrighty " +
                "will not substitute another profile.");
        }

        if (mapping.Model is not null && string.IsNullOrWhiteSpace(mapping.Model))
        {
            return ExecutionProfileResolution.Unavailable(
                $"The '{profile}' mapping for '{agent}' has an empty model. Remove the model to use " +
                "the vendor default, rather than setting it to an empty value.");
        }

        if (mapping.Model is not null && !capability.SupportsModel)
        {
            return ExecutionProfileResolution.Unavailable(
                $"Agent '{agent}' does not accept an explicit model.");
        }

        if (mapping.Effort is { } effort && !capability.Supports(effort))
        {
            return ExecutionProfileResolution.Unavailable(
                capability.SupportsEffort
                    ? $"Agent '{agent}' does not accept effort '{effort.ToToken()}'. It supports: " +
                      $"{Join(capability.SupportedEfforts.Select(level => level.ToToken()).ToArray())}."
                    : $"Agent '{agent}' does not accept a reasoning-effort setting.");
        }

        return ExecutionProfileResolution.Ok(new ExecutionSelection(
            profile,
            agent,
            mapping.Model,
            mapping.Effort,
            source,
            cliVersion,
            resolvedAt,
            mappingSource));
    }

    private static string Describe(ExecutionProfileSource source) => source switch
    {
        ExecutionProfileSource.CommandLine => "requested on the command line",
        ExecutionProfileSource.WorkItem => "set on the work item",
        ExecutionProfileSource.RepositoryDefault => "the repository default",
        _ => "unspecified"
    };

    private static string Join(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? "none configured" : string.Join(", ", values);
}
