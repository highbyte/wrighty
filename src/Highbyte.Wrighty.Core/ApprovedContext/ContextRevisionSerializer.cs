using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Canonical serialization and digesting of an approved context (plan 030 decision 11).
///
/// The digest identifies the exact normalized content one agent run was given. Two runs share a
/// digest only when they were given identical task text with identical provenance, and the same set
/// of relevant entries was resolved the same way. It is not a signature: it does not authenticate
/// GitHub, prove who approved the content, or replace GitHub permissions.
///
/// What it deliberately does NOT cover is the evidence behind each decision — who decided, when,
/// through which route, with which reaction. That evidence is recorded and shown as diagnostics but
/// is not hashed, so re-approving unchanged content produces the same digest no matter who or what
/// re-approved it. Hashing it made an operator cycling the approval field, or a comment whose
/// decision source shifted between equally valid routes, look like a context change to a running
/// session; the property the digest is actually relied on for is the content the model saw.
/// </summary>
public static class ContextRevisionSerializer
{
    /// <summary>
    /// The canonical-form version. Any change to what is serialized, or to the order or framing of
    /// its fields, requires bumping this: the version is hashed, so an old and a new digest of the
    /// same content are deliberately different values in different namespaces, and a stale recorded
    /// revision can never accidentally compare equal to one produced by newer code.
    /// </summary>
    public const int FormatVersion = 2;

    // ASCII unit/record separators, written as escapes rather than literal control characters
    // so the canonical form survives editors, diffs, and copy-paste. Neither can appear in
    // GitHub content, so no field can forge a boundary and make two different contexts hash
    // alike.
    private const char Separator = '\u001f';
    private const char Terminator = '\u001e';

    /// <summary>
    /// Produces the canonical text that gets hashed. Exposed for tests and diagnostics only — it
    /// contains the full approved content, so it must never be logged, emitted in an event, or put
    /// in an error message.
    /// </summary>
    public static string Canonicalize(
        WorkItemId itemId,
        string title,
        string body,
        string? sourceUrl,
        IReadOnlyList<DiscussionEntry> included,
        IReadOnlyList<DiscussionDecision> decisions)
    {
        var builder = new StringBuilder();
        Field(builder, "format", FormatVersion.ToString(CultureInfo.InvariantCulture));
        Field(builder, "item", itemId.Value);
        Field(builder, "title", Normalize(title));
        Field(builder, "body", Normalize(body));
        Field(builder, "url", sourceUrl ?? string.Empty);

        // Chronological by creation, then by stable ID, so the same set never hashes two ways.
        foreach (var entry in Order(included))
        {
            builder.Append("entry").Append(Separator);
            builder.Append(entry.StableId).Append(Separator);
            builder.Append(entry.Author).Append(Separator);
            builder.Append(entry.AuthorAssociation ?? string.Empty).Append(Separator);
            builder.Append(Instant(entry.CreatedAt)).Append(Separator);
            builder.Append(entry.LastEditedAt is { } edited ? Instant(edited) : string.Empty).Append(Separator);
            builder.Append(entry.Url ?? string.Empty).Append(Separator);
            builder.Append(entry.Minimized ? "minimized" : "visible").Append(Separator);
            builder.Append(Normalize(entry.Body)).Append(Terminator);
        }

        // The resolution of EVERY relevant entry, not just the included ones: an entry that was
        // deliberately excluded is part of what was approved, so one silently appearing or
        // disappearing has to be visible even though it contributes no text.
        //
        // Only the identity and the resolution are covered. The evidence behind the resolution is
        // deliberately absent — see the type's remarks.
        foreach (var decision in decisions.OrderBy(d => d.CommentId, StringComparer.Ordinal))
        {
            builder.Append("decision").Append(Separator);
            builder.Append(decision.CommentId).Append(Separator);
            builder.Append(decision.Decision.ToString()).Append(Terminator);
        }

        return builder.ToString();
    }

    /// <summary>Computes the revision for an assembled context.</summary>
    public static ContextRevision Compute(
        WorkItemId itemId,
        string title,
        string body,
        string? sourceUrl,
        IReadOnlyList<DiscussionEntry> included,
        IReadOnlyList<DiscussionDecision> decisions,
        DateTimeOffset capturedAt) =>
        new(FormatVersion,
            Digest(Canonicalize(itemId, title, body, sourceUrl, included, decisions)),
            capturedAt);

    /// <summary>Deterministic chronological ordering: creation time, then stable ID as the tiebreak.</summary>
    public static IReadOnlyList<DiscussionEntry> Order(IReadOnlyList<DiscussionEntry> entries) =>
        entries
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.StableId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// A hash of one piece of content, used for the per-entry and title/body manifest fields. Same
    /// normalization as the canonical form, so a manifest hash and the digest agree about what
    /// "unchanged" means.
    /// </summary>
    public static string HashContent(string content) => Digest(Normalize(content));

    private static void Field(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append(Separator).Append(value).Append(Terminator);

    /// <summary>
    /// Normalizes ONLY representation details that cannot change meaning. Line endings collapse to
    /// LF because a backend may return either. Nothing else is touched: trimming whitespace,
    /// rewriting links, rendering HTML, or summarizing would silently alter the approved
    /// requirements while leaving the digest looking authoritative.
    /// </summary>
    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace("\r", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Round-trip UTC instants, so an equal moment expressed in two offsets hashes identically.
    /// </summary>
    private static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Digest(string canonical) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
