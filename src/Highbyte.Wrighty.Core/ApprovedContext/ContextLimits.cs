namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Bounds on an approved context (plan 030 decision 15). Exceeding a limit fails the launch; it
/// never truncates. Dropping the oldest comments, the newest comments, or the tail of one comment
/// would change the approved requirements while leaving the revision digest looking authoritative.
/// </summary>
public sealed record ContextLimits(
    int MaxDiscussionEntries = ContextLimits.DefaultMaxDiscussionEntries,
    int MaxEntryCharacters = ContextLimits.DefaultMaxEntryCharacters,
    int MaxTotalCharacters = ContextLimits.DefaultMaxTotalCharacters)
{
    public const int DefaultMaxDiscussionEntries = 100;
    public const int DefaultMaxEntryCharacters = 20_000;
    public const int DefaultMaxTotalCharacters = 100_000;

    public static ContextLimits Default { get; } = new();
}

/// <summary>
/// Whether a context fits, and which limit it exceeded if not. The message names counts and limits
/// but never the offending content: these flow into worker events and operator output, and the
/// content is exactly what must not appear there.
/// </summary>
public sealed record ContextLimitResult(bool Within, string? Code = null, string? Message = null)
{
    public const string TooLargeCode = "CONTEXT_TOO_LARGE";

    public static ContextLimitResult Ok { get; } = new(true);

    private static ContextLimitResult Exceeded(string message) => new(false, TooLargeCode, message);

    /// <summary>
    /// Applies the limits to an assembled context.
    ///
    /// Two different populations are counted, deliberately. Every relevant entry counts toward the
    /// entry limit — including excluded and pending ones — because Wrighty still has to retrieve
    /// and classify each of them. Only included bodies count toward the character limit, because
    /// only those reach the agent.
    /// </summary>
    public static ContextLimitResult Check(
        string title,
        string body,
        IReadOnlyList<DiscussionEntry> relevant,
        IReadOnlyList<DiscussionEntry> included,
        ContextLimits limits)
    {
        if (relevant.Count > limits.MaxDiscussionEntries)
            return Exceeded(
                $"The item has {relevant.Count} discussion entries requiring a decision, above the " +
                $"limit of {limits.MaxDiscussionEntries}. Consolidate the discussion or raise " +
                "worker.context.maxDiscussionComments.");

        foreach (var entry in relevant)
        {
            if (entry.Body.Length <= limits.MaxEntryCharacters) continue;
            return Exceeded(
                $"Discussion entry {entry.StableId} is {entry.Body.Length} characters, above the " +
                $"per-entry limit of {limits.MaxEntryCharacters}. Shorten it or raise " +
                "worker.context.maxEntryCharacters.");
        }

        var total = title.Length + body.Length + included.Sum(entry => entry.Body.Length);
        if (total > limits.MaxTotalCharacters)
            return Exceeded(
                $"The approved context is {total} characters, above the total limit of " +
                $"{limits.MaxTotalCharacters}. Consolidate the discussion or raise " +
                "worker.context.maxTotalCharacters.");

        return Ok;
    }

    /// <summary>
    /// Validates a configured limit set. Bounds are applied to the configuration itself so a
    /// mistyped value cannot authorize unbounded allocation from issue-controlled input.
    /// </summary>
    public static ContextLimitResult Validate(ContextLimits limits)
    {
        const int hardMaxEntries = 10_000;
        const int hardMaxCharacters = 5_000_000;

        if (limits.MaxDiscussionEntries <= 0 || limits.MaxEntryCharacters <= 0 ||
            limits.MaxTotalCharacters <= 0)
            return new ContextLimitResult(false, "CONFIG_INVALID",
                "worker.context limits must all be positive.");
        if (limits.MaxDiscussionEntries > hardMaxEntries)
            return new ContextLimitResult(false, "CONFIG_INVALID",
                $"worker.context.maxDiscussionComments must not exceed {hardMaxEntries}.");
        if (limits.MaxEntryCharacters > hardMaxCharacters ||
            limits.MaxTotalCharacters > hardMaxCharacters)
            return new ContextLimitResult(false, "CONFIG_INVALID",
                $"worker.context character limits must not exceed {hardMaxCharacters}.");
        if (limits.MaxEntryCharacters > limits.MaxTotalCharacters)
            return new ContextLimitResult(false, "CONFIG_INVALID",
                "worker.context.maxEntryCharacters must not exceed maxTotalCharacters.");

        return Ok;
    }
}
