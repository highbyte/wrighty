using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Highbyte.Wrighty.Workers;

[JsonConverter(typeof(JsonStringEnumConverter<AgentFailureKind>))]
public enum AgentFailureKind
{
    [JsonStringEnumMemberName("usage-exhausted")]
    UsageExhausted,
    [JsonStringEnumMemberName("rate-limited")]
    RateLimited,
    [JsonStringEnumMemberName("billing-unavailable")]
    BillingUnavailable,
    [JsonStringEnumMemberName("authentication")]
    Authentication,
    [JsonStringEnumMemberName("provider-unavailable")]
    ProviderUnavailable,
    [JsonStringEnumMemberName("permission-denied")]
    PermissionDenied,
    [JsonStringEnumMemberName("context-limit")]
    ContextLimit,
    [JsonStringEnumMemberName("agent-failure")]
    AgentFailure,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentFailureConfidence>))]
public enum AgentFailureConfidence
{
    [JsonStringEnumMemberName("authoritative")]
    Authoritative,
    [JsonStringEnumMemberName("inferred")]
    Inferred
}

/// <summary>
/// A bounded, provider-neutral description of why an agent run failed. It intentionally excludes
/// raw provider payloads and account details so it is safe to retain in Wrighty's machine-local
/// session stores and project through operational JSON.
/// </summary>
public sealed record AgentFailure(
    AgentFailureKind Kind,
    string? ProviderCode,
    DateTimeOffset? RetryAt,
    TimeSpan? RetryAfter,
    bool IsRetryable,
    AgentFailureConfidence Confidence,
    string? SanitizedMessage);

public sealed record AgentCapacityProbeRequest(
    string AgentType,
    Workspace Workspace,
    AgentFailure SuspectedFailure);

public sealed record AgentCapacityProbeResult(
    bool Available,
    AgentFailure? Failure,
    DateTimeOffset ObservedAt);

/// <summary>
/// Optional provider capacity seam. Providers without a read-only capacity surface simply do not
/// register an implementation; a normal, policy-bounded resume remains their capacity probe.
/// </summary>
public interface IAgentCapacityProbe
{
    string AgentType { get; }

    Task<AgentCapacityProbeResult?> ProbeAsync(
        AgentCapacityProbeRequest request,
        CancellationToken cancellationToken);
}

internal static partial class AgentFailureClassifier
{
    private const int MessageLimit = 1000;
    private const int ProviderCodeLimit = 100;

