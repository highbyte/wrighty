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
        CancellationToken cancellationToken)
    {
        if (sessionCache is null || !HasAddress(claim))
        {
            return;
        }

        await sessionCache.PutAsync(
            id.Value,
            new Caching.CachedSessionRecord(
                claim.AgentType,
                claim.SessionId,
                claim.WorkspacePath,
                clock.UtcNow,
                claim.ExpiresAt),
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

    public async Task<ClaimResult> RenewAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle claimHandle,
        string? workspacePath,
        string? sessionId,
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
        await RecordSessionAsync(id, winner.Claim, cancellationToken);
        return Result(winner.Claim, ClaimOutcome.AlreadyOwned, true);
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

    public async Task<ClaimOwnershipResult> GetOwnershipAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var current = await ResolvedAsync(config, issue, id, cancellationToken);
        if (current is null) return new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var local = current.Claim.WorkerIdentity == worker;
        return new ClaimOwnershipResult(local ? ClaimOwnershipState.OwnedByCurrent : ClaimOwnershipState.HeldByOther,
            current.Claim.WorkerIdentity, current.Claim.ExpiresAt, current.Claim.ClaimantId,
            current.Claim.AgentType, current.Claim.SessionId, current.Claim.ClaimantKind, local,
            current.Claim.WorkspacePath);
    }

    public async Task<AgentSessionRecord?> GetAgentSessionAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var issue = resolver.Decode(id, config).IssueNumber;
        var data = await EventsAsync(config, issue, cancellationToken);
        EnsureNoLegacy(data, id);
        var current = ClaimResolver.ResolveLatestGeneration(data.Events);
        var cached = sessionCache is null
            ? null
            : await sessionCache.GetAsync(id.Value, cancellationToken);
        if (current is not null && (HasAddress(current.Claim) || cached is null))
        {
            var worker = await identityProvider.GetIdentityAsync(cancellationToken);
            return new AgentSessionRecord(
                current.Claim.AgentType,
                current.Claim.SessionId,
                current.Claim.WorkspacePath,
                current.Claim.ExpiresAt,
                string.Equals(current.Claim.WorkerIdentity, worker, StringComparison.Ordinal));
        }

        if (cached is null)
            return null;
        return new AgentSessionRecord(
            cached.AgentType,
            cached.SessionId,
            cached.WorkspacePath,
            cached.LastClaimExpiresAt ?? cached.UpdatedAt,
            FromCurrentInstallation: true);
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
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issue}/comments";
        using var ignored = await api.SendJsonAsync(config.GitHubHost, "POST", endpoint, new { body = ClaimMarker.Format(claim) }, token);
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
