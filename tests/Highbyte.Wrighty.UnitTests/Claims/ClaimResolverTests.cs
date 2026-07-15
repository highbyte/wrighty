using Highbyte.Wrighty.Claims;

namespace Highbyte.Wrighty.UnitTests.Claims;

public sealed class ClaimResolverTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    [Fact]
    public void Resolve_selects_the_earliest_server_created_active_claim()
    {
        var later = Event(20, Now.AddSeconds(2), "later", Now.AddHours(1));
        var earlier = Event(10, Now.AddSeconds(1), "earlier", Now.AddHours(1));

        var winner = ClaimResolver.Resolve([later, earlier], Now);

        Assert.NotNull(winner);
        Assert.Equal("earlier", winner.Claim.WorkerIdentity);
    }

    [Fact]
    public void Resolve_uses_comment_id_as_a_deterministic_tie_breaker()
    {
        var highId = Event(20, Now, "high", Now.AddHours(1));
        var lowId = Event(10, Now, "low", Now.AddHours(1));

        var firstView = ClaimResolver.Resolve([highId, lowId], Now);
        var secondView = ClaimResolver.Resolve([lowId, highId], Now);

        Assert.Equal(10, firstView?.CommentId);
        Assert.Equal(firstView, secondView);
    }

    [Fact]
    public void Resolve_ignores_expired_and_released_claims()
    {
        var expired = Event(10, Now.AddHours(-2), "expired", Now.AddMinutes(-1));
        var released = Event(20, Now.AddMinutes(-2), "released", Now.AddHours(1), "released");
        var active = Event(30, Now.AddMinutes(-1), "active", Now.AddHours(1));

        var winner = ClaimResolver.Resolve([expired, released, active], Now);

        Assert.Equal("active", winner?.Claim.WorkerIdentity);
    }

    private static ClaimEvent Event(
        long id,
        DateTimeOffset createdAt,
        string workerIdentity,
        DateTimeOffset expiresAt,
        string state = "active")
    {
        return new ClaimEvent(
            id,
            createdAt,
            new ClaimRecord(1, $"attempt-{id}", workerIdentity, createdAt, expiresAt, state));
    }
}
