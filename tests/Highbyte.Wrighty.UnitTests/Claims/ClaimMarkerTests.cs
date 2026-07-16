using Highbyte.Wrighty.Claims;

namespace Highbyte.Wrighty.UnitTests.Claims;

public sealed class ClaimMarkerTests
{
    [Fact]
    public void Format_round_trips_a_valid_claim()
    {
        var claim = new ClaimRecord(
            1,
            "claim-attempt-1",
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            "active");

        var body = ClaimMarker.Format(claim);

        Assert.StartsWith(
            "_Wrighty: claimed by worker **worker-1** until 2026-07-13 11:00:00 UTC._",
            body);
        Assert.Contains("\"claimAttemptId\":\"claim-attempt-1\"", body);
        Assert.Contains("\"workerIdentity\":\"worker-1\"", body);
        Assert.Contains("\"claimantKind\":\"unknown\"", body);
        Assert.DoesNotContain("\"attempt\":", body);
        Assert.DoesNotContain("\"agent\":", body);
        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal(claim, parsed);
    }

    [Fact]
    public void Format_renders_a_released_summary()
    {
        var claim = new ClaimRecord(
            1,
            "claim-attempt-1",
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            "released");

        var body = ClaimMarker.Format(claim);

        Assert.StartsWith(
            "_Wrighty: claim released by worker **worker-1**._",
            body);
        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal("released", parsed.State);
    }

    [Fact]
    public void Format_includes_agent_context_and_shortens_only_the_visible_session()
    {
        var claim = new ClaimRecord(
            1,
            "claim-attempt-1",
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            "active",
            "codex",
            "session-123456789",
            "agent");

        var body = ClaimMarker.Format(claim);

        Assert.StartsWith(
            "_Wrighty: claimed by Codex worker **worker-1** (session **session-…**) until",
            body);
        Assert.Contains("\"agentType\":\"codex\"", body);
        Assert.Contains("\"sessionId\":\"session-123456789\"", body);
        Assert.Contains("\"claimantKind\":\"agent\"", body);
        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal(claim, parsed);
    }

    [Fact]
    public void TryParse_accepts_legacy_agent_and_attempt_field_names()
    {
        const string body = """
            <!-- wrighty-claim:v1
            {"version":1,"attempt":"legacy-attempt","agent":"legacy-worker","claimedAt":"2026-07-13T10:00:00Z","expiresAt":"2026-07-13T11:00:00Z","state":"active"}
            -->
            """;

        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal("legacy-attempt", parsed.ClaimAttemptId);
        Assert.Equal("legacy-worker", parsed.WorkerIdentity);
        Assert.Equal("unknown", parsed.ClaimantKind);
    }

    [Fact]
    public void TryParse_preserves_an_unknown_future_agent_type()
    {
        const string body = """
            <!-- wrighty-claim:v1
            {"version":1,"claimAttemptId":"attempt","workerIdentity":"worker","agentType":"future-agent","sessionId":"session","claimedAt":"2026-07-13T10:00:00Z","expiresAt":"2026-07-13T11:00:00Z","state":"active"}
            -->
            """;

        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal("future-agent", parsed.AgentType);
        Assert.Equal("session", parsed.SessionId);
        Assert.Equal("unknown", parsed.ClaimantKind);
    }

    [Fact]
    public void TryParse_infers_agent_for_a_recognized_legacy_agent_type()
    {
        const string body = """
            <!-- wrighty-claim:v1
            {"version":1,"claimAttemptId":"attempt","workerIdentity":"worker","agentType":"claude","claimedAt":"2026-07-13T10:00:00Z","expiresAt":"2026-07-13T11:00:00Z","state":"active"}
            -->
            """;

        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal("agent", parsed.ClaimantKind);
    }

    [Fact]
    public void TryParse_treats_an_invalid_claimant_kind_as_unknown()
    {
        const string body = """
            <!-- wrighty-claim:v1
            {"version":1,"claimAttemptId":"attempt","workerIdentity":"worker","claimantKind":"robot","agentType":"codex","claimedAt":"2026-07-13T10:00:00Z","expiresAt":"2026-07-13T11:00:00Z","state":"active"}
            -->
            """;

        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal("unknown", parsed.ClaimantKind);
    }

    [Fact]
    public void TryParse_discards_malformed_optional_context_without_discarding_ownership()
    {
        const string body = """
            <!-- wrighty-claim:v1
            {"version":1,"claimAttemptId":"attempt","workerIdentity":"worker","agentType":"not valid!","sessionId":"https://example.test/session","claimedAt":"2026-07-13T10:00:00Z","expiresAt":"2026-07-13T11:00:00Z","state":"active"}
            -->
            """;

        Assert.True(ClaimMarker.TryParse(body, out var parsed));
        Assert.Equal("worker", parsed.WorkerIdentity);
        Assert.Null(parsed.AgentType);
        Assert.Null(parsed.SessionId);
    }

    [Theory]
    [InlineData("ordinary issue comment")]
    [InlineData("<!-- wrighty-claim:v1\nnot-json\n-->")]
    [InlineData("<!-- wrighty-claim:v1\n{}\n-->")]
    public void TryParse_rejects_non_claim_comments(string body)
    {
        Assert.False(ClaimMarker.TryParse(body, out _));
    }
}
