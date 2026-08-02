namespace Highbyte.Wrighty.Caching;

public interface INodeIdCache
{
    Task<ProjectMetadata?> GetAsync(string key, CancellationToken cancellationToken);

    Task PutAsync(string key, ProjectMetadata value, CancellationToken cancellationToken);

    Task InvalidateAsync(string key, CancellationToken cancellationToken);
}

public sealed record ProjectMetadata(
    string ProjectId,
    string StatusFieldId,
    IReadOnlyDictionary<string, string> StatusOptions,
    string? PriorityFieldId,
    string? ClaimAgentFieldId = null,
    IReadOnlyDictionary<string, string>? AgentOptions = null,
    string? ClaimSessionIdFieldId = null,
    IReadOnlyDictionary<string, string>? PriorityOptions = null,
    string? CreationAttemptIdFieldId = null,
    string? ClaimantTypeFieldId = null,
    IReadOnlyDictionary<string, string>? ClaimantKindOptions = null,
    string? ClaimantFieldId = null,
    string? ClaimWorkspacePathFieldId = null,
    string? ExecutionPolicyFieldId = null,
    IReadOnlyDictionary<string, string>? ExecutionPolicyOptions = null,
    string? AgentPolicyFieldId = null,
    IReadOnlyDictionary<string, string>? AgentPolicyOptions = null,
    string? DispatchStateFieldId = null,
    IReadOnlyDictionary<string, string>? DispatchStateOptionOptions = null,
    string? DispatchNotBeforeFieldId = null,
    string? DispatchAgentFieldId = null,
    IReadOnlyDictionary<string, string>? DispatchAgentOptions = null,
    string? DispatchDetailFieldId = null,
    IReadOnlyDictionary<string, long>? RestFieldIds = null,
    // The priority field's option names in field order — the ordered scale PriorityScale.Rank
    // consumes. Kept beside the unordered name→id option map because a dictionary's order is
    // nothing to build a pick queue on. Null in caches written before the scale existed; the
    // metadata read upgrades those in place.
    IReadOnlyList<string>? PriorityScale = null);
