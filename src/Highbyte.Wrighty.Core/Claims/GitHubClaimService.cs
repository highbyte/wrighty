using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.Claims;

public sealed class GitHubClaimService(
    GhApi api,
    IInstallationIdentityProvider identityProvider,
    IClock clock,
    GitHubWorkItemAddressResolver resolver,
    Caching.IWorkItemRuntimeStore? runtimeStore = null) : IClaimService
{
    private static bool HasAddress(ClaimRecord claim) =>
        !string.IsNullOrWhiteSpace(claim.Agent) ||
        !string.IsNullOrWhiteSpace(claim.SessionId) ||
        !string.IsNullOrWhiteSpace(claim.WorkspacePath);

    /// <summary>
    /// <see cref="RecordSessionAsync"/> for callers whose backend mutation must win over local
    /// bookkeeping. The runtime store lives outside any workspace (for example
    /// <c>~/Library/Caches/wrighty</c> on macOS), so a sandboxed process that is allowed to talk
    /// to GitHub can still be denied this write — the default Codex profile confines file writes
    /// to the workspace. When the caller is completing a release or has already published a
    /// requeue, failing the whole operation over a cache refresh inverts what matters: the cache
    /// entry left behind is stale-tolerated by every reader (session-id guarded), while a vetoed
    /// release leaves the item finished-but-claimed and a thrown post-publish requeue misreports
    /// a transition that already happened. See https://github.com/highbyte/wrighty/issues/85.
    /// </summary>
    private async Task TryRecordSessionAsync(
        WorkItemId id,
        ClaimRecord claim,
        CancellationToken cancellationToken,
        string? branch = null)
    {
        try
        {
            await RecordSessionAsync(id, claim, cancellationToken, branch);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Local bookkeeping only; the backend mutation this call accompanies is authoritative.
        }
    }

    private async Task RecordSessionAsync(
        WorkItemId id,
        ClaimRecord claim,
        CancellationToken cancellationToken,
        string? branch = null)
    {
        if (runtimeStore is null || !HasAddress(claim))
        {
            return;
        }

        var existing = await runtimeStore.GetAsync(id.Value, cancellationToken);
        // The captured run outcome is written separately (RecordRunOutcomeAsync) after the run
        // ends. Carry it forward for the same recorded session so a claim-metadata refresh does not
        // wipe the "what happened" signal; a different session starts with no outcome.
        var sameSession = existing is not null &&
            string.Equals(existing.Session?.Id, claim.SessionId, StringComparison.Ordinal);
        // The machine-local cache is the authoritative source of the workspace path on the recording
        // host, and must never lose a known path to a null in a later claim event — this is what
        // keeps resume working when worker.shareLocalPaths=false redacts the path from the shared
        // claim marker (the marker carries null; the cache still has the real path).
        var workspacePath = claim.WorkspacePath ??
            (sameSession ? existing!.Session?.WorkspacePath : null);
        await runtimeStore.PutAsync(
            id.Value,
            new Caching.StoredWorkItemRuntime(
                new SessionAddress(
                    claim.Agent,
                    claim.SessionId,
                    workspacePath,
                    branch ?? existing?.Session?.Branch),
                sameSession ? existing!.LastRun : null,
                sameSession ? existing!.PendingDispatch : null,
                clock.UtcNow,
                claim.ExpiresAt,
                // Carried forward unconditionally, unlike the run outcome. The context is
                // written by the launch immediately before the process starts, at which point
                // the vendor has not yet reported the session id it will use — so gating it on
                // session-id equality discards it the moment that id lands, which is every run.
                // It is superseded only by the next launch that resolves one, and every launch
                // records before it spawns, so a session can never end up holding a context
                // that some other launch resolved.
                existing?.Context,
                sameSession ? existing!.LastReport : null,
                // Session-gated, and that gate is the budget-reset rule: continuation spend belongs
                // to the session that spent it. Carrying it forward unconditionally would let a
                // fresh session inherit an exhausted budget; dropping it here instead would reset
                // the budget on every claim refresh, which is the more dangerous direction — an
                // automatic loop could then run without limit.
                sameSession ? existing!.Continuation : null),
            cancellationToken);
    }

    public async Task RecordSessionContextAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContextMetadata context,
        CancellationToken cancellationToken)
    {
        if (runtimeStore is null)
            return;

        var existing = await runtimeStore.GetAsync(id.Value, cancellationToken);
        var record = existing is null
            // A launch can resolve a context before any claim metadata reaches the cache, so the
            // record is created rather than skipped. Leaving it unwritten would make the very run
            // that established the context the one unable to prove what it supplied.
            ? new Caching.StoredWorkItemRuntime(null, null, null, clock.UtcNow, null, context)
            : existing with { Context = context, UpdatedAt = clock.UtcNow };
        await runtimeStore.PutAsync(id.Value, record, cancellationToken);
    }

    public async Task RecordContinuationAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.SessionContinuationState continuation,
        CancellationToken cancellationToken)
    {
        if (runtimeStore is null)
            return;

        // Unlike the context, never created from nothing: spend belongs to a recorded session, and
        // an item with no runtime record has no session to continue.
        if (await runtimeStore.GetAsync(id.Value, cancellationToken) is not { } existing)
            return;

        await runtimeStore.PutAsync(
            id.Value,
            existing with { Continuation = continuation, UpdatedAt = clock.UtcNow },
            cancellationToken);
    }

    public async Task RecordRunOutcomeAsync(
        TrackerConfig config,
        WorkItemId id,
        RunOutcome outcome,
        string? finalMessage,
        DateTimeOffset endedAt,
        Workers.AgentFailure? failure,
        CancellationToken cancellationToken)
    {
        if (runtimeStore is null)
        {
            return;
        }

        var existing = await runtimeStore.GetAsync(id.Value, cancellationToken);
        var record = existing is null
            ? new Caching.StoredWorkItemRuntime(
                null,
                new LastRunRecord(outcome, endedAt, finalMessage, failure),
                null,
                endedAt,
                null)
            : existing with
            {
                LastRun = new LastRunRecord(outcome, endedAt, finalMessage, failure),
                UpdatedAt = endedAt
            };
        await runtimeStore.PutAsync(id.Value, record, cancellationToken);
    }

    public async Task RecordPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.PendingDispatch dispatch,
        CancellationToken cancellationToken)
    {
        if (runtimeStore is null)
            return;
        var existing = await runtimeStore.GetAsync(id.Value, cancellationToken)
            ?? throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' has no machine-local session record for deferred dispatch.",
                5);
        await runtimeStore.PutAsync(
            id.Value,
            existing with { PendingDispatch = dispatch, UpdatedAt = clock.UtcNow },
            cancellationToken);
    }

    public async Task ClearPendingDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        if (runtimeStore is null)
            return;
        var existing = await runtimeStore.GetAsync(id.Value, cancellationToken);
        if (existing is null || existing.PendingDispatch is null)
            return;
        await runtimeStore.PutAsync(
            id.Value,
            existing with { PendingDispatch = null, UpdatedAt = clock.UtcNow },
            cancellationToken);
    }

    public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext agentContext, CancellationToken cancellationToken) =>
        TryClaimAsync(config, id, agentContext, cancellationToken, null);

    public async Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext agentContext, CancellationToken cancellationToken, string? expectedClaimToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var data = await EventsAsync(config, issue, cancellationToken);
        EnsureNoLegacy(data, id);
        var worker = await identityProvider.GetInstallationIdAsync(cancellationToken);
        var current = ClaimResolver.Resolve(data.Events, clock.UtcNow);
        var claimantId = ResolveClaimantId(agentContext, generate: current is null);
        if (current is not null)
        {
            if (current.Claim.InstallationId != worker) return Result(current.Claim, ClaimOutcome.HeldByOther, false);
            if (current.Claim.ClaimantId != claimantId) return Result(current.Claim, ClaimOutcome.HeldByLocalClaimant, true);
            if (expectedClaimToken is null) throw Error("CLAIM_TOKEN_REQUIRED", id, current.Claim, true);
            if (current.Claim.ClaimToken != expectedClaimToken) throw Error("CLAIM_STALE", id, current.Claim, true);
            return Result(current.Claim, ClaimOutcome.AlreadyOwned, true);
        }

        var now = clock.UtcNow;
        var claim = NewEvent("acquired", worker, claimantId, agentContext, now, config, null);
        await CreateAsync(config, issue, claim, cancellationToken);
        var resolved = await ResolvedAsync(config, issue, id, cancellationToken);
        return resolved?.Claim.ClaimToken == claim.ClaimToken
            ? Result(claim, ClaimOutcome.Acquired, true)
            : resolved is null
                ? throw new TrackerException("CLAIM_PROTOCOL_ERROR", "The GitHub claim event was not resolved.")
                : Result(resolved.Claim, resolved.Claim.InstallationId == worker ? ClaimOutcome.HeldByLocalClaimant : ClaimOutcome.HeldByOther,
                    resolved.Claim.InstallationId == worker);
    }

    public async Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id,
        AgentExecutionContext claimantContext, string? currentClaimToken, CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var current = await ResolvedAsync(config, issue, id, cancellationToken)
            ?? throw new TrackerException(
                "CLAIM_NOT_FOUND",
                $"Work item '{id}' has no active claim. Takeover is no longer possible after " +
                $"the prior claim expires or is released. Continue with: " +
                $"wrighty worker --item {id.Value} --yes",
                5);
        var worker = await identityProvider.GetInstallationIdAsync(cancellationToken);
        if (current.Claim.InstallationId != worker) throw Error("CLAIM_NOT_OWNER", id, current.Claim, false);
        var claimantId = ResolveClaimantId(claimantContext, generate: true);
        if (current.Claim.ClaimantId == claimantId && currentClaimToken == current.Claim.ClaimToken)
            return Result(current.Claim, ClaimOutcome.AlreadyOwned, true);
        var claim = NewEvent("takenOver", worker, claimantId, claimantContext, clock.UtcNow, config,
            current.Claim.ClaimToken) with
        {
            Agent = claimantContext.Agent ?? current.Claim.Agent,
            SessionId = claimantContext.SessionId ?? current.Claim.SessionId,
            WorkspacePath = current.Claim.WorkspacePath
        };
        await CreateAsync(config, issue, claim, cancellationToken);
        var winner = await ResolvedAsync(config, issue, id, cancellationToken);
        if (winner?.Claim.ClaimToken != claim.ClaimToken) throw Error("CLAIM_STALE", id, winner?.Claim ?? current.Claim, true);
        await RecordSessionAsync(id, claim, cancellationToken);
        return Result(claim, ClaimOutcome.TakenOver, true);
    }

    public Task<ClaimResult> RenewAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        CancellationToken cancellationToken) =>
        RenewAsync(config, id, claimHandle, workspacePath, sessionId, branch: null,
            cancellationToken);

    public async Task<ClaimResult> RenewAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
        string? branch,
        CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var current = await ResolvedAsync(config, issue, id, cancellationToken)
            ?? throw new TrackerException("CLAIM_EXPIRED",
                $"Work item '{id}' no longer has an active claim.", 6);
        var worker = await identityProvider.GetInstallationIdAsync(cancellationToken);
        if (current.Claim.InstallationId != worker)
            throw Error("CLAIM_NOT_OWNER", id, current.Claim, false);
        if (string.IsNullOrWhiteSpace(claimHandle.ClaimToken) ||
            claimHandle.ClaimantId != current.Claim.ClaimantId ||
            claimHandle.ClaimToken != current.Claim.ClaimToken)
            throw Error("CLAIM_STALE", id, current.Claim, true);

        var now = clock.UtcNow;
        var renewed = current.Claim with
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "renewed",
            PreviousClaimToken = current.Claim.ClaimToken,
            ClaimedAt = now,
            ExpiresAt = now.AddMinutes(config.LeaseMinutes),
            Agent = claimHandle.Claimant.Agent ?? current.Claim.Agent,
            SessionId = sessionId ?? current.Claim.SessionId,
            ClaimantKind = ClaimantKinds.ToStorageValue(claimHandle.Claimant.EffectiveClaimantKind),
            WorkspacePath = workspacePath ?? current.Claim.WorkspacePath
        };
        await CreateAsync(config, issue, renewed, cancellationToken);
        var winner = await ResolvedAsync(config, issue, id, cancellationToken);
        if (winner?.Claim.ClaimToken != renewed.ClaimToken ||
            winner.Claim.ClaimantId != renewed.ClaimantId)
            throw Error("CLAIM_STALE", id, winner?.Claim ?? current.Claim, true);
        // Record and return the locally-built claim, not the re-read winner: the two are identical
        // (the stale check above guarantees it) except that a redacted comment (shareLocalPaths=
        // false) drops the workspace path — using `renewed` keeps the real path in the machine-local
        // cache and the in-process result.
        await RecordSessionAsync(id, renewed, cancellationToken, branch);
        await TryCollapseRenewalHistoryAsync(config, issue, renewed.ClaimToken, cancellationToken);
        return Result(renewed, ClaimOutcome.AlreadyOwned, true);
    }

    public Task ReleaseAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        throw new TrackerException("CLAIM_TOKEN_REQUIRED", "Release requires --claimant-id and --claim-token.", 6);

    public async Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
        bool overrideClaimant, CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var current = await ResolvedAsync(config, issue, id, cancellationToken)
            ?? throw new TrackerException("CLAIM_NOT_FOUND", $"Work item '{id}' does not have an active claim.", 5);
        var worker = await identityProvider.GetInstallationIdAsync(cancellationToken);
        if (current.Claim.InstallationId != worker) throw Error("CLAIM_NOT_OWNER", id, current.Claim, false);
        if (!overrideClaimant) await ValidateAsync(config, id, claimHandle, cancellationToken);
        // Tolerant: a denied cache write must not veto the release itself (the sandboxed-finish
        // failure of issue #85 was exactly this write aborting before the release publish below).
        await TryRecordSessionAsync(id, current.Claim, cancellationToken);
        var kind = overrideClaimant ? "overrideReleased" : "released";
        var release = NewEvent(kind, worker, current.Claim.ClaimantId,
            claimHandle.Claimant, clock.UtcNow, config, current.Claim.ClaimToken);
        await CreateAsync(config, issue, release, cancellationToken);
        var after = await ResolvedAsync(config, issue, id, cancellationToken);
        if (after is not null) throw Error("CLAIM_STALE", id, after.Claim, after.Claim.InstallationId == worker);
        await TryCleanupInactiveHistoryAsync(config, issue, cancellationToken);
    }

    public async Task RequeueAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var current = await ResolvedAsync(config, issue, id, cancellationToken)
            ?? throw new TrackerException(
                "CLAIM_NOT_FOUND",
                $"Work item '{id}' does not have an active claim to requeue.",
                5);
        var worker = await identityProvider.GetInstallationIdAsync(cancellationToken);
        if (current.Claim.InstallationId != worker)
            throw Error("CLAIM_NOT_OWNER", id, current.Claim, false);
        await ValidateAsync(config, id, claimHandle, cancellationToken);
        // The workspace path comes from the machine-local cache, never from the claim marker.
        // With the default worker.shareLocalPaths=false the marker cannot carry one, and a
        // marker path is issue-comment content this service refuses as a session address
        // everywhere else (see CachedWorkspace). The cache is also what the eventual launch
        // resolves the resume address from, so requiring it here is the honest predictor of
        // whether the queued session can actually start.
        var cached = runtimeStore is null
            ? null
            : await runtimeStore.GetAsync(id.Value, cancellationToken);
        var workspacePath = CachedWorkspace(current.Claim, worker, cached);
        if (string.IsNullOrWhiteSpace(current.Claim.Agent) ||
            string.IsNullOrWhiteSpace(current.Claim.SessionId) ||
            string.IsNullOrWhiteSpace(workspacePath))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' has no complete agent session recorded on this " +
                "installation to queue.",
                5);

        var now = clock.UtcNow;
        var requeued = current.Claim with
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "requeued",
            PreviousClaimToken = current.Claim.ClaimToken,
            ClaimToken = Guid.NewGuid().ToString("N"),
            ClaimedAt = now,
            ExpiresAt = now.AddMinutes(config.LeaseMinutes),
            // The cache-resolved path, so the session record keeps the address that was
            // verified — not whatever path the resolved marker happened to carry.
            WorkspacePath = workspacePath
        };
        await CreateAsync(config, issue, requeued, cancellationToken);
        // Tolerant: the requeue is already published, so throwing here would misreport a
        // transition that happened; the cache keeps its last verified session address.
        await TryRecordSessionAsync(id, requeued, cancellationToken);
        if (await ResolvedAsync(config, issue, id, cancellationToken) is not null)
            throw new TrackerException(
                "CLAIM_PROTOCOL_ERROR",
                $"Work item '{id}' remained actively claimed after it was requeued.",
                9);
        var latest = ClaimResolver.ResolveLatestGeneration(
            (await EventsAsync(config, issue, cancellationToken)).Events);
        if (latest?.Claim.EventId != requeued.EventId)
            throw new TrackerException(
                "CLAIM_STALE",
                $"Work item '{id}' changed while its session was being queued.",
                6);
        await TryResolveHandoverAsync(
            config, id, "The session was requeued for a continuous worker.", cancellationToken);
    }

    private async Task TryResolveHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await ResolveHandoverAsync(config, id, reason, cancellationToken);
        }
        catch (TrackerException)
        {
            // Trimming a stale handover comment is housekeeping; never fail the operation for it.
        }
    }

    public async Task<ClaimOwnershipResult> ValidateAsync(TrackerConfig config, WorkItemId id,
        ClaimHandle claimHandle, CancellationToken cancellationToken)
    {
        var ownership = await GetOwnershipAsync(config, id, cancellationToken);
        var issue = resolver.Decode(id, config).IssueNumber;
        var current = await ResolvedAsync(config, issue, id, cancellationToken);
        if (current is null) throw new TrackerException("CLAIM_REQUIRED", $"Work item '{id}' requires an active claim.", 6);
        var worker = await identityProvider.GetInstallationIdAsync(cancellationToken);
        if (current.Claim.InstallationId != worker) throw Error("CLAIM_HELD", id, current.Claim, false);
        if (string.IsNullOrWhiteSpace(claimHandle.ClaimToken)) throw Error("CLAIM_TOKEN_REQUIRED", id, current.Claim, true);
        if (claimHandle.ClaimantId != current.Claim.ClaimantId || claimHandle.ClaimToken != current.Claim.ClaimToken)
            throw Error("CLAIM_STALE", id, current.Claim, true);
        return ownership;
    }

    public async Task<bool> IsOwnedByCurrentWorkerAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        (await GetOwnershipAsync(config, id, cancellationToken)).State == ClaimOwnershipState.OwnedByCurrent;

    public async Task<ClaimOwnershipResult> GetOwnershipAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
        (await GetClaimStateAsync(config, id, cancellationToken)).Ownership;

    public async Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        (await GetClaimStateAsync(config, id, cancellationToken)).Session;

    public async Task<ClaimStateReading> GetClaimStateAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var data = await EventsAsync(config, issue, cancellationToken);
        EnsureNoLegacy(data, id);
        var worker = await identityProvider.GetInstallationIdAsync(cancellationToken);
        var active = ClaimResolver.Resolve(data.Events, clock.UtcNow);
        var cached = runtimeStore is null
            ? null
            : await runtimeStore.GetAsync(id.Value, cancellationToken);
        var ownership = active is null
            ? new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed)
            : Ownership(active.Claim, worker, cached);
        var latest = ClaimResolver.ResolveLatestGeneration(data.Events);
        return new ClaimStateReading(ownership, Session(latest, cached, worker));
    }

    private static ClaimOwnershipResult Ownership(
        ClaimRecord claim, string worker, Caching.StoredWorkItemRuntime? cached)
    {
        var local = claim.InstallationId == worker;
        return new ClaimOwnershipResult(
            local ? ClaimOwnershipState.OwnedByCurrent : ClaimOwnershipState.HeldByOther,
            claim.InstallationId, claim.ExpiresAt, claim.ClaimantId,
            claim.Agent, claim.SessionId, claim.ClaimantKind, local,
            CachedWorkspace(claim, worker, cached));
    }

    /// <summary>
    /// The workspace path for a claim, from the machine-local runtime store — the only place it is
    /// read from. The claim marker's own copy is discarded in <c>EventsAsync</c> because it is
    /// issue-comment content, so this is not a fallback for a redacted marker but the whole
    /// lookup. Without it the recording host cannot resume its own session, because the ownership
    /// it reads would have no address at all.
    ///
    /// Two conditions guard it, and both matter. The claim must belong to this installation, so
    /// another host cannot be handed a path that means nothing on its filesystem. And the cached
    /// session must be the one this claim holds — a path left over from a previous session would
    /// point a resume at the wrong workspace, which is worse than refusing.
    /// </summary>
    private static string? CachedWorkspace(
        ClaimRecord claim, string worker, Caching.StoredWorkItemRuntime? cached)
    {
        if (!string.Equals(claim.InstallationId, worker, StringComparison.Ordinal)) return null;
        if (cached?.Session is not { } session) return null;
        if (string.IsNullOrWhiteSpace(claim.SessionId) ||
            !string.Equals(session.Id, claim.SessionId, StringComparison.Ordinal))
            return null;
        return session.WorkspacePath;
    }

    private static AgentSessionRecord? Session(
        ClaimEvent? latest,
        Caching.StoredWorkItemRuntime? cached,
        string worker)
    {
        if (latest is not null && (HasAddress(latest.Claim) || cached is null))
        {
            // The branch and run outcome are machine-local metadata that never travel through claim
            // comments; attach the cached ones only when they belong to the same recorded session.
            var sameSession = cached is not null &&
                string.Equals(cached.Session?.Id, latest.Claim.SessionId, StringComparison.Ordinal);
            return new AgentSessionRecord(
                latest.Claim.Agent,
                latest.Claim.SessionId,
                // The claim marker's own path is discarded at the parse boundary, so this reads
                // only the machine-local cache, and only for the session the claim holds. Another
                // installation has no cache, so it correctly sees no path and cannot resume.
                sameSession ? cached!.Session?.WorkspacePath : null,
                latest.Claim.ExpiresAt,
                string.Equals(latest.Claim.InstallationId, worker, StringComparison.Ordinal),
                sameSession ? cached!.Session?.Branch : null,
                sameSession ? cached!.LastRun?.Outcome : null,
                sameSession ? cached!.LastRun?.FinalMessage : null,
                sameSession ? cached!.LastRun?.EndedAt : null,
                sameSession ? cached!.LastRun?.Failure : null,
                sameSession ? cached!.PendingDispatch?.ToInfo(true) : null,
                sameSession ? cached!.Context : null,
                sameSession ? cached!.LastReport : null,
                sameSession ? cached!.Continuation : null);
        }

        if (cached is null)
            return null;
        return new AgentSessionRecord(
            cached.Session?.Agent,
            cached.Session?.Id,
            cached.Session?.WorkspacePath,
            cached.LastClaimExpiresAt ?? cached.UpdatedAt,
            FromCurrentInstallation: true,
            Branch: cached.Session?.Branch,
            Outcome: cached.LastRun?.Outcome,
            FinalMessage: cached.LastRun?.FinalMessage,
            EndedAt: cached.LastRun?.EndedAt,
            Failure: cached.LastRun?.Failure,
            Dispatch: cached.PendingDispatch?.ToInfo(true),
            Context: cached.Context,
            LastReport: cached.LastReport,
            Continuation: cached.Continuation);
    }

    private async Task<ClaimEvent?> ResolvedAsync(TrackerConfig config, int issue, WorkItemId id, CancellationToken token)
    {
        var data = await EventsAsync(config, issue, token);
        EnsureNoLegacy(data, id);
        return ClaimResolver.Resolve(data.Events, clock.UtcNow);
    }

    private async Task<EventData> EventsAsync(TrackerConfig config, int issue, CancellationToken token)
    {
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments?per_page=100";
        using var document = await api.GetPaginatedAsync(config.GitHubHost, endpoint, token);
        var events = new List<ClaimEvent>();
        var legacySchema = false;
        foreach (var page in document.RootElement.EnumerateArray())
            foreach (var comment in page.EnumerateArray())
            {
                var body = comment.GetProperty("body").GetString() ?? "";
                legacySchema |= ClaimMarker.HasLegacyMarker(body);
                if (!ClaimMarker.TryParse(body, out var claim))
                    continue;

                // A marker only counts when the repository vouches for whoever wrote the comment.
                // Anyone able to comment can publish one otherwise, and the claim chain is what
                // decides which worker owns an item — see ClaimMarkerTrust.
                if (!comment.TryGetProperty("author_association", out var association))
                    throw new TrackerException(
                        "CLAIM_PROTOCOL_ERROR",
                        "A GitHub issue comment carried no author association, so a claim marker " +
                        "on it cannot be attributed. Refusing to read the claim chain rather than " +
                        "trusting an unattributable marker.",
                        6);
                // Untrusted markers are skipped rather than raising: a forged comment must not be
                // able to stop the protocol either, which raising here would let it do.
                if (!ClaimMarkerTrust.MayCarryMarker(association.GetString()))
                    continue;

                events.Add(new ClaimEvent(comment.GetProperty("id").GetInt64(), comment.GetProperty("created_at").GetDateTimeOffset(), claim));
            }
        return new EventData(events, legacySchema);
    }

    private async Task CreateAsync(TrackerConfig config, int issue, ClaimRecord claim, CancellationToken token)
    {
        // The claim marker is published to the (possibly public) issue. When shareLocalPaths=false,
        // redact the absolute workspace path from what is serialized; the real path stays in the
        // machine-local work-item runtime store.
        //
        // What is published here is for people reading the issue. Nothing that decides where a
        // process runs reads it back: a marker is comment content, this service never asks for a
        // comment's author, and the resolver never checks one, so any account able to comment can
        // put a marker on the issue. The session address an unattended launch resumes into is
        // therefore taken from the machine-local store alone — see Session and Ownership below.
        var published = config.EffectiveWorker.ShareLocalPaths
            ? claim
            : claim with { WorkspacePath = null };
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments";
        using var ignored = await api.SendJsonAsync(config.GitHubHost, "POST", endpoint, new { body = ClaimMarker.Format(published) }, token);
    }

    public async Task PostHandoverAsync(
        TrackerConfig config,
        Workers.HandoverContent content,
        CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(content.Id, config).IssueNumber;
        await UpsertHandoverAsync(
            config, issue, Workers.HandoverRenderer.Render(content), cancellationToken);
    }

    public async Task ResolveHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        string reason,
        CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var existing = await FindHandoverCommentAsync(config, issue, cancellationToken);
        if (existing is not { } commentId)
        {
            // Nothing was ever posted (e.g. handoverComment=off): no stale instructions to trim.
            return;
        }

        await EditCommentAsync(
            config, commentId, Workers.HandoverRenderer.RenderResolved(reason), cancellationToken);
    }

    private async Task UpsertHandoverAsync(
        TrackerConfig config,
        int issue,
        string body,
        CancellationToken cancellationToken)
    {
        // Still one handover per issue, but re-posted rather than edited in place. GitHub neither
        // moves an edited comment to the bottom of the thread nor notifies anyone about the edit,
        // so an in-place return to "needs attention" — after a requeue trimmed the comment to its
        // resolved form — is invisible: the operator sees the newest run report at the bottom and
        // no guidance anywhere near it. Deleting and recreating puts the guidance below the report
        // it belongs to and produces a notification that attention is needed again.
        var existing = await FindHandoverCommentAsync(config, issue, cancellationToken);
        if (existing is { } commentId)
        {
            try
            {
                await api.DeleteAsync(config.GitHubHost,
                    $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/comments/{commentId}",
                    cancellationToken);
            }
            catch (TrackerException)
            {
                // A second marker comment must never exist — the finder takes the oldest, which
                // would freeze the guidance at this stale body forever. If the delete failed the
                // comment is still there, so fall back to the in-place edit.
                await EditCommentAsync(config, commentId, body, cancellationToken);
                return;
            }
        }

        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments";
        using var ignored = await api.SendJsonAsync(
            config.GitHubHost, "POST", endpoint, new { body }, cancellationToken);
    }

    public async Task RecordRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.AgentRunReport report,
        CancellationToken cancellationToken)
    {
        if (runtimeStore is null) return;
        var existing = await runtimeStore.GetAsync(id.Value, cancellationToken);
        var record = existing is null
            ? new Caching.StoredWorkItemRuntime(null, null, null, clock.UtcNow, null, null, report)
            : existing with { LastReport = report, UpdatedAt = clock.UtcNow };
        await runtimeStore.PutAsync(id.Value, record, cancellationToken);
    }

    public async Task PublishRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        ApprovedContext.AgentRunReport report,
        string? branch,
        CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var body = ApprovedContext.RunReportRenderer.Render(report, id, branch);

        // Keyed on this run's report id, not on "a report exists". Handover is one rolling comment
        // because only the latest matters; run reports are a history, so each run keeps its own and
        // a republish after a failed request updates that one rather than overwriting an earlier
        // run's record or adding a duplicate beside it.
        var existing = await FindCommentAsync(
            config, issue, candidate => candidate.Contains(report.ReportId, StringComparison.Ordinal),
            cancellationToken);
        if (existing is { } commentId)
        {
            await EditCommentAsync(config, commentId, body, cancellationToken);
            return;
        }

        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments";
        using var ignored = await api.SendJsonAsync(
            config.GitHubHost, "POST", endpoint, new { body }, cancellationToken);
    }

    private async Task<long?> FindCommentAsync(
        TrackerConfig config,
        int issue,
        Func<string, bool> matches,
        CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments?per_page=100";
        using var document = await api.GetPaginatedAsync(config.GitHubHost, endpoint, cancellationToken);
        foreach (var page in document.RootElement.EnumerateArray())
            foreach (var comment in page.EnumerateArray())
            {
                var body = comment.GetProperty("body").GetString() ?? "";
                if (matches(body))
                    return comment.GetProperty("id").GetInt64();
            }

        return null;
    }

    private async Task<long?> FindHandoverCommentAsync(
        TrackerConfig config,
        int issue,
        CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments?per_page=100";
        using var document = await api.GetPaginatedAsync(config.GitHubHost, endpoint, cancellationToken);
        foreach (var page in document.RootElement.EnumerateArray())
            foreach (var comment in page.EnumerateArray())
            {
                var body = comment.GetProperty("body").GetString() ?? "";
                if (Workers.HandoverRenderer.IsHandover(body))
                    return comment.GetProperty("id").GetInt64();
            }

        return null;
    }

    private async Task EditCommentAsync(
        TrackerConfig config,
        long commentId,
        string body,
        CancellationToken cancellationToken)
    {
        var endpoint =
            $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/comments/{commentId}";
        using var ignored = await api.SendJsonAsync(
            config.GitHubHost, "PATCH", endpoint, new { body }, cancellationToken);
    }

    private async Task TryCleanupInactiveHistoryAsync(
        TrackerConfig config,
        int issue,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await EventsAsync(config, issue, cancellationToken);
            if (data.LegacySchema || ClaimResolver.Resolve(data.Events, clock.UtcNow) is not null) return;
            var obsolete = data.Events
                .OrderByDescending(value => value.CreatedAt)
                .ThenByDescending(value => value.CommentId)
                .Skip(config.ClaimHistoryLimit)
                .ToArray();
            foreach (var item in obsolete)
                await api.DeleteAsync(config.GitHubHost,
                    $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/comments/{item.CommentId}",
                    cancellationToken);
        }
        catch (TrackerException)
        {
            // Inactive history retention is housekeeping and must never fail a completed release.
        }
    }

    private async Task TryCollapseRenewalHistoryAsync(
        TrackerConfig config,
        int issue,
        string activeToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = await EventsAsync(config, issue, cancellationToken);
            // Only collapse while the generation identified by activeToken is still the resolved
            // active claim — never touch a chain a concurrent takeover or release has moved on.
            var active = ClaimResolver.Resolve(data.Events, clock.UtcNow);
            if (active?.Claim.ClaimToken != activeToken) return;
            // Every renewal of one generation keeps the same claim token and points its
            // previousClaimToken at the generation-establishing acquired token, not at the prior
            // renewal — so the chain resolves identically with only the newest renewal present.
            // Delete the superseded renewals (best effort) to stop them accumulating as comment
            // noise on the issue. The ordering matches ClaimResolver, so the kept event is exactly
            // the one resolution would pick.
            var superseded = data.Events
                .Where(value => value.Claim.EventType == "renewed"
                    && value.Claim.ClaimToken == activeToken)
                .OrderByDescending(value => value.CreatedAt)
                .ThenByDescending(value => value.CommentId)
                .Skip(1)
                .ToArray();
            foreach (var item in superseded)
                await api.DeleteAsync(config.GitHubHost,
                    $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/comments/{item.CommentId}",
                    cancellationToken);
        }
        catch (TrackerException)
        {
            // Collapsing renewal history is housekeeping and must never fail the renewal itself.
        }
    }

    private static ClaimRecord NewEvent(string type, string worker, string claimantId,
        AgentExecutionContext context, DateTimeOffset now, TrackerConfig config, string? previous) =>
        new(3, Guid.NewGuid().ToString("N"), worker, now, now.AddMinutes(config.LeaseMinutes), type,
            claimantId, Guid.NewGuid().ToString("N"), previous, context.Agent, context.SessionId,
            ClaimantKinds.ToStorageValue(context.EffectiveClaimantKind));

    private static ClaimResult Result(ClaimRecord claim, ClaimOutcome outcome, bool takeover) =>
        new(outcome, claim.InstallationId, claim.ExpiresAt, claim.EventId,
            claim.Agent, claim.SessionId, claim.ClaimantKind, claim.ClaimantId,
            outcome is ClaimOutcome.Acquired or ClaimOutcome.AlreadyOwned or ClaimOutcome.TakenOver
                ? claim.ClaimToken
                : null,
            takeover,
            claim.WorkspacePath);

    private static string ResolveClaimantId(AgentExecutionContext context, bool generate)
    {
        if (!string.IsNullOrWhiteSpace(context.ClaimantId)) return context.ClaimantId;
        if (context.EffectiveClaimantKind == ClaimantKind.Human) return "human-cli";
        if (context.EffectiveClaimantKind == ClaimantKind.Automation)
            throw new TrackerException("ARGUMENT_INVALID", "Automation requires an explicit claimant ID.", 2);
        return generate ? $"claimant:{Guid.NewGuid():N}" : "";
    }

    private static void EnsureNoLegacy(EventData data, WorkItemId id)
    {
        if (data.LegacySchema) throw new TrackerException(
            "CLAIM_SCHEMA_UNSUPPORTED",
            $"Work item '{id}' contains a pre-v3 Wrighty claim marker. Use a fresh issue with " +
            "this pre-release schema; do not mix Wrighty claim protocols.",
            6);
    }

    private static TrackerException Error(string code, WorkItemId id, ClaimRecord claim, bool local) =>
        new(code, $"Claim handle for work item '{id}' is not current (claimant {Short(claim.ClaimantId)}).", 6,
            new Dictionary<string, object?>
            {
                ["id"] = id.Value,
                ["installationId"] = claim.InstallationId,
                ["claimantId"] = Short(claim.ClaimantId),
                ["claimantKind"] = claim.ClaimantKind,
                ["agent"] = claim.Agent,
                ["expiresAt"] = claim.ExpiresAt,
                ["sameInstallation"] = local,
                ["takeoverAvailable"] = local
            });
    private static string Short(string value) => value.Length <= 12 ? value : $"{value[..12]}…";
    private sealed record EventData(IReadOnlyList<ClaimEvent> Events, bool LegacySchema);
}
