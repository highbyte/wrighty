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
    string? AgentTypeFieldId = null,
    IReadOnlyDictionary<string, string>? AgentTypeOptions = null,
    string? SessionIdFieldId = null,
    IReadOnlyDictionary<string, string>? PriorityOptions = null,
    string? CreationAttemptIdFieldId = null);
