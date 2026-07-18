using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.AgentContext;

namespace Highbyte.Wrighty.Claims;

public static class ClaimMarker
{
    public const string Prefix = "<!-- wrighty-claim:v2";
    public const string LegacyPrefix = "<!-- wrighty-claim:v1";
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
            if (value is null || value.Version != 2 || string.IsNullOrWhiteSpace(value.EventId) ||
                string.IsNullOrWhiteSpace(value.WorkerIdentity) || string.IsNullOrWhiteSpace(value.ClaimantId) ||
                string.IsNullOrWhiteSpace(value.ClaimToken) || value.ExpiresAt <= value.ClaimedAt ||
                value.EventType is not ("acquired" or "takenOver" or "released" or "overrideReleased" or "renewed"))
                return false;
            if (value.EventType != "acquired" && string.IsNullOrWhiteSpace(value.PreviousClaimToken)) return false;
            claim = value with
            {
                AgentType = Normalize(value.AgentType),
                SessionId = NormalizeOpaque(value.SessionId),
                WorkspacePath = NormalizeWorkspace(value.WorkspacePath),
                ClaimantKind = ClaimantKinds.ToStorageValue(ClaimantKinds.FromStorageValue(value.ClaimantKind, value.AgentType))
            };
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static bool HasActiveLegacyClaim(string body, DateTimeOffset now)
    {
        var json = Payload(body, LegacyPrefix);
        if (json is null) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("state", out var state) && state.GetString() == "active" &&
                   root.TryGetProperty("expiresAt", out var expires) && expires.GetDateTimeOffset() > now;
        }
        catch (JsonException) { return false; }
    }

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
        (claim.AgentType is null ? "" : $" ({claim.AgentType})");

    private static string Short(string value) => value.Length <= 12 ? value : $"{value[..12]}…";
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static string? NormalizeOpaque(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl) ? null : value;
    private static string? NormalizeWorkspace(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) ? null : value;
}
