using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Projects;

public interface IProjectClient
{
    Task<ProjectInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken);

    Task EnsureAgentContextSchemaAsync(
        TrackerConfig config,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubProjectItem>> FindByCreationAttemptIdAsync(
        TrackerConfig config,
        string creationAttemptId,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitHubProjectItem>>([]);

    Task UpdateCreationAttemptIdAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string creationAttemptId,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyList<GitHubProjectItem>> ListAsync(
        TrackerConfig config,
        string? status,
        int? limit,
        CancellationToken cancellationToken);

    async Task<IReadOnlyList<GitHubProjectItem>> ListAsync(
        TrackerConfig config,
        string? status,
        int? limit,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken)
    {
        return (await ListAsync(config, status, limit, cancellationToken))
            .Where(item => archiveScope switch
            {
                ArchiveScope.Active => !item.Summary.Archived,
                ArchiveScope.Archived => item.Summary.Archived,
                _ => true
            })
            .ToArray();
    }

    Task ArchiveAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task UnarchiveAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task UpdateStatusAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string status,
        CancellationToken cancellationToken);

    Task UpdateAgentContextAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string? agentType,
        string? sessionId,
        CancellationToken cancellationToken);

    Task UpdateClaimantProjectionAsync(TrackerConfig config, GitHubProjectItem item,
        string? claimantKind, string? claimantId, string? agentType, string? sessionId,
        CancellationToken cancellationToken) =>
        UpdateAgentContextAsync(config, item, agentType, sessionId, cancellationToken);

    Task UpdateWorkspacePathAsync(TrackerConfig config, GitHubProjectItem item,
        string? workspacePath, CancellationToken cancellationToken) => Task.CompletedTask;

    Task ValidateCreateFieldsAsync(
        TrackerConfig config,
        string status,
        string? priority,
        CancellationToken cancellationToken);

    Task<string> AddIssueAsync(
        TrackerConfig config,
        string issueNodeId,
        CancellationToken cancellationToken);

    Task UpdatePriorityAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string priority,
        CancellationToken cancellationToken);

    Task ClearPriorityAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task ValidateUpdateFieldsAsync(
        TrackerConfig config,
        string? status,
        string? priority,
        bool clearPriority,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record ProjectInitializationResult(
    bool Changed,
    IReadOnlyList<string> Actions);
