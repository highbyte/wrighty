namespace Highbyte.Wrighty.Claims;

public static class ClaimResolver
{
    public static ClaimEvent? Resolve(
        IEnumerable<ClaimEvent> events,
        DateTimeOffset now)
    {
        return events
            .Where(item => item.Claim.State == "active" && item.Claim.ExpiresAt > now)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.CommentId)
            .FirstOrDefault();
    }
}
