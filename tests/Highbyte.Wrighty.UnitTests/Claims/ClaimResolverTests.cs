using Highbyte.Wrighty.Claims;

namespace Highbyte.Wrighty.UnitTests.Claims;

public sealed class ClaimResolverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T10:00:00Z");

    [Fact]
    public void First_server_ordered_takeover_wins_and_stale_transitions_are_ignored()
    {
        var acquired = Event(1, Now, "acquired", "token-1", null, "agent:one");
        var losing = Event(30, Now.AddSeconds(1), "takenOver", "token-3", "token-1", "agent:three");
        var winner = Event(20, Now.AddSeconds(1), "takenOver", "token-2", "token-1", "human:web");
        var staleRelease = Event(40, Now.AddSeconds(2), "released", "unused", "token-1", "agent:one");

        var resolved = ClaimResolver.Resolve([losing, staleRelease, winner, acquired], Now);

        Assert.Equal("token-2", resolved?.Claim.ClaimToken);
        Assert.Equal("human:web", resolved?.Claim.ClaimantId);
    }

    [Fact]
    public void Exact_release_ends_chain_and_expired_acquisition_allows_a_new_chain()
    {
        var old = Event(1, Now.AddHours(-2), "acquired", "old", null, "old").WithExpiry(Now.AddHours(-1));
        var fresh = Event(2, Now.AddMinutes(-30), "acquired", "fresh", null, "fresh");
        var released = Event(3, Now.AddMinutes(-20), "released", "unused", "fresh", "fresh");

        Assert.Null(ClaimResolver.Resolve([old, fresh, released], Now));
    }

    [Fact]
    public void Renewed_event_preserves_generation_and_extends_expiry()
    {
        var acquired = Event(1, Now.AddMinutes(-50), "acquired", "token-1", null, "agent:one")
            .WithExpiry(Now.AddMinutes(10));
        var renewed = Event(2, Now.AddMinutes(-1), "renewed", "token-1", "token-1", "agent:one")
            .WithExpiry(Now.AddMinutes(59));

        var resolved = ClaimResolver.Resolve([acquired, renewed], Now);

        Assert.Equal("token-1", resolved?.Claim.ClaimToken);
        Assert.Equal(Now.AddMinutes(59), resolved?.Claim.ExpiresAt);
    }

    private static ClaimEvent Event(long id, DateTimeOffset at, string type, string token,
        string? previous, string claimant) => new(id, at,
            new ClaimRecord(2, $"event-{id}", "worker", at, Now.AddHours(1), type,
                claimant, token, previous, null, null, "human"));
}

internal static class ClaimEventTestExtensions
{
    public static ClaimEvent WithExpiry(this ClaimEvent value, DateTimeOffset expiry) =>
        value with { Claim = value.Claim with { ExpiresAt = expiry } };
}
