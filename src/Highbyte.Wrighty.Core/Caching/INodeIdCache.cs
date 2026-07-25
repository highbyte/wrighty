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
    string? DispatchDetailFieldId = null);
