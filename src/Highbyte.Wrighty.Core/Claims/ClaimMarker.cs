using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.AgentContext;

namespace Highbyte.Wrighty.Claims;

public static class ClaimMarker
{
    public const string Prefix = "<!-- wrighty-claim:v1";
    private const string Suffix = "-->";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Format(ClaimRecord claim)
    {
        var actor = FormatActor(claim);
        var summary = claim.State == "released"
            ? $"_Wrighty: claim released by {actor}._"
            : $"_Wrighty: claimed by {actor} until {claim.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC._";

        return $"{summary}\n\n{Prefix}\n{JsonSerializer.Serialize(claim, JsonOptions)}\n{Suffix}";
    }

    public static bool TryParse(string body, out ClaimRecord claim)
    {
        claim = null!;
        var start = body.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        start += Prefix.Length;
        var end = body.IndexOf(Suffix, start, StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        var json = body[start..end].Trim();
        try
        {
            var payload = JsonSerializer.Deserialize<ClaimPayload>(json, JsonOptions);
            var claimAttemptId = SelectCurrentOrLegacy(
                payload?.ClaimAttemptId,
                payload?.LegacyAttempt);
            var workerIdentity = SelectCurrentOrLegacy(
                payload?.WorkerIdentity,
                payload?.LegacyAgent);

            if (payload is null ||
                payload.Version != 1 ||
                string.IsNullOrWhiteSpace(claimAttemptId) ||
                string.IsNullOrWhiteSpace(workerIdentity) ||
                payload.ExpiresAt <= payload.ClaimedAt ||
                (payload.State != "active" && payload.State != "released"))
            {
                return false;
            }

            claim = new ClaimRecord(
                payload.Version,
                claimAttemptId,
                workerIdentity,
                payload.ClaimedAt,
                payload.ExpiresAt,
                payload.State,
                NormalizeAgentType(payload.AgentType),
                NormalizeSessionId(payload.SessionId),
                ClaimantKinds.ToStorageValue(ClaimantKinds.FromStorageValue(
                    payload.ClaimantKind,
                    payload.AgentType)));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string FormatActor(ClaimRecord claim)
    {
        var worker = $"worker **{claim.WorkerIdentity}**";
        var kind = ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType);
        var typedWorker = kind switch
        {
            ClaimantKind.Agent when !string.IsNullOrWhiteSpace(claim.AgentType) =>
                $"{ToDisplayName(claim.AgentType)} {worker}",
            ClaimantKind.Agent => $"Agent {worker}",
            ClaimantKind.Human => $"Human {worker}",
            ClaimantKind.Automation => $"Automation {worker}",
            _ => worker
        };
        return string.IsNullOrWhiteSpace(claim.SessionId)
            ? typedWorker
            : $"{typedWorker} (session **{Shorten(claim.SessionId)}**)";
    }

    private static string ToDisplayName(string agentType) => agentType switch
    {
        "codex" => "Codex",
        "claude" => "Claude",
        "copilot" => "Copilot",
        _ => "Other agent"
    };

    private static string Shorten(string value) =>
        value.Length <= 8 ? value : $"{value[..8]}…";

    private static string? NormalizeAgentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character =>
                character is not (>= 'a' and <= 'z') &&
                character is not (>= '0' and <= '9') &&
                character != '-'))
        {
            return null;
        }

        return value;
    }

    private static string? NormalizeSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl) ||
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
    }

    private static string? SelectCurrentOrLegacy(string? current, string? legacy)
    {
        if (!string.IsNullOrWhiteSpace(current) &&
            !string.IsNullOrWhiteSpace(legacy) &&
            !string.Equals(current, legacy, StringComparison.Ordinal))
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(current) ? current : legacy;
    }

    private sealed record ClaimPayload
    {
        public int Version { get; init; }

        public string? ClaimAttemptId { get; init; }

        public string? WorkerIdentity { get; init; }

        public string? AgentType { get; init; }

        public string? SessionId { get; init; }

        public string? ClaimantKind { get; init; }

        [JsonPropertyName("attempt")]
        public string? LegacyAttempt { get; init; }

        [JsonPropertyName("agent")]
        public string? LegacyAgent { get; init; }

        public DateTimeOffset ClaimedAt { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }

        public string State { get; init; } = string.Empty;
    }
}
