using System.Text;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class AgentFailureClassifierTests
{
    [Fact]
    public async Task Claude_preserves_authoritative_usage_failure_and_exact_reset()
    {
        const string fixture =
            """
            {
              "type": "result",
              "subtype": "error",
              "is_error": true,
              "session_id": "claude-session",
              "result": "Usage limit reached.",
              "error": {
                "code": "usage_limit_reached",
                "message": "Usage limit reached for user@example.com.",
                "resetAt": "2026-07-24T04:00:00Z"
              }
            }
            """;

        var result = await new ClaudeAgentAdapter().InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(AgentFailureKind.UsageExhausted, result.Failure?.Kind);
        Assert.Equal(AgentFailureConfidence.Authoritative, result.Failure?.Confidence);
        Assert.Equal("usage_limit_reached", result.Failure?.ProviderCode);
        Assert.True(result.Failure?.IsRetryable);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 24, 4, 0, 0, TimeSpan.Zero),
            result.Failure?.RetryAt);
        Assert.DoesNotContain("user@example.com", result.Failure?.SanitizedMessage ?? "");
    }

    [Fact]
    public async Task Claude_live_session_limit_message_uses_next_local_reset()
    {
        const string fixture =
            """
            {
              "type": "result",
              "subtype": "success",
              "is_error": true,
              "session_id": "claude-session",
              "result": "You've hit your session limit · resets 8:40pm (Europe/Stockholm)"
            }
            """;
        var observedAt = DateTimeOffset.Parse("2026-07-23T16:45:00Z");

        var result = await new ClaudeAgentAdapter(() => observedAt).InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(AgentFailureKind.UsageExhausted, result.Failure?.Kind);
        Assert.Equal(AgentFailureConfidence.Inferred, result.Failure?.Confidence);
        Assert.Null(result.Failure?.ProviderCode);
        Assert.True(result.Failure?.IsRetryable);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-23T18:40:00Z"),
            result.Failure?.RetryAt);
    }

    [Fact]
    public async Task Codex_retains_complete_turn_failed_event_and_retry_after()
    {
        var fixture = string.Join('\n',
            """{"type":"thread.started","thread_id":"codex-session"}""",
            """{"type":"turn.started"}""",
            """
            {"type":"turn.failed","error":{"code":"rate_limit_exceeded","message":"Too many requests.","retryAfter":45}}
            """) + "\n";

        var result = await new CodexAgentAdapter().InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(AgentFailureKind.RateLimited, result.Failure?.Kind);
        Assert.Equal(AgentFailureConfidence.Authoritative, result.Failure?.Confidence);
        Assert.Equal(TimeSpan.FromSeconds(45), result.Failure?.RetryAfter);
        Assert.Equal("codex-session", result.SessionId);
    }

    [Fact]
    public async Task Copilot_uses_error_event_instead_of_persisting_raw_result_json()
    {
        var fixture = string.Join('\n',
            """
            {"type":"error","error":{"code":"ai_credits_exhausted","message":"AI credits exhausted for owner@example.com."}}
            """,
            """
            {"type":"result","sessionId":"copilot-session","exitCode":1,"message":"Run stopped."}
            """) + "\n";

        var result = await new CopilotAgentAdapter().InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(AgentFailureKind.UsageExhausted, result.Failure?.Kind);
        Assert.Equal("Run stopped.", result.FinalMessage);
        Assert.DoesNotContain("\"sessionId\"", result.FinalMessage ?? "");
        Assert.DoesNotContain("owner@example.com", result.Failure?.SanitizedMessage ?? "");
    }

    [Fact]
    public async Task Copilot_live_session_quota_error_uses_monthly_reset_and_plain_message()
    {
        var fixture = string.Join('\n',
            """
            {"type":"assistant.turn_start","data":{"turnId":"0"}}
            """,
            """
            {"type":"session.error","data":{"errorType":"quota","errorCode":"quota_exceeded","message":"You have no quota (Request ID: D405:19338C:E333D6:F49278:6A624E86)","statusCode":402}}
            """,
            """
            {"type":"result","sessionId":"copilot-session","exitCode":1,"usage":{"premiumRequests":0}}
            """) + "\n";
        var observedAt = DateTimeOffset.Parse("2026-07-23T17:25:00Z");

        var result = await new CopilotAgentAdapter(() => observedAt).InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(AgentOutcome.Failed, result.Outcome);
        Assert.Equal(AgentFailureKind.UsageExhausted, result.Failure?.Kind);
        Assert.Equal(AgentFailureConfidence.Authoritative, result.Failure?.Confidence);
        Assert.Equal("quota_exceeded", result.Failure?.ProviderCode);
        Assert.True(result.Failure?.IsRetryable);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            result.Failure?.RetryAt);
        Assert.Equal("You have no quota (Request ID=[redacted])", result.FinalMessage);
        Assert.DoesNotContain("D405", result.Failure?.SanitizedMessage ?? "");
        Assert.DoesNotContain("\"type\"", result.FinalMessage ?? "");
    }

    [Theory]
    [InlineData("authentication_error", AgentFailureKind.Authentication, false)]
    [InlineData("payment_required", AgentFailureKind.BillingUnavailable, false)]
    [InlineData("permission_denied", AgentFailureKind.PermissionDenied, false)]
    [InlineData("context_length_exceeded", AgentFailureKind.ContextLimit, false)]
    [InlineData("service_unavailable", AgentFailureKind.ProviderUnavailable, true)]
    [InlineData("agent_failure", AgentFailureKind.AgentFailure, false)]
    public async Task Structured_codes_map_to_distinct_operator_actions(
        string code,
        AgentFailureKind expected,
        bool retryable)
    {
        var fixture =
            $$"""
              {"type":"result","subtype":"error","is_error":true,"session_id":"session","error":{"code":"{{code}}","message":"Provider stopped."} }
              """;

        var result = await new ClaudeAgentAdapter().InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(expected, result.Failure?.Kind);
        Assert.Equal(retryable, result.Failure?.IsRetryable);
        Assert.Equal(AgentFailureConfidence.Authoritative, result.Failure?.Confidence);
    }

    [Theory]
    [InlineData("Usage limit reached. Try again after 2026-07-24T04:00:00Z.",
        AgentFailureKind.UsageExhausted)]
    [InlineData("You are temporarily rate limited.", AgentFailureKind.RateLimited)]
    [InlineData("Maximum context length exceeded.", AgentFailureKind.ContextLimit)]
    public async Task Message_only_classification_is_explicitly_inferred(
        string message,
        AgentFailureKind expected)
    {
        var fixture =
            $$"""
              {"type":"result","subtype":"error","is_error":true,"session_id":"session","result":"{{message}}"}
              """;

        var result = await new ClaudeAgentAdapter().InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(expected, result.Failure?.Kind);
        Assert.Equal(AgentFailureConfidence.Inferred, result.Failure?.Confidence);
    }

    [Theory]
    [InlineData("Usage reporting is enabled.")]
    [InlineData("The rate limit documentation was updated.")]
    [InlineData("Processed quota configuration without errors.")]
    [InlineData("The context limit configuration was loaded successfully.")]
    [InlineData("The session limit configuration was updated.")]
    public async Task Near_miss_text_is_not_treated_as_retryable_usage_exhaustion(string message)
    {
        var fixture =
            $$"""
              {"type":"result","subtype":"error","is_error":true,"session_id":"session","result":"{{message}}"}
              """;

        var result = await new ClaudeAgentAdapter().InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.Equal(AgentFailureKind.Unknown, result.Failure?.Kind);
        Assert.False(result.Failure?.IsRetryable);
    }

    [Fact]
    public async Task Sanitization_redacts_named_secrets_and_bounds_the_message()
    {
        var longTail = new string('x', 1500);
        var fixture =
            $$"""
              {"type":"result","subtype":"error","is_error":true,"session_id":"session","result":"Usage limit reached. api_key=sk-sensitive password='also-sensitive' {{longTail}}"}
              """;

        var result = await new ClaudeAgentAdapter().InterpretAsync(
            Stream(fixture), 1, CancellationToken.None);

        Assert.DoesNotContain("sk-sensitive", result.Failure?.SanitizedMessage ?? "");
        Assert.DoesNotContain("also-sensitive", result.Failure?.SanitizedMessage ?? "");
        Assert.True(result.Failure?.SanitizedMessage?.Length <= 1001);
    }

    private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));
}
