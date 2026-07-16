using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.LocalMarkdown;

public sealed record LocalMarkdownImportRequest(
    IReadOnlyList<string> Paths,
    bool Recursive,
    bool Archive,
    bool Move,
    bool DryRun,
    IReadOnlyDictionary<string, string> FieldMappings,
    string? ForceStatus);

public sealed record LocalMarkdownImportItem(
    string SourcePath,
    int Id,
    string DestinationPath,
    string Title,
    string Status,
    string? Priority);

public sealed record LocalMarkdownImportResult(
    bool DryRun,
    bool Moved,
    IReadOnlyList<LocalMarkdownImportItem> Items);

public interface ILocalMarkdownImportBackend
{
    Task<LocalMarkdownImportResult> ImportAsync(
        TrackerConfig config,
        LocalMarkdownImportRequest request,
        CancellationToken cancellationToken);
}
