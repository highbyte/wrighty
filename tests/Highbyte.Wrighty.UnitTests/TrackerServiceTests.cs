using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;

namespace Highbyte.Wrighty.UnitTests;

public sealed class TrackerServiceTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1
    };

    [Fact]
    public async Task Operational_reads_use_the_backend_snapshot_and_report_missing_items()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>());
        var service = Service(projects, claims);

        var operational = await service.GetOperationalAsync(Config, Id(1), CancellationToken.None);
        Assert.Equal("Item 1", operational.Item.Title);
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, operational.Claim.State);

        var list = await service.ListOperationalAsync(
            Config, new ListWorkItemsRequest(null, null), CancellationToken.None);
        Assert.Equal("Item 1", Assert.Single(list).Item.Title);

        var missingService = Service(projects, claims, new MissingWorkItems());
        var missing = await Assert.ThrowsAsync<TrackerException>(() =>
            missingService.GetOperationalAsync(Config, Id(1), CancellationToken.None));
        Assert.Equal("WORK_ITEM_NOT_FOUND", missing.Code);
    }

    private sealed class MissingWorkItems : IWorkItemBackend
    {
        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            Task.FromResult<WorkItemDetail?>(null);

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config, CreateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config, WorkItemId id, WorkItemPatch patch,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task PickAsync_skips_a_held_item_and_moves_the_first_claimable_item()
    {
        var first = Item(1, "P0");
        var second = Item(2, "P1");
        var projects = new FakeProjects([first, second]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>
        {
            [1] = ClaimOutcome.HeldByOther,
            [2] = ClaimOutcome.Acquired
        });
        var service = Service(projects, claims);

        var picked = await service.PickAsync(
            Config,
            "Todo",
            "In Progress",
            AgentExecutionContext.None,
            CancellationToken.None);

        Assert.Equal(Id(2), picked.Id);
        Assert.Equal("In Progress", picked.Status);
        Assert.Equal([1, 2], claims.Attempts);
        Assert.Equal((2, "In Progress"), Assert.Single(projects.StatusUpdates));
    }

    [Fact]
    public async Task ClaimAsync_maps_contention_to_a_stable_error()
    {
        var service = Service(
            new FakeProjects([Item(1, "P1")]),
            new FakeClaims(new Dictionary<int, ClaimOutcome>
            {
                [1] = ClaimOutcome.HeldByOther
            }));

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => service.ClaimAsync(
                Config,
                Id(1),
                AgentExecutionContext.None,
                CancellationToken.None));

        Assert.Equal("CLAIM_HELD", exception.Code);
        Assert.Equal(6, exception.ExitCode);
    }

    [Fact]
    public async Task ClaimAsync_projects_the_winning_agent_context()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var service = Service(
            projects,
            new FakeClaims(new Dictionary<int, ClaimOutcome> { [1] = ClaimOutcome.Acquired }));

        await service.ClaimAsync(
            Config,
            Id(1),
            new AgentExecutionContext("codex", "session-1", AgentContextSource.ExplicitOption),
            CancellationToken.None);

        Assert.Equal((1, "codex", "session-1"), Assert.Single(projects.AgentContextUpdates));
    }

    [Fact]
    public async Task ReleaseAsync_clears_current_agent_context()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var service = Service(
            projects,
            new FakeClaims(new Dictionary<int, ClaimOutcome> { [1] = ClaimOutcome.Acquired }));

        await service.ReleaseAsync(Config, Id(1), CancellationToken.None);

        Assert.Equal((1, null, null), Assert.Single(projects.AgentContextUpdates));
    }

    [Fact]
    public async Task UpdateAsync_requires_current_worker_claim_before_backend_access()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>())
        {
            OwnershipState = ClaimOwnershipState.HeldByOther
        };
        var backend = new FakeBackend(projects);
        var service = Service(projects, claims, backend);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => service.UpdateAsync(
            Config,
            Id(1),
            WorkItemPatch.StatusOnly("Done"),
            expectedRevision: null,
            Handle,
            CancellationToken.None));

        Assert.Equal("CLAIM_HELD", exception.Code);
        Assert.Equal(0, backend.UpdateCalls);
    }

    [Fact]
    public async Task UpdateAsync_validates_patch_before_claim_lookup()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>());
        var service = Service(projects, claims, new FakeBackend(projects));
        var empty = new WorkItemPatch(default, default, default, default);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => service.UpdateAsync(
            Config,
            Id(1),
            empty,
            CancellationToken.None));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
        Assert.Equal(0, claims.OwnershipReads);
    }

    [Fact]
    public async Task FinishAsync_updates_default_status_and_releases_owned_claim()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>());
        var service = Service(projects, claims);

        var result = await service.FinishAsync(Config, Id(1), null, Handle, CancellationToken.None);

        Assert.Equal(FinishDisposition.Finished, result.Disposition);
        Assert.True(result.StatusChanged);
        Assert.True(result.ClaimReleased);
        Assert.Equal("Done", result.Item.Status);
        Assert.Equal(1, claims.ReleaseCalls);
    }

    [Fact]
    public async Task FinishAsync_requires_claim_even_when_target_status_is_already_set()
    {
        var item = Item(1, "P1") with
        {
            Summary = Item(1, "P1").Summary with { Status = "Done" }
        };
        var projects = new FakeProjects([item]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>())
        {
            OwnershipState = ClaimOwnershipState.Unclaimed
        };
        var service = Service(projects, claims);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            service.FinishAsync(Config, Id(1), null, CancellationToken.None));

        Assert.Equal("CLAIM_REQUIRED", exception.Code);
        Assert.Equal(0, claims.ReleaseCalls);
    }

    [Fact]
    public async Task FinishAsync_never_changes_an_item_owned_by_another_worker()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>())
        {
            OwnershipState = ClaimOwnershipState.HeldByOther
        };
        var backend = new FakeBackend(projects);
        var service = Service(projects, claims, backend);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            service.FinishAsync(Config, Id(1), null, Handle, CancellationToken.None));

        Assert.Equal("CLAIM_HELD", exception.Code);
        Assert.Equal(0, backend.UpdateCalls);
        Assert.Equal(0, claims.ReleaseCalls);
    }

    [Fact]
    public async Task FinishAsync_reports_retryable_partial_result_when_release_fails()
    {
        var projects = new FakeProjects([Item(1, "P1")]);
        var claims = new FakeClaims(new Dictionary<int, ClaimOutcome>())
        {
            ReleaseException = new TrackerException("GH_API_ERROR", "failed")
        };
        var service = Service(projects, claims);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            service.FinishAsync(Config, Id(1), null, Handle, CancellationToken.None));

        Assert.Equal("PARTIAL_FINISH", exception.Code);
        Assert.Equal("GH_API_ERROR", exception.Details["causeCode"]);
        Assert.True(Assert.IsType<bool>(exception.Details["statusApplied"]));
        Assert.Equal("#1", exception.Details["displayId"]);
    }

    private static TrackerService Service(
        FakeProjects projects,
        FakeClaims claims,
        IWorkItemBackend? workItems = null)
    {
        var resolver = new GitHubWorkItemAddressResolver();
        var backend = new GitHubTrackerBackend(
            projects,
            claims,
            resolver,
            workItems ?? new FakeBackend(projects));
        return new TrackerService(new TrackerBackendRegistry([backend]));
    }

    private static ClaimHandle Handle { get; } = new(AgentExecutionContext.Human, "token");

    private static WorkItemId Id(int number) =>
        new GitHubWorkItemAddressResolver().FromIssueNumber(Config, number);

    private static GitHubProjectItem Item(int number, string priority)
    {
        var id = Id(number);
        return new GitHubProjectItem(
            new GitHubWorkItemAddress("github.com", "owner", "repo", number),
            new WorkItemSummary(
                id,
                $"Item {number}",
                $"https://github.com/owner/repo/issues/{number}",
                "Todo",
                priority),
            $"ISSUE{number}",
            $"ITEM{number}");
    }

    private sealed class FakeProjects(IReadOnlyList<GitHubProjectItem> items) : IProjectClient
    {
        public IReadOnlyList<GitHubProjectItem> Items { get; } = items;

        public List<(int IssueNumber, string Status)> StatusUpdates { get; } = [];

        public List<(int IssueNumber, string? AgentType, string? SessionId)> AgentContextUpdates { get; } = [];

        public Task<ProjectInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectInitializationResult(false, []));

        public Task EnsureAgentContextSchemaAsync(
            TrackerConfig config,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
            StatusUpdates.Add((item.Number, status));
            return Task.CompletedTask;
        }

        public Task UpdateAgentContextAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            string? agentType,
            string? sessionId,
            CancellationToken cancellationToken)
        {
            AgentContextUpdates.Add((item.Number, agentType, sessionId));
            return Task.CompletedTask;
        }

        public Task ValidateCreateFieldsAsync(
            TrackerConfig config,
            string status,
            string? priority,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> AddIssueAsync(
            TrackerConfig config,
            string issueNodeId,
            CancellationToken cancellationToken) => Task.FromResult("ITEM");

        public Task UpdatePriorityAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            string priority,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeClaims(IReadOnlyDictionary<int, ClaimOutcome> outcomes) : IClaimService
    {
        public ClaimOwnershipState OwnershipState { get; init; } = ClaimOwnershipState.OwnedByCurrent;

        public int OwnershipReads { get; private set; }

        public int ReleaseCalls { get; private set; }

        public Exception? ReleaseException { get; init; }

        public List<int> Attempts { get; } = [];

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken)
        {
            var number = new GitHubWorkItemAddressResolver().Decode(id, config).IssueNumber;
            Attempts.Add(number);
            var outcome = outcomes[number];
            return Task.FromResult(new ClaimResult(
                outcome,
                outcome == ClaimOutcome.HeldByOther ? "other" : "self",
                DateTimeOffset.UtcNow.AddHours(1),
                AgentType: agentContext.AgentType,
                SessionId: agentContext.SessionId));
        }
        public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext agentContext, CancellationToken cancellationToken,
            string? expectedClaimToken) => TryClaimAsync(config, id, agentContext, cancellationToken);
        public Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext claimantContext, string? currentClaimToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            if (ReleaseException is not null) throw ReleaseException;
            return Task.CompletedTask;
        }
        public Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
            bool overrideClaimant, CancellationToken cancellationToken) => ReleaseAsync(config, id, cancellationToken);
        public async Task<ClaimOwnershipResult> ValidateAsync(TrackerConfig config, WorkItemId id,
            ClaimHandle claimHandle, CancellationToken cancellationToken)
        {
            var ownership = await GetOwnershipAsync(config, id, cancellationToken);
            if (ownership.State != ClaimOwnershipState.OwnedByCurrent)
                throw new TrackerException("CLAIM_HELD", "not owned", 6);
            return ownership;
        }

        public Task<bool> IsOwnedByCurrentWorkerAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(OwnershipState == ClaimOwnershipState.OwnedByCurrent);

        public Task<ClaimOwnershipResult> GetOwnershipAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            OwnershipReads++;
            return Task.FromResult(new ClaimOwnershipResult(
                OwnershipState,
                OwnershipState == ClaimOwnershipState.HeldByOther ? "other" : "self",
                DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class FakeBackend(FakeProjects projects) : IWorkItemBackend
    {
        public int UpdateCalls { get; private set; }

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            var item = projects.Items.Single(candidate => candidate.Summary.Id == id).Summary;
            return Task.FromResult<WorkItemDetail?>(new WorkItemDetail(
                item.Id,
                item.Title,
                string.Empty,
                item.Url,
                item.Status,
                item.Priority,
                item.Archived));
        }

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            WorkItemPatch patch,
            CancellationToken cancellationToken)
        {
            UpdateCalls++;
            var item = projects.Items.Single(candidate => candidate.Summary.Id == id);
            await projects.UpdateStatusAsync(
                config,
                item,
                patch.Status.Value!,
                cancellationToken);
            var detail = new WorkItemDetail(
                id,
                item.Title,
                string.Empty,
                item.Url,
                patch.Status.Value,
                item.Priority);
            return new UpdateWorkItemResult(detail, true, ["status"]);
        }
    }
}
