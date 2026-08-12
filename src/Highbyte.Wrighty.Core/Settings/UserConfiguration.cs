using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Workers;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Settings;

/// <summary>
/// What this machine's settings file holds, described the way repository configuration is.
///
/// The shape deliberately mirrors <see cref="RepositoryConfigurationSnapshot"/>: same descriptor
/// type, same revision idea, same stored-versus-effective split. A surface that already renders one
/// should not have to learn a second vocabulary to render the other — the scopes differ in *what*
/// they hold and who they belong to, not in how they are presented.
/// </summary>
/// <param name="SourcePath">Absolute path to the settings file, whether or not it exists yet.</param>
/// <param name="Stored">The settings as read, or defaults when the file is absent.</param>
/// <param name="Revision">
/// A hash of the file's exact bytes, or <see cref="AbsentRevision"/> when there is no file.
/// Compared before a write so a long-lived web process cannot silently overwrite a change made by
/// the CLI in between — the file is hand-editable and shared by every Wrighty on this machine.
/// </param>
/// <param name="Exists">Whether the current-version file is on disk.</param>
/// <param name="AwaitingMigration">
/// Whether values are being read from the previous schema version. Reported rather than hidden:
/// the next save rewrites the file at the current version, which is a change worth expecting.
/// </param>
public sealed record UserConfigurationSnapshot(
    string SourcePath,
    UserSettings Stored,
    string Revision,
    bool Exists,
    bool AwaitingMigration,
    IReadOnlyList<ConfigurationSettingDescriptor> Settings)
{
    /// <summary>
    /// The revision of a file that is not there. A distinct sentinel rather than an empty string so
    /// "no file yet" cannot be confused with "revision not supplied", which is the difference
    /// between a first write and a blind one.
    /// </summary>
    public const string AbsentRevision = "absent";
}

public sealed record UserConfigurationMutationResult(
    UserConfigurationSnapshot Before,
    UserConfigurationSnapshot After,
    IReadOnlyList<ConfigurationChange> Changes,
    bool Saved);

/// <summary>
/// One edit to this machine's settings. Mirrors the repository mutation seam so both scopes stay
/// typed: a surface hands over an intent, never a hand-built settings object, and cannot silently
/// drop a field it did not know about.
/// </summary>
public abstract record UserConfigurationMutation
{
    internal abstract UserSettings Apply(UserSettings settings);
}

/// <summary>
/// Sets or clears the symbolic host label — the first user-scoped setting to become editable here.
///
/// Chosen deliberately as the first case: it already exists, nothing depends on it, and it is a
/// plain nullable string. If the scope plumbing is wrong, it will be wrong here in a way that is
/// obvious, rather than tangled with a nested per-agent mapping.
/// </summary>
public sealed record HostLabelMutation(string? Label) : UserConfigurationMutation
{
    internal override UserSettings Apply(UserSettings settings) =>
        settings with
        {
            HostLabel = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim()
        };
}

/// <summary>
/// Sets or clears what one profile means for one agent on this machine.
///
/// Scoped to a single (profile, agent) pair rather than replacing the whole map, because two
/// surfaces edit this file and a whole-map write from a page rendered minutes ago would silently
/// drop a mapping the CLI added in between. The revision check would catch a concurrent write, but
/// only if the page were reloaded first — a narrow mutation is correct even when it is not.
///
/// A mapping carrying neither a model nor an effort is removed rather than stored empty: an entry
/// that says nothing still shows as configured, and resolution would fail on it.
/// </summary>
public sealed record ProfileMappingMutation(
    string Profile,
    string Agent,
    string? Model,
    Workers.ExecutionEffort? Effort) : UserConfigurationMutation
{
    internal override UserSettings Apply(UserSettings settings)
    {
        var profiles = settings.WorkerProfiles.ToDictionary(
            entry => entry.Key,
            entry => new Dictionary<string, ExecutionProfileMapping>(
                entry.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var profile = profiles.Keys.FirstOrDefault(existing =>
            string.Equals(existing, Profile, StringComparison.OrdinalIgnoreCase)) ?? Profile;
        var agents = profiles.TryGetValue(profile, out var existingAgents)
            ? existingAgents
            : new Dictionary<string, ExecutionProfileMapping>(StringComparer.OrdinalIgnoreCase);

        var mapping = new ExecutionProfileMapping
        {
            Model = string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(),
            Effort = Effort
        };

        if (mapping.IsEmpty)
        {
            agents.Remove(Agent);
        }
        else
        {
            agents[Agent] = mapping;
        }

        // An empty profile entry would list as configured while resolving to nothing.
        if (agents.Count == 0)
        {
            profiles.Remove(profile);
        }
        else
        {
            profiles[profile] = agents;
        }

        return settings with
        {
            WorkerProfiles = profiles.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyDictionary<string, ExecutionProfileMapping>)entry.Value,
                StringComparer.OrdinalIgnoreCase)
        };
    }
}

public interface IUserConfigurationService
{
    Task<UserConfigurationSnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies one edit, refusing when the file changed since <paramref name="expectedRevision"/>
    /// was read. The caller re-reads and decides; nothing is merged automatically, because two
    /// concurrent edits to the same machine's settings are a person changing their mind in two
    /// places, not a conflict a tool should resolve on their behalf.
    /// </summary>
    Task<UserConfigurationMutationResult> MutateAsync(
        string expectedRevision,
        UserConfigurationMutation mutation,
        bool dryRun,
        CancellationToken cancellationToken);
}

public sealed class UserConfigurationService(UserSettingsStore store) : IUserConfigurationService
{
    public const string RevisionConflict = "USER_CONFIGURATION_REVISION_CONFLICT";