    private static readonly HashSet<string> UsageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "usage_exhausted",
        "usage_limit_reached",
        "quota_exhausted",
        "quota_exceeded",
        "monthly_limit_reached",
        "ai_credits_exhausted",
        "premium_requests_exhausted"
    };

    private static readonly HashSet<string> RateLimitCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "rate_limit_exceeded",
        "rate_limited",
        "too_many_requests",
        "requests_limit_exceeded",
        "throttled"
    };

    private static readonly HashSet<string> BillingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "billing_unavailable",
        "billing_error",
        "payment_required",
        "insufficient_quota",
        "insufficient_credits",
        "credit_balance_exhausted"
    };

    private static readonly HashSet<string> AuthenticationCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "authentication_error",
        "authentication_failed",
        "unauthorized",
        "invalid_api_key",
        "invalid_token",
        "token_expired"
    };

    private static readonly HashSet<string> ProviderUnavailableCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "service_unavailable",
        "provider_unavailable",
        "overloaded",
        "server_error",
        "internal_server_error",
        "bad_gateway",
        "gateway_timeout"
    };

    private static readonly HashSet<string> PermissionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "permission_denied",
        "forbidden",
        "sandbox_denied",
        "tool_denied"
    };

    private static readonly HashSet<string> ContextLimitCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "context_length_exceeded",
        "context_limit_exceeded",
        "prompt_too_long",
        "max_tokens_exceeded"
    };

    private static readonly HashSet<string> AgentFailureCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "agent_failure",
        "execution_failed",
        "turn_failed"
    };

    public static AgentFailure FromEvent(
        string provider,
        JsonElement terminalError,
        int exitCode,
        DateTimeOffset? observedAt = null)
    {
        var evidence = ErrorEvidence.Read(terminalError);
        var providerCode = SanitizeCode(evidence.Code);
        var message = SanitizeMessage(evidence.Message);
        var retryAt = evidence.RetryAt ??
                      TryReadIsoTimestamp(message) ??
                      TryReadProviderReset(provider, providerCode, message, observedAt);
        var retryAfter = evidence.RetryAfter;

        if (TryFromCode(providerCode, retryAt, retryAfter, message) is { } authoritative)
            return authoritative;

        return FromMessage(provider, message, providerCode, retryAt, retryAfter, exitCode);
    }

    public static AgentFailure Unknown(string provider, string? message, int exitCode) =>
        FromMessage(
            provider,
            SanitizeMessage(message),
            providerCode: exitCode == 0 ? null : $"exit-{exitCode}",
            retryAt: null,
            retryAfter: null,
            exitCode);

    private static AgentFailure? TryFromCode(
        string? code,
        DateTimeOffset? retryAt,
        TimeSpan? retryAfter,
        string? message)
    {
        if (code is null)
            return null;
        if (UsageCodes.Contains(code))
            return Failure(AgentFailureKind.UsageExhausted, code, retryAt, retryAfter, true,
                AgentFailureConfidence.Authoritative, message);
        if (RateLimitCodes.Contains(code))
            return Failure(AgentFailureKind.RateLimited, code, retryAt, retryAfter, true,
                AgentFailureConfidence.Authoritative, message);
        if (BillingCodes.Contains(code))
            return Failure(AgentFailureKind.BillingUnavailable, code, retryAt, retryAfter, false,
                AgentFailureConfidence.Authoritative, message);
        if (AuthenticationCodes.Contains(code))
            return Failure(AgentFailureKind.Authentication, code, retryAt, retryAfter, false,
                AgentFailureConfidence.Authoritative, message);
        if (ProviderUnavailableCodes.Contains(code))
            return Failure(AgentFailureKind.ProviderUnavailable, code, retryAt, retryAfter, true,
                AgentFailureConfidence.Authoritative, message);
        if (PermissionCodes.Contains(code))
            return Failure(AgentFailureKind.PermissionDenied, code, retryAt, retryAfter, false,
                AgentFailureConfidence.Authoritative, message);
        if (ContextLimitCodes.Contains(code))
            return Failure(AgentFailureKind.ContextLimit, code, retryAt, retryAfter, false,
                AgentFailureConfidence.Authoritative, message);
        if (AgentFailureCodes.Contains(code))
            return Failure(AgentFailureKind.AgentFailure, code, retryAt, retryAfter, false,
                AgentFailureConfidence.Authoritative, message);
        return null;
    }

    private static AgentFailure FromMessage(
        string provider,
        string? message,
        string? providerCode,
        DateTimeOffset? retryAt,
        TimeSpan? retryAfter,
        int exitCode)
    {
        var value = message ?? string.Empty;

        // Order matters. "Limit" alone is never enough to identify subscription usage: context,
        // billing, and temporary request limits have different operator actions.
        if (Matches(value,
                "context length exceeded", "context window exceeded", "context window is full",
                "prompt is too long",
                "maximum context", "max token"))
            return Inferred(AgentFailureKind.ContextLimit, providerCode, false);

        if (Matches(value,
                "invalid api key", "authentication failed", "not authenticated",
                "please log in", "login required", "unauthorized"))
            return Inferred(AgentFailureKind.Authentication, providerCode, false);

        if (Matches(value,
                "payment required", "billing account", "billing unavailable",
                "insufficient credits", "credit balance"))
            return Inferred(AgentFailureKind.BillingUnavailable, providerCode, false);

        if (Matches(value,
                "permission denied", "operation not permitted", "sandbox denied",
                "tool permission", "forbidden"))
            return Inferred(AgentFailureKind.PermissionDenied, providerCode, false);

        if (Matches(value,
                "usage limit reached", "usage limits reached", "quota exhausted",
                "monthly limit reached", "ai credits exhausted",
                "premium requests exhausted", "included requests exhausted",
                "you've hit your usage limit", "you have hit your usage limit",
                "you've hit your session limit", "you have hit your session limit",
                "session limit reached"))
            return Inferred(AgentFailureKind.UsageExhausted, providerCode, true);

        if (Matches(value,
                "temporarily rate limited", "rate limit exceeded",
                "too many requests", "request limit exceeded", "retry after"))
            return Inferred(AgentFailureKind.RateLimited, providerCode, true);

        if (Matches(value,
                "service unavailable", "provider unavailable", "temporarily unavailable",
                "server overloaded", "internal server error", "bad gateway", "gateway timeout"))
            return Inferred(AgentFailureKind.ProviderUnavailable, providerCode, true);

        return Failure(
            AgentFailureKind.Unknown,
            providerCode ?? (exitCode == 0 ? null : $"exit-{exitCode}"),
            retryAt,
            retryAfter,
            false,
            AgentFailureConfidence.Inferred,
            message);

        AgentFailure Inferred(AgentFailureKind kind, string? code, bool retryable) =>
            Failure(
                kind,
                code,
                retryAt,
                retryAfter,
                retryable,
                AgentFailureConfidence.Inferred,
                message);
    }

    private static AgentFailure Failure(
        AgentFailureKind kind,
        string? providerCode,
        DateTimeOffset? retryAt,
        TimeSpan? retryAfter,
        bool retryable,
        AgentFailureConfidence confidence,
        string? message) =>
        new(kind, providerCode, retryAt, retryAfter, retryable, confidence, message);

    private static bool Matches(string value, params string[] patterns) =>
        patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static string? SanitizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = string.Concat(value.Trim().Take(ProviderCodeLimit).Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':'
                ? character
                : '_'));
        return normalized.Length == 0 ? null : normalized;
    }

    internal static string? SanitizeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var sanitized = BearerSecretRegex().Replace(value, "$1[redacted]");
        sanitized = NamedSecretRegex().Replace(sanitized, "$1=[redacted]");
        sanitized = RequestIdRegex().Replace(sanitized, "$1=[redacted]");
        sanitized = EmailRegex().Replace(sanitized, "[redacted-email]");
        sanitized = WhitespaceRegex().Replace(sanitized, " ").Trim();
        return sanitized.Length <= MessageLimit ? sanitized : $"{sanitized[..MessageLimit]}…";
    }

    private static DateTimeOffset? TryReadIsoTimestamp(string? message)
    {
        if (message is null)
            return null;
        var match = IsoTimestampRegex().Match(message);
        return match.Success &&
               DateTimeOffset.TryParse(
                   match.Value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out var timestamp)
            ? timestamp
            : null;
    }

    private static DateTimeOffset? TryReadProviderReset(
        string provider,
        string? providerCode,
        string? message,
        DateTimeOffset? observedAt)
    {
        var observed = observedAt ?? DateTimeOffset.UtcNow;
        if (string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase) &&
            providerCode is "quota_exceeded" or "ai_credits_exhausted" or
                "premium_requests_exhausted")
        {
            var utc = observed.ToUniversalTime();
            return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero)
                .AddMonths(1);
        }

        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
            return TryReadCodexReset(message, observed);

        if (!string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase) ||
            message is null)
            return null;
        var match = ClaudeLocalResetRegex().Match(message);
        if (!match.Success)
            return null;
        var timeText = match.Groups["time"].Value
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (!DateTime.TryParseExact(
                timeText,
                ["h:mmtt", "htt"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedTime))
            return null;

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value);
            var localObserved = TimeZoneInfo.ConvertTime(observed, zone);
            var localReset = DateTime.SpecifyKind(
                localObserved.Date.Add(parsedTime.TimeOfDay),
                DateTimeKind.Unspecified);
            if (localReset <= localObserved.DateTime)
                localReset = localReset.AddDays(1);
            if (zone.IsInvalidTime(localReset))
                return null;
            return new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localReset, zone),
                TimeSpan.Zero);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    // Codex is the only vendor that states its reset purely in prose: no code, no retryAfter, and
    // no zone — "…or try again at Jul 28th, 2026 11:31 PM." Without this the fallback backoff
    // burns every bounded attempt long before a multi-day subscription window actually reopens,
    // parking a recoverable item in needs-attention. The stated time carries no zone, so it is
    // read as machine-local, matching how the operator sees it in their own terminal.
    private static DateTimeOffset? TryReadCodexReset(string? message, DateTimeOffset observed)
    {
        if (message is null)
            return null;
        var match = CodexStatedResetRegex().Match(message);
        if (!match.Success)
            return null;

        var text = string.Join(
            ' ',
            match.Groups["month"].Value,
            match.Groups["day"].Value,
            match.Groups["year"].Value,
            WhitespaceRegex()
                .Replace(match.Groups["time"].Value, string.Empty)
                .Replace(".", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant());
        if (!DateTime.TryParseExact(
                text,
                ["MMM d yyyy h:mmtt", "MMMM d yyyy h:mmtt"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
            return null;

        var zone = TimeZoneInfo.Local;
        if (zone.IsInvalidTime(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)))
            return null;
        var reset = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), zone),
            TimeSpan.Zero);

        // Stay conservative: only trust a reset that is actually ahead of the failure and within a
        // plausible subscription window, so a misread date can never defer an item indefinitely.
        return reset <= observed || reset > observed.AddDays(31) ? null : reset;
    }

    [GeneratedRegex(@"(?i)\b(authorization\s*:\s*bearer\s+|bearer\s+)[^\s,;]+")]
    private static partial Regex BearerSecretRegex();

    [GeneratedRegex(
        @"(?i)\b(api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|password|secret)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;}]+)")]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"(?i)\b(request(?:\s+|[-_])?id)\s*[:=]\s*[A-Z0-9:._-]+")]
    private static partial Regex RequestIdRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})\b")]
    private static partial Regex IsoTimestampRegex();

    [GeneratedRegex(
        @"(?i)\btry\s+again\s+at\s+(?<month>[A-Z]{3,9})\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+" +
        @"(?<year>\d{4})\s+(?<time>\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?))")]
    private static partial Regex CodexStatedResetRegex();

    [GeneratedRegex(
        @"(?i)\bresets?\s+(?<time>\d{1,2}(?::\d{2})?\s*(?:a\.?m\.?|p\.?m\.?))\s*\((?<zone>[A-Z0-9._+/\-]+)\)")]
    private static partial Regex ClaudeLocalResetRegex();

    private sealed record ErrorEvidence(
        string? Code,
        string? Message,
        DateTimeOffset? RetryAt,
        TimeSpan? RetryAfter)
    {
        private static readonly string[] CodeNames =
            ["code", "error_code", "errorCode", "kind"];
        private static readonly string[] MessageNames =
            ["message", "error_message", "errorMessage", "detail", "result"];
        private static readonly string[] RetryAtNames =
            ["retry_at", "retryAt", "reset_at", "resetAt", "resets_at", "resetsAt"];
        private static readonly string[] RetryAfterNames =
            ["retry_after", "retryAfter", "retry_after_seconds", "retryAfterSeconds"];

        public static ErrorEvidence Read(JsonElement root)
        {
            var error = root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("error", out var errorValue)
                ? errorValue
                : root.ValueKind == JsonValueKind.Object &&
                  root.TryGetProperty("data", out var dataValue) &&
                  dataValue.ValueKind == JsonValueKind.Object
                    ? dataValue
                    : root;
            var code = ReadString(error, CodeNames) ?? ReadString(root, ["subtype"]);
            if (string.Equals(code, "success", StringComparison.OrdinalIgnoreCase))
                code = null;
            return new ErrorEvidence(
                code,
                ReadString(error, MessageNames) ?? ReadString(root, MessageNames),
                ReadTimestamp(error, RetryAtNames) ?? ReadTimestamp(root, RetryAtNames),
                ReadDuration(error, RetryAfterNames) ?? ReadDuration(root, RetryAfterNames));
        }

        private static string? ReadString(JsonElement element, IReadOnlyList<string> names)
        {
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString();
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();
                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetRawText();
            }
            return null;
        }

        private static DateTimeOffset? ReadTimestamp(
            JsonElement element,
            IReadOnlyList<string> names)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.Number &&
                    value.TryGetInt64(out var unixSeconds))
                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                if (value.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(
                        value.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var timestamp))
                    return timestamp;
            }
            return null;
        }

        private static TimeSpan? ReadDuration(
            JsonElement element,
            IReadOnlyList<string> names)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDouble(out var seconds) &&
                    seconds >= 0)
                    return TimeSpan.FromSeconds(seconds);
                if (value.ValueKind == JsonValueKind.String &&
                    double.TryParse(
                        value.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out seconds) &&
                    seconds >= 0)
                    return TimeSpan.FromSeconds(seconds);
            }
            return null;
        }
    }
}
