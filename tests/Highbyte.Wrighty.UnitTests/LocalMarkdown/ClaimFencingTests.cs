using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.UnitTests.LocalMarkdown;

public sealed class ClaimFencingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wrighty-fencing-{Guid.NewGuid():N}");
    private readonly FakeClock clock = new(DateTimeOffset.Parse("2026-07-16T10:00:00Z"));
    private TrackerConfig Config => new()
    {
        Backend = "local-markdown",
        SourcePath = Path.Combine(directory, ".wrighty.json"),
        LocalMarkdown = new LocalMarkdownBackendConfig(),
        LeaseMinutes = 60
    };

    [Fact]
    public async Task Takeover_rotates_generation_preserves_item_and_fences_every_old_mutation()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend, "Original", "Body", "Todo", "P1");
        var agent = Context(ClaimantKind.Agent, "agent:one", "codex");
        var first = await backend.TryClaimAsync(Config, id, agent, CancellationToken.None);
        var old = new ClaimHandle(agent, first.ClaimToken);
        await backend.RenewClaimAsync(Config, id, old, "/tmp/resumable", "session-one",
            CancellationToken.None);
        var human = Context(ClaimantKind.Human, "human:web");

        var takeover = await backend.TakeoverAsync(Config, id, human, null, CancellationToken.None);

        Assert.Equal(ClaimOutcome.TakenOver, takeover.Outcome);
        Assert.NotEqual(first.ClaimToken, takeover.ClaimToken);
        Assert.Equal("/tmp/resumable", takeover.WorkspacePath);
        Assert.Equal("session-one", takeover.SessionId);
        Assert.Equal("codex", takeover.AgentType);
        var detail = await backend.GetAsync(Config, id, CancellationToken.None);
        Assert.Equal(("Original", "Body", "Todo", "P1"), (detail!.Title, detail.Body, detail.Status, detail.Priority));
        await AssertStale(() => backend.UpdateAsync(Config, id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("Done"), false, ClaimHandle: old), CancellationToken.None));
        await AssertStale(() => backend.ReleaseAsync(Config, id, old, false, CancellationToken.None));
        await AssertStale(() => backend.ArchiveAsync(Config, id, old, CancellationToken.None));

        var current = new ClaimHandle(human, takeover.ClaimToken);
        var update = await backend.UpdateAsync(Config, id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("In Progress"), false, ClaimHandle: current), CancellationToken.None);
        Assert.Equal("In Progress", update.Item.Status);
    }

    [Fact]
    public async Task Same_installation_claim_is_not_already_owned_and_exact_reconnect_is_idempotent()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var firstContext = Context(ClaimantKind.Agent, "agent:one", "codex");
        var first = await backend.TryClaimAsync(Config, id, firstContext, CancellationToken.None);
        var second = await backend.TryClaimAsync(Config, id,
            Context(ClaimantKind.Agent, "agent:two", "codex"), CancellationToken.None);
        Assert.Equal(ClaimOutcome.HeldByLocalClaimant, second.Outcome);

        var reconnect = await backend.TryClaimAsync(Config, id, firstContext, CancellationToken.None, first.ClaimToken);
        Assert.Equal(ClaimOutcome.AlreadyOwned, reconnect.Outcome);
        Assert.Equal(first.ClaimToken, reconnect.ClaimToken);
        var missing = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.TryClaimAsync(Config, id, firstContext, CancellationToken.None));
        Assert.Equal("CLAIM_TOKEN_REQUIRED", missing.Code);
    }

    [Fact]
    public async Task Override_release_is_same_installation_only_and_changes_no_fields()
    {
        var owner = Backend("worker-a");
        var other = Backend("worker-b");
        var id = await Create(owner, "Keep", "Everything", "Todo", "P2");
        var claim = await owner.TryClaimAsync(Config, id, Context(ClaimantKind.Agent, "agent:abandoned"), CancellationToken.None);
        var denied = await Assert.ThrowsAsync<TrackerException>(() => other.ReleaseAsync(Config, id,
            new ClaimHandle(Context(ClaimantKind.Human, "human:other"), null), true, CancellationToken.None));
        Assert.Equal("CLAIM_NOT_OWNER", denied.Code);

        await owner.ReleaseAsync(Config, id,
            new ClaimHandle(Context(ClaimantKind.Human, "human:operator"), null), true, CancellationToken.None);
        var detail = await owner.GetAsync(Config, id, CancellationToken.None);
        Assert.Equal(("Keep", "Everything", "Todo", "P2"), (detail!.Title, detail.Body, detail.Status, detail.Priority));
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await owner.GetClaimOwnershipAsync(Config, id, CancellationToken.None)).State);
        Assert.NotNull(claim.ClaimToken);
    }

    [Fact]
    public async Task Expired_claim_uses_normal_acquisition_not_takeover()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        await backend.TryClaimAsync(Config, id, Context(ClaimantKind.Agent, "agent:old"), CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddHours(2);
        var exception = await Assert.ThrowsAsync<TrackerException>(() => backend.TakeoverAsync(
            Config, id, Context(ClaimantKind.Human, "human:web"), null, CancellationToken.None));
        Assert.Equal("CLAIM_NOT_FOUND", exception.Code);
        Assert.Contains("Takeover is no longer possible", exception.Message);
        Assert.Contains($"wrighty worker --item {id.Value} --yes", exception.Message);
        Assert.Equal(ClaimOutcome.Acquired, (await backend.TryClaimAsync(Config, id,
            Context(ClaimantKind.Human, "human:web"), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Session_record_survives_release_and_new_claim()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var agent = Context(ClaimantKind.Agent, "agent:one", "codex");
        var claim = await backend.TryClaimAsync(Config, id, agent, CancellationToken.None);
        var handle = new ClaimHandle(agent, claim.ClaimToken);
        await backend.RenewClaimAsync(Config, id, handle, "/tmp/workspace-one", "session-one",
            CancellationToken.None);

        await backend.ReleaseAsync(Config, id, handle, false, CancellationToken.None);

        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(Config, id, CancellationToken.None)).State);
        var session = await backend.GetAgentSessionAsync(Config, id, CancellationToken.None);
        Assert.NotNull(session);
        Assert.True(session!.IsComplete);
        Assert.Equal(("codex", "session-one", "/tmp/workspace-one"),
            (session.AgentType, session.SessionId, session.WorkspacePath));
        Assert.True(session.FromCurrentInstallation);

        var human = await backend.TryClaimAsync(Config, id,
            Context(ClaimantKind.Human, "human:web"), CancellationToken.None);
        Assert.Equal(ClaimOutcome.Acquired, human.Outcome);
        var afterHumanClaim = await backend.GetAgentSessionAsync(Config, id, CancellationToken.None);
        Assert.NotNull(afterHumanClaim);
        Assert.Equal("session-one", afterHumanClaim!.SessionId);
    }

    [Fact]
    public async Task Claim_lifecycle_does_not_rewrite_the_item_document()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var path = Directory.GetFiles(Path.Combine(directory, ".wrighty", "items"), "*.md").Single();
        var before = await File.ReadAllTextAsync(path);
        var agent = Context(ClaimantKind.Agent, "agent:one", "codex");
        var claim = await backend.TryClaimAsync(Config, id, agent, CancellationToken.None);
        var handle = new ClaimHandle(agent, claim.ClaimToken);
        await backend.RenewClaimAsync(Config, id, handle, "/tmp/ws", "session-one", CancellationToken.None);
        await backend.TakeoverAsync(Config, id, Context(ClaimantKind.Human, "human:web"), null,
            CancellationToken.None);

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Corrupt_runtime_state_fails_closed()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var statePath = Path.Combine(directory, ".wrighty", ".runtime-state.json");
        await File.WriteAllTextAsync(statePath, "{ not json");

        var corrupt = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.TryClaimAsync(Config, id, AgentExecutionContext.Human, CancellationToken.None));
        Assert.Equal("LOCAL_STORE_INVALID", corrupt.Code);
        Assert.Contains("runtime-state", corrupt.Message);

        await File.WriteAllTextAsync(statePath, """{ "version": 99, "claims": {}, "sessions": {} }""");
        var unsupported = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.GetClaimOwnershipAsync(Config, id, CancellationToken.None));
        Assert.Equal("LOCAL_STORE_INVALID", unsupported.Code);

        File.Delete(statePath);
        Assert.Equal(ClaimOutcome.Acquired, (await backend.TryClaimAsync(
            Config, id, AgentExecutionContext.Human, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Release_without_a_handle_reports_the_required_token()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);

        var unclaimed = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.ReleaseAsync(Config, id, CancellationToken.None));
        Assert.Equal("CLAIM_NOT_FOUND", unclaimed.Code);

        await backend.TryClaimAsync(Config, id,
            Context(ClaimantKind.Agent, "agent:one", "codex"), CancellationToken.None);
        var held = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.ReleaseAsync(Config, id, CancellationToken.None));
        Assert.Equal("CLAIM_TOKEN_REQUIRED", held.Code);
    }

    [Fact]
    public async Task Release_clears_worker_state_from_the_document()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var agent = Context(ClaimantKind.Agent, "agent:one", "codex");
        var claim = await backend.TryClaimAsync(Config, id, agent, CancellationToken.None);
        var handle = new ClaimHandle(agent, claim.ClaimToken);
        await backend.UpdateAsync(Config, id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(default, default, default, default,
                    WorkerState: OptionalValue<string?>.From(WorkerDispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        await backend.ReleaseAsync(Config, id, handle, false, CancellationToken.None);

        var detail = await backend.GetAsync(Config, id, CancellationToken.None);
        Assert.Null(detail!.WorkerState);
        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(Config, id, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Unarchive_discards_a_stale_runtime_claim_entry()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var agent = Context(ClaimantKind.Agent, "agent:one", "codex");
        var claim = await backend.TryClaimAsync(Config, id, agent, CancellationToken.None);
        var handle = new ClaimHandle(agent, claim.ClaimToken);
        await backend.ArchiveAsync(Config, id, handle, CancellationToken.None);

        var statePath = Path.Combine(directory, ".wrighty", ".runtime-state.json");
        var state = await File.ReadAllTextAsync(statePath);
        var expired = state.Replace(
            "\"claims\": {}",
            "\"claims\": { \"1\": { \"workerIdentity\": \"worker-a\", " +
            "\"claimantId\": \"agent:stale\", \"claimToken\": \"stale-token\", " +
            "\"claimantKind\": \"agent\", " +
            $"\"claimedAt\": \"{clock.UtcNow.AddHours(-2):O}\", " +
            $"\"expiresAt\": \"{clock.UtcNow.AddHours(-1):O}\" }} }}");
        Assert.NotEqual(state, expired);
        await File.WriteAllTextAsync(statePath, expired);

        var restored = await backend.UnarchiveAsync(Config, id, CancellationToken.None);

        Assert.False(restored.Item.Archived);
        Assert.DoesNotContain("agent:stale", await File.ReadAllTextAsync(statePath));
        Assert.Equal(ClaimOutcome.Acquired, (await backend.TryClaimAsync(
            Config, id, agent, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Init_check_reports_pending_legacy_claim_migration()
    {
        var backend = Backend("worker-a");
        await Create(backend);
        var path = Directory.GetFiles(Path.Combine(directory, ".wrighty", "items"), "*.md").Single();
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, content.Replace(
            "status: Todo",
            "status: Todo\nclaimEpoch: 0"));

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.InitializeAsync(Config, checkOnly: true, CancellationToken.None));

        Assert.Equal("STORE_INITIALIZATION_REQUIRED", exception.Code);
        Assert.Contains("migrate legacy claim frontmatter from 1 document(s)", exception.Message);
        Assert.Contains("claimEpoch", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Legacy_claim_frontmatter_fails_closed_until_migration()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var path = Directory.GetFiles(Path.Combine(directory, ".wrighty", "items"), "*.md").Single();
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, content.Replace(
            "status: Todo",
            "status: Todo\nclaimEpoch: 1\nclaim:\n" +
            "  workerIdentity: worker-a\n" +
            $"  claimedAt: {clock.UtcNow:O}\n" +
            $"  expiresAt: {clock.UtcNow.AddHours(1):O}"));

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.GetClaimOwnershipAsync(Config, id, CancellationToken.None));
        Assert.Equal("STORE_MIGRATION_REQUIRED", exception.Code);
        Assert.Contains("wrighty init", exception.Message);

        var migration = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.InitializeAsync(Config, false, CancellationToken.None));
        Assert.Equal("CLAIM_FORMAT_UNSUPPORTED", migration.Code);
    }

    [Fact]
    public async Task Migration_moves_legacy_claims_to_sidecar_and_strips_documents()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var path = Directory.GetFiles(Path.Combine(directory, ".wrighty", "items"), "*.md").Single();
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, content.Replace(
            "status: Todo",
            "status: Todo\nclaimEpoch: 3\nclaim:\n" +
            "  version: 2\n" +
            "  workerIdentity: worker-a\n" +
            "  claimantId: agent:legacy\n" +
            "  claimToken: legacy-token\n" +
            "  agentType: codex\n" +
            "  sessionId: session-legacy\n" +
            "  workspacePath: /tmp/legacy\n" +
            "  claimantKind: agent\n" +
            $"  claimedAt: {clock.UtcNow:O}\n" +
            $"  expiresAt: {clock.UtcNow.AddHours(1):O}"));

        var result = await backend.InitializeAsync(Config, false, CancellationToken.None);

        Assert.Contains(result.Actions, action => action.Contains("migrated legacy claim frontmatter"));
        var migrated = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("claimEpoch", migrated);
        Assert.DoesNotContain("claimToken", migrated);
        var ownership = await backend.GetClaimOwnershipAsync(Config, id, CancellationToken.None);
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, ownership.State);
        Assert.Equal("agent:legacy", ownership.ClaimantId);
        var mutation = await backend.UpdateAsync(Config, id,
            new UpdateWorkItemOperation(
                WorkItemPatch.StatusOnly("In Progress"),
                false,
                ClaimHandle: new ClaimHandle(
                    Context(ClaimantKind.Agent, "agent:legacy", "codex"), "legacy-token")),
            CancellationToken.None);
        Assert.Equal("In Progress", mutation.Item.Status);

        Assert.False((await backend.InitializeAsync(Config, false, CancellationToken.None)).Changed);
    }

    [Fact]
    public async Task Migration_preserves_expired_claim_session_as_durable_record()
    {
        var backend = Backend("worker-a");
        var id = await Create(backend);
        var path = Directory.GetFiles(Path.Combine(directory, ".wrighty", "items"), "*.md").Single();
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, content.Replace(
            "status: Todo",
            "status: Todo\nclaimEpoch: 2\nclaim:\n" +
            "  version: 2\n" +
            "  workerIdentity: worker-a\n" +
            "  claimantId: agent:expired\n" +
            "  claimToken: expired-token\n" +
            "  agentType: claude\n" +
            "  sessionId: session-expired\n" +
            "  workspacePath: /tmp/expired\n" +
            "  claimantKind: agent\n" +
            $"  claimedAt: {clock.UtcNow.AddHours(-3):O}\n" +
            $"  expiresAt: {clock.UtcNow.AddHours(-2):O}"));

        await backend.InitializeAsync(Config, false, CancellationToken.None);

        Assert.Equal(ClaimOwnershipState.Unclaimed,
            (await backend.GetClaimOwnershipAsync(Config, id, CancellationToken.None)).State);
        var session = await backend.GetAgentSessionAsync(Config, id, CancellationToken.None);
        Assert.NotNull(session);
        Assert.True(session!.IsComplete);
        Assert.Equal(("claude", "session-expired", "/tmp/expired"),
            (session.AgentType, session.SessionId, session.WorkspacePath));
        Assert.True(session.FromCurrentInstallation);
    }

    [Fact]
    public async Task Mutation_holding_lock_finishes_before_takeover_then_old_generation_is_fenced()
    {
        var setup = Backend("worker-a");
        var id = await Create(setup);
        var agent = Context(ClaimantKind.Agent, "agent:one", "codex");
        var claim = await setup.TryClaimAsync(Config, id, agent, CancellationToken.None);
        var old = new ClaimHandle(agent, claim.ClaimToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new LocalMarkdownTrackerBackend(new Identity("worker-a"), clock, async (operation, _) =>
        {
            if (operation != "update") return;
            entered.TrySetResult();
            await resume.Task;
        });

        var update = backend.UpdateAsync(Config, id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("In Progress"), false, ClaimHandle: old), CancellationToken.None);
        await entered.Task;
        var takeover = backend.TakeoverAsync(Config, id, Context(ClaimantKind.Human, "human:web"), null, CancellationToken.None);
        resume.TrySetResult();

        Assert.Equal("In Progress", (await update).Item.Status);
        Assert.Equal(ClaimOutcome.TakenOver, (await takeover).Outcome);
        await AssertStale(() => backend.UpdateAsync(Config, id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("Done"), false, ClaimHandle: old), CancellationToken.None));
    }

    [Fact]
    public async Task Takeover_holding_lock_rotates_before_waiting_old_mutation()
    {
        var setup = Backend("worker-a");
        var id = await Create(setup);
        var agent = Context(ClaimantKind.Agent, "agent:one", "codex");
        var claim = await setup.TryClaimAsync(Config, id, agent, CancellationToken.None);
        var old = new ClaimHandle(agent, claim.ClaimToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new LocalMarkdownTrackerBackend(new Identity("worker-a"), clock, async (operation, _) =>
        {
            if (operation != "takeover") return;
            entered.TrySetResult();
            await resume.Task;
        });

        var takeover = backend.TakeoverAsync(Config, id, Context(ClaimantKind.Human, "human:web"), null, CancellationToken.None);
        await entered.Task;
        var update = backend.UpdateAsync(Config, id,
            new UpdateWorkItemOperation(WorkItemPatch.StatusOnly("Done"), false, ClaimHandle: old), CancellationToken.None);
        resume.TrySetResult();

        Assert.Equal(ClaimOutcome.TakenOver, (await takeover).Outcome);
        Assert.Equal("CLAIM_STALE", (await Assert.ThrowsAsync<TrackerException>(() => update)).Code);
    }

    private LocalMarkdownTrackerBackend Backend(string worker) => new(new Identity(worker), clock);

    private async Task<WorkItemId> Create(LocalMarkdownTrackerBackend backend, string title = "Item",
        string body = "Body", string status = "Todo", string? priority = null)
    {
        await backend.InitializeAsync(Config, false, CancellationToken.None);
        return (await backend.CreateAsync(Config,
            new CreateWorkItemOperation(new CreateWorkItemRequest(title, body, status, priority), false),
            CancellationToken.None)).Id;
    }

    private static AgentExecutionContext Context(ClaimantKind kind, string id, string? agent = null) =>
        new(agent, agent is null ? null : id, AgentContextSource.ExplicitOption, ClaimantKind: kind, ClaimantId: id);

    private static async Task AssertStale(Func<Task> action) =>
        Assert.Equal("CLAIM_STALE", (await Assert.ThrowsAsync<TrackerException>(action)).Code);

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class Identity(string value) : IWorkerIdentityProvider
    { public Task<string> GetIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(value); }
    private sealed class FakeClock(DateTimeOffset value) : IClock
    { public DateTimeOffset UtcNow { get; set; } = value; }
}
