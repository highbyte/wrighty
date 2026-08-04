using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Projects;

public interface IProjectClient
{
    /// <param name="projectCreated">
    /// Whether this run created the Project, rather than adopting one that already existed.
    /// Wrighty owns the schema of a Project it created and provisions the priority scale there;
    /// on an adopted board the priority field belongs to whoever set it up, and its options are
    /// never created or extended. Defaults to false so any caller that cannot tell is treated as
    /// adopting — the choice that changes nothing.
    /// </param>
    Task<ProjectInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken,
        bool projectCreated = false);

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

    Task ValidatePolicyAsync(
        TrackerConfig config,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task<string> AddIssueAsync(
        TrackerConfig config,
        string issueNodeId,
        CancellationToken cancellationToken);

    async Task<ProjectItemReference> AddIssueAsync(
        TrackerConfig config,
        string issueNodeId,
        long? issueDatabaseId,
        CancellationToken cancellationToken) =>
        new(
            await AddIssueAsync(config, issueNodeId, cancellationToken),
            null);

    Task UpdatePriorityAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string priority,
        CancellationToken cancellationToken);

    /// <summary>
    /// Decision 10's reapproval cycle: needs-review then approved, moving the batch cutoff to now.
    /// Default-refuses so only clients with an approval surface offer it.
    /// </summary>
    Task CycleContextApprovalAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken) =>
        throw new Errors.TrackerException(
            "CONTEXT_APPROVAL_UNSUPPORTED",
            "This backend has no context approval field to cycle.",
            3);

    /// <summary>
    /// Revokes base approval after an issue title/body edit. This can only narrow authority, so an
    /// automated workflow may perform it without manufacturing approval.
    /// </summary>
    Task InvalidateContextApprovalAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken) =>
        throw new Errors.TrackerException(
            "CONTEXT_APPROVAL_UNSUPPORTED",
            "This backend has no context approval field to invalidate.",
            3);

    Task UpdatePolicyAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>
    /// Updates the display-only Project dispatch-state projection after the authoritative issue label
    /// has changed. Implementations keep the authoritative label valid if projection fails.
    /// </summary>
    Task UpdateDispatchStateProjectionAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string? dispatchState,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Projects installation-local retry/handoff detail to optional display-only Project fields.
    /// The issue label and local dispatch record remain authoritative.
    /// </summary>
    Task UpdateDispatchProjectionAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        DispatchInfo dispatch,
        CancellationToken cancellationToken) => Task.CompletedTask;

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

/// <summary>
/// <paramref name="FieldDatabaseIds"/> maps field names to their REST database IDs when the
/// initialization discovered them; view creation uses it to preselect card fields.
/// </summary>
public sealed record ProjectInitializationResult(
    bool Changed,
    IReadOnlyList<string> Actions,
    IReadOnlyDictionary<string, long>? FieldDatabaseIds = null);

public sealed record ProjectItemReference(
    string NodeId,
    long? DatabaseId);
