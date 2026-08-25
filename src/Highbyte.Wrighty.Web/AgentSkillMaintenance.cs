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
/// Inspects and explicitly updates the bundled Wrighty skill without coupling the web assembly to
/// the CLI's filesystem implementation.
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
}

public sealed record SkillStatusPageModel(
    IReadOnlyList<WebSkillInstallation> Installations,
    string? Notice = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public IReadOnlyList<WebSkillInstallation> Attention() => Installations
        .Where(installation => installation.State is
            WebSkillInstallationState.Outdated or
            WebSkillInstallationState.Modified or
            WebSkillInstallationState.Malformed)
        .ToArray();
}
