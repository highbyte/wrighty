using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Canonical serialization and digesting of an approved context (plan 030 decision 11).
///
/// The digest identifies the exact normalized content one agent run was given. Two runs share a
/// digest only when they were given identical task text, identical provenance, and identical
/// approval evidence. It is not a signature: it does not authenticate GitHub, prove who approved
/// the content, or replace GitHub permissions.
/// </summary>
public static class ContextRevisionSerializer
{
    /// <summary>
    /// The canonical-form version. Any change to what is serialized, or to the order or framing of
    /// its fields, requires bumping this: the version is hashed, so an old and a new digest of the
    /// same content are deliberately different values in different namespaces, and a stale recorded
    /// revision can never accidentally compare equal to one produced by newer code.
    /// </summary>
    public const int FormatVersion = 1;

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

        // The evidence for EVERY relevant entry, not just the included ones. An entry that was
        // deliberately excluded, or included by a different actor's reaction, is a different
        // approved context even though the included text is identical.
        foreach (var decision in decisions.OrderBy(d => d.CommentId, StringComparer.Ordinal))
        {
            builder.Append("decision").Append(Separator);
            builder.Append(decision.CommentId).Append(Separator);
            builder.Append(decision.Decision.ToString()).Append(Separator);
            builder.Append(decision.Source.ToString()).Append(Separator);
            builder.Append(decision.DecidedBy ?? string.Empty).Append(Separator);
            builder.Append(decision.DecidedAt is { } at ? Instant(at) : string.Empty).Append(Separator);
            builder.Append(decision.ReactionId ?? string.Empty).Append(Terminator);
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
