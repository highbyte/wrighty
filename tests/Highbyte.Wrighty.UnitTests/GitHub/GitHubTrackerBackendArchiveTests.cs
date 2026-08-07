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
        // The claim is released and its projection cleared while the item is still active (before
        // archiving), so the projection write succeeds and nothing is stranded on the archived item.
        Assert.Equal((null, null), Assert.Single(projects.AgentContextUpdates));
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
    public async Task Archive_does_not_archive_when_claim_release_fails()
    {
        var projects = new FakeProjects(archived: false);
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent)
        {
            ReleaseException = new TrackerException("GH_API_ERROR", "release failed", 10)
        };

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).ArchiveAsync(Config, Id, Handle, CancellationToken.None));

        // Release happens before archive, so a failed release leaves the item unarchived and still
        // claimed — a clean, retryable state instead of the old archived-but-claim-stranded trap.
        Assert.Equal("GH_API_ERROR", exception.Code);
        Assert.False(projects.IsArchived);
        Assert.Equal(0, projects.ArchiveCalls);
    }

    [Fact]
    public async Task Release_on_an_archived_item_skips_projection_clear_and_succeeds()
    {
        // Recovery path for a claim stranded on an already-archived item: the issue-level release
        // must still succeed, and the projection clear (which GitHub rejects on archived items) is
        // skipped rather than failing the whole release.
        var projects = new FakeProjects(archived: true);
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent);

        await Backend(projects, claims).ReleaseAsync(
            Config, Id, Handle, overrideClaimant: false, DispatchStateOnRelease.Clear,
            CancellationToken.None);

        Assert.Equal(1, claims.ReleaseCalls);
        Assert.Empty(projects.AgentContextUpdates);
    }

    [Theory]
    [InlineData(true, "/Users/secret/ws")]
    [InlineData(false, null)]
    public async Task Renew_publishes_the_workspace_path_to_the_project_field_only_when_sharing(
        bool shareLocalPaths,
        string? expected)
    {
        var projects = new FakeProjects(archived: false);
        var config = Config with { Worker = new WorkerConfig { ShareLocalPaths = shareLocalPaths } };

        await Backend(projects, new FakeClaims(ClaimOwnershipState.OwnedByCurrent)).RenewClaimAsync(
            config, Id, Handle, "/Users/secret/ws", "session-1", branch: null, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(projects.WorkspacePathUpdates));
    }

    [Fact]
    public async Task Dispatch_presentation_failure_does_not_invalidate_the_authoritative_state()
    {
        var projects = new FakeProjects(archived: false)
        {
            RecoveryProjectionException = new TrackerException(
                "GH_API_ERROR", "projection failed")
        };
        var dispatch = new Highbyte.Wrighty.Workers.DispatchInfo(
            DispatchStates.RetryScheduled,
            "Agent usage is exhausted.",
            "codex",
            null,
            DateTimeOffset.Parse("2026-07-24T04:02:00Z"),
            1,
            5,
            DateTimeOffset.Parse("2026-07-23T22:00:00Z"),
            true);

        await Backend(
                projects,
                new FakeClaims(ClaimOwnershipState.Unclaimed))
            .PresentDispatchAsync(
                Config, Id, dispatch, CancellationToken.None);

        Assert.Equal(dispatch, Assert.Single(projects.RecoveryProjectionUpdates));
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
        Assert.Equal("other", exception.Details["installationId"]);
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
    public async Task Unarchive_retries_detail_while_active_project_index_catches_up()
    {
        var projects = new FakeProjects(archived: true);
        var claims = new FakeClaims(ClaimOwnershipState.Unclaimed);
        var workItems = new FakeWorkItems(projects) { MissingReads = 2 };
        var delays = new List<TimeSpan>();
        var backend = new GitHubTrackerBackend(
            projects,
            claims,
            new GitHubWorkItemAddressResolver(),
            workItems,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await backend.UnarchiveAsync(Config, Id, CancellationToken.None);

        Assert.False(result.Item.Archived);
        Assert.Equal(3, workItems.Reads);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)],
            delays);
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

    [Fact]
    public async Task Requeue_marks_eligible_in_progress_item_and_ends_claim()
    {
        var projects = new FakeProjects(archived: false)
        {
            Status = Config.DefaultPickTo,
            AutomaticExecutionAllowed = true
        };
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent);

        await Backend(projects, claims).RequeueAsync(
            Config, Id, Handle, CancellationToken.None);

        Assert.Equal(DispatchStates.Queued, projects.DispatchState);
        Assert.Equal(1, claims.RequeueCalls);
        Assert.Equal(1, claims.ClearPendingDispatchCalls);
    }

    [Theory]
    [InlineData(false, "In Progress")]
    [InlineData(true, "Todo")]
    public async Task Requeue_rejects_ineligible_or_wrong_status_item(
        bool automaticExecutionAllowed,
        string status)
    {
        var projects = new FakeProjects(archived: false)
        {
            Status = status,
            AutomaticExecutionAllowed = automaticExecutionAllowed
        };
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => Backend(projects, claims).RequeueAsync(
                Config, Id, Handle, CancellationToken.None));

        Assert.Equal("WORKER_ITEM_INELIGIBLE", exception.Code);
        Assert.Equal(0, claims.RequeueCalls);
    }

    [Theory]
    [InlineData(ClaimOutcome.Acquired)]
    [InlineData(ClaimOutcome.AlreadyOwned)]
    public async Task Queue_paused_accepts_a_fresh_or_retained_claim_and_requeues(
        ClaimOutcome outcome)
    {
        // AlreadyOwned counts alongside Acquired: a retained claim from this installation's own
        // finished run is the ordinary state of a waiting item, and it is ours to requeue.
        var projects = WaitingProjects();
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent)
        {
            Session = WaitingSession(),
            NextClaim = new ClaimResult(
                outcome, "worker", DateTimeOffset.Parse("2026-07-15T13:00:00Z"),
                Agent: "codex", SessionId: "session-1", ClaimantKind: "agent",
                ClaimantId: "agent:generated", ClaimToken: "token-9")
        };

        await Backend(projects, claims).QueuePausedAsync(Config, Id, CancellationToken.None);

        Assert.Equal(DispatchStates.Queued, projects.DispatchState);
        Assert.Equal(1, claims.RequeueCalls);
        Assert.Equal(1, claims.ClearPendingDispatchCalls);
        // The requeue handle is built from what the claim service recorded, not from the request:
        // the claimant id is generated during the claim, so a handle assembled from the request
        // carries a null id and fails validation against the live claim.
        Assert.Equal("agent:generated", claims.RequeuedHandle!.Claimant.ClaimantId);
        Assert.Equal("token-9", claims.RequeuedHandle.ClaimToken);
        // The recorded session id travels into the claim: publishing a claim without it would
        // republish the item as one whose session has no address, silently ending resumability.
        Assert.Equal("session-1", claims.ClaimedWith!.SessionId);
    }

    [Fact]
    public async Task Queue_paused_takes_over_a_retained_agent_claim()
    {
        // The ordinary live state: the ended run retained its claim, so the fresh claim attempt
        // reports HeldByLocalClaimant. Without the takeover, continuation is blocked for the whole
        // remaining lease — up to an hour on defaults — which is exactly when a clarifying
        // comment arrives.
        var projects = WaitingProjects();
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent)
        {
            Session = WaitingSession(),
            NextClaim = new ClaimResult(
                ClaimOutcome.HeldByLocalClaimant, "worker",
                DateTimeOffset.Parse("2026-07-15T13:00:00Z"),
                ClaimantKind: "agent", ClaimantId: "agent:worker:run1"),
            TakeoverResult = new ClaimResult(
                ClaimOutcome.TakenOver, "worker",
                DateTimeOffset.Parse("2026-07-15T13:00:00Z"),
                Agent: "codex", SessionId: "session-1", ClaimantKind: "agent",
                ClaimantId: "claimant:rotated", ClaimToken: "token-rotated")
        };

        await Backend(projects, claims).QueuePausedAsync(Config, Id, CancellationToken.None);

        Assert.Equal(1, claims.TakeoverCalls);
        Assert.Equal(DispatchStates.Queued, projects.DispatchState);
        Assert.Equal(1, claims.RequeueCalls);
        // The requeue runs under the rotated takeover handle, not the stale attempt's.
        Assert.Equal("token-rotated", claims.RequeuedHandle!.ClaimToken);
        Assert.Equal("claimant:rotated", claims.RequeuedHandle.Claimant.ClaimantId);
    }

    [Fact]
    public async Task Queue_paused_never_displaces_a_human_claimant()
    {
        // A human or automation claimant on a needs-attention item is an operator intervening
        // (edit --takeover); displacing them would fence the claim they are working with.
        var projects = WaitingProjects();
        var claims = new FakeClaims(ClaimOwnershipState.OwnedByCurrent)
        {
            Session = WaitingSession(),
            NextClaim = new ClaimResult(
                ClaimOutcome.HeldByLocalClaimant, "worker",
                DateTimeOffset.Parse("2026-07-15T13:00:00Z"),
                ClaimantKind: "human", ClaimantId: "human:web")
        };

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).QueuePausedAsync(Config, Id, CancellationToken.None));

        Assert.Equal("CLAIM_HELD", exception.Code);
        Assert.Equal(0, claims.TakeoverCalls);
        Assert.Equal(0, claims.RequeueCalls);
        Assert.Equal(DispatchStates.NeedsAttention, projects.DispatchState);
    }

    [Fact]
    public async Task Queue_paused_refuses_when_the_claim_is_held_elsewhere()
    {
        var projects = WaitingProjects();
        var claims = new FakeClaims(ClaimOwnershipState.HeldByOther)
        {
            Session = WaitingSession(),
            NextClaim = new ClaimResult(
                ClaimOutcome.HeldByOther, "other",
                DateTimeOffset.Parse("2026-07-15T13:00:00Z"))
        };

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).QueuePausedAsync(Config, Id, CancellationToken.None));

        // A live claim means a run has not finished releasing it; failing is the correct answer,
        // and the item is reconsidered once the claim lapses.
        Assert.Equal("CLAIM_HELD", exception.Code);
        Assert.Equal(0, claims.RequeueCalls);
        Assert.Equal(DispatchStates.NeedsAttention, projects.DispatchState);
    }

    [Fact]
    public async Task Queue_paused_requires_a_complete_session_on_this_installation()
    {
        var projects = WaitingProjects();
        var claims = new FakeClaims(ClaimOwnershipState.Unclaimed);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).QueuePausedAsync(Config, Id, CancellationToken.None));

        Assert.Equal("RESUME_ADDRESS_UNAVAILABLE", exception.Code);
        // Refused before any claim was attempted, so nothing was published to the issue.
        Assert.Equal(0, claims.ClaimAttempts);
        Assert.Equal(DispatchStates.NeedsAttention, projects.DispatchState);
    }

    [Fact]
    public async Task Queue_paused_rejects_an_item_that_is_not_waiting()
    {
        var projects = new FakeProjects(archived: false)
        {
            Status = Config.DefaultPickTo,
            AutomaticExecutionAllowed = true
        };
        var claims = new FakeClaims(ClaimOwnershipState.Unclaimed) { Session = WaitingSession() };

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Backend(projects, claims).QueuePausedAsync(Config, Id, CancellationToken.None));

        Assert.Equal("WORKER_ITEM_NOT_PAUSED", exception.Code);
        Assert.Equal(0, claims.ClaimAttempts);
    }

    private static FakeProjects WaitingProjects() => new(archived: false)
    {
        Status = Config.DefaultPickTo,
        AutomaticExecutionAllowed = true,
        DispatchState = DispatchStates.NeedsAttention
    };

    private static AgentSessionRecord WaitingSession() => new(
        "codex", "session-1", "/tmp/waiting-ws",
        DateTimeOffset.Parse("2026-07-15T12:00:00Z"),
        FromCurrentInstallation: true);

    [Fact]
    public async Task Import_capability_validates_fields_and_archives_only_active_items()
    {
        var activeProjects = new FakeProjects(archived: false);
        var active = Backend(
            activeProjects,
            new FakeClaims(ClaimOwnershipState.Unclaimed));

        await active.ValidateImportFieldsAsync(
            Config, "Todo", "P1", CancellationToken.None);
        await active.ArchiveImportedAsync(Config, Id, CancellationToken.None);

        Assert.Equal(("Todo", "P1"), Assert.Single(activeProjects.ValidatedFields));
        Assert.Equal(1, activeProjects.ArchiveCalls);

        var archivedProjects = new FakeProjects(archived: true);
        await Backend(
                archivedProjects,
                new FakeClaims(ClaimOwnershipState.Unclaimed))
            .ArchiveImportedAsync(Config, Id, CancellationToken.None);
        Assert.Equal(0, archivedProjects.ArchiveCalls);
    }

    [Fact]
    public async Task Adoption_reports_when_work_item_capability_is_absent()
    {
        var backend = Backend(
            new FakeProjects(archived: false),
            new FakeClaims(ClaimOwnershipState.Unclaimed));

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => backend.AdoptAsync(
                Config,
                "42",
                new AdoptWorkItemOptions(null, null, false, null),
                CancellationToken.None));

        Assert.Equal("NOT_SUPPORTED", exception.Code);
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
        public Exception? RecoveryProjectionException { get; init; }
        public List<(string? Agent, string? SessionId)> AgentContextUpdates { get; } = [];
        public List<string?> WorkspacePathUpdates { get; } = [];
        public List<Highbyte.Wrighty.Workers.DispatchInfo> RecoveryProjectionUpdates { get; } = [];
        public string Status { get; init; } = "Todo";
        public bool AutomaticExecutionAllowed { get; init; }
        public string? DispatchState { get; set; }
        public List<(string Status, string? Priority)> ValidatedFields { get; } = [];

        public Task<ProjectInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken,
            bool projectCreated = false) =>
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

        public Task UpdateWorkspacePathAsync(
            TrackerConfig config, GitHubProjectItem item, string? workspacePath,
            CancellationToken cancellationToken)
        {
            WorkspacePathUpdates.Add(workspacePath);
            return Task.CompletedTask;
        }

        public Task UpdateDispatchProjectionAsync(
            TrackerConfig config,
            GitHubProjectItem item,
            Highbyte.Wrighty.Workers.DispatchInfo dispatch,
            CancellationToken cancellationToken)
        {
            RecoveryProjectionUpdates.Add(dispatch);
            if (RecoveryProjectionException is not null)
                throw RecoveryProjectionException;
            return Task.CompletedTask;
        }

        public Task UpdateStatusAsync(
            TrackerConfig config, GitHubProjectItem item, string status, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ValidateCreateFieldsAsync(
            TrackerConfig config, string status, string? priority, CancellationToken cancellationToken)
        {
            ValidatedFields.Add((status, priority));
            return Task.CompletedTask;
        }

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
        public int RequeueCalls { get; private set; }
        public int ClearPendingDispatchCalls { get; private set; }
        public AgentSessionRecord? Session { get; init; }
        public ClaimResult? NextClaim { get; init; }
        public ClaimResult? TakeoverResult { get; init; }
        public int ClaimAttempts { get; private set; }
        public int TakeoverCalls { get; private set; }
        public AgentExecutionContext? ClaimedWith { get; private set; }
        public ClaimHandle? RequeuedHandle { get; private set; }

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext agentExecutionContext, CancellationToken cancellationToken,
            string? expectedClaimToken)
        {
            ClaimAttempts++;
            ClaimedWith = agentExecutionContext;
            return NextClaim is not null
                ? Task.FromResult(NextClaim)
                : TryClaimAsync(config, id, agentExecutionContext, cancellationToken);
        }
        public Task<AgentSessionRecord?> GetAgentSessionAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            Task.FromResult(Session);
        public Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext claimantContext, string? currentClaimToken,
            CancellationToken cancellationToken)
        {
            TakeoverCalls++;
            return TakeoverResult is not null
                ? Task.FromResult(TakeoverResult)
                : throw new NotSupportedException();
        }

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
        public Task<ClaimResult> RenewAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
            string? workspacePath, string? sessionId, string? branch, CancellationToken cancellationToken) =>
            Task.FromResult(new ClaimResult(
                ClaimOutcome.AlreadyOwned, "worker", DateTimeOffset.Parse("2026-07-15T13:00:00Z"),
                SessionId: sessionId, ClaimantKind: "agent", ClaimantId: "agent:one",
                WorkspacePath: workspacePath));
        public Task RequeueAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            CancellationToken cancellationToken)
        {
            RequeueCalls++;
            RequeuedHandle = claimHandle;
            return Task.CompletedTask;
        }
        public Task ClearPendingDispatchAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            ClearPendingDispatchCalls++;
            return Task.CompletedTask;
        }
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
        public int MissingReads { get; init; }
        public int Reads { get; private set; }

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken)
        {
            Reads++;
            if (Reads <= MissingReads)
            {
                return Task.FromResult<WorkItemDetail?>(null);
            }

            return Task.FromResult<WorkItemDetail?>(new WorkItemDetail(
                id, "Title", "Body", "https://example.test/42", projects.Status, "P1",
                projects.IsArchived,
                AutomaticExecutionAllowed: projects.AutomaticExecutionAllowed,
                DispatchState: projects.DispatchState));
        }

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            WorkItemPatch patch,
            CancellationToken cancellationToken)
        {
            if (patch.DispatchState.IsSpecified)
                projects.DispatchState = patch.DispatchState.Value;
            return Task.FromResult(new UpdateWorkItemResult(
                new WorkItemDetail(
                    id, "Title", "Body", "https://example.test/42", projects.Status, "P1",
                    projects.IsArchived,
                    AutomaticExecutionAllowed: projects.AutomaticExecutionAllowed,
                    DispatchState: projects.DispatchState),
                true,
                ["wrighty.dispatch.state"]));
        }
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
