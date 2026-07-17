namespace Highbyte.Wrighty.Claims;

public static class ClaimResolver
{
    public static ClaimEvent? Resolve(IEnumerable<ClaimEvent> events, DateTimeOffset now)
    {
        ClaimEvent? current = null;
        foreach (var item in events.OrderBy(value => value.CreatedAt).ThenBy(value => value.CommentId))
        {
            current = Apply(current, item);
        }
        return current?.Claim.ExpiresAt > now ? current : null;
    }

    private static ClaimEvent? Apply(ClaimEvent? current, ClaimEvent item)
    {
        var claim = item.Claim;
        if (claim.EventType == "acquired")
        {
            return current is null || current.Claim.ExpiresAt <= item.CreatedAt ? item : current;
        }

        if (current is null ||
            claim.PreviousClaimToken != current.Claim.ClaimToken ||
            claim.WorkerIdentity != current.Claim.WorkerIdentity)
        {
            return current;
        }

        return claim.EventType is "released" or "overrideReleased" ? null : item;
    }
}
