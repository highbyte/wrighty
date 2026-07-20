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
