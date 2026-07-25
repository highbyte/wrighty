using System.Text.Json.Serialization;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// The vendor-neutral permission posture the worker requests when it spawns a headless agent.
/// Each adapter maps a profile onto its own vendor flags; the abstraction stays deliberately thin
/// because the vendors' native surfaces differ.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentPermissionProfile>))]
public enum AgentPermissionProfile
{
    /// <summary>Least privilege that still completes tracked work: command execution and network
    /// stay available — the GitHub backend needs them for the agent's own <c>wrighty</c> calls —
    /// while file writes are confined to the workspace wherever the vendor can express it.</summary>
    [JsonStringEnumMemberName("workspace")]
    Workspace,

    /// <summary>Unrestricted vendor access. An explicit opt-in, never a silent fallback.</summary>
    [JsonStringEnumMemberName("full")]
    Full
}

/// <summary>
/// How much of the requested profile the vendor actually enforces. A vendor that cannot express
/// <see cref="AgentPermissionProfile.Workspace"/> must report that honestly rather than let the
/// operator believe a run is confined when it is not.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentPermissionEnforcement>))]
public enum AgentPermissionEnforcement
{
    /// <summary>The vendor confines the run as requested.</summary>
    [JsonStringEnumMemberName("enforced")]
    Enforced,

    /// <summary>The vendor narrows the run, but cannot enforce every requested restriction.</summary>
    [JsonStringEnumMemberName("partial")]
    Partial,

    /// <summary>The run is unrestricted, either by request or because the vendor offers nothing
    /// narrower that works headlessly.</summary>
    [JsonStringEnumMemberName("unrestricted")]
    Unrestricted
}

/// <summary>
/// The effective permission posture of one agent for one requested profile, including the vendor
/// arguments that produce it so the operator can see what was actually granted.
/// </summary>
public sealed record AgentPermissions(
    string Agent,
    AgentPermissionProfile Requested,
    AgentPermissionEnforcement Enforcement,
    bool ConfinesFileWrites,
    bool AllowsNetwork,
    IReadOnlyList<string> VendorArguments,
    string Summary)
{
    /// <summary>True when the operator asked for a narrower posture than the vendor delivers.
    /// The worker reports this rather than silently weakening or silently upgrading the run.</summary>
    public bool IsWeakerThanRequested =>
        Requested == AgentPermissionProfile.Workspace &&
        Enforcement != AgentPermissionEnforcement.Enforced;

    public string ProfileName => AgentPermissionProfiles.Name(Requested);
}

public static class AgentPermissionProfiles
{
    public const string WorkspaceName = "workspace";
    public const string FullName = "full";

    public static string Name(AgentPermissionProfile profile) =>
        profile == AgentPermissionProfile.Full ? FullName : WorkspaceName;

    /// <summary>
    /// Parses a configured profile name. An unrecognized value is a configuration error rather
    /// than a silent fallback: guessing here would decide how much privilege an unattended agent
    /// receives.
    /// </summary>
    public static AgentPermissionProfile Parse(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AgentPermissionProfile.Workspace;
        return value.Trim().ToLowerInvariant() switch
        {
            WorkspaceName => AgentPermissionProfile.Workspace,
            FullName => AgentPermissionProfile.Full,
            _ => throw new TrackerException(
                "CONFIG_INVALID",
                $"{property} must be {WorkspaceName} or {FullName}.",
                3)
        };
    }
}
