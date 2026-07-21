using System.Text.Json;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Initialization;

namespace Highbyte.Wrighty.UnitTests.Initialization;

public sealed class GitHubInitializationClientTests
{
    [Fact]
    public async Task GetRepository_returns_repository_details()
    {
        var process = new QueueGhProcess("""
            {"data":{"repository":{"id":"R_1","name":"repo","nameWithOwner":"owner/repo","owner":{"login":"owner"},"viewerPermission":"WRITE"}}}
            """);

        var result = await Client(process).GetRepositoryAsync(
            "github.example", "owner/repo", CancellationToken.None);

        Assert.Equal("R_1", result.NodeId);
        Assert.Equal("owner/repo", result.NameWithOwner);
        Assert.Equal("owner", result.Owner);
        Assert.Equal("repo", result.Name);
        Assert.Equal("WRITE", result.ViewerPermission);
        Assert.Contains("github.example", process.Calls.Single().Arguments);
        using var input = JsonDocument.Parse(process.Calls.Single().StandardInput!);
        Assert.Equal("owner", input.RootElement.GetProperty("variables").GetProperty("owner").GetString());
        Assert.Equal("repo", input.RootElement.GetProperty("variables").GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetRepository_reports_an_inaccessible_repository()
    {
        var process = new QueueGhProcess("""{"data":{"repository":null}}""");

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Client(process).GetRepositoryAsync("github.com", "owner/missing", CancellationToken.None));

        Assert.Equal("REPOSITORY_NOT_FOUND", exception.Code);
        Assert.Equal(5, exception.ExitCode);
    }

    [Theory]
    [InlineData("READ")]
    [InlineData(null)]
    public async Task GetRepository_requires_issue_write_permission(string? permission)
    {
        var response = JsonSerializer.Serialize(new
        {
            data = new
            {
                repository = new
                {
                    id = "R_1",
                    name = "repo",
                    nameWithOwner = "owner/repo",
                    owner = new { login = "owner" },
                    viewerPermission = permission
                }
            }
        });
        var process = new QueueGhProcess(response);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Client(process).GetRepositoryAsync("github.com", "owner/repo", CancellationToken.None));

        Assert.Equal("REPOSITORY_ACCESS_DENIED", exception.Code);
        Assert.Equal(permission ?? "READ", exception.Details["viewerPermission"]);
    }

