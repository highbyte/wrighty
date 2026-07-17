using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;

namespace Highbyte.Wrighty.UnitTests.GitHub;

public sealed class GitHubTrackerBackendArchiveTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1
    };

    private static readonly WorkItemId Id = new("github:owner/repo#42");
    private static ClaimHandle Handle { get; } = new(AgentExecutionContext.Human, "token");

    [Fact]
    public async Task Archive_archives_active_item_and_releases_claim()
    {
        var projects = new FakeProjects(archived: false);
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent);
        var guard = new RecordingGuard();
        var backend = Backend(projects, claims, guard);

        var result = await backend.ArchiveAsync(Config, Id, Handle, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.True(result.Archived);
        Assert.True(result.Item.Archived);
        Assert.Equal(1, projects.ArchiveCalls);
        Assert.Equal(1, claims.ReleaseCalls);
        Assert.Equal(0, guard.Checks);
    }

    [Fact]
    public async Task Archive_of_already_archived_owned_item_is_rejected()
    {
        var projects = new FakeProjects(archived: true);
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent);
        var guard = new RecordingGuard();
        var backend = Backend(projects, claims, guard);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.ArchiveAsync(Config, Id, Handle, CancellationToken.None));

        Assert.Equal("WORK_ITEM_ARCHIVED", exception.Code);
        Assert.Equal(0, projects.ArchiveCalls);
        Assert.Equal(0, claims.ReleaseCalls);
        Assert.Empty(projects.AgentContextUpdates);
        Assert.Equal(0, guard.Checks);
    }

    [Fact]
    public async Task Archive_of_already_archived_unclaimed_item_is_rejected()
    {
        var projects = new FakeProjects(archived: true);
        var claims = new FakeClaims(ClaimOwnershipState.Unclaimed);

        var exception = await Assert.ThrowsAsync<TrackerException>(() => Backend(projects, claims).ArchiveAsync(
            Config, Id, Handle, CancellationToken.None));

        Assert.Equal("WORK_ITEM_ARCHIVED", exception.Code);
        Assert.Equal(0, claims.ReleaseCalls);
        Assert.Empty(projects.AgentContextUpdates);
    }

    [Fact]
    public async Task Archive_reports_partial_update_when_claim_release_fails()
    {
        var projects = new FakeProjects(archived: false);
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent)
        {
            ReleaseException = new InvalidOperationException("release failed")
        };

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).ArchiveAsync(Config, Id, Handle, CancellationToken.None));

        Assert.Equal("PARTIAL_UPDATE", exception.Code);
        Assert.Equal("claimRelease", exception.Details["failedStage"]);
        Assert.True(projects.IsArchived);
    }

    [Fact]
    public async Task Unarchive_of_active_item_is_no_op()
    {
        var projects = new FakeProjects(archived: false);
        var claims = new FakeClaims(ClaimOwnershipState.HeldByOther);

        var result = await Backend(projects, claims).UnarchiveAsync(
            Config, Id, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.False(result.Archived);
        Assert.Equal(0, claims.OwnershipReads);
        Assert.Equal(0, projects.UnarchiveCalls);
    }

    [Fact]
    public async Task Unarchive_rejects_archived_item_with_active_claim()
    {
        var projects = new FakeProjects(archived: true);
        var claims = new FakeClaims(ClaimOwnershipState.HeldByOther);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).UnarchiveAsync(Config, Id, CancellationToken.None));

        Assert.Equal("CLAIM_HELD", exception.Code);
        Assert.Equal("other", exception.Details["workerIdentity"]);
        Assert.Equal(0, projects.UnarchiveCalls);
    }

    [Fact]
    public async Task Unarchive_restores_unclaimed_item_and_clears_agent_projection()
    {
        var projects = new FakeProjects(archived: true);
        var claims = new FakeClaims(ClaimOwnershipState.Unclaimed);

        var result = await Backend(projects, claims).UnarchiveAsync(
            Config, Id, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(result.Archived);
        Assert.False(result.Item.Archived);
        Assert.Equal(1, projects.UnarchiveCalls);
        Assert.Equal((null, null), Assert.Single(projects.AgentContextUpdates));
    }

    [Fact]
    public async Task Unarchive_reports_partial_update_when_projection_clear_fails()
    {
        var projects = new FakeProjects(archived: true)
        {
            AgentContextException = new InvalidOperationException("projection failed")
        };
        var claims = new FakeClaims(ClaimOwnershipState.Unclaimed);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).UnarchiveAsync(Config, Id, CancellationToken.None));

        Assert.Equal("PARTIAL_UPDATE", exception.Code);
        Assert.Equal("agentContextClear", exception.Details["failedStage"]);
        Assert.False(projects.IsArchived);
    }

    [Fact]
    public async Task Archive_reports_missing_project_item()
    {
        var projects = new FakeProjects(archived: false) { IncludeItem = false };

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, new FakeClaims(ClaimOwnershipState.OwnedByCurrent))
                .ArchiveAsync(Config, Id, Handle, CancellationToken.None));

        Assert.Equal("PROJECT_ITEM_NOT_FOUND", exception.Code);
    }

    private static GitHubTrackerBackend Backend(
        FakeProjects projects,
        FakeClaims claims,
        RecordingGuard? guard = null) => new(
            projects,
            claims,
            new GitHubWorkItemAddressResolver(),
            new FakeWorkItems(projects));

    private sealed class FakeProjects(bool archived) : IProjectClient
    {
        public bool IsArchived { get; private set; } = archived;
        public bool IncludeItem { get; init; } = true;
        public int ArchiveCalls { get; private set; }
        public int UnarchiveCalls { get; private set; }
        public Exception? AgentContextException { get; init; }
        public List<(string? AgentType, string? SessionId)> AgentContextUpdates { get; } = [];

        public Task<ProjectInitializationResult> InitializeAsync(
            TrackerConfig config, bool checkOnly, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectInitializationResult(false, []));

        public Task EnsureAgentContextSchemaAsync(
            TrackerConfig config, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<GitHubProjectItem>> ListAsync(
            TrackerConfig config, string? status, int? limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GitHubProjectItem>>(IncludeItem ? [Item()] : []);

        public Task ArchiveAsync(
            TrackerConfig config, GitHubProjectItem item, CancellationToken cancellationToken)
        {
            ArchiveCalls++;
            IsArchived = true;
            return Task.CompletedTask;
        }

        public Task UnarchiveAsync(
            TrackerConfig config, GitHubProjectItem item, CancellationToken cancellationToken)
        {
            UnarchiveCalls++;
            IsArchived = false;
            return Task.CompletedTask;
        }

        public Task UpdateAgentContextAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            string? agentType,
            string? sessionId,
            CancellationToken cancellationToken)
        {
            AgentContextUpdates.Add((agentType, sessionId));
            if (AgentContextException is not null)
            {
                throw AgentContextException;
            }

            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(
            TrackerConfig config, GitHubProjectItem item, string status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ValidateCreateFieldsAsync(
            TrackerConfig config, string status, string? priority, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> AddIssueAsync(
            TrackerConfig config, string issueNodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdatePriorityAsync(
            TrackerConfig config, GitHubProjectItem item, string priority, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private GitHubProjectItem Item() => new(
            new GitHubWorkItemAddress("github.com", "owner", "repo", 42),
            new WorkItemSummary(Id, "Title", "https://example.test/42", "Todo", "P1", IsArchived),
            "ISSUE_42",
            "ITEM_42");
    }

    private sealed class FakeClaims(ClaimOwnershipState state) : IClaimService
    {
        public int OwnershipReads { get; private set; }
        public int ReleaseCalls { get; private set; }
        public Exception? ReleaseException { get; init; }

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext agentExecutionContext, CancellationToken cancellationToken,
            string? expectedClaimToken) => TryClaimAsync(config, id, agentExecutionContext, cancellationToken);
        public Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext claimantContext, string? currentClaimToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            if (ReleaseException is not null)
            {
                throw ReleaseException;
            }

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
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            Task.FromResult(state == ClaimOwnershipState.OwnedByCurrent);

        public Task<ClaimOwnershipResult> GetOwnershipAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken)
        {
            OwnershipReads++;
            return Task.FromResult(new ClaimOwnershipResult(
                state,
                state == ClaimOwnershipState.HeldByOther ? "other" : null,
                state == ClaimOwnershipState.HeldByOther
                    ? DateTimeOffset.Parse("2026-07-15T12:00:00Z")
                    : null));
        }
    }

    private sealed class FakeWorkItems(FakeProjects projects) : IWorkItemBackend
    {
        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            Task.FromResult<WorkItemDetail?>(new WorkItemDetail(
                id, "Title", "Body", "https://example.test/42", "Todo", "P1", projects.IsArchived));

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            WorkItemPatch patch,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingGuard : IWorkItemMutationGuard
    {
        public int Checks { get; private set; }

        public Task EnsureOwnedAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken)
        {
            Checks++;
            return Task.CompletedTask;
        }
    }
}
