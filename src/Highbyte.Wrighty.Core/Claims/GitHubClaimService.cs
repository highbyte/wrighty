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
    IWorkerIdentityProvider identityProvider,
    IClock clock,
    GitHubWorkItemAddressResolver resolver,
    Caching.ISessionRecordCache? sessionCache = null) : IClaimService
{
    private static bool HasAddress(ClaimRecord claim) =>
        !string.IsNullOrWhiteSpace(claim.AgentType) ||
        !string.IsNullOrWhiteSpace(claim.SessionId) ||
        !string.IsNullOrWhiteSpace(claim.WorkspacePath);

    private async Task RecordSessionAsync(
        WorkItemId id,
        ClaimRecord claim,
        CancellationToken cancellationToken,
        string? branch = null)
    {
        if (sessionCache is null || !HasAddress(claim))
        {
            return;
        }

        var existing = await sessionCache.GetAsync(id.Value, cancellationToken);
        // The captured run outcome is written separately (RecordRunOutcomeAsync) after the run
        // ends. Carry it forward for the same recorded session so a claim-metadata refresh does not
        // wipe the "what happened" signal; a different session starts with no outcome.
        var sameSession = existing is not null &&
            string.Equals(existing.SessionId, claim.SessionId, StringComparison.Ordinal);
        // The machine-local cache is the authoritative source of the workspace path on the recording
        // host, and must never lose a known path to a null in a later claim event — this is what
        // keeps resume working when worker.shareLocalPaths=false redacts the path from the shared
        // claim marker (the marker carries null; the cache still has the real path).
        var workspacePath = claim.WorkspacePath ??
            (sameSession ? existing!.WorkspacePath : null);
        await sessionCache.PutAsync(
            id.Value,
            new Caching.CachedSessionRecord(
                claim.AgentType,
                claim.SessionId,
                workspacePath,
                clock.UtcNow,
                claim.ExpiresAt,
                branch ?? existing?.Branch,
                sameSession ? existing!.Outcome : null,
                sameSession ? existing!.FinalMessage : null,
                sameSession ? existing!.EndedAt : null,
                sameSession ? existing!.Failure : null,
                sameSession ? existing!.Dispatch : null),
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
        if (sessionCache is null)
        {
            return;
        }

        var existing = await sessionCache.GetAsync(id.Value, cancellationToken);
        var record = existing is null
            ? new Caching.CachedSessionRecord(
                null, null, null, endedAt, null, null, outcome, finalMessage, endedAt, failure)
            : existing with
            {
                Outcome = outcome,
                FinalMessage = finalMessage,
                EndedAt = endedAt,
                Failure = failure
            };
        await sessionCache.PutAsync(id.Value, record, cancellationToken);
    }

    public async Task RecordDeferredDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        Workers.DeferredDispatch dispatch,
        CancellationToken cancellationToken)
    {
        if (sessionCache is null)
            return;
        var existing = await sessionCache.GetAsync(id.Value, cancellationToken)
            ?? throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' has no machine-local session record for deferred dispatch.",
                5);
        await sessionCache.PutAsync(
            id.Value,
            existing with { Dispatch = dispatch, UpdatedAt = clock.UtcNow },
            cancellationToken);
    }

    public async Task ClearDeferredDispatchAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        if (sessionCache is null)
            return;
        var existing = await sessionCache.GetAsync(id.Value, cancellationToken);
        if (existing is null || existing.Dispatch is null)
            return;
        await sessionCache.PutAsync(
            id.Value,
            existing with { Dispatch = null, UpdatedAt = clock.UtcNow },
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
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var current = ClaimResolver.Resolve(data.Events, clock.UtcNow);
        var claimantId = ResolveClaimantId(agentContext, generate: current is null);
        if (current is not null)
        {
            if (current.Claim.WorkerIdentity != worker) return Result(current.Claim, ClaimOutcome.HeldByOther, false);
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
                : Result(resolved.Claim, resolved.Claim.WorkerIdentity == worker ? ClaimOutcome.HeldByLocalClaimant : ClaimOutcome.HeldByOther,
                    resolved.Claim.WorkerIdentity == worker);
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
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (current.Claim.WorkerIdentity != worker) throw Error("CLAIM_NOT_OWNER", id, current.Claim, false);
        var claimantId = ResolveClaimantId(claimantContext, generate: true);
        if (current.Claim.ClaimantId == claimantId && currentClaimToken == current.Claim.ClaimToken)
            return Result(current.Claim, ClaimOutcome.AlreadyOwned, true);
        var claim = NewEvent("takenOver", worker, claimantId, claimantContext, clock.UtcNow, config,
            current.Claim.ClaimToken) with
        {
            AgentType = claimantContext.AgentType ?? current.Claim.AgentType,
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
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (current.Claim.WorkerIdentity != worker)
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
            AgentType = claimHandle.Claimant.AgentType ?? current.Claim.AgentType,
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
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (current.Claim.WorkerIdentity != worker) throw Error("CLAIM_NOT_OWNER", id, current.Claim, false);
        if (!overrideClaimant) await ValidateAsync(config, id, claimHandle, cancellationToken);
        await RecordSessionAsync(id, current.Claim, cancellationToken);
        var kind = overrideClaimant ? "overrideReleased" : "released";
        var release = NewEvent(kind, worker, current.Claim.ClaimantId,
            claimHandle.Claimant, clock.UtcNow, config, current.Claim.ClaimToken);
        await CreateAsync(config, issue, release, cancellationToken);
        var after = await ResolvedAsync(config, issue, id, cancellationToken);
        if (after is not null) throw Error("CLAIM_STALE", id, after.Claim, after.Claim.WorkerIdentity == worker);
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
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (current.Claim.WorkerIdentity != worker)
            throw Error("CLAIM_NOT_OWNER", id, current.Claim, false);
        await ValidateAsync(config, id, claimHandle, cancellationToken);
        if (string.IsNullOrWhiteSpace(current.Claim.AgentType) ||
            string.IsNullOrWhiteSpace(current.Claim.SessionId) ||
            string.IsNullOrWhiteSpace(current.Claim.WorkspacePath))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' does not have a complete agent session to queue.",
                5);

        var now = clock.UtcNow;
        var requeued = current.Claim with
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "requeued",
            PreviousClaimToken = current.Claim.ClaimToken,
            ClaimToken = Guid.NewGuid().ToString("N"),
            ClaimedAt = now,
            ExpiresAt = now.AddMinutes(config.LeaseMinutes)
        };
        await CreateAsync(config, issue, requeued, cancellationToken);
        await RecordSessionAsync(id, requeued, cancellationToken);
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
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (current.Claim.WorkerIdentity != worker) throw Error("CLAIM_HELD", id, current.Claim, false);
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
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var active = ClaimResolver.Resolve(data.Events, clock.UtcNow);
        var ownership = active is null
            ? new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed)
            : Ownership(active.Claim, worker);
        var latest = ClaimResolver.ResolveLatestGeneration(data.Events);
        var cached = sessionCache is null
            ? null
            : await sessionCache.GetAsync(id.Value, cancellationToken);
        return new ClaimStateReading(ownership, Session(latest, cached, worker));
    }

    private static ClaimOwnershipResult Ownership(ClaimRecord claim, string worker)
    {
        var local = claim.WorkerIdentity == worker;
        return new ClaimOwnershipResult(
            local ? ClaimOwnershipState.OwnedByCurrent : ClaimOwnershipState.HeldByOther,
            claim.WorkerIdentity, claim.ExpiresAt, claim.ClaimantId,
            claim.AgentType, claim.SessionId, claim.ClaimantKind, local,
            claim.WorkspacePath);
    }

    private static AgentSessionRecord? Session(
        ClaimEvent? latest,
        Caching.CachedSessionRecord? cached,
        string worker)
    {
        if (latest is not null && (HasAddress(latest.Claim) || cached is null))
        {
            // The branch and run outcome are machine-local metadata that never travel through claim
            // comments; attach the cached ones only when they belong to the same recorded session.
            var sameSession = cached is not null &&
                string.Equals(cached.SessionId, latest.Claim.SessionId, StringComparison.Ordinal);
            return new AgentSessionRecord(
                latest.Claim.AgentType,
                latest.Claim.SessionId,
                // When shareLocalPaths=false the claim marker carries no path; fall back to the
                // machine-local cache so the recording host can still resume. Another installation
                // has no cache, so it correctly sees no path and cannot resume.
                latest.Claim.WorkspacePath ?? (sameSession ? cached!.WorkspacePath : null),
                latest.Claim.ExpiresAt,
                string.Equals(latest.Claim.WorkerIdentity, worker, StringComparison.Ordinal),
                sameSession ? cached!.Branch : null,
                sameSession ? cached!.Outcome : null,
                sameSession ? cached!.FinalMessage : null,
                sameSession ? cached!.EndedAt : null,
                sameSession ? cached!.Failure : null,
                sameSession ? cached!.Dispatch?.ToInfo(true) : null);
        }

        if (cached is null)
            return null;
        return new AgentSessionRecord(
            cached.AgentType,
            cached.SessionId,
            cached.WorkspacePath,
            cached.LastClaimExpiresAt ?? cached.UpdatedAt,
            FromCurrentInstallation: true,
            Branch: cached.Branch,
            Outcome: cached.Outcome,
            FinalMessage: cached.FinalMessage,
            EndedAt: cached.EndedAt,
            Failure: cached.Failure,
            Dispatch: cached.Dispatch?.ToInfo(true));
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
        var legacy = false;
        foreach (var page in document.RootElement.EnumerateArray())
            foreach (var comment in page.EnumerateArray())
            {
                var body = comment.GetProperty("body").GetString() ?? "";
                legacy |= ClaimMarker.HasActiveLegacyClaim(body, clock.UtcNow);
                if (ClaimMarker.TryParse(body, out var claim))
                    events.Add(new ClaimEvent(comment.GetProperty("id").GetInt64(), comment.GetProperty("created_at").GetDateTimeOffset(), claim));
            }
        return new EventData(events, legacy);
    }

    private async Task CreateAsync(TrackerConfig config, int issue, ClaimRecord claim, CancellationToken token)
    {
        // The claim marker is published to the (possibly public) issue. When shareLocalPaths=false,
        // redact the absolute workspace path from what is serialized; the real path stays in the
        // machine-local session cache, which is authoritative for resume on the recording host.
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
        var existing = await FindHandoverCommentAsync(config, issue, cancellationToken);
        if (existing is { } commentId)
        {
            await EditCommentAsync(config, commentId, body, cancellationToken);
            return;
        }

        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments";
        using var ignored = await api.SendJsonAsync(
            config.GitHubHost, "POST", endpoint, new { body }, cancellationToken);
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
            if (data.ActiveLegacy || ClaimResolver.Resolve(data.Events, clock.UtcNow) is not null) return;
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
        new(2, Guid.NewGuid().ToString("N"), worker, now, now.AddMinutes(config.LeaseMinutes), type,
            claimantId, Guid.NewGuid().ToString("N"), previous, context.AgentType, context.SessionId,
            ClaimantKinds.ToStorageValue(context.EffectiveClaimantKind));

    private static ClaimResult Result(ClaimRecord claim, ClaimOutcome outcome, bool takeover) =>
        new(outcome, claim.WorkerIdentity, claim.ExpiresAt, claim.EventId,
            claim.AgentType, claim.SessionId, claim.ClaimantKind, claim.ClaimantId,
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
        if (data.ActiveLegacy) throw new TrackerException("CLAIM_FORMAT_UNSUPPORTED",
            $"Work item '{id}' has an active v1 claim. Release active claims with the previous Wrighty version before upgrading; do not mix versions.", 6);
    }

    private static TrackerException Error(string code, WorkItemId id, ClaimRecord claim, bool local) =>
        new(code, $"Claim handle for work item '{id}' is not current (claimant {Short(claim.ClaimantId)}).", 6,
            new Dictionary<string, object?>
            {
                ["id"] = id.Value,
                ["workerIdentity"] = claim.WorkerIdentity,
                ["claimantId"] = Short(claim.ClaimantId),
                ["claimantKind"] = claim.ClaimantKind,
                ["agentType"] = claim.AgentType,
                ["expiresAt"] = claim.ExpiresAt,
                ["sameInstallation"] = local,
                ["takeoverAvailable"] = local
            });
    private static string Short(string value) => value.Length <= 12 ? value : $"{value[..12]}…";
    private sealed record EventData(IReadOnlyList<ClaimEvent> Events, bool ActiveLegacy);
}
