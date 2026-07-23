using System.Text.Json;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

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
        var first = await service.TryClaimAsync(SharedConfig, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(SharedConfig, ItemId, new ClaimHandle(agent, first.ClaimToken),
            "/tmp/resumable", "one", CancellationToken.None);
        var human = AgentExecutionContext.Human with { ClaimantId = "human:web" };

        var takeover = await service.TakeoverAsync(SharedConfig, ItemId, human, null, CancellationToken.None);

        Assert.Equal(ClaimOutcome.TakenOver, takeover.Outcome);
        Assert.NotEqual(first.ClaimToken, takeover.ClaimToken);
        Assert.Equal("/tmp/resumable", takeover.WorkspacePath);
        Assert.Equal("one", takeover.SessionId);
        Assert.Equal("codex", takeover.AgentType);
        var stale = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(() =>
            service.ValidateAsync(SharedConfig, ItemId, new ClaimHandle(agent, first.ClaimToken), CancellationToken.None));
        Assert.Equal("CLAIM_STALE", stale.Code);
    }

    [Fact]
    public async Task Consecutive_renewals_collapse_to_the_latest_on_the_active_chain()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");
        var agent = new AgentExecutionContext("codex", "sess", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "codex:sess");
        var claim = await service.TryClaimAsync(Config, ItemId, agent, CancellationToken.None);
        var handle = new ClaimHandle(agent, claim.ClaimToken);

        // The worker renews several times in quick succession (pre-spawn, session capture, keep-alive).
        await service.RenewAsync(Config, ItemId, handle, "/tmp/ws", "sess", CancellationToken.None);
        await service.RenewAsync(Config, ItemId, handle, "/tmp/ws", "sess", CancellationToken.None);
        await service.RenewAsync(Config, ItemId, handle, "/tmp/ws", "sess", CancellationToken.None);

        // Only the acquisition and a single (latest) renewal remain — earlier renewals are collapsed.
        var renewals = process.Comments.Count(comment =>
            ClaimMarker.TryParse(comment.Body, out var parsed) && parsed.EventType == "renewed");
        Assert.Equal(1, renewals);
        // Ownership is unaffected: the surviving renewal still resolves as the active claim.
        Assert.True(await service.IsOwnedByCurrentWorkerAsync(Config, ItemId, CancellationToken.None));
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
            SharedConfig, ItemId, agent, CancellationToken.None);
        await active.RenewAsync(
            SharedConfig,
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
            SharedConfig, ItemId, CancellationToken.None);
        var session = await expired.GetAgentSessionAsync(
            SharedConfig, ItemId, CancellationToken.None);

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
            SharedConfig, ItemId, CancellationToken.None))?.FromCurrentInstallation);
    }

    [Fact]
    public async Task Requeue_ends_ownership_preserves_session_and_allows_new_agent_claim()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");
        var agent = new AgentExecutionContext(
            "claude",
            "session-queued",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:old");
        var acquired = await service.TryClaimAsync(
            SharedConfig, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(
            SharedConfig,
            ItemId,
            new ClaimHandle(agent, acquired.ClaimToken),
            "/tmp/queued-workspace",
            "session-queued",
            CancellationToken.None);
        var humanContext = AgentExecutionContext.Human with { ClaimantId = "human:web" };
        var human = await service.TakeoverAsync(
            SharedConfig, ItemId, humanContext, acquired.ClaimToken, CancellationToken.None);

        await service.RequeueAsync(
            SharedConfig,
            ItemId,
            new ClaimHandle(humanContext, human.ClaimToken),
            CancellationToken.None);

        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await service.GetOwnershipAsync(
                SharedConfig, ItemId, CancellationToken.None)).State);
        var session = await service.GetAgentSessionAsync(
            SharedConfig, ItemId, CancellationToken.None);
        Assert.Equal("session-queued", session?.SessionId);
        Assert.Equal("/tmp/queued-workspace", session?.WorkspacePath);
        Assert.True(ClaimMarker.TryParse(process.Comments[^1].Body, out var requeued));
        Assert.Equal("requeued", requeued.EventType);
        Assert.Equal(human.ClaimToken, requeued.PreviousClaimToken);
        Assert.NotEqual(human.ClaimToken, requeued.ClaimToken);

        var resumedContext = agent with { ClaimantId = "agent:new" };
        var resumed = await service.TryClaimAsync(
            SharedConfig, ItemId, resumedContext, CancellationToken.None);
        Assert.Equal(ClaimOutcome.Acquired, resumed.Outcome);
        Assert.NotEqual(human.ClaimToken, resumed.ClaimToken);
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

    [Fact]
    public async Task Release_preserves_recorded_session_in_machine_local_cache()
    {
        var process = new InMemoryCommentsProcess(Now);
        var cache = new InMemorySessionCache();
        var service = new GitHubClaimService(
            new GhApi(process),
            new FixedIdentity("worker-a"),
            new FixedClock(Now),
            new GitHubWorkItemAddressResolver(),
            cache);
        var agent = new AgentExecutionContext(
            "claude",
            "session-kept",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:one");
        var claim = await service.TryClaimAsync(Config, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(
            Config,
            ItemId,
            new ClaimHandle(agent, claim.ClaimToken),
            "/tmp/kept-workspace",
            "session-kept",
            CancellationToken.None);

        await service.ReleaseAsync(
            Config, ItemId, new ClaimHandle(agent, claim.ClaimToken), false, CancellationToken.None);

        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await service.GetOwnershipAsync(Config, ItemId, CancellationToken.None)).State);
        var session = await service.GetAgentSessionAsync(Config, ItemId, CancellationToken.None);
        Assert.True(session?.IsComplete);
        Assert.Equal("session-kept", session?.SessionId);
        Assert.Equal("/tmp/kept-workspace", session?.WorkspacePath);
        Assert.True(session?.FromCurrentInstallation);
    }

    private sealed class InMemorySessionCache : Highbyte.Wrighty.Caching.ISessionRecordCache
    {
        private readonly Dictionary<string, Highbyte.Wrighty.Caching.CachedSessionRecord> entries =
            new(StringComparer.Ordinal);

        public Task<Highbyte.Wrighty.Caching.CachedSessionRecord?> GetAsync(
            string key, CancellationToken cancellationToken) =>
            Task.FromResult(entries.GetValueOrDefault(key));

        public Task PutAsync(
            string key,
            Highbyte.Wrighty.Caching.CachedSessionRecord value,
            CancellationToken cancellationToken)
        {
            entries[key] = value;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Claim_state_reading_fetches_the_comment_chain_once()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");
        var agent = new AgentExecutionContext(
            "claude",
            "session-combined",
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:combined");
        var claim = await service.TryClaimAsync(SharedConfig, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(
            SharedConfig,
            ItemId,
            new ClaimHandle(agent, claim.ClaimToken),
            "/tmp/combined-workspace",
            "session-combined",
            CancellationToken.None);
        process.FetchCount = 0;

        var reading = await service.GetClaimStateAsync(SharedConfig, ItemId, CancellationToken.None);

        Assert.Equal(1, process.FetchCount);
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, reading.Ownership.State);
        Assert.Equal("agent:combined", reading.Ownership.ClaimantId);
        Assert.True(reading.Session?.IsComplete);
        Assert.Equal("session-combined", reading.Session?.SessionId);
        Assert.Equal("/tmp/combined-workspace", reading.Session?.WorkspacePath);
        Assert.True(reading.Session?.FromCurrentInstallation);
    }

    [Fact]
    public async Task Handover_comment_is_created_once_and_edited_in_place()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");

        await service.PostHandoverAsync(Config, Handover(HandoverPhase.NeedsAttention),
            CancellationToken.None);
        await service.PostHandoverAsync(Config, Handover(HandoverPhase.Completed),
            CancellationToken.None);

        var handovers = process.Comments
            .Where(comment => Highbyte.Wrighty.Workers.HandoverRenderer.IsHandover(comment.Body))
            .ToArray();
        Assert.Single(handovers);
        Assert.Contains("completed", handovers[0].Body);
        Assert.Contains("`session-host`", handovers[0].Body);
        Assert.Contains("/tmp/kept", handovers[0].Body);
    }

    [Fact]
    public async Task Minimal_handover_omits_host_and_workspace_but_keeps_the_branch()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");

        await service.PostHandoverAsync(
            Config,
            Handover(HandoverPhase.NeedsAttention) with
            {
                Visibility = Highbyte.Wrighty.Configuration.HandoverCommentMode.Minimal
            },
            CancellationToken.None);

        var body = process.Comments.Single(
            comment => Highbyte.Wrighty.Workers.HandoverRenderer.IsHandover(comment.Body)).Body;
        Assert.DoesNotContain("session-host", body);
        Assert.DoesNotContain("/tmp/kept", body);
        Assert.Contains("feature/x", body);
    }

    [Fact]
    public async Task Resolve_handover_trims_the_comment_to_the_resolved_form()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");
        await service.PostHandoverAsync(Config, Handover(HandoverPhase.NeedsAttention),
            CancellationToken.None);

        await service.ResolveHandoverAsync(
            Config, ItemId, "The session was requeued.", CancellationToken.None);

        var body = process.Comments.Single(
            comment => Highbyte.Wrighty.Workers.HandoverRenderer.IsHandover(comment.Body)).Body;
        Assert.Contains("resolved", body);
        Assert.Contains("The session was requeued.", body);
        Assert.DoesNotContain("Next actions", body);
    }

    [Fact]
    public async Task Resolve_handover_is_a_no_op_when_none_was_posted()
    {
        var process = new InMemoryCommentsProcess(Now);
        var service = CreateService(process, "worker-a");

        await service.ResolveHandoverAsync(
            Config, ItemId, "Archived.", CancellationToken.None);

        Assert.Empty(process.Comments);
    }

    private static TrackerConfig RedactedConfig =>
        Config with { Worker = new WorkerConfig { ShareLocalPaths = false } };

    // Opt in to publishing local workspace paths to the claim marker (the non-default behavior).
    private static TrackerConfig SharedConfig =>
        Config with { Worker = new WorkerConfig { ShareLocalPaths = true } };

    [Fact]
    public async Task Redacted_claim_marker_omits_the_path_but_the_cache_keeps_it_for_resume()
    {
        var process = new InMemoryCommentsProcess(Now);
        var cache = new InMemorySessionCache();
        var service = new GitHubClaimService(
            new GhApi(process), new FixedIdentity("worker-a"), new FixedClock(Now),
            new GitHubWorkItemAddressResolver(), cache);
        var agent = new AgentExecutionContext(
            "claude", "session-red", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:one");
        var claim = await service.TryClaimAsync(RedactedConfig, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(
            RedactedConfig, ItemId, new ClaimHandle(agent, claim.ClaimToken),
            "/Users/secret/ws", "session-red", CancellationToken.None);

        // The published claim marker must not carry the absolute path...
        Assert.All(process.Comments, comment => Assert.DoesNotContain("/Users/secret/ws", comment.Body));
        // ...but the recording host resolves it from the machine-local cache, so resume still works.
        var session = await service.GetAgentSessionAsync(RedactedConfig, ItemId, CancellationToken.None);
        Assert.Equal("/Users/secret/ws", session!.WorkspacePath);
        Assert.True(session.IsComplete);
        Assert.True(session.FromCurrentInstallation);
    }

    [Fact]
    public async Task Redacted_claim_marker_hides_the_path_from_another_installation()
    {
        var process = new InMemoryCommentsProcess(Now);
        var recorder = new GitHubClaimService(
            new GhApi(process), new FixedIdentity("worker-a"), new FixedClock(Now),
            new GitHubWorkItemAddressResolver(), new InMemorySessionCache());
        var agent = new AgentExecutionContext(
            "claude", "session-red", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:one");
        var claim = await recorder.TryClaimAsync(RedactedConfig, ItemId, agent, CancellationToken.None);
        await recorder.RenewAsync(
            RedactedConfig, ItemId, new ClaimHandle(agent, claim.ClaimToken),
            "/Users/secret/ws", "session-red", CancellationToken.None);

        // A different installation has its own (empty) cache, so it sees no path and cannot resume.
        var other = new GitHubClaimService(
            new GhApi(process), new FixedIdentity("worker-b"), new FixedClock(Now),
            new GitHubWorkItemAddressResolver(), new InMemorySessionCache());
        var session = await other.GetAgentSessionAsync(RedactedConfig, ItemId, CancellationToken.None);
        Assert.Null(session!.WorkspacePath);
        Assert.False(session.FromCurrentInstallation);
    }

    [Fact]
    public async Task Default_omits_the_workspace_path_from_the_claim_marker()
    {
        var process = new InMemoryCommentsProcess(Now);
        var cache = new InMemorySessionCache();
        var service = new GitHubClaimService(
            new GhApi(process), new FixedIdentity("worker-a"), new FixedClock(Now),
            new GitHubWorkItemAddressResolver(), cache);
        var agent = new AgentExecutionContext(
            "claude", "session-open", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:one");
        var claim = await service.TryClaimAsync(Config, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(
            Config, ItemId, new ClaimHandle(agent, claim.ClaimToken),
            "/Users/shared/ws", "session-open", CancellationToken.None);

        // Privacy-preserving default: the absolute path is never published to the claim marker...
        Assert.All(process.Comments, comment => Assert.DoesNotContain("/Users/shared/ws", comment.Body));
        // ...but the recording host still resolves it from the machine-local cache for resume.
        var session = await service.GetAgentSessionAsync(Config, ItemId, CancellationToken.None);
        Assert.Equal("/Users/shared/ws", session!.WorkspacePath);
    }

    [Fact]
    public async Task Opting_in_with_shareLocalPaths_publishes_the_workspace_path()
    {
        var process = new InMemoryCommentsProcess(Now);
        var cache = new InMemorySessionCache();
        var service = new GitHubClaimService(
            new GhApi(process), new FixedIdentity("worker-a"), new FixedClock(Now),
            new GitHubWorkItemAddressResolver(), cache);
        var agent = new AgentExecutionContext(
            "claude", "session-open", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent, ClaimantId: "agent:one");
        var claim = await service.TryClaimAsync(SharedConfig, ItemId, agent, CancellationToken.None);
        await service.RenewAsync(
            SharedConfig, ItemId, new ClaimHandle(agent, claim.ClaimToken),
            "/Users/shared/ws", "session-open", CancellationToken.None);

        Assert.Contains(process.Comments, comment => comment.Body.Contains("/Users/shared/ws"));
    }

    private static Highbyte.Wrighty.Workers.HandoverContent Handover(
        Highbyte.Wrighty.Workers.HandoverPhase phase) =>
        new(
            ItemId,
            phase,
            RunOutcome.Succeeded,
            "The agent finished the change.",
            "session-host",
            "/tmp/kept",
            "feature/x",
            [new Highbyte.Wrighty.Workers.WorkerOperatorAction(
                "Open the recorded session",
                ["wrighty resume-command github:owner/repo#42"],
                "Resume where the agent left off.")],
            Highbyte.Wrighty.Configuration.HandoverCommentMode.Full);

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

        public int FetchCount { get; set; }

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            if (arguments.Contains("--paginate"))
            {
                FetchCount++;
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
