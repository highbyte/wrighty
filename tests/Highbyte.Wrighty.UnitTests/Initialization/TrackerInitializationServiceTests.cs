using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;

namespace Highbyte.Wrighty.UnitTests.Initialization;

public sealed class TrackerInitializationServiceTests
{
    [Fact]
    public async Task Missing_config_creates_links_initializes_and_saves_the_durable_project()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(repository: "owner/repo"),
            CancellationToken.None);

        Assert.True(result.CreatedProject);
        Assert.True(result.LinkedRepository);
        Assert.Equal("Wrighty - owner/repo", result.ProjectTitle);
        Assert.Equal(12, result.Config.ProjectNumber);
        Assert.Equal(1, fixture.Store.Saves);
        Assert.Equal(12, fixture.Store.Saved!.ProjectNumber);
        Assert.Equal(1, fixture.GitHub.Creates);
        Assert.Equal(1, fixture.GitHub.Links);
        Assert.Equal(1, fixture.Projects.Initializations);
    }

    [Fact]
    public async Task Missing_config_uses_discovered_origin_when_repository_is_omitted()
    {
        var fixture = new Fixture
        {
            DiscoveryResult = new DiscoveredGitHubRepository("github.com", "owner/repo")
        };

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(),
            CancellationToken.None);

        Assert.Equal("owner/repo", result.Config.Repository);
        Assert.Equal("origin", fixture.Discovery.LastRemote);
    }

    [Fact]
    public async Task Explicit_github_without_explicit_or_discovered_repository_is_actionable()
    {
        var fixture = new Fixture();

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync(
                "/work",
                Request(backend: "github"),
                CancellationToken.None));

        Assert.Equal("REPOSITORY_REQUIRED", exception.Code);
        Assert.Contains("--repository OWNER/REPOSITORY", exception.Message);
        Assert.Equal(0, fixture.GitHub.RepositoryReads);
        Assert.Equal(0, fixture.Store.Saves);
    }

    [Fact]
    public async Task Existing_config_accepts_matching_arguments_as_assertions()
    {
        var fixture = new Fixture();
        fixture.Store.Existing = ExistingConfig();
        fixture.GitHub.ExistingProject = Project(10, ["owner/repo"]);

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(
                repository: "OWNER/REPO",
                projectOwner: "OWNER",
                projectNumber: 10),
            CancellationToken.None);

        Assert.False(result.CreatedProject);
        Assert.Equal(0, fixture.Store.Saves);
        Assert.Equal(0, fixture.GitHub.Creates);
    }

    [Fact]
    public async Task Existing_config_rejects_conflicting_identity_before_github_access()
    {
        var fixture = new Fixture();
        fixture.Store.Existing = ExistingConfig();

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync(
                "/work",
                Request(repository: "owner/other"),
                CancellationToken.None));

        Assert.Equal("CONFIG_CONFLICT", exception.Code);
        Assert.Equal(0, fixture.GitHub.RepositoryReads);
        Assert.Equal(0, fixture.Store.Saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_config_rejects_bootstrap_only_title_or_remote(bool useTitle)
    {
        var fixture = new Fixture();
        fixture.Store.Existing = ExistingConfig();
        var request = useTitle
            ? Request(projectTitle: "Another")
            : Request(remote: "upstream");

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync("/work", request, CancellationToken.None));

        Assert.Equal("OPTION_BOOTSTRAP_ONLY", exception.Code);
        Assert.Equal(0, fixture.GitHub.RepositoryReads);
    }

    [Fact]
    public async Task Check_without_matching_project_is_read_only()
    {
        var fixture = new Fixture();

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync(
                "/work",
                Request(repository: "owner/repo", checkOnly: true),
                CancellationToken.None));

        Assert.Equal("PROJECT_INITIALIZATION_REQUIRED", exception.Code);
        Assert.Equal(0, fixture.GitHub.Creates);
        Assert.Equal(0, fixture.GitHub.Links);
        Assert.Equal(0, fixture.Store.Saves);
        Assert.Equal(0, fixture.Projects.Initializations);
    }

    [Fact]
    public async Task Missing_config_reuses_one_exact_project_title_match()
    {
        var fixture = new Fixture();
        fixture.GitHub.MatchingProjects = [Project(27)];

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(repository: "owner/repo", projectTitle: "Tracker"),
            CancellationToken.None);

        Assert.False(result.CreatedProject);
        Assert.Equal(27, result.Config.ProjectNumber);
        Assert.Equal(27, fixture.Store.Saved!.ProjectNumber);
        Assert.Equal(0, fixture.GitHub.Creates);
    }

    [Fact]
    public async Task Missing_config_rejects_ambiguous_exact_project_title_without_mutation()
    {
        var fixture = new Fixture();
        fixture.GitHub.MatchingProjects = [Project(27), Project(31)];

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync(
                "/work",
                Request(repository: "owner/repo", projectTitle: "Tracker"),
                CancellationToken.None));

        Assert.Equal("PROJECT_TITLE_AMBIGUOUS", exception.Code);
        Assert.Equal(new[] { 27, 31 }, exception.Details["projectNumbers"]);
        Assert.Equal(0, fixture.GitHub.Creates);
        Assert.Equal(0, fixture.Store.Saves);
    }

    [Fact]
    public async Task Cross_owner_bootstrap_persists_link_opt_out_and_does_not_link()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(repository: "owner/repo", projectOwner: "planning-org"),
            CancellationToken.None);

        Assert.False(result.Config.LinkRepository);
        Assert.False(result.LinkedRepository);
        Assert.Equal(0, fixture.GitHub.Links);
        Assert.Contains(result.Actions, action => action.Contains("owners differ"));
    }

    [Fact]
    public async Task Partial_bootstrap_keeps_allocated_project_in_saved_configuration()
    {
        var fixture = new Fixture();
        fixture.Projects.Failure = new TrackerException("PROJECT_SCHEMA_INVALID", "wrong type", 5);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync(
                "/work",
                Request(repository: "owner/repo"),
                CancellationToken.None));

        Assert.Equal("PARTIAL_INITIALIZATION", exception.Code);
        Assert.Equal(12, fixture.Store.Saved!.ProjectNumber);
        Assert.Equal(12, exception.Details["projectNumber"]);
    }

    [Fact]
    public async Task Explicit_local_bootstrap_saves_custom_configuration_and_initializes_store()
    {
        var fixture = new Fixture();
        fixture.LocalBackend.InitializedResult = new BackendInitializationResult(
            true, ["created local store"]);

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(
                backend: "local-markdown",
                localPath: "tasks",
                statuses: ["Ready", "Doing", "Done"],
                priorities: ["High", "Low"]),
            CancellationToken.None);

        Assert.Equal("local-markdown", result.Config.Backend);
        Assert.Equal("tasks", result.Config.LocalMarkdown!.Path);
        Assert.Equal(["Ready", "Doing", "Done"], result.Config.LocalMarkdown.Statuses);
        Assert.Equal("explicit", result.BackendSelection);
        Assert.True(result.Changed);
        Assert.Equal(1, fixture.Store.Saves);
        Assert.Equal(1, fixture.LocalBackend.Initializations);
        Assert.False(fixture.LocalBackend.LastCheckOnly);
        Assert.Contains("wrote configuration", result.Actions);
        Assert.Contains("created local store", result.Actions);
    }

    [Fact]
    public async Task No_repository_or_remote_defaults_to_local_markdown()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.InitializeAsync(
            "/work", Request(), CancellationToken.None);

        Assert.Equal("local-markdown", result.Config.Backend);
        Assert.Equal("defaulted", result.BackendSelection);
        Assert.Equal(".wrighty", result.Config.LocalMarkdown!.Path);
        Assert.Equal(["Todo", "In Progress", "Done"], result.Config.LocalMarkdown.Statuses);
        Assert.Equal(["P0", "P1", "P2", "P3"], result.Config.LocalMarkdown.Priorities);
    }

    [Fact]
    public async Task Local_options_infer_local_backend_and_check_mode_does_not_save()
    {
        var fixture = new Fixture();

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(localPath: "items", checkOnly: true),
            CancellationToken.None);

        Assert.Equal("inferred", result.BackendSelection);
        Assert.Equal(0, fixture.Store.Saves);
        Assert.True(fixture.LocalBackend.LastCheckOnly);
        Assert.Equal(Path.GetFullPath("/work/items"), result.ProjectUrl);
    }

    [Fact]
    public async Task Existing_local_configuration_is_reused_without_saving()
    {
        var fixture = new Fixture();
        fixture.Store.Existing = LocalConfig();
        fixture.LocalBackend.InitializedResult = new BackendInitializationResult(false, ["store valid"]);

        var result = await fixture.Service.InitializeAsync(
            "/work",
            Request(backend: "local-markdown", localPath: "items"),
            CancellationToken.None);

        Assert.Equal("configured", result.BackendSelection);
        Assert.False(result.Changed);
        Assert.Equal(0, fixture.Store.Saves);
        Assert.Contains("store valid", result.Actions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_local_configuration_rejects_conflicting_or_bootstrap_options(bool pathConflict)
    {
        var fixture = new Fixture();
        fixture.Store.Existing = LocalConfig();
        var request = pathConflict
            ? Request(backend: "local-markdown", localPath: "other")
            : Request(backend: "local-markdown", statuses: ["Todo"]);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync("/work", request, CancellationToken.None));

        Assert.Equal(pathConflict ? "CONFIG_CONFLICT" : "OPTION_BOOTSTRAP_ONLY", exception.Code);
        Assert.Equal(0, fixture.LocalBackend.Initializations);
    }

    [Fact]
    public async Task Local_backend_requires_registered_backend()
    {
        var fixture = new Fixture();
        var service = new TrackerInitializationService(
            fixture.Store, fixture.Discovery, fixture.GitHub, fixture.Projects);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            service.InitializeAsync(
                "/work", Request(backend: "local-markdown"), CancellationToken.None));

        Assert.Equal("BACKEND_UNSUPPORTED", exception.Code);
    }

    [Fact]
    public async Task Invalid_initialization_arguments_are_rejected_before_dependencies()
    {
        var requests = new[]
        {
            Request(backend: "unknown"),
            Request(backend: "local-markdown", repository: "owner/repo"),
            Request(backend: "local-markdown", projectOwner: "owner"),
            Request(backend: "local-markdown", projectNumber: 1),
            Request(backend: "local-markdown", projectTitle: "Tracker"),
            Request(backend: "local-markdown", githubHost: "github.com"),
            Request(backend: "local-markdown", noLinkRepository: true, noLinkRepositorySpecified: true),
            Request(projectNumber: 0),
            Request(projectNumber: 1, projectTitle: "Tracker"),
            Request(projectTitle: " "),
            Request(projectOwner: " "),
            Request(remote: " "),
            Request(githubHost: " "),
            Request(githubHost: "https://github.com"),
            Request(githubHost: "github .com"),
            Request(repository: "owner"),
            Request(repository: "owner/"),
            Request(repository: "owner/repo!")
        };

        foreach (var request in requests)
        {
            var fixture = new Fixture();
            var exception = await Assert.ThrowsAsync<TrackerException>(() =>
                fixture.Service.InitializeAsync("/work", request, CancellationToken.None));
            Assert.Contains(exception.Code, new[] { "ARGUMENT_INVALID", "OPTION_BACKEND_MISMATCH" });
            Assert.Equal(0, fixture.Store.Loads);
        }
    }

    [Fact]
    public async Task GitHub_backend_rejects_local_options()
    {
        var fixture = new Fixture();

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync(
                "/work",
                Request(backend: "github", repository: "owner/repo", localPath: "items"),
                CancellationToken.None));

        Assert.Equal("OPTION_BACKEND_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task Discovered_remote_must_match_explicit_host()
    {
        var fixture = new Fixture
        {
            DiscoveryResult = new DiscoveredGitHubRepository("github.example", "owner/repo")
        };

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            fixture.Service.InitializeAsync(
                "/work",
                Request(backend: "github", githubHost: "github.com"),
                CancellationToken.None));

        Assert.Equal("GIT_REMOTE_UNSUPPORTED", exception.Code);
    }

    [Fact]
    public async Task Existing_configuration_conflicts_report_the_specific_option()
    {
        var requests = new[]
        {
            Request(backend: "local-markdown"),
            Request(githubHost: "github.example"),
            Request(projectOwner: "other"),
            Request(projectNumber: 11),
            Request(noLinkRepository: true, noLinkRepositorySpecified: true)
        };

        foreach (var request in requests)
        {
            var fixture = new Fixture();
            fixture.Store.Existing = ExistingConfig();
            var exception = await Assert.ThrowsAsync<TrackerException>(() =>
                fixture.Service.InitializeAsync("/work", request, CancellationToken.None));
            Assert.Equal("CONFIG_CONFLICT", exception.Code);
            Assert.NotNull(exception.Details["option"]);
        }
    }

    [Fact]
    public async Task Existing_project_number_must_resolve_and_check_requires_repository_link()
    {
        var missingFixture = new Fixture();
        missingFixture.Store.Existing = ExistingConfig();
        var missing = await Assert.ThrowsAsync<TrackerException>(() =>
            missingFixture.Service.InitializeAsync("/work", Request(), CancellationToken.None));
        Assert.Equal("PROJECT_NOT_FOUND", missing.Code);

        var unlinkedFixture = new Fixture();
        unlinkedFixture.Store.Existing = ExistingConfig();
        unlinkedFixture.GitHub.ExistingProject = Project(10);
        var unlinked = await Assert.ThrowsAsync<TrackerException>(() =>
            unlinkedFixture.Service.InitializeAsync(
                "/work", Request(checkOnly: true), CancellationToken.None));
        Assert.Equal("PROJECT_INITIALIZATION_REQUIRED", unlinked.Code);
        Assert.Equal(0, unlinkedFixture.GitHub.Links);
    }

    private static TrackerInitializationRequest Request(
        string? repository = null,
        string? githubHost = null,
        string? remote = null,
        string? projectOwner = null,
        int? projectNumber = null,
        string? projectTitle = null,
        bool checkOnly = false,
        string? backend = null,
        string? localPath = null,
        IReadOnlyList<string>? statuses = null,
        IReadOnlyList<string>? priorities = null,
        bool noLinkRepository = false,
        bool noLinkRepositorySpecified = false) => new(
        repository,
        githubHost,
        remote,
        projectOwner,
        projectNumber,
        projectTitle,
        noLinkRepository,
        noLinkRepositorySpecified,
        null,
        checkOnly,
        backend,
        localPath,
        statuses,
        priorities);

    private static TrackerConfig ExistingConfig() => new()
    {
        Repository = "owner/repo",
        ProjectOwner = "owner",
        ProjectNumber = 10
    };

    private static TrackerConfig LocalConfig() => new()
    {
        Backend = "local-markdown",
        SourcePath = "/work/.wrighty.json",
        LocalMarkdown = new LocalMarkdownBackendConfig
        {
            Path = "items",
            Statuses = ["Todo", "Done"],
            Priorities = ["P1"]
        }
    };

    private static GitHubProjectInfo Project(int number, IReadOnlyList<string>? repositories = null) =>
        new(
            $"PROJECT_{number}",
            "owner",
            number,
            "Tracker",
            $"https://github.com/users/owner/projects/{number}",
            repositories ?? []);

    private sealed class Fixture
    {
        public FakeStore Store { get; } = new();

        public FakeDiscovery Discovery { get; } = new();

        public FakeGitHub GitHub { get; } = new();

        public FakeProjects Projects { get; } = new();

        public FakeLocalBackend LocalBackend { get; } = new();

        public DiscoveredGitHubRepository? DiscoveryResult
        {
            set => Discovery.Result = value;
        }

        public TrackerInitializationService Service =>
            new(
                Store,
                Discovery,
                GitHub,
                Projects,
                new TrackerBackendRegistry([LocalBackend]));
    }

    private sealed class FakeStore : ITrackerConfigStore
    {
        public TrackerConfig? Existing { get; set; }

        public TrackerConfig? Saved { get; private set; }

        public int Saves { get; private set; }

        public int Loads { get; private set; }

        public string ResolvePath(string startDirectory, string? explicitPath) =>
            explicitPath ?? "/work/.wrighty.json";

        public Task<TrackerConfig?> TryLoadPathAsync(string path, CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(Existing);
        }

        public Task SaveAsync(string path, TrackerConfig config, CancellationToken cancellationToken)
        {
            Saves++;
            Saved = config;
            return Task.CompletedTask;
        }

        public Task<TrackerConfig> LoadAsync(string startDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(Existing!);
    }

    private sealed class FakeDiscovery : IRepositoryDiscovery
    {
        public DiscoveredGitHubRepository? Result { get; set; }

        public string? LastRemote { get; private set; }

        public Task<DiscoveredGitHubRepository?> DiscoverAsync(
            string directory,
            string remoteName,
            CancellationToken cancellationToken)
        {
            LastRemote = remoteName;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeGitHub : IGitHubInitializationClient
    {
        public GitHubProjectInfo? ExistingProject { get; set; }

        public IReadOnlyList<GitHubProjectInfo>? MatchingProjects { get; set; }

        public int RepositoryReads { get; private set; }

        public int Creates { get; private set; }

        public int Links { get; private set; }

        public Task<GitHubRepositoryInfo> GetRepositoryAsync(
            string host,
            string repository,
            CancellationToken cancellationToken)
        {
            RepositoryReads++;
            return Task.FromResult(new GitHubRepositoryInfo(
                "REPOSITORY",
                "owner/repo",
                "owner",
                "repo",
                "ADMIN"));
        }

        public Task<GitHubProjectInfo?> GetProjectAsync(
            string host,
            string owner,
            int number,
            CancellationToken cancellationToken) => Task.FromResult(ExistingProject);

        public Task<IReadOnlyList<GitHubProjectInfo>> FindProjectsByTitleAsync(
            string host,
            string owner,
            string title,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GitHubProjectInfo>>(
                MatchingProjects ?? (ExistingProject is null ? [] : [ExistingProject]));

        public Task<GitHubProjectInfo> CreateProjectAsync(
            string host,
            string owner,
            string title,
            CancellationToken cancellationToken)
        {
            Creates++;
            return Task.FromResult(new GitHubProjectInfo(
                "PROJECT_12",
                owner,
                12,
                title,
                $"https://github.com/users/{owner}/projects/12",
                []));
        }

        public Task LinkRepositoryAsync(
            string host,
            string projectNodeId,
            string repositoryNodeId,
            CancellationToken cancellationToken)
        {
            Links++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjects : IProjectClient
    {
        public Exception? Failure { get; set; }

        public int Initializations { get; private set; }

        public Task<ProjectInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken)
        {
            Initializations++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new ProjectInitializationResult(false, ["Project schema is valid."]));
        }

        public Task EnsureAgentContextSchemaAsync(TrackerConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubProjectItem>> ListAsync(TrackerConfig config, string? status, int? limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateStatusAsync(TrackerConfig config, GitHubProjectItem item, string status, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAgentContextAsync(TrackerConfig config, GitHubProjectItem item, string? agentType, string? sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ValidateCreateFieldsAsync(TrackerConfig config, string status, string? priority, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> AddIssueAsync(TrackerConfig config, string issueNodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdatePriorityAsync(TrackerConfig config, GitHubProjectItem item, string priority, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeLocalBackend : ITrackerBackend
    {
        public string Name => "local-markdown";

        public IWorkItemAddressResolver AddressResolver { get; } = new LocalMarkdownWorkItemAddressResolver();

        public BackendInitializationResult InitializedResult { get; set; } =
            new(false, ["local store valid"]);

        public int Initializations { get; private set; }

        public bool LastCheckOnly { get; private set; }

        public Task<BackendInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken)
        {
            Initializations++;
            LastCheckOnly = checkOnly;
            return Task.FromResult(InitializedResult);
        }

        public Task<IReadOnlyList<WorkItemSummary>> ListAsync(TrackerConfig config, ListWorkItemsRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkItemDetail?> GetAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CreateWorkItemResult> CreateAsync(TrackerConfig config, CreateWorkItemOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UpdateWorkItemResult> UpdateAsync(TrackerConfig config, WorkItemId id, UpdateWorkItemOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id, AgentExecutionContext agentContext, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id, AgentExecutionContext agentContext, CancellationToken cancellationToken, string? expectedClaimToken) => throw new NotSupportedException();
        public Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id, AgentExecutionContext claimantContext, string? currentClaimToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle, bool overrideClaimant, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArchiveWorkItemResult> ArchiveAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArchiveWorkItemResult> ArchiveAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArchiveWorkItemResult> UnarchiveAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
