namespace Highbyte.Wrighty.Text;

/// <summary>
/// Count-aware pluralization for user-facing messages, so output reads "1 item" / "2 items"
/// instead of the mechanical "item(s)".
/// </summary>
public static class Grammar
{
    /// <summary>The noun with a plain "s" appended when the count is not one.</summary>
    public static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    /// <summary>An explicit singular/plural pair, for irregular forms and verb phrases
    /// ("item was"/"items were").</summary>
    public static string Plural(int count, string singular, string plural) =>
        count == 1 ? singular : plural;
}
