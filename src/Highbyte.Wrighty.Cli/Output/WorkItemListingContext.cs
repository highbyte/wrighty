namespace Highbyte.Wrighty.Cli.Output;

public sealed record WorkItemListingContext(
    IReadOnlyList<string> StatusOrder,
    string StatusOrderSource,
    string ArchiveScope,
    string? StatusFilter,
    IReadOnlyDictionary<string, string>? Fields,
    int? Limit,
    int ReturnedCount,
    string Completeness)
{
    public string CountScope { get; } = "returned-items";
}
