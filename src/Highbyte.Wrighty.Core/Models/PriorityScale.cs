namespace Highbyte.Wrighty.Models;

/// <summary>
/// The one priority ranking every ordered surface shares — fresh picks, the queued and
/// continuation scans, boards, and listings all inherit their order from it (plan 037).
///
/// <para>The scale is an ordered list of names owned by the backend: the configured priority list
/// on Local Markdown, the Project field's option order on GitHub. Rank is position in that list —
/// never parsed out of the names, which is how the previous GitHub ranking silently un-ordered any
/// scale that was not numeric.</para>
///
/// <para>An item with no priority ranks after everything. A value the scale does not contain ranks
/// after every scale value but before nothing at all: whoever set it expressed more intent than
/// whoever set none, even if the scale has since moved under them. Unknown values never fail a
/// read — on GitHub the field belongs to the user and its options can change at any time, so a
/// value that stops matching must degrade in order, not in availability.</para>
/// </summary>
public static class PriorityScale
{
    /// <summary>The rank of an item with no priority: after everything else.</summary>
    public const int None = int.MaxValue;

    /// <summary>The rank of a set value the scale does not contain: last among the prioritized.</summary>
    public const int Unknown = int.MaxValue - 1;

    public static int Rank(IReadOnlyList<string>? scale, string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority))
            return None;
        if (scale is not null)
            for (var index = 0; index < scale.Count; index++)
                if (string.Equals(scale[index], priority, StringComparison.OrdinalIgnoreCase))
                    return index;
        return Unknown;
    }
}
