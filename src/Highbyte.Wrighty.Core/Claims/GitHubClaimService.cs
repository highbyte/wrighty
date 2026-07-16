using System.Text.Json;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Claims;

public sealed class GitHubClaimService(
    GhApi api,
    IWorkerIdentityProvider identityProvider,
    IClock clock,
    GitHubWorkItemAddressResolver resolver) : IClaimService
{
    public async Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken)
    {
        var issueNumber = resolver.Decode(id, config).IssueNumber;
        var workerIdentity = await identityProvider.GetIdentityAsync(cancellationToken);
        var current = ClaimResolver.Resolve(
            await GetEventsAsync(config, issueNumber, cancellationToken),
            clock.UtcNow);

        if (current is not null)
        {
            return new ClaimResult(
                current.Claim.WorkerIdentity == workerIdentity
                    ? ClaimOutcome.AlreadyOwned
                    : ClaimOutcome.HeldByOther,
                current.Claim.WorkerIdentity,
                current.Claim.ExpiresAt,
                current.Claim.ClaimAttemptId,
                current.Claim.AgentType,
                current.Claim.SessionId,
                current.Claim.ClaimantKind);
        }

        var now = clock.UtcNow;
        var claimAttemptId = Guid.NewGuid().ToString("N");
        var claim = new ClaimRecord(
            1,
            claimAttemptId,
            workerIdentity,
            now,
            now.AddMinutes(config.LeaseMinutes),
            "active",
            agentContext.AgentType,
            agentContext.SessionId,
            ClaimantKinds.ToStorageValue(agentContext.EffectiveClaimantKind));
        var commentId = await CreateCommentAsync(
            config,
            issueNumber,
            ClaimMarker.Format(claim),
            cancellationToken);

        var winner = ClaimResolver.Resolve(
            await GetEventsAsync(config, issueNumber, cancellationToken),
            clock.UtcNow);

        if (winner?.Claim.ClaimAttemptId == claimAttemptId)
        {
            await TryCleanupHistoryAsync(config, issueNumber, cancellationToken);
            return new ClaimResult(
                ClaimOutcome.Acquired,
                workerIdentity,
                claim.ExpiresAt,
                claimAttemptId,
                claim.AgentType,
                claim.SessionId,
                claim.ClaimantKind);
        }

        try
        {
            await DeleteCommentAsync(config, commentId, cancellationToken);
        }
        catch (TrackerException)
        {
            // The losing claim is harmless because resolution always chooses the earliest event.
        }

        if (winner is null)
        {
            throw new TrackerException(
                "CLAIM_PROTOCOL_ERROR",
                "The claim comment was created but no winning claim could be resolved.");
        }

        return new ClaimResult(
            ClaimOutcome.HeldByOther,
            winner.Claim.WorkerIdentity,
            winner.Claim.ExpiresAt,
            winner.Claim.ClaimAttemptId,
            winner.Claim.AgentType,
            winner.Claim.SessionId,
            winner.Claim.ClaimantKind);
    }

    public async Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var issueNumber = resolver.Decode(id, config).IssueNumber;
        var workerIdentity = await identityProvider.GetIdentityAsync(cancellationToken);
        var current = ClaimResolver.Resolve(
            await GetEventsAsync(config, issueNumber, cancellationToken),
            clock.UtcNow);

        if (current is null)
        {
            throw new TrackerException(
                "CLAIM_NOT_FOUND",
                $"Issue #{issueNumber} does not have an active claim.",
                5);
        }

        if (current.Claim.WorkerIdentity != workerIdentity)
        {
            throw new TrackerException(
                "CLAIM_NOT_OWNER",
                $"Issue #{issueNumber} is claimed by worker {current.Claim.WorkerIdentity}.",
                7,
                new Dictionary<string, object?>
                {
                    ["workerIdentity"] = current.Claim.WorkerIdentity,
                    ["expiresAt"] = current.Claim.ExpiresAt
                });
        }

        await UpdateCommentAsync(
            config,
            current.CommentId,
            ClaimMarker.Format(current.Claim with { State = "released" }),
            cancellationToken);

        await TryCleanupHistoryAsync(config, issueNumber, cancellationToken);
    }

    public async Task<bool> IsOwnedByCurrentWorkerAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        return (await GetOwnershipAsync(config, id, cancellationToken)).State ==
               ClaimOwnershipState.OwnedByCurrent;
    }

    public async Task<ClaimOwnershipResult> GetOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var issueNumber = resolver.Decode(id, config).IssueNumber;
        var workerIdentity = await identityProvider.GetIdentityAsync(cancellationToken);
        var current = ClaimResolver.Resolve(
            await GetEventsAsync(config, issueNumber, cancellationToken),
            clock.UtcNow);
        return current is null
            ? new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed)
            : current.Claim.WorkerIdentity == workerIdentity
                ? new ClaimOwnershipResult(
                    ClaimOwnershipState.OwnedByCurrent,
                    current.Claim.WorkerIdentity,
                    current.Claim.ExpiresAt)
                : new ClaimOwnershipResult(
                    ClaimOwnershipState.HeldByOther,
                    current.Claim.WorkerIdentity,
                    current.Claim.ExpiresAt);
    }

    private async Task<IReadOnlyList<ClaimEvent>> GetEventsAsync(
        TrackerConfig config,
        int issueNumber,
        CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issueNumber}/comments?per_page=100";
        using var document = await api.GetPaginatedAsync(
            config.GitHubHost,
            endpoint,
            cancellationToken);

        var events = new List<ClaimEvent>();
        foreach (var page in document.RootElement.EnumerateArray())
        {
            foreach (var comment in page.EnumerateArray())
            {
                var body = comment.GetProperty("body").GetString() ?? string.Empty;
                if (!ClaimMarker.TryParse(body, out var claim))
                {
                    continue;
                }

                events.Add(new ClaimEvent(
                    comment.GetProperty("id").GetInt64(),
                    comment.GetProperty("created_at").GetDateTimeOffset(),
                    claim));
            }
        }

        return events;
    }

    private async Task<long> CreateCommentAsync(
        TrackerConfig config,
        int issueNumber,
        string body,
        CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issueNumber}/comments";
        using var document = await api.SendJsonAsync(
            config.GitHubHost,
            "POST",
            endpoint,
            new { body },
            cancellationToken);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private async Task UpdateCommentAsync(
        TrackerConfig config,
        long commentId,
        string body,
        CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/comments/{commentId}";
        using var document = await api.SendJsonAsync(
            config.GitHubHost,
            "PATCH",
            endpoint,
            new { body },
            cancellationToken);
    }

    private Task DeleteCommentAsync(
        TrackerConfig config,
        long commentId,
        CancellationToken cancellationToken)
    {
        var endpoint = $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/comments/{commentId}";
        return api.DeleteAsync(config.GitHubHost, endpoint, cancellationToken);
    }

    private async Task TryCleanupHistoryAsync(
        TrackerConfig config,
        int issueNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.UtcNow;
            var inactive = (await GetEventsAsync(config, issueNumber, cancellationToken))
                .Where(item => item.Claim.State != "active" || item.Claim.ExpiresAt <= now)
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.CommentId)
                .Skip(config.ClaimHistoryLimit);

            foreach (var item in inactive)
            {
                await DeleteCommentAsync(config, item.CommentId, cancellationToken);
            }
        }
        catch (TrackerException)
        {
            // Claim history retention is housekeeping and must not fail the claim operation.
        }
    }

}
