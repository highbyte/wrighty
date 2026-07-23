using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class RetryScheduleTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly WorkItemId ItemId = new("local:42");
    private static readonly WorkerUsageFailureConfig Policy = new()
    {
        InitialRetryMinutes = 30,
        BackoffMultiplier = 2,
        MaxRetryHours = 6,
        ResetGraceMinutes = 2
    };

    [Fact]
    public void Exact_reset_uses_grace_and_bounded_installation_jitter()
    {
        var reset = Now.AddHours(2);
        var failure = Failure(retryAt: reset);

        var scheduled = RetrySchedule.ChooseNotBefore(Now, ItemId, failure, Policy, 1);

        Assert.InRange(
            scheduled,
            reset.AddMinutes(2),
            reset.AddMinutes(2).AddSeconds(30));
    }

    [Fact]
    public void Reset_in_the_past_becomes_a_near_immediate_jittered_attempt()
    {
        var failure = Failure(retryAt: Now.AddHours(-1));

        var scheduled = RetrySchedule.ChooseNotBefore(Now, ItemId, failure, Policy, 1);

        Assert.InRange(scheduled, Now, Now.AddSeconds(30));
    }

    [Fact]
    public void Retry_after_is_preferred_over_fallback_backoff()
    {
        var failure = Failure(retryAfter: TimeSpan.FromSeconds(45));

        var scheduled = RetrySchedule.ChooseNotBefore(Now, ItemId, failure, Policy, 4);

        Assert.InRange(
            scheduled,
            Now.AddSeconds(45),
            Now.AddSeconds(75));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(8, 360)]
    public void Fallback_backoff_is_exponential_and_capped(
        int attempt,
        int expectedMinutes)
    {
        var scheduled = RetrySchedule.ChooseNotBefore(
            Now, ItemId, Failure(), Policy, attempt);

        Assert.InRange(
            scheduled,
            Now.AddMinutes(expectedMinutes),
            Now.AddMinutes(expectedMinutes).AddSeconds(30));
    }

    [Fact]
    public void Jitter_is_stable_for_one_installation_item_and_attempt()
    {
        var first = RetrySchedule.ChooseNotBefore(
            Now, ItemId, Failure(), Policy, 2);
        var repeated = RetrySchedule.ChooseNotBefore(
            Now, ItemId, Failure(), Policy, 2);

        Assert.Equal(first, repeated);
        Assert.InRange(first, Now.AddMinutes(60), Now.AddMinutes(60).AddSeconds(30));
    }

    private static AgentFailure Failure(
        DateTimeOffset? retryAt = null,
        TimeSpan? retryAfter = null) =>
        new(
            AgentFailureKind.UsageExhausted,
            "usage_limit_reached",
            retryAt,
            retryAfter,
            true,
            AgentFailureConfidence.Authoritative,
            "Usage limit reached.");
}
