using System.Text.Json;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;

namespace Highbyte.Wrighty.Initialization;

public sealed class GitHubInitializationClient(GhApi api) : IGitHubInitializationClient
{
    private const string ProjectViewsApiVersion = "2026-03-10";

    private const string RepositoryQuery = """
        query($owner: String!, $name: String!) {
          repository(owner: $owner, name: $name) {
            id
            name
            nameWithOwner
            owner { login }
            viewerPermission
          }
        }
        """;

    private const string ProjectQuery = """
        query($owner: String!, $number: Int!) {
          repositoryOwner(login: $owner) {
            __typename
            ... on User {
              projectV2(number: $number) {
                id number title url
                repositories(first: 100) { nodes { nameWithOwner } }
              }
            }
            ... on Organization {
              projectV2(number: $number) {
                id number title url
                repositories(first: 100) { nodes { nameWithOwner } }
              }
            }
          }
        }
        """;

    private const string ProjectListQuery = """
        query($owner: String!, $cursor: String) {
          repositoryOwner(login: $owner) {
            __typename
            ... on User {
              projectsV2(first: 100, after: $cursor) {
                nodes {
                  id number title url
                  repositories(first: 100) { nodes { nameWithOwner } }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
            ... on Organization {
              projectsV2(first: 100, after: $cursor) {
                nodes {
                  id number title url
                  repositories(first: 100) { nodes { nameWithOwner } }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """;

    private const string OwnerQuery = """
        query($owner: String!) {
          repositoryOwner(login: $owner) { __typename id login }
        }
        """;

    private const string ProjectViewsQuery = """
        query($owner: String!, $number: Int!) {
          repositoryOwner(login: $owner) {
            ... on User {
              projectV2(number: $number) {
                views(first: 100) { nodes { id number name layout } }
              }
            }
            ... on Organization {
              projectV2(number: $number) {
                views(first: 100) { nodes { id number name layout } }
              }
            }
          }
        }
        """;

    private const string CreateProjectMutation = """
        mutation($ownerId: ID!, $title: String!) {
          createProjectV2(input: { ownerId: $ownerId, title: $title }) {
            projectV2 { id number title url }
          }
        }
        """;

    private const string LinkRepositoryMutation = """
        mutation($projectId: ID!, $repositoryId: ID!) {
          linkProjectV2ToRepository(input: {
            projectId: $projectId,
            repositoryId: $repositoryId
          }) {
            repository { id }
          }
        }
        """;

    public async Task<GitHubRepositoryInfo> GetRepositoryAsync(
        string host,
        string repository,
        CancellationToken cancellationToken)
    {
        var parts = repository.Split('/');
        using var document = await api.GraphQlAsync(
            host,
            RepositoryQuery,
            new { owner = parts[0], name = parts[1] },
            cancellationToken);
        ThrowIfErrors(document.RootElement);
        var node = document.RootElement.GetProperty("data").GetProperty("repository");
        if (node.ValueKind == JsonValueKind.Null)
        {
            throw new TrackerException(
                "REPOSITORY_NOT_FOUND",
                $"Repository '{repository}' was not found or is inaccessible.",
                5);
        }

        var permission = node.GetProperty("viewerPermission").GetString() ?? "READ";
        if (permission is not ("ADMIN" or "MAINTAIN" or "WRITE" or "TRIAGE"))
        {
            throw new TrackerException(
                "REPOSITORY_ACCESS_DENIED",
                $"Repository '{repository}' is visible, but the current GitHub identity cannot create issues in it.",
                5,
                new Dictionary<string, object?> { ["viewerPermission"] = permission });
        }

        return new GitHubRepositoryInfo(
            node.GetProperty("id").GetString()!,
            node.GetProperty("nameWithOwner").GetString()!,
            node.GetProperty("owner").GetProperty("login").GetString()!,
            node.GetProperty("name").GetString()!,
            permission);
    }

    public async Task<GitHubProjectInfo?> GetProjectAsync(
        string host,
        string owner,
        int number,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            host,
            ProjectQuery,
            new { owner, number },
            cancellationToken);
        ThrowIfErrors(document.RootElement);
        var ownerNode = document.RootElement.GetProperty("data").GetProperty("repositoryOwner");
        if (ownerNode.ValueKind == JsonValueKind.Null ||
            !ownerNode.TryGetProperty("projectV2", out var project) ||
            project.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ParseProject(
            owner,
            project,
            ownerNode.TryGetProperty("__typename", out var ownerType)
                ? ownerType.GetString()
                : null);
    }

