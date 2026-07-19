using System.Text.Json;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Claims;

public sealed class GitHubClaimServiceTests
{
    private static readonly WorkItemId ItemId = new("github:owner/repo#42");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1,
        LeaseMinutes = 60
    };

    [Fact]
    public async Task Claim_then_release_round_trips_through_issue_comments()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");

        var context = new AgentExecutionContext(
            "codex",
            "session-123456789",
            AgentContextSource.ExplicitOption,
            ClaimantId: "codex:session-123456789");
        var claim = await service.TryClaimAsync(Config, ItemId, context, CancellationToken.None);
        var ownedBeforeRelease = await service.IsOwnedByCurrentWorkerAsync(
            Config,
            ItemId,
            CancellationToken.None);
        await service.ReleaseAsync(Config, ItemId, new ClaimHandle(context, claim.ClaimToken), false, CancellationToken.None);
        var ownedAfterRelease = await service.IsOwnedByCurrentWorkerAsync(
            Config,
            ItemId,
            CancellationToken.None);

        Assert.Equal(ClaimOutcome.Acquired, claim.Outcome);
        Assert.Equal("worker-a", claim.WorkerIdentity);
        Assert.Equal("codex", claim.AgentType);
        Assert.Equal("session-123456789", claim.SessionId);
        Assert.Equal("agent", claim.ClaimantKind);
        Assert.True(ownedBeforeRelease);
        Assert.False(ownedAfterRelease);
        Assert.Equal(2, process.Comments.Count);
        Assert.True(ClaimMarker.TryParse(process.Comments[^1].Body, out var stored));
        Assert.Equal("released", stored.State);
        Assert.Equal("codex", stored.AgentType);
        Assert.Equal("session-123456789", stored.SessionId);
        Assert.Equal("agent", stored.ClaimantKind);
    }

    [Fact]
    public async Task A_second_agent_observes_the_existing_claim_without_writing()
    {
        var process = new InMemoryCommentsProcess(Now);
        var first = CreateService(process, "worker-a");
        var second = CreateService(process, "worker-b");
        await first.TryClaimAsync(Config, ItemId, AgentExecutionContext.None, CancellationToken.None);

        var result = await second.TryClaimAsync(
            Config,
            ItemId,
            AgentExecutionContext.None,
            CancellationToken.None);

        Assert.Equal(ClaimOutcome.HeldByOther, result.Outcome);
        Assert.Equal("worker-a", result.WorkerIdentity);
        Assert.Single(process.Comments);
    }

    [Fact]
    public async Task Ownership_query_reports_the_winning_other_worker_and_expiry()
    {
        var process = new InMemoryCommentsProcess(Now);
        var first = CreateService(process, "worker-a");
        var second = CreateService(process, "worker-b");
        await first.TryClaimAsync(Config, ItemId, AgentExecutionContext.None, CancellationToken.None);

        var ownership = await second.GetOwnershipAsync(
            Config,
            ItemId,
            CancellationToken.None);

        Assert.Equal(ClaimOwnershipState.HeldByOther, ownership.State);
        Assert.Equal("worker-a", ownership.WorkerIdentity);
        Assert.Equal(Now.AddMinutes(60), ownership.ExpiresAt);
    }

    [Fact]
    public async Task Inactive_history_is_bounded_without_touching_an_active_chain()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");
        var config = Config with { ClaimHistoryLimit = 2 };

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var context = AgentExecutionContext.Human;
            var claim = await service.TryClaimAsync(
                config,
                ItemId,
                context,
                CancellationToken.None);
            await service.ReleaseAsync(config, ItemId, new ClaimHandle(context, claim.ClaimToken), false, CancellationToken.None);
        }

        Assert.Equal(2, process.Comments.Count);
        Assert.All(
            process.Comments,
            comment =>
            {
                Assert.True(ClaimMarker.TryParse(comment.Body, out var claim));
                Assert.Contains(claim.EventType, new[] { "acquired", "released" });
            });
    }

    [Fact]
    public async Task Zero_history_limit_removes_inactive_events()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");
        var config = Config with { ClaimHistoryLimit = 0 };

        var context = AgentExecutionContext.Human;
        var claim = await service.TryClaimAsync(
            config,
            ItemId,
            context,
            CancellationToken.None);
        await service.ReleaseAsync(config, ItemId, new ClaimHandle(context, claim.ClaimToken), false, CancellationToken.None);

        Assert.Empty(process.Comments);
    }

    [Fact]
    public async Task Takeover_rotates_token_and_old_handle_is_stale()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");
        var agent = new AgentExecutionContext("codex", "one", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "codex:one");
        var first = await service.TryClaimAsync(Config, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(Config, ItemId, new ClaimHandle(agent, first.ClaimToken),
            "/tmp/resumable", "one", CancellationToken.None);
        var human = AgentExecutionContext.Human with { ClaimantId = "human:web" };

        var takeover = await service.TakeoverAsync(Config, ItemId, human, null, CancellationToken.None);

        Assert.Equal(ClaimOutcome.TakenOver, takeover.Outcome);
        Assert.NotEqual(first.ClaimToken, takeover.ClaimToken);
        Assert.Equal("/tmp/resumable", takeover.WorkspacePath);
        Assert.Equal("one", takeover.SessionId);
        Assert.Equal("codex", takeover.AgentType);
        var stale = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(() =>
            service.ValidateAsync(Config, ItemId, new ClaimHandle(agent, first.ClaimToken), CancellationToken.None));
        Assert.Equal("CLAIM_STALE", stale.Code);
    }

    [Fact]
    public async Task Expired_claim_retains_recoverable_agent_session_without_active_ownership()
    {
        var process = new InMemoryCommentsProcess(Now);
        var active = CreateService(process, "worker-a");
        var agent = new AgentExecutionContext(
            "claude",
            "session-old",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:old");
        var claim = await active.TryClaimAsync(
            Config, ItemId, agent, CancellationToken.None);
        await active.RenewAsync(
            Config,
            ItemId,
            new ClaimHandle(agent, claim.ClaimToken),
            "/tmp/old-workspace",
            "session-old",
            CancellationToken.None);
        var expired = new GitHubClaimService(
            new GhApi(process),
            new FixedIdentity("worker-a"),
            new FixedClock(Now.AddHours(2)),
            new GitHubWorkItemAddressResolver());

        var ownership = await expired.GetOwnershipAsync(
            Config, ItemId, CancellationToken.None);
        var session = await expired.GetAgentSessionAsync(
            Config, ItemId, CancellationToken.None);

        Assert.Equal(ClaimOwnershipState.Unclaimed, ownership.State);
        Assert.True(session?.IsComplete);
        Assert.Equal("claude", session?.AgentType);
        Assert.Equal("session-old", session?.SessionId);
        Assert.Equal("/tmp/old-workspace", session?.WorkspacePath);
        Assert.True(session?.FromCurrentInstallation);

        var remote = new GitHubClaimService(
            new GhApi(process),
            new FixedIdentity("worker-b"),
            new FixedClock(Now.AddHours(2)),
            new GitHubWorkItemAddressResolver());
        Assert.False((await remote.GetAgentSessionAsync(
            Config, ItemId, CancellationToken.None))?.FromCurrentInstallation);
    }

    [Fact]
    public async Task Active_v1_claim_blocks_v2_acquisition()
    {
        var process = new InMemoryCommentsProcess(Now);
        process.Comments.Add(new Comment(100, Now,
            $"{ClaimMarker.LegacyPrefix}\n{{\"version\":1,\"state\":\"active\",\"expiresAt\":\"{Now.AddHours(1):O}\"}}\n-->"));
        var service = CreateService(process, "worker-a");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(() =>
            service.TryClaimAsync(Config, ItemId, AgentExecutionContext.Human, CancellationToken.None));

        Assert.Equal("CLAIM_FORMAT_UNSUPPORTED", exception.Code);
    }

    private static GitHubClaimService CreateService(
        InMemoryCommentsProcess process,
        string identity)
    {
        return new GitHubClaimService(
            new GhApi(process),
            new FixedIdentity(identity),
            new FixedClock(Now),
            new GitHubWorkItemAddressResolver());
    }

    private sealed class FixedIdentity(string identity) : IWorkerIdentityProvider
    {
        public Task<string> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(identity);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class InMemoryCommentsProcess(DateTimeOffset now) : IGhProcess
    {
        private long nextId = 1;

        public List<Comment> Comments { get; } = [];

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            if (arguments.Contains("--paginate"))
            {
                return JsonAsync(new[]
                {
                    Comments.Select(comment => new
                    {
                        id = comment.Id,
                        created_at = comment.CreatedAt,
                        body = comment.Body
                    })
                });
            }

            var methodIndex = arguments.IndexOf("--method");
            var method = methodIndex >= 0 ? arguments[methodIndex + 1] : "GET";
            if (method == "POST")
            {
                using var input = JsonDocument.Parse(standardInput!);
                var comment = new Comment(
                    nextId++,
                    now.AddMilliseconds(nextId),
                    input.RootElement.GetProperty("body").GetString()!);
                Comments.Add(comment);
                return JsonAsync(new { id = comment.Id });
            }

            var endpoint = arguments[^1];
            var commentId = long.Parse(endpoint.Split('/')[^1]);
            var existing = Comments.Single(comment => comment.Id == commentId);
            if (method == "PATCH")
            {
                using var input = JsonDocument.Parse(standardInput!);
                existing.Body = input.RootElement.GetProperty("body").GetString()!;
                return JsonAsync(new { id = existing.Id });
            }

            if (method == "DELETE")
            {
                Comments.Remove(existing);
                return Task.FromResult(new GhProcessResult(0, string.Empty, string.Empty));
            }

            throw new InvalidOperationException($"Unexpected gh call: {string.Join(' ', arguments)}");
        }

        private static Task<GhProcessResult> JsonAsync(object value) =>
            Task.FromResult(new GhProcessResult(0, JsonSerializer.Serialize(value), string.Empty));
    }

    public sealed class Comment(long id, DateTimeOffset createdAt, string body)
    {
        public long Id { get; } = id;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public string Body { get; set; } = body;
    }
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> items, T value)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(items[index], value))
            {
                return index;
            }
        }

        return -1;
    }
}
