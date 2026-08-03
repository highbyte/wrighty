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

/// <summary>
/// A view to create through the Projects REST API. <paramref name="Filter"/> uses the Project
/// filter query syntax; <paramref name="VisibleFieldIds"/> carries field database IDs and can only
/// be set at creation — the views API has no update operation, so an existing view's shown fields
/// must be adjusted manually.
/// </summary>
public sealed record GitHubProjectViewSpec(
    string Name,
    string Layout,
    string? Filter = null,
    IReadOnlyList<long>? VisibleFieldIds = null);

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

    Task<IReadOnlyList<GitHubProjectViewInfo>> ListProjectViewsAsync(
        string host,
        GitHubProjectInfo project,
        CancellationToken cancellationToken);

    Task CreateProjectViewAsync(
        string host,
        GitHubProjectInfo project,
        GitHubProjectViewSpec view,
        CancellationToken cancellationToken);
}
