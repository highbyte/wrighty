namespace Highbyte.Wrighty.Web;

/// <summary>The repository-aware state of one physical Wrighty skill installation.</summary>
public enum WebSkillInstallationState
{
    Missing,
    Current,
    Outdated,
    Modified,
    Malformed
}

/// <summary>
/// A physical skill target and the agent families that share it. <see cref="AgentSelection"/> is
/// an opaque allowlisted token returned to the maintenance service when an explicit update is
/// requested; paths from the browser are never accepted.
/// </summary>
public sealed record WebSkillInstallation(
    string AgentSelection,
    string AgentLabel,
    string Scope,
    string Path,
    WebSkillInstallationState State,
    string? InstalledVersion,
    string BundledVersion)
{
    public bool CanUpdate => State == WebSkillInstallationState.Outdated;
}

/// <summary>
/// Inspects and explicitly maintains the bundled Wrighty skill without coupling the web assembly
/// to the CLI's filesystem implementation.
/// </summary>
public interface IWebSkillMaintenance
{
    Task<IReadOnlyList<WebSkillInstallation>> InspectAsync(
        string workingDirectory,
        CancellationToken cancellationToken);

    Task<WebSkillInstallation> UpdateAsync(
        string agentSelection,
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken);

    Task<WebSkillInstallation> InstallAsync(
        string agentSelection,
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken);

    Task<WebSkillInstallation> UninstallAsync(
        string agentSelection,
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WebSkillInstallation>> UpdateAllOutdatedAsync(
        string workingDirectory,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WebSkillInstallation>> UninstallAllAsync(
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public sealed record SkillTargetStatus(
    string AgentSelection,
    string AgentLabel,
    IReadOnlyList<WebSkillInstallation> Installations)
{
    public IReadOnlyList<WebSkillInstallation> Installed => Installations
        .Where(installation => installation.State != WebSkillInstallationState.Missing)
        .ToArray();

    public bool IsMissing => Installed.Count == 0;

    public bool IsDuplicate => Installed.Count > 1;

    public bool NeedsAttention => IsMissing || IsDuplicate || Installed.Any(installation =>
        installation.State != WebSkillInstallationState.Current);
}

public sealed record SkillInventorySnapshot(
    IReadOnlyList<WebSkillInstallation> Installations,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public IReadOnlyList<SkillTargetStatus> Targets() => Installations
        .GroupBy(installation => installation.AgentSelection, StringComparer.OrdinalIgnoreCase)
        .Select(group => new SkillTargetStatus(
            group.Key,
            group.First().AgentLabel,
            group.OrderBy(installation => installation.Scope == "user" ? 0 : 1).ToArray()))
        .ToArray();

}
