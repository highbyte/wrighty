using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>What a comment is, for the purpose of assembling approved task context.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WrightyCommentKind>))]
public enum WrightyCommentKind
{
    /// <summary>Ordinary discussion. Requires an approval decision before an agent may see it.</summary>
    [JsonStringEnumMemberName("discussion")]
    Discussion,

    /// <summary>A claim-protocol event. Outside the approval model entirely.</summary>
    [JsonStringEnumMemberName("claim")]
    Claim,

    /// <summary>The operational handover comment. Outside the approval model.</summary>
    [JsonStringEnumMemberName("handover")]
    Handover,

    /// <summary>A historical run report. Outside the approval model, for a different reason.</summary>
    [JsonStringEnumMemberName("session-report")]
    SessionReport
}

/// <summary>The identity metadata a session report carries so it can be matched and validated.</summary>
public sealed record SessionReportMarker(string ItemId, string RunId, string ReportId);

public sealed record CommentClassification(
    WrightyCommentKind Kind,
    SessionReportMarker? Report = null,
    string? Reason = null)
{
    /// <summary>
    /// Whether this comment is excluded from task context without needing an approval decision.
    /// </summary>
    public bool IsProtocol => Kind != WrightyCommentKind.Discussion;

    public static CommentClassification Discussion(string? reason = null) =>
        new(WrightyCommentKind.Discussion, Reason: reason);
}

/// <summary>
/// Decides which comments are Wrighty's own and therefore never reach an agent as task context.
///
/// This is a security boundary in the quiet direction: anything it classifies as protocol is
/// silently removed from what the agent sees, so a mistake here lets someone hide requirements
/// rather than inject them. It therefore refuses to classify on any of the weak signals the design
/// explicitly rules out — the substring "Wrighty", an author's username, an account's bot flag, a
/// human-facing prefix, or a loose <c>&lt;!-- wrighty-… --&gt;</c> pattern. A comment that merely
/// looks like protocol stays ordinary discussion and gets decided like any other.
///
/// The three kinds do not have equal defences, and that asymmetry is deliberate rather than
/// overlooked:
///
/// <list type="bullet">
/// <item><b>Claims</b> carry a strict versioned payload that must parse and satisfy internal
/// invariants. A collaborator could still hand-craft one; that is a pre-existing property of the
/// unsigned claim protocol, is not solved here, and is documented rather than papered over.</item>
/// <item><b>Handovers</b> carry no payload at all — the marker is a bare comment. Strict parsing is
/// therefore impossible, so an author check is the only defence available, and it is required.</item>
/// <item><b>Session reports</b> carry a payload AND require the author check, because a report is
/// visible in the issue and its marker is trivially copyable. Recognising an authorised author's
/// report grants nothing new: that author could already exclude the comment explicitly.</item>
/// </list>
/// </summary>
public static class WrightyCommentClassifier
{
    /// <summary>The strict session-report marker prefix. A bare substring match is never sufficient.</summary>
    public const string SessionReportPrefix = AgentRunReport.MarkerPrefix;

    private const string MarkerSuffix = "-->";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Classifies one comment.
    ///
    /// <paramref name="authorCanExcludeContent"/> answers whether the comment's author satisfies the
    /// same policy that permits explicitly excluding a comment. It is required for the kinds whose
    /// markers are forgeable, and it is a parameter rather than an internal lookup because
    /// resolving it needs backend authorization that this pure classification step must not perform.
    /// </summary>
    public static CommentClassification Classify(
        string? body,
        string? author,
        Func<string?, bool> authorCanExcludeContent)
    {
        if (string.IsNullOrWhiteSpace(body))
            // An empty comment carries no requirements, but it is still ordinary discussion: calling
            // it protocol would let an empty body slip past the decision rules.
            return CommentClassification.Discussion("empty");

        if (ClaimMarker.TryParse(body, out _))
            return new CommentClassification(WrightyCommentKind.Claim);

        // Legacy claim markers are recognised for migration and diagnostics only, never translated.
        if (ClaimMarker.HasLegacyMarker(body))
            return new CommentClassification(WrightyCommentKind.Claim, Reason: "legacy claim marker");

        if (TryParseSessionReport(body, out var report))
        {
            if (!authorCanExcludeContent(author))
                return CommentClassification.Discussion(
                    "session-report marker from an author without exclusion authority");
            return new CommentClassification(WrightyCommentKind.SessionReport, report);
        }

        if (HasHandoverMarker(body))
        {
            if (!authorCanExcludeContent(author))
                return CommentClassification.Discussion(
                    "handover marker from an author without exclusion authority");
            return new CommentClassification(WrightyCommentKind.Handover);
        }

        return CommentClassification.Discussion();
    }

    /// <summary>
    /// Strictly parses a session-report marker. Returns false unless the marker is well formed, its
    /// payload is valid JSON, and every identity field is present and internally usable — a marker
    /// with a missing or blank run id identifies no run and cannot be matched on retry.
    /// </summary>
    public static bool TryParseSessionReport(string? body, out SessionReportMarker marker)
    {
        marker = null!;
        if (string.IsNullOrEmpty(body)) return false;

        var start = body.IndexOf(SessionReportPrefix, StringComparison.Ordinal);
        if (start < 0) return false;
        start += SessionReportPrefix.Length;
        var end = body.IndexOf(MarkerSuffix, start, StringComparison.Ordinal);
        if (end < 0) return false;

        var payload = body[start..end].Trim();
        if (payload.Length == 0) return false;

        try
        {
            var value = JsonSerializer.Deserialize<SessionReportMarker>(payload, JsonOptions);
            if (value is null ||
                string.IsNullOrWhiteSpace(value.ItemId) ||
                string.IsNullOrWhiteSpace(value.RunId) ||
                string.IsNullOrWhiteSpace(value.ReportId))
                return false;
            marker = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Whether a comment carries the handover marker.
    ///
    /// This is a plain substring test, and deliberately so: it must agree with
    /// <see cref="HandoverRenderer.IsHandover"/>, which the backend uses to find the one handover
    /// comment to edit in place. A classifier that were stricter could decide a comment is not a
    /// handover while the renderer still edits it, and the issue would accumulate duplicates.
    ///
    /// The consequence is that this test carries no security weight on its own — the marker has no
    /// payload to validate and appears verbatim in the issue. The author check in
    /// <see cref="Classify"/> is what actually prevents someone hiding a comment by pasting it.
    /// </summary>
    public static bool HasHandoverMarker(string? body) =>
        body is not null && body.Contains(HandoverRenderer.Marker, StringComparison.Ordinal);

    /// <summary>
    /// Whether a report marker belongs to the item it was found on. A report whose payload names a
    /// different item is not this item's protocol comment, however well formed it is — that is the
    /// shape a copied marker takes.
    /// </summary>
    public static bool BelongsTo(SessionReportMarker marker, WorkItemId id) =>
        string.Equals(marker.ItemId, id.Value, StringComparison.Ordinal);
}
