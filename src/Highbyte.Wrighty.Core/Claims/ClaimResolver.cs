namespace Highbyte.Wrighty.Claims;

public static class ClaimResolver
{
    public static ClaimEvent? Resolve(IEnumerable<ClaimEvent> events, DateTimeOffset now)
    {
        ClaimEvent? current = null;
        foreach (var item in events.OrderBy(value => value.CreatedAt).ThenBy(value => value.CommentId))
        {
            var claim = item.Claim;
            if (claim.EventType == "acquired")
            {
                if (current is null || current.Claim.ExpiresAt <= item.CreatedAt) current = item;
                continue;
            }
            if (current is null || claim.PreviousClaimToken != current.Claim.ClaimToken) continue;
            if (claim.WorkerIdentity != current.Claim.WorkerIdentity) continue;
            current = claim.EventType is "released" or "overrideReleased" ? null : item;
        }
        return current?.Claim.ExpiresAt > now ? current : null;
    }
}
