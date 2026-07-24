namespace Highbyte.Wrighty.Initialization;

public sealed record GitHubRepositoryInfo(
    string NodeId,
    string NameWithOwner,
    string Owner,
    string Name,
    string ViewerPermission);

public sealed record GitHubProjectInfo(
    string NodeId,
    string Owner,
    int Number,
    string Title,
    string Url,
    IReadOnlyList<string> LinkedRepositories,
    string OwnerType = "User");

public sealed record GitHubProjectViewInfo(
    string NodeId,
    int Number,
    string Name,
    string Layout,
    string Url);

public sealed record LegacyWorkerPolicyLabels(
    IReadOnlyList<string> Labels);

public interface IGitHubInitializationClient
{
    Task<GitHubRepositoryInfo> GetRepositoryAsync(
        string host,
        string repository,
        CancellationToken cancellationToken);

    Task<GitHubProjectInfo?> GetProjectAsync(
        string host,
        string owner,
        int number,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GitHubProjectInfo>> FindProjectsByTitleAsync(
        string host,
        string owner,
        string title,
        CancellationToken cancellationToken);

    Task<GitHubProjectInfo> CreateProjectAsync(
        string host,
        string owner,
        string title,
        CancellationToken cancellationToken);

    Task LinkRepositoryAsync(
        string host,
        string projectNodeId,
        string repositoryNodeId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> InitializeWorkerLabelsAsync(
        string host,
        string repository,
        bool checkOnly,
        CancellationToken cancellationToken);

    Task<LegacyWorkerPolicyLabels> GetLegacyWorkerPolicyLabelsAsync(
        string host,
        string repository,
        int issueNumber,
        CancellationToken cancellationToken) =>
        Task.FromResult(new LegacyWorkerPolicyLabels([]));

    Task RemoveLegacyWorkerPolicyLabelsAsync(
        string host,
        string repository,
        int issueNumber,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task<IReadOnlyList<GitHubProjectViewInfo>> ListProjectViewsAsync(
        string host,
        GitHubProjectInfo project,
        CancellationToken cancellationToken);

    Task CreateProjectViewAsync(
        string host,
        GitHubProjectInfo project,
        string name,
        CancellationToken cancellationToken);
}
