using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Reaction names, normalized across the two vocabularies GitHub uses for the same thing.
///
/// The REST API and configuration both spell these <c>+1</c>, <c>-1</c>, <c>hooray</c>; GraphQL
/// spells them <c>THUMBS_UP</c>, <c>THUMBS_DOWN</c>, <c>HOORAY</c>. Decisions are read over GraphQL
/// but configured in the REST spelling, so every comparison goes through here rather than matching
/// raw strings — a mismatch would silently make every reaction fail to decide anything, which looks
/// exactly like nobody having reacted.
/// </summary>
public static class ReactionKinds
{
    public const string ThumbsUp = "+1";
    public const string ThumbsDown = "-1";
    public const string Hooray = "hooray";
    public const string Rocket = "rocket";

    /// <summary>Every reaction GitHub supports, in the configuration spelling.</summary>
    public static IReadOnlyList<string> All { get; } =
        [ThumbsUp, ThumbsDown, "laugh", Hooray, "confused", "heart", Rocket, "eyes"];

    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["THUMBS_UP"] = ThumbsUp,
        ["+1"] = ThumbsUp,
        ["THUMBSUP"] = ThumbsUp,
        ["THUMBS_DOWN"] = ThumbsDown,
        ["-1"] = ThumbsDown,
        ["THUMBSDOWN"] = ThumbsDown,
        ["LAUGH"] = "laugh",
        ["HOORAY"] = Hooray,
        ["TADA"] = Hooray,
        ["CONFUSED"] = "confused",
        ["HEART"] = "heart",
        ["ROCKET"] = Rocket,
        ["EYES"] = "eyes"
    };

    /// <summary>
    /// Normalizes a reaction name from either vocabulary, or null when it is not one GitHub
    /// supports. Null rather than a fallback: an unrecognized reaction must never accidentally
    /// compare equal to a configured decision reaction.
    /// </summary>
    public static string? Normalize(string? value) =>
        value is not null && Canonical.TryGetValue(value.Trim(), out var canonical) ? canonical : null;

    public static bool Matches(string? left, string? right)
    {
        var a = Normalize(left);
        return a is not null && a == Normalize(right);
    }

    /// <summary>
    /// Validates a configured reaction name, rejecting anything GitHub does not support. A typo
    /// here would otherwise produce a policy that can never match, and an operator would see
    /// approvals silently doing nothing.
    /// </summary>
    public static string Parse(string? value, string property) =>
        Normalize(value) ?? throw new TrackerException(
            "CONFIG_INVALID",
            $"{property} must be one of: {string.Join(", ", All)}.",
            3);
}
