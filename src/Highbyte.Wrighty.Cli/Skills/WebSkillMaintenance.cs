using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli.Skills;

/// <summary>Adapts the CLI's guarded skill manager to the repository-aware web surface.</summary>
public sealed class WebSkillMaintenance : IWebSkillMaintenance
{
    private readonly ISkillManager skills;
    private readonly IReadOnlyList<SkillTargetGroup> groups;

    public WebSkillMaintenance(
        ISkillManager skills,
        IReadOnlyList<AgentDescriptor> descriptors)
    {
        this.skills = skills;
        groups = descriptors
            .Where(descriptor => descriptor.SkillTarget is not null)
            .GroupBy(descriptor => descriptor.SkillTarget!.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selected = group.OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
                    .ToArray();
                return new SkillTargetGroup(
                    string.Join('-', selected.Select(descriptor => descriptor.Id)),
                    string.Join(',', selected.Select(descriptor => descriptor.Id)),
                    string.Join(", ", selected.Select(descriptor => descriptor.DisplayName)));
            })
            .OrderBy(group => group.AgentSelection, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<WebSkillInstallation>> InspectAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var installations = new List<WebSkillInstallation>();
        foreach (var group in groups)
        {
            foreach (var scope in new[] { SkillScope.Project, SkillScope.User })
            {
                var result = AssertSingle(await skills.CheckAsync(
                    group.AgentSelection,
                    scope,
                    workingDirectory,
                    projectDirectory: null,
                    cancellationToken));
                installations.Add(Map(group, result));
            }
        }
        return installations;
    }

    public async Task<WebSkillInstallation> UpdateAsync(
        string agentSelection,
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var group = Group(agentSelection);
        var parsedScope = Scope(scope);
        var inspection = AssertSingle(await skills.CheckAsync(
            group.AgentSelection,
            parsedScope,
            workingDirectory,
            projectDirectory: null,
            cancellationToken));
        if (inspection.State != SkillInstallationState.Outdated)
        {
            throw new TrackerException(
                "SKILL_UPDATE_NOT_ALLOWED",
                $"The {scope} skill for {group.AgentLabel} is {Token(inspection.State)}, not outdated. " +
                "Only a recognized outdated installation can be updated from the web console.",
                9);
        }

        var updated = AssertSingle(await skills.UpdateAsync(
            group.AgentSelection,
            parsedScope,
            workingDirectory,
            projectDirectory: null,
            force: false,
            cancellationToken));
        return Map(group, updated);
    }

    public async Task<WebSkillInstallation> InstallAsync(
        string agentSelection,
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var group = Group(agentSelection);
        var parsedScope = Scope(scope);
        var inspection = AssertSingle(await skills.CheckAsync(
            group.AgentSelection,
            parsedScope,
            workingDirectory,
            projectDirectory: null,
            cancellationToken));
        if (inspection.State != SkillInstallationState.Missing)
        {
            throw new TrackerException(
                "SKILL_INSTALL_NOT_ALLOWED",
                $"The {scope} skill for {group.AgentLabel} is {Token(inspection.State)}, not missing.",
                9);
        }

        var installed = AssertSingle(await skills.InstallAsync(
            group.AgentSelection,
            parsedScope,
            workingDirectory,
            projectDirectory: null,
            force: false,
            cancellationToken));
        return Map(group, installed);
    }

    public async Task<WebSkillInstallation> UninstallAsync(
        string agentSelection,
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var group = Group(agentSelection);
        var parsedScope = Scope(scope);
        var inspection = AssertSingle(await skills.CheckAsync(
            group.AgentSelection,
            parsedScope,
            workingDirectory,
            projectDirectory: null,
            cancellationToken));
        if (inspection.State is not (
            SkillInstallationState.Current or SkillInstallationState.Outdated))
        {
            throw new TrackerException(
                "SKILL_UNINSTALL_NOT_ALLOWED",
                $"The {scope} skill for {group.AgentLabel} is {Token(inspection.State)}. " +
                "Only an unmodified recognized installation can be removed from the web console.",
                9);
        }

        var removed = AssertSingle(await skills.UninstallAsync(
            group.AgentSelection,
            parsedScope,
            workingDirectory,
            projectDirectory: null,
            force: false,
            cancellationToken));
        return Map(group, removed);
    }

