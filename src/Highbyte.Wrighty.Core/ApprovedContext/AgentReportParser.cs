using System.Text.Json;
using System.Text.RegularExpressions;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// What an agent said about its own run: narrative only, never a verdict. Wrighty decides what a run
/// achieved from its own observation, and nothing parsed here can change that.
/// </summary>
public sealed record AgentReportContent(
    string? Summary = null,
    IReadOnlyList<string>? Changes = null,
    IReadOnlyList<string>? Verification = null,
    IReadOnlyList<string>? Decisions = null,
    IReadOnlyList<string>? RequestedInput = null,
    IReadOnlyList<string>? RemainingWork = null,
    IReadOnlyList<string>? References = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Summary) &&
        (Changes?.Count ?? 0) == 0 &&
        (Verification?.Count ?? 0) == 0 &&
        (Decisions?.Count ?? 0) == 0 &&
        (RequestedInput?.Count ?? 0) == 0 &&
        (RemainingWork?.Count ?? 0) == 0 &&
        (References?.Count ?? 0) == 0;
}

/// <summary>
/// Reads the report block an agent was asked to end its response with.
///
/// Everything here is defensive, because the input is a language model's best effort at a format
/// rather than a serialized object. A missing block, malformed JSON, unexpected types, or an
/// enormous field are all ordinary outcomes rather than errors: the worker still has its own
/// observed facts, and a run that did real work must not be reported as broken because its closing
/// block was mistyped.
///
/// Nothing parsed here is trusted as fact. The fields are an agent's account of its own run, they
/// are labelled as such wherever they are rendered, and no field can carry an outcome — the
/// contract does not ask for one, and this reads none even if a model invents it.
/// </summary>
public static class AgentReportParser
{
    /// <summary>The fenced tag the prompt asks for.</summary>
    public const string BlockTag = "wrighty-report";

    /// <summary>
    /// Caps on what a single report may contribute. A report is published where collaborators read
    /// it, so an agent that pastes a log into a field must not turn a comment into that log.
    /// </summary>
    public const int MaxSummaryCharacters = 2_000;
    public const int MaxItemCharacters = 500;
    public const int MaxItemsPerField = 20;

    /// <summary>How much raw response is kept when there is no usable block.</summary>
    public const int MaxFallbackCharacters = 4_000;

    // Any whitespace after the tag, not specifically a newline. A well-formed block puts its body on
    // the next line, but the block reaches this having crossed a vendor's own output format, and one
    // of them was seen delivering it with the newlines already gone. Refusing that would discard a
    // report the agent got right over a transport detail it never saw.
    private static readonly Regex Block = new(
        @"```" + BlockTag + @"\s+(?<body>.*?)\s*```",
        RegexOptions.Singleline | RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    // An opening fence with nothing closing it, to the end of the message. Only <see
    // cref="WithoutReportBlock"/> uses this, and only after the well-formed blocks are gone, so what
    // remains genuinely has no terminator.
    //
    // It exists because a message can be cut mid-block before anything strips it — a truncation
    // upstream, or a vendor that stopped mid-write — and the result is the worst of both: the block
    // is not parseable as a report, and not removable as a block. Leaving it in publishes half a
    // JSON object as the agent's closing words, and its unclosed fence goes on to break whatever
    // renders it.
    private static readonly Regex UnterminatedBlock = new(
        @"```" + BlockTag + @"[\s\S]*\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// The report an agent's final message carries, or null when it carries none usable.
    ///
    /// The last block wins. An agent that writes an example and then its real report — or that
    /// corrects itself — ends with the one it meant, and taking the first would capture the
    /// illustration instead.
    /// </summary>
    public static AgentReportContent? TryParse(string? finalMessage)
    {
        if (string.IsNullOrWhiteSpace(finalMessage)) return null;

        Match? last = null;
        try
        {
            for (var match = Block.Match(finalMessage); match.Success; match = match.NextMatch())
                last = match;
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological response is not worth more time than the run it describes.
            return null;
        }

        if (last is null) return null;

        try
        {
            using var document = JsonDocument.Parse(last.Groups["body"].Value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var report = new AgentReportContent(
                Text(document.RootElement, "summary", MaxSummaryCharacters),
                Items(document.RootElement, "changes"),
                Items(document.RootElement, "verification"),
                Items(document.RootElement, "decisions"),
                Items(document.RootElement, "requestedInput"),
                Items(document.RootElement, "remainingWork"),
                Items(document.RootElement, "references"));
            return report.IsEmpty ? null : report;
        }
        catch (JsonException)
        {
            // The agent wrote something block-shaped that is not JSON. The raw response is still
            // kept by the caller, so nothing is lost that was not already unstructured.
            return null;
        }
    }

    /// <summary>
    /// The final message with its report block removed, for any surface that quotes an agent's
    /// closing words.
    ///
    /// Every such surface needs this and for the same two reasons: the block's content is already
    /// rendered as structured fields beside it, and a fenced block quoted inside another fenced
    /// block closes it early — which was seen breaking a published GitHub comment before the
    /// handover started stripping it.
    ///
    /// A block whose closing fence is missing is removed too, from its opening fence to the end.
    /// That is deliberately more aggressive than the well-formed case: there is no way to tell where
    /// such a block was meant to end, and keeping a partial JSON object because its terminator was
    /// lost preserves nothing an operator can use. Callers that need the report itself parse it from
    /// the complete response before anything trims it.
    /// </summary>
    public static string? WithoutReportBlock(string? finalMessage)
    {
        if (string.IsNullOrWhiteSpace(finalMessage)) return null;
        try
        {
            var stripped = Block.Replace(finalMessage, string.Empty);
            stripped = UnterminatedBlock.Replace(stripped, string.Empty).Trim();
            return stripped.Length == 0 ? null : stripped;
        }
        catch (RegexMatchTimeoutException)
        {
            return finalMessage.Trim();
        }
    }

    /// <summary>
    /// The bounded raw response, for a run whose agent produced no usable block. Truncation is
    /// marked, because a report that simply stops is indistinguishable from an agent that stopped.
    /// </summary>
    public static string? BoundedFallback(string? finalMessage)
    {
        if (string.IsNullOrWhiteSpace(finalMessage)) return null;
        var trimmed = finalMessage.Trim();
        return trimmed.Length <= MaxFallbackCharacters
            ? trimmed
            : trimmed[..MaxFallbackCharacters] + "\n… (truncated)";
    }

    private static string? Text(JsonElement root, string name, int limit) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Bound(value.GetString(), limit)
            : null;

    /// <summary>
    /// A list field. A model that writes a bare string where a list was asked for has still said
    /// something useful, so that is read as a single item rather than discarded.
    /// </summary>
    private static IReadOnlyList<string>? Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;

        if (value.ValueKind == JsonValueKind.String)
            return Bound(value.GetString(), MaxItemCharacters) is { } single ? [single] : null;

        if (value.ValueKind != JsonValueKind.Array) return null;

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (items.Count == MaxItemsPerField) break;
            var text = element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
            if (Bound(text, MaxItemCharacters) is { } bounded) items.Add(bounded);
        }
        return items.Count == 0 ? null : items;
    }

    private static string? Bound(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= limit ? trimmed : trimmed[..limit] + "…";
    }
}