    public async Task<IReadOnlyList<GitHubProjectInfo>> FindProjectsByTitleAsync(
        string host,
        string owner,
        string title,
        CancellationToken cancellationToken)
    {
        var matches = new List<GitHubProjectInfo>();
        string? cursor = null;
        do
        {
            using var document = await api.GraphQlAsync(
                host,
                ProjectListQuery,
                new { owner, cursor },
                cancellationToken);
            ThrowIfErrors(document.RootElement);
            var ownerNode = document.RootElement.GetProperty("data").GetProperty("repositoryOwner");
            if (ownerNode.ValueKind == JsonValueKind.Null ||
                !ownerNode.TryGetProperty("projectsV2", out var projects))
            {
                throw new TrackerException(
                    "PROJECT_OWNER_NOT_FOUND",
                    $"GitHub Project owner '{owner}' was not found or is inaccessible.",
                    5);
            }

            foreach (var project in projects.GetProperty("nodes").EnumerateArray())
            {
                if (string.Equals(project.GetProperty("title").GetString(), title, StringComparison.Ordinal))
                {
                    matches.Add(ParseProject(
                        owner,
                        project,
                        ownerNode.TryGetProperty("__typename", out var ownerType)
                            ? ownerType.GetString()
                            : null));
                }
            }

            var pageInfo = projects.GetProperty("pageInfo");
            cursor = pageInfo.GetProperty("hasNextPage").GetBoolean()
                ? pageInfo.GetProperty("endCursor").GetString()
                : null;
        }
        while (cursor is not null);

        return matches;
    }

    public async Task<GitHubProjectInfo> CreateProjectAsync(
        string host,
        string owner,
        string title,
        CancellationToken cancellationToken)
    {
        using var ownerDocument = await api.GraphQlAsync(
            host,
            OwnerQuery,
            new { owner },
            cancellationToken);
        ThrowIfErrors(ownerDocument.RootElement);
        var ownerNode = ownerDocument.RootElement.GetProperty("data").GetProperty("repositoryOwner");
        if (ownerNode.ValueKind == JsonValueKind.Null)
        {
            throw new TrackerException(
                "PROJECT_OWNER_NOT_FOUND",
                $"GitHub Project owner '{owner}' was not found or is inaccessible.",
                5);
        }

        using var document = await api.GraphQlAsync(
            host,
            CreateProjectMutation,
            new { ownerId = ownerNode.GetProperty("id").GetString(), title },
            cancellationToken);
        ThrowIfErrors(document.RootElement);
        var project = document.RootElement.GetProperty("data")
            .GetProperty("createProjectV2")
            .GetProperty("projectV2");
        return ParseProject(
            owner,
            project,
            ownerNode.TryGetProperty("__typename", out var ownerType)
                ? ownerType.GetString()
                : null);
    }

    public async Task LinkRepositoryAsync(
        string host,
        string projectNodeId,
        string repositoryNodeId,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            host,
            LinkRepositoryMutation,
            new { projectId = projectNodeId, repositoryId = repositoryNodeId },
            cancellationToken);
        ThrowIfErrors(document.RootElement);
    }

    public async Task<IReadOnlyList<GitHubProjectViewInfo>> ListProjectViewsAsync(
        string host,
        GitHubProjectInfo projectInfo,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            host,
            ProjectViewsQuery,
            new { owner = projectInfo.Owner, number = projectInfo.Number },
            cancellationToken);
        ThrowIfErrors(document.RootElement);
        var ownerNode = document.RootElement.GetProperty("data").GetProperty("repositoryOwner");
        if (ownerNode.ValueKind == JsonValueKind.Null ||
            !ownerNode.TryGetProperty("projectV2", out var project) ||
            project.ValueKind == JsonValueKind.Null)
        {
            throw new TrackerException(
                "PROJECT_NOT_FOUND",
                $"Project {projectInfo.Owner}/{projectInfo.Number} was not found or is inaccessible.",
                5);
        }

        return project.GetProperty("views").GetProperty("nodes").EnumerateArray()
            .Select(view => new GitHubProjectViewInfo(
                view.GetProperty("id").GetString()!,
                view.GetProperty("number").GetInt32(),
                view.GetProperty("name").GetString()!,
                view.GetProperty("layout").GetString()!,
                $"{projectInfo.Url}/views/{view.GetProperty("number").GetInt32()}"))
            .ToArray();
    }

    public async Task CreateProjectViewAsync(
        string host,
        GitHubProjectInfo project,
        string name,
        CancellationToken cancellationToken)
    {
        var ownerPath = string.Equals(project.OwnerType, "Organization", StringComparison.Ordinal)
            ? $"orgs/{project.Owner}"
            : $"users/{project.Owner}";
        using var response = await api.SendVersionedJsonAsync(
            host,
            "POST",
            $"/{ownerPath}/projectsV2/{project.Number}/views",
            ProjectViewsApiVersion,
            new { name, layout = "board" },
            cancellationToken);
    }

    private static GitHubProjectInfo ParseProject(
        string owner,
        JsonElement project,
        string? ownerType = null)
    {
        var repositories = project.TryGetProperty("repositories", out var connection)
            ? connection.GetProperty("nodes").EnumerateArray()
                .Select(node => node.GetProperty("nameWithOwner").GetString()!)
                .ToArray()
            : [];
        return new GitHubProjectInfo(
            project.GetProperty("id").GetString()!,
            owner,
            project.GetProperty("number").GetInt32(),
            project.GetProperty("title").GetString()!,
            project.GetProperty("url").GetString()!,
            repositories,
            ownerType ?? "User");
    }

    private static void ThrowIfErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.GetArrayLength() == 0)
        {
            return;
        }

        var message = string.Join(
            "; ",
            errors.EnumerateArray().Select(error => error.GetProperty("message").GetString()));
        throw new TrackerException("GH_API_ERROR", message);
    }
}