    [Fact]
    public async Task GetProject_parses_linked_repositories()
    {
        var process = new QueueGhProcess("""
            {"data":{"repositoryOwner":{"projectV2":{"id":"P_7","number":7,"title":"Tracker","url":"https://example.test/7","repositories":{"nodes":[{"nameWithOwner":"owner/repo"},{"nameWithOwner":"owner/other"}]}}}}}
            """);

        var result = await Client(process).GetProjectAsync(
            "github.com", "owner", 7, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("P_7", result.NodeId);
        Assert.Equal("owner", result.Owner);
        Assert.Equal(7, result.Number);
        Assert.Equal(["owner/repo", "owner/other"], result.LinkedRepositories);
    }

    [Theory]
    [InlineData("{\"data\":{\"repositoryOwner\":null}}")]
    [InlineData("{\"data\":{\"repositoryOwner\":{\"projectV2\":null}}}")]
    public async Task GetProject_returns_null_when_project_is_unavailable(string response)
    {
        var result = await Client(new QueueGhProcess(response)).GetProjectAsync(
            "github.com", "owner", 7, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindProjectsByTitle_follows_pagination_and_matches_exact_title()
    {
        var process = new QueueGhProcess(
            """
            {"data":{"repositoryOwner":{"projectsV2":{"nodes":[{"id":"P_1","number":1,"title":"Other","url":"https://example.test/1"},{"id":"P_2","number":2,"title":"Tracker","url":"https://example.test/2"}],"pageInfo":{"hasNextPage":true,"endCursor":"next"}}}}}
            """,
            """
            {"data":{"repositoryOwner":{"projectsV2":{"nodes":[{"id":"P_3","number":3,"title":"Tracker","url":"https://example.test/3","repositories":{"nodes":[{"nameWithOwner":"owner/repo"}]}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
            """);

        var results = await Client(process).FindProjectsByTitleAsync(
            "github.com", "owner", "Tracker", CancellationToken.None);

        Assert.Equal([2, 3], results.Select(project => project.Number));
        Assert.Empty(results[0].LinkedRepositories);
        Assert.Equal(["owner/repo"], results[1].LinkedRepositories);
        Assert.Equal(2, process.Calls.Count);
        using var secondInput = JsonDocument.Parse(process.Calls[1].StandardInput!);
        Assert.Equal("next", secondInput.RootElement.GetProperty("variables").GetProperty("cursor").GetString());
    }

    [Theory]
    [InlineData("{\"data\":{\"repositoryOwner\":null}}")]
    [InlineData("{\"data\":{\"repositoryOwner\":{}}}")]
    public async Task FindProjectsByTitle_reports_missing_owner(string response)
    {
        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Client(new QueueGhProcess(response)).FindProjectsByTitleAsync(
                "github.com", "missing", "Tracker", CancellationToken.None));

        Assert.Equal("PROJECT_OWNER_NOT_FOUND", exception.Code);
    }

    [Fact]
    public async Task CreateProject_resolves_owner_then_creates_project()
    {
        var process = new QueueGhProcess(
            """{"data":{"repositoryOwner":{"id":"O_1","login":"owner"}}}""",
            """{"data":{"createProjectV2":{"projectV2":{"id":"P_8","number":8,"title":"Tracker","url":"https://example.test/8"}}}}""");

        var result = await Client(process).CreateProjectAsync(
            "github.com", "owner", "Tracker", CancellationToken.None);

        Assert.Equal("P_8", result.NodeId);
        Assert.Equal(8, result.Number);
        Assert.Equal("Tracker", result.Title);
        Assert.Empty(result.LinkedRepositories);
        using var createInput = JsonDocument.Parse(process.Calls[1].StandardInput!);
        Assert.Equal("O_1", createInput.RootElement.GetProperty("variables").GetProperty("ownerId").GetString());
    }

    [Fact]
    public async Task CreateProject_reports_missing_owner_without_attempting_mutation()
    {
        var process = new QueueGhProcess("""{"data":{"repositoryOwner":null}}""");

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Client(process).CreateProjectAsync("github.com", "missing", "Tracker", CancellationToken.None));

        Assert.Equal("PROJECT_OWNER_NOT_FOUND", exception.Code);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task LinkRepository_sends_project_and_repository_ids()
    {
        var process = new QueueGhProcess("""{"data":{"linkProjectV2ToRepository":{"repository":{"id":"R_1"}}}}""");

        await Client(process).LinkRepositoryAsync(
            "github.com", "P_1", "R_1", CancellationToken.None);

        using var input = JsonDocument.Parse(process.Calls.Single().StandardInput!);
        var variables = input.RootElement.GetProperty("variables");
        Assert.Equal("P_1", variables.GetProperty("projectId").GetString());
        Assert.Equal("R_1", variables.GetProperty("repositoryId").GetString());
    }

    [Fact]
    public async Task InitializeWorkerLabels_creates_every_missing_managed_label()
    {
        var notFound = new GhProcessResult(1, string.Empty, "HTTP 404: Not Found");
        var created = new GhProcessResult(0, "{}", string.Empty);
        var process = new QueueGhProcess(
            notFound, notFound, notFound, notFound,
            created, created, created, created);

        var actions = await Client(process).InitializeWorkerLabelsAsync(
            "github.com", "owner/repo", false, CancellationToken.None);

        Assert.Equal(4, actions.Count);
        Assert.Equal(8, process.Calls.Count);
        var createdLabels = process.Calls.Skip(4)
            .Select(call => JsonDocument.Parse(call.StandardInput!).RootElement
                .GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(
            ["wrighty:auto", "wrighty:agent=claude", "wrighty:agent=codex", "wrighty:agent=copilot"],
            createdLabels);
    }

    [Fact]
    public async Task InitializeWorkerLabels_check_reports_all_missing_labels_without_writes()
    {
        var notFound = new GhProcessResult(1, string.Empty, "HTTP 404: Not Found");
        var process = new QueueGhProcess(notFound, notFound, notFound, notFound);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Client(process).InitializeWorkerLabelsAsync(
                "github.com", "owner/repo", true, CancellationToken.None));

        Assert.Equal("PROJECT_INITIALIZATION_REQUIRED", exception.Code);
        Assert.Contains("wrighty:agent=copilot", exception.Message);
        Assert.Equal(4, process.Calls.Count);
        Assert.All(process.Calls, call => Assert.DoesNotContain("--method", call.Arguments));
    }

    [Fact]
    public async Task ListProjectViews_returns_exact_layout_and_project_relative_url()
    {
        var process = new QueueGhProcess("""
            {"data":{"repositoryOwner":{"projectV2":{"views":{"nodes":[
              {"id":"VIEW_1","number":1,"name":"View 1","layout":"TABLE_LAYOUT"},
              {"id":"VIEW_2","number":2,"name":"Wrighty Board","layout":"BOARD_LAYOUT"}
            ]}}}}}
            """);
        var project = new GitHubProjectInfo(
            "P_15",
            "owner",
            15,
            "Tracker",
            "https://github.example/users/owner/projects/15",
            []);

        var views = await Client(process).ListProjectViewsAsync(
            "github.example", project, CancellationToken.None);

        Assert.Equal(2, views.Count);
        Assert.Equal("BOARD_LAYOUT", views[1].Layout);
        Assert.Equal(
            "https://github.example/users/owner/projects/15/views/2",
            views[1].Url);
    }

    [Theory]
    [InlineData("User", "/users/owner/projectsV2/15/views")]
    [InlineData("Organization", "/orgs/owner/projectsV2/15/views")]
    public async Task CreateProjectView_uses_owner_specific_versioned_endpoint(
        string ownerType,
        string endpoint)
    {
        var process = new QueueGhProcess("""{"id":46278138,"name":"Wrighty Board","layout":"board"}""");
        var project = new GitHubProjectInfo(
            "P_15",
            "owner",
            15,
            "Tracker",
            "https://github.com/users/owner/projects/15",
            [],
            ownerType);

        await Client(process).CreateProjectViewAsync(
            "github.com", project, "Wrighty Board", CancellationToken.None);

        var call = Assert.Single(process.Calls);
        Assert.Contains(endpoint, call.Arguments);
        Assert.Contains("X-GitHub-Api-Version: 2026-03-10", call.Arguments);
        using var input = JsonDocument.Parse(call.StandardInput!);
        Assert.Equal("Wrighty Board", input.RootElement.GetProperty("name").GetString());
        Assert.Equal("board", input.RootElement.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task GraphQl_errors_are_combined_into_tracker_error()
    {
        var process = new QueueGhProcess("""{"errors":[{"message":"first"},{"message":"second"}]}""");

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Client(process).LinkRepositoryAsync("github.com", "P_1", "R_1", CancellationToken.None));

        Assert.Equal("GH_API_ERROR", exception.Code);
        Assert.Equal("first; second", exception.Message);
    }

    private static GitHubInitializationClient Client(IGhProcess process) => new(new GhApi(process));

    private sealed class QueueGhProcess : IGhProcess
    {
        private readonly Queue<GhProcessResult> responses;

        public QueueGhProcess(params string[] responses)
        {
            this.responses = new Queue<GhProcessResult>(responses.Select(response =>
                new GhProcessResult(0, response, string.Empty)));
        }

        public QueueGhProcess(params GhProcessResult[] responses)
        {
            this.responses = new Queue<GhProcessResult>(responses);
        }

        public List<Call> Calls { get; } = [];

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(arguments, standardInput));
            return Task.FromResult(responses.Dequeue());
        }
    }

    private sealed record Call(IReadOnlyList<string> Arguments, string? StandardInput);
}