    public async Task<UserConfigurationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var settings = await store.LoadAsync(cancellationToken);
        return Snapshot(settings, await RevisionAsync(cancellationToken));
    }

    public async Task<UserConfigurationMutationResult> MutateAsync(
        string expectedRevision,
        UserConfigurationMutation mutation,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var before = await ReadAsync(cancellationToken);
        Guard(expectedRevision, before.Revision);

        var updated = mutation.Apply(before.Stored);
        var changes = Describe(before.Stored, updated);
        if (dryRun || changes.Count == 0)
        {
            // Nothing to write is reported as a successful no-op rather than a failure: an operator
            // re-submitting an unchanged form has not made a mistake.
            return new UserConfigurationMutationResult(before, before, changes, Saved: false);
        }

        // Re-checked immediately before writing. The read above and this save are not one atomic
        // operation, and the window between them is exactly when a CLI on the same machine writes.
        Guard(expectedRevision, await RevisionAsync(cancellationToken));
        await store.SaveAsync(updated, cancellationToken);

        var after = await ReadAsync(cancellationToken);
        return new UserConfigurationMutationResult(before, after, changes, Saved: true);
    }

    private static void Guard(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        throw new TrackerException(
            RevisionConflict,
            "Your machine's settings changed since they were read. Reload and reapply the change.",
            2,
            new Dictionary<string, object?>
            {
                ["expectedRevision"] = expected,
                ["actualRevision"] = actual
            });
    }

    private async Task<string> RevisionAsync(CancellationToken cancellationToken)
    {
        var path = store.SourcePath;
        if (!File.Exists(path))
        {
            return UserConfigurationSnapshot.AbsentRevision;
        }

        try
        {
            return RepositoryConfigurationService.Revision(
                await File.ReadAllBytesAsync(path, cancellationToken));
        }
        catch (IOException)
        {
            // Unreadable right now — most likely another process mid-write. Returning a value that
            // can never match makes the next mutation refuse rather than overwrite blindly.
            return Guid.NewGuid().ToString("N");
        }
    }

    private UserConfigurationSnapshot Snapshot(UserSettings settings, string revision) =>
        new(store.SourcePath,
            settings,
            revision,
            store.Exists,
            store.AwaitingMigration,
            Describe(settings));

    /// <summary>
    /// The catalogue for this scope. Small on purpose: a setting appears here when it is genuinely
    /// machine-local, and the description says *why* it is, because the split is the whole point of
    /// having two scopes at all.
    /// </summary>
    private static IEnumerable<string> Pairs(UserSettings settings) =>
        settings.WorkerProfiles.SelectMany(
            profile => profile.Value.Keys.Select(agent => $"{profile.Key}.{agent}"));

    private static (string Profile, string Agent) Split(string id)
    {
        var separator = id.LastIndexOf('.');
        return (id[..separator], id[(separator + 1)..]);
    }

    private static string? Describe(ExecutionProfileMapping? mapping) =>
        mapping is null
            ? null
            : $"{mapping.Model ?? "vendor default"} / {mapping.Effort?.ToToken() ?? "vendor default"}";

    private static IReadOnlyList<ConfigurationSettingDescriptor> Describe(UserSettings settings) =>
    [
        new ConfigurationSettingDescriptor(
            "hostLabel",
            ConfigurationScope.User,
            "string?",
            settings.HostLabel,
            settings.HostLabel ?? HostLabelProvider.AnonymousLabel,
            settings.HostLabel is null ? "default" : "user",
            ConfigurationEditMode.Ordinary,
            ConfigurationEffectiveBoundary.NextCommand,
            RequiresQuiescence: false,
            Sensitivity: null,
            "Symbolic name shown when this machine hands work over. Machine-local because it names " +
            "the installation, not the project; the anonymous label applies when unset."),
        new ConfigurationSettingDescriptor(
            "workerProfiles",
            ConfigurationScope.User,
            "map<profile, map<agent, {model, effort}>>",
            settings.WorkerProfiles.Count == 0 ? null : settings.WorkerProfiles,
            settings.WorkerProfiles,
            settings.WorkerProfiles.Count == 0 ? "default" : "user",
            // Read-only here for now: the vocabulary is repository policy, but what a profile means
            // in vendor terms depends on what this operator has installed and is entitled to. An
            // editor for it needs the model discovery that names those choices.
            ConfigurationEditMode.ReadOnly,
            ConfigurationEffectiveBoundary.FreshAgentLaunch,
            RequiresQuiescence: false,
            Sensitivity: null,
            "What each execution profile resolves to on this machine. Machine-local because a model " +
            "identifier describes your installation and entitlement, which the repository never " +
            "agreed to. Edit with 'wrighty config profile set'.")
    ];

    private static IReadOnlyList<ConfigurationChange> Describe(
        UserSettings before, UserSettings after)
    {
        var changes = new List<ConfigurationChange>();
        if (!string.Equals(before.HostLabel, after.HostLabel, StringComparison.Ordinal))
        {
            changes.Add(new ConfigurationChange("hostLabel", before.HostLabel, after.HostLabel));
        }

        // Reported per (profile, agent) rather than as one workerProfiles diff, so the notice names
        // what actually changed instead of printing two maps at the operator.
        foreach (var id in Pairs(before).Union(Pairs(after), StringComparer.OrdinalIgnoreCase))
        {
            var was = Describe(before.FindMapping(Split(id).Profile, Split(id).Agent));
            var now = Describe(after.FindMapping(Split(id).Profile, Split(id).Agent));
            if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                changes.Add(new ConfigurationChange($"workerProfiles.{id}", was, now));
            }
        }

        return changes;
    }
}
