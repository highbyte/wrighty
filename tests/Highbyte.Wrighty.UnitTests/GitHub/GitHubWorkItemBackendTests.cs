using System.Text.Json;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;

namespace Highbyte.Wrighty.UnitTests.GitHub;

public sealed class GitHubWorkItemBackendTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1,
        DefaultPickFrom = "Todo"
    };

    private static readonly GitHubWorkItemAddressResolver Resolver = new();
    private const string AttemptId = "019f5c485c2b7862aeac80eb638a7b5c";
    private const string TemporaryLabel = "sit-create-019f5c485c2b7862aeac80eb638a7b5c";

    [Fact]
    public async Task GetAsync_returns_exact_markdown_body_and_project_fields()
    {
        var process = new QueueGhProcess(IssueResponse(
            "Line one\n\n**bold**\n",
            labels:
            [
                "wrighty:auto",
                "wrighty:agent=claude",
                "wrighty:worker-state=needs-attention"
            ]));
        var projects = new FakeProjects { Items = [Item(42, "Todo", "P1")] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            new RecordingGuard(),
            (_, _) => Task.CompletedTask);

        var detail = await backend.GetAsync(Config, Id(42), CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Line one\n\n**bold**\n", detail.Body);
        Assert.Equal("Todo", detail.Status);
        Assert.Equal("P1", detail.Priority);
        Assert.True(detail.AutomationEligible);
        Assert.Equal("claude", detail.PreferredAgent);
        Assert.Equal(WorkerDispatchStates.NeedsAttention, detail.WorkerState);
    }

    [Fact]
    public async Task GetAsync_returns_null_when_issue_is_not_in_the_project()
    {
        var process = new QueueGhProcess();
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            new FakeProjects(),
            Resolver,
            new RecordingGuard());

        var detail = await backend.GetAsync(Config, Id(42), CancellationToken.None);

        Assert.Null(detail);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task CreateAsync_preflights_fields_before_allocating_an_issue()
    {
        var process = new QueueGhProcess();
        var projects = new FakeProjects { FailureStage = "preflight" };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.CreateAsync(
            Config,
            new CreateWorkItemRequest("Example", "", "Missing", null),
            CancellationToken.None));

        Assert.Equal("STATUS_NOT_FOUND", exception.Code);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task AdoptAsync_adds_existing_issue_and_defaults_status_without_creation_metadata()
    {
        var process = new QueueGhProcess(IssueResponse("Existing body"));
        var projects = new FakeProjects();
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            new RecordingGuard());

        var result = await backend.AdoptAsync(
            Config,
            "43",
            new AdoptWorkItemOptions(null, null, false, null),
            CancellationToken.None);

        Assert.Equal(AdoptDisposition.Adopted, result.Disposition);
        Assert.Equal("github:owner/repo#43", result.Id.Value);
        Assert.Equal("ISSUE_NODE", Assert.Single(projects.AddedIssueNodeIds));
        Assert.Equal("Todo", Assert.Single(projects.StatusUpdates));
        Assert.Empty(projects.CreationAttemptUpdates);
        Assert.Equal(["projectMembership", "status"], result.AppliedStages);
    }

    [Fact]
    public async Task AdoptAsync_agent_does_not_imply_auto_and_existing_fields_are_preserved()
    {
        var process = new QueueGhProcess(
            IssueResponse("Existing body", labels: ["triage", "wrighty:auto"]),
            "{}",
            IssueResponse("Existing body", labels: ["triage", "wrighty:auto"]),
            "{}");
        var projects = new FakeProjects { Items = [Item(43, "In Progress", "P1")] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            new RecordingGuard());

        var result = await backend.AdoptAsync(
            Config,
            "owner/repo#43",
            new AdoptWorkItemOptions(null, null, false, "codex"),
            CancellationToken.None);

        Assert.Equal(AdoptDisposition.Reconciled, result.Disposition);
        Assert.Empty(projects.StatusUpdates);
        Assert.Empty(projects.PriorityUpdates);
        var patch = process.Calls.Single(call => call.Method == "PATCH");
        Assert.Contains("wrighty:auto", patch.StandardInput);
        Assert.Contains("wrighty:agent=codex", patch.StandardInput);
    }

    [Fact]
    public async Task AdoptAsync_reports_partial_after_membership_when_status_fails()
    {
        var process = new QueueGhProcess(IssueResponse("Existing body"));
        var projects = new FakeProjects { FailureStage = "status-set" };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            new RecordingGuard());

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.AdoptAsync(
            Config,
            "43",
            new AdoptWorkItemOptions(null, null, false, null),
            CancellationToken.None));

        Assert.Equal("PARTIAL_ADOPT", exception.Code);
        Assert.Equal("github:owner/repo#43", exception.Details["id"]);
        Assert.Equal("status", exception.Details["failedStage"]);
        Assert.Equal(
            ["projectMembership"],
            Assert.IsType<string[]>(exception.Details["appliedFields"]));
    }

    [Fact]
    public async Task AdoptAsync_rejects_pull_request_url_before_project_write()
    {
        var process = new QueueGhProcess(JsonSerializer.Serialize(new
        {
            number = 43,
            node_id = "PULL_NODE",
            title = "Pull request",
            body = "",
            html_url = "https://github.com/owner/repo/pull/43",
            labels = Array.Empty<object>(),
            pull_request = new { url = "https://api.github.com/repos/owner/repo/pulls/43" }
        }));
        var projects = new FakeProjects();
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            new RecordingGuard());

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.AdoptAsync(
            Config,
            "https://github.com/owner/repo/pull/43",
            new AdoptWorkItemOptions(null, null, false, null),
            CancellationToken.None));

        Assert.Equal("ADOPT_SOURCE_UNSUPPORTED", exception.Code);
        Assert.Empty(projects.AddedIssueNodeIds);
    }

    [Fact]
    public async Task AdoptAsync_preserves_archived_membership_without_options()
    {
        var archived = Item(43, "Done", "P1");
        archived = archived with
        {
            Summary = archived.Summary with { Archived = true }
        };
        var projects = new FakeProjects { Items = [archived] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(new QueueGhProcess(IssueResponse("Existing body"))),
            projects,
            Resolver,
            new RecordingGuard());

        var result = await backend.AdoptAsync(
            Config,
            "43",
            new AdoptWorkItemOptions(null, null, false, null),
            CancellationToken.None);

        Assert.Equal(AdoptDisposition.AlreadyAdopted, result.Disposition);
        Assert.Empty(result.AppliedStages);
        Assert.Empty(projects.AddedIssueNodeIds);
    }

    [Fact]
    public async Task AdoptAsync_refuses_to_modify_archived_membership()
    {
        var archived = Item(43, "Done", null);
        archived = archived with
        {
            Summary = archived.Summary with { Archived = true }
        };
        var projects = new FakeProjects { Items = [archived] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(new QueueGhProcess(IssueResponse("Existing body"))),
            projects,
            Resolver,
            new RecordingGuard());

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => backend.AdoptAsync(
                Config,
                "43",
                new AdoptWorkItemOptions("Todo", null, false, null),
                CancellationToken.None));

        Assert.Equal("ADOPT_SOURCE_UNSUPPORTED", exception.Code);
        Assert.Contains("does not unarchive", exception.Message);
    }

    [Fact]
    public async Task AdoptAsync_repairs_missing_status_and_applies_explicit_priority()
    {
        var projects = new FakeProjects { Items = [Item(43, null, "P2")] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(new QueueGhProcess(IssueResponse("Existing body"))),
            projects,
            Resolver,
            new RecordingGuard());

        var result = await backend.AdoptAsync(
            Config,
            "43",
            new AdoptWorkItemOptions(null, "P0", false, null),
            CancellationToken.None);

        Assert.Equal(AdoptDisposition.Reconciled, result.Disposition);
        Assert.Equal(["Todo"], projects.StatusUpdates);
        Assert.Equal(["P0"], projects.PriorityUpdates);
        Assert.Equal(["status", "priority"], result.AppliedStages);
    }

    [Fact]
    public async Task Custom_fields_are_rejected_as_not_supported_before_GitHub_access()
    {
        var process = new QueueGhProcess();
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), new FakeProjects(), Resolver, new RecordingGuard());

        var create = await Assert.ThrowsAsync<TrackerException>(() => backend.CreateAsync(
            Config,
            new CreateWorkItemRequest("Example", "", "Todo", null,
                new Dictionary<string, string?> { ["epic"] = "PLAT-3" }),
            CancellationToken.None));
        var update = await Assert.ThrowsAsync<TrackerException>(() => backend.UpdateAsync(
            Config,
            Id(42),
            new WorkItemPatch(default, default, default, default,
                OptionalValue<IReadOnlyDictionary<string, string?>>.From(
                    new Dictionary<string, string?> { ["epic"] = "PLAT-4" })),
            CancellationToken.None));

        Assert.Equal("NOT_SUPPORTED", create.Code);
        Assert.Equal("NOT_SUPPORTED", update.Code);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task CreateAsync_allocates_adds_and_sets_fields()
    {
        var process = new QueueGhProcess(
            PermissionResponse,
            "{}",
            "[[]]",
            IssueResponse("Body", labels: [TemporaryLabel]),
            IssueResponse("Body"),
            LabelResponse,
            "",
            "[[]]",
            "");
        var projects = new FakeProjects();
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());

        var result = await backend.CreateAsync(
            Config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Example", "Body", "Todo", "P1"),
                false,
                AttemptId),
            CancellationToken.None);

        Assert.Equal("github:owner/repo#43", result.Id.Value);
        Assert.Equal("ISSUE_NODE", Assert.Single(projects.AddedIssueNodeIds));
        Assert.Equal("Todo", Assert.Single(projects.StatusUpdates));
        Assert.Equal("P1", Assert.Single(projects.PriorityUpdates));
        Assert.Equal(AttemptId, Assert.Single(projects.CreationAttemptUpdates));
        Assert.Equal(9, process.Calls.Count);
        Assert.Equal("POST", process.Calls[3].Method);
        Assert.Contains(TemporaryLabel, process.Calls[3].StandardInput);
        Assert.Equal(2, process.Calls.Count(call => call.Method == "DELETE"));
    }

    [Fact]
    public async Task CreateAsync_fails_before_issue_allocation_without_label_permission()
    {
        var process = new QueueGhProcess("""{ "permissions": { "push": false } }""");
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), new FakeProjects(), Resolver, new RecordingGuard());

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.CreateAsync(
            Config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Example", "Body", "Todo", null),
                false,
                AttemptId),
            CancellationToken.None));

        Assert.Equal("GITHUB_PERMISSION_REQUIRED", exception.Code);
        Assert.Single(process.Calls);
        Assert.DoesNotContain(process.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task CreateAsync_returns_durable_project_match_without_label_mutation()
    {
        var process = new QueueGhProcess(IssueResponse("Current body"));
        var projects = new FakeProjects
        {
            Items = [Item(43, "In Progress", "P1") with { CreationAttemptId = AttemptId }]
        };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());

        var result = await backend.CreateAsync(
            Config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Original", "Original body", "Todo", null),
                false,
                AttemptId),
            CancellationToken.None);

        Assert.Equal(CreateDisposition.Resumed, result.Disposition);
        Assert.Equal("Current body", result.Item!.Body);
        Assert.Single(process.Calls);
        Assert.Equal("GET", process.Calls[0].Method);
        Assert.Empty(projects.StatusUpdates);
    }

    [Fact]
    public async Task CreateAsync_recovers_issue_by_temporary_label_without_second_issue_post()
    {
        var process = new QueueGhProcess(
            PermissionResponse,
            "{}",
            $"[[{IssueResponse("Body", labels: [TemporaryLabel])}]]",
            IssueResponse("Body"),
            LabelResponse,
            "",
            "[[]]",
            "");
        var projects = new FakeProjects { Items = [Item(43, "Todo", "P1")] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());

        var result = await backend.CreateAsync(
            Config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Example", "Body", "Todo", "P1"),
                false,
                AttemptId),
            CancellationToken.None);

        Assert.Equal(CreateDisposition.Resumed, result.Disposition);
        Assert.DoesNotContain(process.Calls, call =>
            call.Method == "POST" && call.Arguments.Last().EndsWith("/issues", StringComparison.Ordinal));
        Assert.Empty(projects.AddedIssueNodeIds);
        Assert.Equal(2, process.Calls.Count(call => call.Method == "DELETE"));
    }

    [Fact]
    public async Task CreateAsync_resumes_missing_status_after_creation_id_was_recorded()
    {
        var process = new QueueGhProcess(
            IssueResponse("Body", labels: [TemporaryLabel]),
            IssueResponse("Body"),
            LabelResponse,
            "",
            "[[]]",
            "");
        var projects = new FakeProjects
        {
            Items = [Item(43, null, "P1") with { CreationAttemptId = AttemptId }]
        };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());

        var result = await backend.CreateAsync(
            Config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Example", "Body", "Todo", "P1"),
                false,
                AttemptId),
            CancellationToken.None);

        Assert.Equal(CreateDisposition.Resumed, result.Disposition);
        Assert.Equal("Todo", Assert.Single(projects.StatusUpdates));
        Assert.Contains("status-set", result.EffectiveReconciledStages);
        Assert.Equal(2, process.Calls.Count(call => call.Method == "DELETE"));
    }

    [Theory]
    [InlineData("project-add")]
    [InlineData("status-set")]
    [InlineData("priority-set")]
    [InlineData("final-read")]
    public async Task CreateAsync_reports_each_stable_partial_create_stage(string stage)
    {
        var responses = new[]
        {
            PermissionResponse,
            "{}",
            "[[]]",
            IssueResponse("Body", labels: [TemporaryLabel])
        };
        var process = new QueueGhProcess(responses);
        var projects = new FakeProjects
        {
            FailureStage = stage,
            Items = []
        };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            new RecordingGuard(),
            (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.CreateAsync(
            Config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Example", "Body", "Todo", "P1"),
                false,
                AttemptId),
            CancellationToken.None));

        Assert.Equal("PARTIAL_CREATE", exception.Code);
        Assert.Equal(10, exception.ExitCode);
        Assert.Equal("github:owner/repo#43", exception.Details["id"]);
        Assert.Equal("#43", exception.Details["displayId"]);
        Assert.Equal(stage, exception.Details["failedStage"]);
        Assert.DoesNotContain(process.Calls, call => call.Method == "DELETE");
        Assert.Equal(4, process.Calls.Count);
    }

    [Fact]
    public async Task UpdateAsync_applies_issue_priority_and_status_in_order_with_guard_checks()
    {
        var process = new QueueGhProcess(
            IssueResponse("Old body"),
            "{}",
            IssueResponse("New body", "New title"));
        var projects = new FakeProjects { Items = [Item(43, "Todo", "P1")] };
        var guard = new RecordingGuard();
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            guard);
        var patch = new WorkItemPatch(
            OptionalValue<string>.From("New title"),
            OptionalValue<string>.From("New body"),
            OptionalValue<string>.From("Done"),
            OptionalValue<string?>.From("P0"));

        var result = await backend.UpdateAsync(Config, Id(43), patch, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(["title", "body", "priority", "status"], result.ChangedFields);
        Assert.Equal(3, guard.Checks);
        Assert.Equal(["priority:P0", "status:Done"], projects.UpdateOrder);
        Assert.Equal("PATCH", process.Calls[1].Method);
        Assert.Contains("New title", process.Calls[1].StandardInput);
        Assert.Equal("Done", result.Item.Status);
        Assert.Equal("P0", result.Item.Priority);
    }

    [Fact]
    public async Task UpdateAsync_no_op_performs_no_mutation_or_guard_check()
    {
        var process = new QueueGhProcess(IssueResponse("Body"));
        var projects = new FakeProjects { Items = [Item(43, "Todo", "P1")] };
        var guard = new RecordingGuard();
        var backend = new GitHubWorkItemBackend(
            new GhApi(process),
            projects,
            Resolver,
            guard);

        var result = await backend.UpdateAsync(
            Config,
            Id(43),
            WorkItemPatch.StatusOnly("todo"),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Empty(result.ChangedFields);
        Assert.Equal(0, guard.Checks);
        Assert.Equal(0, projects.UpdateValidations);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task UpdateAsync_clears_priority()
    {
        var process = new QueueGhProcess(
            IssueResponse("Body"),
            IssueResponse("Body"));
        var projects = new FakeProjects { Items = [Item(43, "Todo", "P1")] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());
        var patch = new WorkItemPatch(
            default,
            default,
            default,
            OptionalValue<string?>.From(null));

        var result = await backend.UpdateAsync(Config, Id(43), patch, CancellationToken.None);

        Assert.Equal(["priority"], result.ChangedFields);
        Assert.Equal(["priority:clear"], projects.UpdateOrder);
        Assert.Null(result.Item.Priority);
    }

    [Fact]
    public async Task UpdateAsync_replaces_managed_worker_labels_and_preserves_user_labels()
    {
        var process = new QueueGhProcess(
            IssueResponse(
                "Body",
                labels:
                [
                    "team:platform",
                    "wrighty:agent=codex",
                    "wrighty:worker-state=queued"
                ]),
            "{}",
            "{}",
            "{}",
            "{}",
            IssueResponse(
                "Body",
                labels:
                [
                    "team:platform",
                    "wrighty:auto",
                    "wrighty:agent=claude",
                    "wrighty:worker-state=needs-attention"
                ]));
        var projects = new FakeProjects { Items = [Item(43, "In Progress", "P1")] };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());
        var patch = new WorkItemPatch(
            default,
            default,
            default,
            default,
            AutomationEligible: OptionalValue<bool>.From(true),
            PreferredAgent: OptionalValue<string?>.From("claude"),
            WorkerState: OptionalValue<string?>.From(WorkerDispatchStates.NeedsAttention));

        var result = await backend.UpdateAsync(
            Config, Id(43), patch, CancellationToken.None);

        Assert.True(result.Item.AutomationEligible);
        Assert.Equal("claude", result.Item.PreferredAgent);
        Assert.Equal(WorkerDispatchStates.NeedsAttention, result.Item.WorkerState);
        var issuePatch = Assert.Single(process.Calls,
            call => call.Method == "PATCH" &&
                    call.Arguments.Last().EndsWith("/issues/43", StringComparison.Ordinal));
        Assert.Contains("team:platform", issuePatch.StandardInput);
        Assert.DoesNotContain("wrighty:agent=codex", issuePatch.StandardInput);
        Assert.Contains("wrighty:auto", issuePatch.StandardInput);
        Assert.Contains("wrighty:agent=claude", issuePatch.StandardInput);
        Assert.Contains("wrighty:worker-state=needs-attention", issuePatch.StandardInput);
    }

    [Fact]
    public async Task UpdateAsync_reports_applied_and_pending_fields_after_partial_failure()
    {
        var process = new QueueGhProcess(IssueResponse("Old body"), "{}");
        var projects = new FakeProjects
        {
            Items = [Item(43, "Todo", "P1")],
            FailureStage = "priority-set"
        };
        var backend = new GitHubWorkItemBackend(
            new GhApi(process), projects, Resolver, new RecordingGuard());
        var patch = new WorkItemPatch(
            OptionalValue<string>.From("New title"),
            default,
            OptionalValue<string>.From("Done"),
            OptionalValue<string?>.From("P0"));

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.UpdateAsync(
            Config,
            Id(43),
            patch,
            CancellationToken.None));

        Assert.Equal("PARTIAL_UPDATE", exception.Code);
        Assert.Equal("priority-set", exception.Details["failedStage"]);
        Assert.Equal(["title"], Assert.IsType<string[]>(exception.Details["appliedFields"]));
        Assert.Equal(
            ["priority", "status"],
            Assert.IsType<string[]>(exception.Details["pendingFields"]));
        Assert.Equal("GH_API_ERROR", exception.Details["causeCode"]);
    }

    [Fact]
    public async Task UpdateAsync_reports_claim_loss_after_an_earlier_write()
    {
        var process = new QueueGhProcess(IssueResponse("Body"), "{}");
        var projects = new FakeProjects { Items = [Item(43, "Todo", "P1")] };
        var guard = new RecordingGuard(failAtCheck: 2);
        var backend = new GitHubWorkItemBackend(new GhApi(process), projects, Resolver, guard);
        var patch = new WorkItemPatch(
            OptionalValue<string>.From("New title"),
            default,
            default,
            OptionalValue<string?>.From("P0"));

        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.UpdateAsync(
            Config,
            Id(43),
            patch,
            CancellationToken.None));

        Assert.Equal("PARTIAL_UPDATE", exception.Code);
        Assert.Equal("CLAIM_LOST", exception.Details["causeCode"]);
        Assert.Equal(["title"], Assert.IsType<string[]>(exception.Details["appliedFields"]));
        Assert.Equal(["priority"], Assert.IsType<string[]>(exception.Details["pendingFields"]));
        Assert.Empty(projects.UpdateOrder);
    }

    private static WorkItemId Id(int number) => Resolver.FromIssueNumber(Config, number);

    private static GitHubProjectItem Item(int number, string? status, string? priority) => new(
        new GitHubWorkItemAddress("github.com", "owner", "repo", number),
        new WorkItemSummary(
            Id(number),
            "Example",
            $"https://github.com/owner/repo/issues/{number}",
            status,
            priority),
        "ISSUE_NODE",
        "PROJECT_ITEM");

    private const string PermissionResponse = """
        { "permissions": { "push": true } }
        """;

    private static string LabelResponse => JsonSerializer.Serialize(new
    {
        description = $"SIT create sha256:{CreationAttempt.ComputeIntentHash(
            new CreateWorkItemRequest("Example", "Body", "Todo", "P1"),
            false)}"
    });

    private static string IssueResponse(
        string body,
        string title = "Example",
        string[]? labels = null) => JsonSerializer.Serialize(new
        {
            number = 43,
            node_id = "ISSUE_NODE",
            title,
            body,
            html_url = "https://github.com/owner/repo/issues/43",
            labels = (labels ?? []).Select(name => new { name }).ToArray()
        });

    private sealed class FakeProjects : IProjectClient
    {
        public IReadOnlyList<GitHubProjectItem> Items { get; set; } = [];

        public string? FailureStage { get; init; }

        public List<string> AddedIssueNodeIds { get; } = [];

        public List<string> StatusUpdates { get; } = [];

        public List<string> PriorityUpdates { get; } = [];

        public List<string> CreationAttemptUpdates { get; } = [];

        public List<string> UpdateOrder { get; } = [];

        public int UpdateValidations { get; private set; }

        public Task<ProjectInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task EnsureAgentContextSchemaAsync(
            TrackerConfig config,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<GitHubProjectItem>> FindByCreationAttemptIdAsync(
            TrackerConfig config,
            string creationAttemptId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitHubProjectItem>>(
            Items.Where(item => string.Equals(
                item.CreationAttemptId,
                creationAttemptId,
                StringComparison.Ordinal)).ToArray());

        public Task UpdateCreationAttemptIdAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            string creationAttemptId,
            CancellationToken cancellationToken)
        {
            CreationAttemptUpdates.Add(creationAttemptId);
            Items = Items.Select(value => value.Number == item.Number
                ? value with { CreationAttemptId = creationAttemptId }
                : value).ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GitHubProjectItem>> ListAsync(
            TrackerConfig config,
            string? status,
            int? limit,
            CancellationToken cancellationToken) => Task.FromResult(Items);

        public Task UpdateStatusAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            string status,
            CancellationToken cancellationToken)
        {
            if (FailureStage == "status-set")
            {
                throw Failure();
            }

            StatusUpdates.Add(status);
            UpdateOrder.Add($"status:{status}");
            Items = Items.Select(value => value.Number == item.Number
                ? value with { Summary = value.Summary with { Status = status } }
                : value).ToArray();
            return Task.CompletedTask;
        }

        public Task UpdateAgentContextAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            string? agentType,
            string? sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ValidateCreateFieldsAsync(
            TrackerConfig config,
            string status,
            string? priority,
            CancellationToken cancellationToken)
        {
            if (FailureStage == "preflight")
            {
                throw new TrackerException("STATUS_NOT_FOUND", "Missing", 5);
            }

            return Task.CompletedTask;
        }

        public Task<string> AddIssueAsync(
            TrackerConfig config,
            string issueNodeId,
            CancellationToken cancellationToken)
        {
            if (FailureStage == "project-add")
            {
                throw Failure();
            }

            AddedIssueNodeIds.Add(issueNodeId);
            if (FailureStage != "final-read")
            {
                Items = [.. Items, Item(43, null, null)];
            }
            return Task.FromResult("PROJECT_ITEM");
        }

        public Task UpdatePriorityAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            string priority,
            CancellationToken cancellationToken)
        {
            if (FailureStage == "priority-set")
            {
                throw Failure();
            }

            PriorityUpdates.Add(priority);
            UpdateOrder.Add($"priority:{priority}");
            Items = Items.Select(value => value.Number == item.Number
                ? value with { Summary = value.Summary with { Priority = priority } }
                : value).ToArray();
            return Task.CompletedTask;
        }

        public Task ClearPriorityAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            CancellationToken cancellationToken)
        {
            UpdateOrder.Add("priority:clear");
            Items = Items.Select(value => value.Number == item.Number
                ? value with { Summary = value.Summary with { Priority = null } }
                : value).ToArray();
            return Task.CompletedTask;
        }

        public Task ValidateUpdateFieldsAsync(
            TrackerConfig config,
            string? status,
            string? priority,
            bool clearPriority,
            CancellationToken cancellationToken)
        {
            UpdateValidations++;
            if (FailureStage == "preflight")
            {
                throw new TrackerException("STATUS_NOT_FOUND", "Missing", 5);
            }

            return Task.CompletedTask;
        }

        private static TrackerException Failure() => new("GH_API_ERROR", "Simulated failure");
    }

    private sealed class RecordingGuard(int? failAtCheck = null) : IWorkItemMutationGuard
    {
        public int Checks { get; private set; }

        public Task EnsureOwnedAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            Checks++;
            if (Checks == failAtCheck)
            {
                throw new TrackerException("CLAIM_LOST", "Lost", 6);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class QueueGhProcess(params string[] responses) : IGhProcess
    {
        private readonly Queue<string> responses = new(responses);

        public List<Call> Calls { get; } = [];

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            var methodIndex = Array.IndexOf(arguments.ToArray(), "--method");
            var method = methodIndex >= 0 ? arguments[methodIndex + 1] : "GET";
            Calls.Add(new Call(method, arguments, standardInput));
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No queued gh response.");
            }

            return Task.FromResult(new GhProcessResult(0, responses.Dequeue(), string.Empty));
        }
    }

    private sealed record Call(
        string Method,
        IReadOnlyList<string> Arguments,
        string? StandardInput);
}
