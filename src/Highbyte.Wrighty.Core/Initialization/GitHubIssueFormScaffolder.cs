using System.Text;
using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Initialization;

public interface IGitHubIssueFormScaffolder
{
    Task<IReadOnlyList<string>> ScaffoldAsync(
        string workingDirectory,
        TrackerConfig config,
        string remoteName,
        CancellationToken cancellationToken);
}

public sealed class GitHubIssueFormScaffolder(
    IRepositoryDiscovery repositoryDiscovery,
    IGitProcess git) : IGitHubIssueFormScaffolder
{
    private static readonly string[] Agents = ["claude", "codex", "copilot"];

    public async Task<IReadOnlyList<string>> ScaffoldAsync(
        string workingDirectory,
        TrackerConfig config,
        string remoteName,
        CancellationToken cancellationToken)
    {
        var discovered = await repositoryDiscovery.DiscoverAsync(
            workingDirectory,
            remoteName,
            cancellationToken);
        if (discovered is null ||
            !string.Equals(discovered.Host, config.GitHubHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(discovered.Repository, config.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                $"Issue forms were not created because Git remote '{remoteName}' does not identify " +
                $"the configured repository '{config.Repository}'."
            ];
        }

        var rootResult = await git.RunAsync(
            ["-C", workingDirectory, "rev-parse", "--show-toplevel"],
            cancellationToken);
        if (rootResult.ExitCode != 0 || string.IsNullOrWhiteSpace(rootResult.StandardOutput))
        {
            return ["Issue forms were not created because the local Git repository root could not be resolved."];
        }

        var root = rootResult.StandardOutput.Trim();
        var directory = Path.Combine(root, ".github", "ISSUE_TEMPLATE");
        Directory.CreateDirectory(directory);
        var actions = new List<string>();
        foreach (var agent in Agents)
        {
            var path = Path.Combine(directory, $"wrighty-{agent}.yml");
            var content = BuildForm(config, agent);
            if (File.Exists(path))
            {
                var existing = await File.ReadAllTextAsync(path, cancellationToken);
                actions.Add(string.Equals(existing, content, StringComparison.Ordinal)
                    ? $"Wrighty {AgentTitle(agent)} issue form is available: {path}"
                    : $"Did not overwrite conflicting Wrighty issue form: {path}");
                continue;
            }

            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
            actions.Add($"created Wrighty {AgentTitle(agent)} issue form: {path}");
        }

        actions.Add("Review, commit, and push the issue-form files so GitHub can offer them from the repository's default branch.");
        return actions;
    }

    internal static string BuildForm(TrackerConfig config, string agent)
    {
        var title = AgentTitle(agent);
        return $$"""
            name: Wrighty worker task ({{title}})
            description: Create work explicitly authorized for unattended {{title}} processing
            labels:
              - "wrighty:auto"
              - "wrighty:agent={{agent}}"
            projects:
              - "{{config.EffectiveProjectOwner}}/{{config.ProjectNumber}}"
            body:
              - type: textarea
                id: description
                attributes:
                  label: Description
                  description: Describe the desired outcome, constraints, and verification.
                validations:
                  required: true

            """;
    }

    private static string AgentTitle(string agent) => agent switch
    {
        "claude" => "Claude",
        "codex" => "Codex",
        "copilot" => "Copilot",
        _ => throw new ArgumentOutOfRangeException(nameof(agent), agent, "Unsupported worker agent.")
    };
}