    public async Task<IReadOnlyList<WebSkillInstallation>> InstallAllMissingAsync(
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        _ = Scope(scope);
        var missingGroups = (await InspectAsync(workingDirectory, cancellationToken))
            .GroupBy(installation => installation.AgentSelection, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.All(installation =>
                installation.State == WebSkillInstallationState.Missing))
            .Select(group => group.Key)
            .ToArray();
        var installed = new List<WebSkillInstallation>();
        foreach (var agentSelection in missingGroups)
        {
            installed.Add(await InstallAsync(
                agentSelection,
                scope,
                workingDirectory,
                cancellationToken));
        }
        return installed;
    }

    public async Task<IReadOnlyList<WebSkillInstallation>> UpdateAllOutdatedAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var outdated = (await InspectAsync(workingDirectory, cancellationToken))
            .Where(installation => installation.State == WebSkillInstallationState.Outdated)
            .ToArray();
        var updated = new List<WebSkillInstallation>();
        foreach (var installation in outdated)
        {
            updated.Add(await UpdateAsync(
                installation.AgentSelection,
                installation.Scope,
                workingDirectory,
                cancellationToken));
        }
        return updated;
    }

    public async Task<IReadOnlyList<WebSkillInstallation>> UninstallAllAsync(
        string scope,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var results = await skills.UninstallAsync(
            "all",
            Scope(scope),
            workingDirectory,
            projectDirectory: null,
            force: false,
            cancellationToken);
        return results
            .Where(result => result.Changed)
            .Select(result => Map(GroupByResultAgent(result.Agent), result))
            .ToArray();
    }

    private SkillTargetGroup Group(string agentSelection) =>
        groups.FirstOrDefault(candidate => string.Equals(
                candidate.AgentSelection,
                agentSelection,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new TrackerException(
                "SKILL_MAINTENANCE_INVALID",
                "The requested skill target is not registered.",
                2);

    private SkillTargetGroup GroupByResultAgent(string resultAgent) =>
        groups.FirstOrDefault(candidate => string.Equals(
                candidate.ResultAgent,
                resultAgent,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new TrackerException(
                "SKILL_STATUS_UNAVAILABLE",
                "The skill operation returned an unregistered physical target.",
                9);

    private static SkillScope Scope(string scope) => scope.ToLowerInvariant() switch
    {
        "project" => SkillScope.Project,
        "user" => SkillScope.User,
        _ => throw new TrackerException(
            "SKILL_MAINTENANCE_INVALID",
            "The requested skill scope must be project or user.",
            2)
    };

    private static SkillOperationResult AssertSingle(
        IReadOnlyList<SkillOperationResult> results) =>
        results.Count == 1
            ? results[0]
            : throw new TrackerException(
                "SKILL_STATUS_UNAVAILABLE",
                "The registered skill target did not resolve to one physical installation.",
                9);

    private static WebSkillInstallation Map(
        SkillTargetGroup group,
        SkillOperationResult result) => new(
            group.AgentSelection,
            group.AgentLabel,
            result.Scope,
            result.Path,
            result.State switch
            {
                SkillInstallationState.Missing => WebSkillInstallationState.Missing,
                SkillInstallationState.Current => WebSkillInstallationState.Current,
                SkillInstallationState.Outdated => WebSkillInstallationState.Outdated,
                SkillInstallationState.Modified => WebSkillInstallationState.Modified,
                _ => WebSkillInstallationState.Malformed
            },
            result.PreviousVersion,
            result.Version);

    private static string Token(SkillInstallationState state) =>
        state.ToString().ToLowerInvariant();

    private sealed record SkillTargetGroup(
        string ResultAgent,
        string AgentSelection,
        string AgentLabel);
}
