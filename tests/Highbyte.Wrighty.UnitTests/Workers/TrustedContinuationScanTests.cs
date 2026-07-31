using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// The continuation scan against a real store: a trusted reply moving a waiting item to queued,
/// the consume-before-queue ordering, and the refusal paths that put the spend back.
///
/// The evaluator's decision logic has its own tests; these cover what surrounds it — the part a
/// live run found untested when a <see cref="NotSupportedException"/> reached an operator.
/// </summary>
public sealed class TrustedContinuationScanTests : IDisposable
{
    private const string Trusted = "highbyte";

    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-continuation-{Guid.NewGuid():N}");

    private static readonly DateTimeOffset Created = new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Captured = new(2026, 7, 26, 13, 0, 0, TimeSpan.Zero);

    /// <summary>Well past any debounce, so timing never accidentally decides a test about flow.</summary>
    private readonly FakeClock clock = new(Captured.AddHours(1));

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    [Fact]
    public async Task A_trusted_reply_queues_the_waiting_session_then_records_the_spend()
    {
        var fixture = await CreateWaitingItemAsync();
        SessionContinuationState? atQueueTime = null;
        fixture.Backend.BeforeQueue = async () =>
            atQueueTime = (await fixture.Inner.GetAgentSessionAsync(
                fixture.Config, fixture.Id, CancellationToken.None))?.Continuation;

        var results = await fixture.Scan.RunAsync(
            fixture.Config, Options(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ContinuationOutcome.Queue, result.Outcome);
        Assert.Equal(Trusted, result.Actor);
        // The ordering guarantee, reversed by live evidence: nothing is spent until the queue
        // transition is published. Spend-first burned the trigger whenever the scan was stopped
        // between the two writes — the key stayed consumed for a comment no run had seen.
        Assert.True(atQueueTime?.ConsumedKeys is null or []);
        // The item moved, and the spend followed it.
        var detail = await fixture.Inner.GetAsync(fixture.Config, fixture.Id, CancellationToken.None);
        Assert.Equal(DispatchStates.Queued, detail!.DispatchState);
        var after = (await fixture.Inner.GetAgentSessionAsync(
            fixture.Config, fixture.Id, CancellationToken.None))?.Continuation;
        Assert.Equal(1, after!.AutomaticContinuations);
        var key = Assert.Single(after.ConsumedKeys!);
        Assert.StartsWith("comment:c1@", key);
    }

    [Fact]
    public async Task A_refusal_spends_nothing()
    {
        var fixture = await CreateWaitingItemAsync();
        // A session mid-way through its budget, so any accidental write shows: a refusal must
        // leave the spend, the cooldown clock, and the key list byte-for-byte alone — there is no
        // longer a consume-then-restore round trip whose interruption could burn the trigger.
        var earlier = Captured.AddMinutes(-30);
        var seeded = new SessionContinuationState(
            ["comment:old@2026-07-26T11:00:00.0000000+00:00"],
            AutomaticContinuations: 2,
            LastQueuedAt: earlier);
        await fixture.Inner.RecordContinuationAsync(
            fixture.Config, fixture.Id, seeded, CancellationToken.None);
        fixture.Backend.QueueFailure = new TrackerException(
            "CLAIM_HELD", "The session is still claimed elsewhere.", 6);

        var results = await fixture.Scan.RunAsync(
            fixture.Config, Options(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ContinuationOutcome.QueueUnavailable, result.Outcome);
        Assert.Equal("The session is still claimed elsewhere.", result.Reason);
        var after = (await fixture.Inner.GetAgentSessionAsync(
            fixture.Config, fixture.Id, CancellationToken.None))?.Continuation;
        Assert.NotNull(after);
        Assert.Equal(2, after!.AutomaticContinuations);
        Assert.Equal(earlier, after.LastQueuedAt);
        Assert.Equal(seeded.ConsumedKeys, after.ConsumedKeys);
        // The item did not move, so the operator sees it exactly where it was.
        var detail = await fixture.Inner.GetAsync(fixture.Config, fixture.Id, CancellationToken.None);
        Assert.Equal(DispatchStates.NeedsAttention, detail!.DispatchState);
    }

    [Fact]
    public async Task A_lost_spend_write_does_not_report_a_queued_continuation_as_broken()
    {
        // The bookkeeping write can fail after the queue transition already published. The queue
        // is the outcome that matters — the worker picks the item up regardless — so the result
        // stays Queue and the cost is one uncounted budget turn, the same as a crash here.
        var fixture = await CreateWaitingItemAsync();
        fixture.Backend.RecordFailure = new TrackerException("GH_API_ERROR", "write failed", 10);

        var results = await fixture.Scan.RunAsync(
            fixture.Config, Options(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ContinuationOutcome.Queue, result.Outcome);
        var detail = await fixture.Inner.GetAsync(fixture.Config, fixture.Id, CancellationToken.None);
        Assert.Equal(DispatchStates.Queued, detail!.DispatchState);
        var after = (await fixture.Inner.GetAgentSessionAsync(
            fixture.Config, fixture.Id, CancellationToken.None))?.Continuation;
        Assert.True(after?.ConsumedKeys is null or []);
    }

    [Fact]
    public async Task A_backend_that_cannot_queue_reports_why_and_restores_the_spend()
    {
        // The defect a live run surfaced: a backend inheriting the interface's throwing default
        // let a NotSupportedException reach the operator. The scan must translate it and put the
        // consumption back, because the refusal is permanent — a consumed trigger would never
        // queue anything and only an edit could bring it back.
        var fixture = await CreateWaitingItemAsync();
        fixture.Backend.QueueFailure = new NotSupportedException();

        var results = await fixture.Scan.RunAsync(
            fixture.Config, Options(), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(ContinuationOutcome.QueueUnavailable, result.Outcome);
        Assert.Contains("cannot queue", result.Reason);
        var after = (await fixture.Inner.GetAgentSessionAsync(
            fixture.Config, fixture.Id, CancellationToken.None))?.Continuation;
        Assert.Equal(0, after?.AutomaticContinuations ?? 0);
        Assert.True(after?.ConsumedKeys is null or []);
    }

    [Fact]
    public async Task Preflight_reports_the_waiting_reply_ready_without_spending_it()
    {
        // Preflight runs before the operator confirms execution, and `worker --once` exits when it
        // reports nothing — which used to mean a bounded run could never continue a waiting item.
        // It must see the reply as work, but only the real scan inside the run may spend it.
        var fixture = await CreateWaitingItemAsync();
        var worker = new WorkerService(
            fixture.Tracker,
            new UnusedRunner(),
            new CurrentWorkspace(),
            [new ClaudeAgentAdapter()],
            clock: () => clock.UtcNow,
            continuations: fixture.Scan);
        var events = new List<WorkerEvent>();

        var hasWork = await worker.PreflightAsync(
            fixture.Config, Options(), directory,
            value =>
            {
                events.Add(value);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(hasWork);
        var ready = Assert.Single(events, value => value.Type == "ready");
        Assert.Equal(fixture.Id.Value, ready.ItemId);
        Assert.Contains(Trusted, ready.Message);
        // Probing consumed nothing and moved nothing.
        var after = (await fixture.Inner.GetAgentSessionAsync(
            fixture.Config, fixture.Id, CancellationToken.None))?.Continuation;
        Assert.Equal(0, after?.AutomaticContinuations ?? 0);
        var detail = await fixture.Inner.GetAsync(fixture.Config, fixture.Id, CancellationToken.None);
        Assert.Equal(DispatchStates.NeedsAttention, detail!.DispatchState);
    }

    private static WorkerOptions Options() =>
        new(
            "claude", true, null, WorkspaceMode.Current,
            new Dictionary<string, string>(), null, TimeSpan.FromMinutes(10),
            FencedAction.Kill, null, "agent", false, false);

    // --- skipping items that cannot have changed ------------------------------------------------

    [Fact]
    public async Task An_item_whose_revision_has_not_moved_is_not_read_again()
    {
        var fixture = await CreateWaitingItemAsync(withTrustedReply: false);

        // The first pass reads, finds nothing to act on, and remembers the revision it saw.
        await fixture.Scan.RunAsync(fixture.Config, Options(), CancellationToken.None);
        Assert.Equal(1, fixture.Provider.Reads);
        var readsAfterFirst = fixture.Provider.Reads;

        await fixture.Scan.RunAsync(fixture.Config, Options(), CancellationToken.None);

        Assert.Equal(readsAfterFirst, fixture.Provider.Reads);
    }

    [Fact]
    public async Task An_item_that_moved_since_it_was_last_examined_is_read_again()
    {
        var fixture = await CreateWaitingItemAsync(withTrustedReply: false);
        await fixture.Scan.RunAsync(fixture.Config, Options(), CancellationToken.None);
        var readsAfterFirst = fixture.Provider.Reads;

        // Put the stored observation behind the item's real revision, which is what a new comment
        // does. The gate must then let the read through.
        var session = await fixture.Tracker.GetAgentSessionAsync(
            fixture.Config, fixture.Id, CancellationToken.None);
        await fixture.Tracker.RecordContinuationAsync(
            fixture.Config,
            fixture.Id,
            (session!.Continuation ?? new SessionContinuationState()) with
            {
                LastObservedItemUpdatedAt = Created.AddYears(-1)
            },
            CancellationToken.None);

        await fixture.Scan.RunAsync(fixture.Config, Options(), CancellationToken.None);

        Assert.True(
            fixture.Provider.Reads > readsAfterFirst,
            "an item that moved must still be examined");
    }

    [Fact]
    public void A_revision_that_arrives_late_still_counts_as_a_change()
    {
        // The constraint behind storing the observed revision rather than the wall clock. GitHub
        // propagates a comment edit to the issue's own updatedAt with a short delay, so a poll taken
        // just after an edit reads the pre-edit value. Had the scan stored "now" instead, the edit
        // would land *earlier* than the stored instant and never look newer again — skipped
        // permanently rather than one poll late.
        var observedBeforeTheEditPropagated = new DateTimeOffset(2026, 7, 31, 10, 41, 36, TimeSpan.Zero);
        var editInstant = observedBeforeTheEditPropagated.AddSeconds(9);
        var wallClockAtThatPoll = observedBeforeTheEditPropagated.AddSeconds(14);

        var state = new SessionContinuationState()
            .WithObservedItemRevision(observedBeforeTheEditPropagated);
        Assert.True(state.MayHaveChangedSince(editInstant));

        var wallClockState = new SessionContinuationState()
            .WithObservedItemRevision(wallClockAtThatPoll);
        Assert.False(wallClockState.MayHaveChangedSince(editInstant));
    }

    [Fact]
    public void An_unknown_revision_is_always_treated_as_changed()
    {
        // Null is not evidence of no change. A backend that cannot report a revision keeps paying
        // for the read rather than silently going quiet.
        var seen = new SessionContinuationState()
            .WithObservedItemRevision(new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero));

        Assert.True(seen.MayHaveChangedSince(null));
        Assert.True(new SessionContinuationState().MayHaveChangedSince(null));
    }

    [Fact]
    public void An_unknown_revision_is_never_recorded_as_observed()
    {
        var state = new SessionContinuationState();

        Assert.Same(state, state.WithObservedItemRevision(null));
    }

    private sealed record Fixture(
        TrackerConfig Config,
        WorkItemId Id,
        LocalMarkdownTrackerBackend Inner,
        InterceptingBackend Backend,
        TrackerService Tracker,
        TrustedContinuationScan Scan,
        FixedContextProvider Provider);

    /// <summary>
    /// A waiting item as a paused run leaves it: In Progress, needs-attention, automatic execution
    /// allowed, with a complete recorded session this installation can resume — plus an approved
    /// conversation whose newest comment is a trusted author's reply.
    /// </summary>
    private async Task<Fixture> CreateWaitingItemAsync(bool withTrustedReply = true)
    {
        var inner = new LocalMarkdownTrackerBackend(new FakeIdentity(), clock);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, ".wrighty.json"),
            LocalMarkdown = new LocalMarkdownBackendConfig(),
            LeaseMinutes = 60
        };
        await inner.InitializeAsync(config, false, CancellationToken.None);
        var created = await inner.CreateAsync(config, new CreateWorkItemOperation(
            new CreateWorkItemRequest("Continue me", "Body", "In Progress", "P1",
                AutomaticExecutionAllowed: true, AgentPolicy: "claude"), false),
            CancellationToken.None);

        var context = new AgentExecutionContext(
            "claude", "session-waiting", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:worker:waiting");
        var claim = await inner.TryClaimAsync(config, created.Id, context, CancellationToken.None);
        var handle = new ClaimHandle(context, claim.ClaimToken);
        await inner.RenewClaimAsync(
            config, created.Id, handle, directory, "session-waiting", CancellationToken.None);
        await inner.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: handle),
            CancellationToken.None);

        var backend = new InterceptingBackend(inner);
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var provider = new FixedContextProvider(ExecutionContextResult.Approved(
            withTrustedReply
                ? Snapshot(created.Id, Comment("c1", "Use the retry budget from the config."))
                // No reply to act on, so the item stays needs-attention across scans — which is the
                // only way to observe the gate rather than the dispatch-state check that follows a
                // queue.
                : Snapshot(created.Id)));
        var scan = new TrustedContinuationScan(
            tracker, _ => provider, clock: () => clock.UtcNow);
        return new Fixture(config, created.Id, inner, backend, tracker, scan, provider);
    }

    private static GitHubComment Comment(string id, string body) =>
        new(id, Trusted, "OWNER", Cutoff.AddMinutes(30), null,
            $"https://github.com/owner/repo/issues/42#issuecomment-{id}", body,
            false, null, []);

    /// <summary>Resolved by the real resolver, so the fixture carries real decisions.</summary>
    private static ExecutionContextSnapshot Snapshot(WorkItemId item, params GitHubComment[] comments)
    {
        var conversation = new GitHubConversation(
            "Add retry handling", "The worker should retry once.",
            "https://github.com/owner/repo/issues/42",
            Created.AddHours(-1), null, null, comments);
        var result = new ApprovedContextResolver(
                isApprover: actor => actor == Trusted,
                canExcludeContent: actor => actor == Trusted,
                policy: null,
                isTrustedAuthor: actor => actor == Trusted)
            .Resolve(
                item, conversation,
                new ContextApproval(ContextApprovalSource.ProjectField, Cutoff, Cutoff),
                ContextLimits.Default, Captured);
        Assert.True(result.IsApproved, $"fixture did not resolve: {result.Code} {result.Message}");
        return result.Snapshot!;
    }

    private sealed class FixedContextProvider(ExecutionContextResult result)
        : IExecutionContextProvider
    {
        /// <summary>How many times the conversation was actually read — the cost the gate avoids.</summary>
        public int Reads { get; private set; }

        public Task<ExecutionContextResult> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            ContextReadPurpose purpose,
            ContextLimits limits,
            CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Forwards the continuation members explicitly — and re-lists the interface, which is what
    /// makes these declarations take part in interface mapping. Without both, calls through
    /// <see cref="ITrackerBackend"/> land on the default members (no-op record, throwing queue)
    /// instead of delegating.
    /// </summary>
    private sealed class InterceptingBackend(ITrackerBackend inner)
        : DelegatingTrackerBackend(inner), ITrackerBackend
    {
        public Func<Task>? BeforeQueue { get; set; }

        public Exception? QueueFailure { get; set; }

        public Exception? RecordFailure { get; set; }

        public Task RecordContinuationAsync(
            TrackerConfig config,
            WorkItemId id,
            SessionContinuationState continuation,
            CancellationToken cancellationToken) =>
            RecordFailure is not null
                ? throw RecordFailure
                : Inner.RecordContinuationAsync(config, id, continuation, cancellationToken);

        public async Task QueuePausedAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            if (BeforeQueue is not null)
                await BeforeQueue();
            if (QueueFailure is not null)
                throw QueueFailure;
            await Inner.QueuePausedAsync(config, id, cancellationToken);
        }
    }

    private sealed class CurrentWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
    }

    private sealed class UnusedRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(AgentInvocation invocation,
            IAgentAdapter adapter, TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Preflight must not launch an agent.");
    }

    private sealed class FakeIdentity(string identity = "worker-test") : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult(identity);
    }

    private sealed class FakeClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }
}
