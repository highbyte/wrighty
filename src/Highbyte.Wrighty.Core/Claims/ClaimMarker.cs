using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.AgentContext;

namespace Highbyte.Wrighty.Claims;

public static class ClaimMarker
{
    public const string Prefix = "<!-- wrighty-claim:v3";
    private static readonly string[] LegacyPrefixes =
        ["<!-- wrighty-claim:v1", "<!-- wrighty-claim:v2"];
    private const string Suffix = "-->";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Format(ClaimRecord claim)
    {
        var verb = claim.EventType switch
        {
            "takenOver" => "claim taken over",
            "released" => "claim released",
            "overrideReleased" => "claim override-released",
            "renewed" => "claim renewed",
            "requeued" => "agent session queued",
            _ => "claimed"
        };
        return $"_Wrighty: {verb} by {Actor(claim)}._\n\n{Prefix}\n{JsonSerializer.Serialize(claim, JsonOptions)}\n{Suffix}";
    }

    public static bool TryParse(string body, out ClaimRecord claim)
    {
        claim = null!;
        var json = Payload(body, Prefix);
        if (json is null) return false;
        try
        {
            var value = JsonSerializer.Deserialize<ClaimRecord>(json, JsonOptions);
            if (value is null || value.Version != 3 || string.IsNullOrWhiteSpace(value.EventId) ||
                string.IsNullOrWhiteSpace(value.InstallationId) || string.IsNullOrWhiteSpace(value.ClaimantId) ||
                string.IsNullOrWhiteSpace(value.ClaimToken) || value.ExpiresAt <= value.ClaimedAt ||
                value.EventType is not ("acquired" or "takenOver" or "released" or
                    "overrideReleased" or "renewed" or "requeued"))
                return false;
            if (value.EventType != "acquired" && string.IsNullOrWhiteSpace(value.PreviousClaimToken)) return false;
            claim = value with
            {
                Agent = Normalize(value.Agent),
                SessionId = NormalizeOpaque(value.SessionId),
                WorkspacePath = NormalizeWorkspace(value.WorkspacePath),
                ClaimantKind = ClaimantKinds.ToStorageValue(ClaimantKinds.FromStorageValue(value.ClaimantKind))
            };
            return true;
        }
        catch (JsonException) { return false; }
    }

    // TODO(post-1.0): Remove pre-v3 claim-marker detection once pre-1.0 issues are no longer
    // expected. This guard is intentionally read-only and must never translate legacy comments.
    public static bool HasLegacyMarker(string body) =>
        LegacyPrefixes.Any(prefix => body.Contains(prefix, StringComparison.Ordinal));

    private static string? Payload(string body, string prefix)
    {
        var start = body.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0) return null;
        start += prefix.Length;
        var end = body.IndexOf(Suffix, start, StringComparison.Ordinal);
        return end < 0 ? null : body[start..end].Trim();
    }

    private static string Actor(ClaimRecord claim) =>
        $"{claim.ClaimantKind} **{Short(claim.ClaimantId)}**" +
        (claim.Agent is null ? "" : $" ({claim.Agent})");

    private static string Short(string value) => value.Length <= 12 ? value : $"{value[..12]}…";
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static string? NormalizeOpaque(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl) ? null : value;
    private static string? NormalizeWorkspace(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) ? null : value;
}
