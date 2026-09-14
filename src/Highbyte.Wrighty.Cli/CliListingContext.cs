using Highbyte.Wrighty.Cli.Output;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Cli;

public sealed partial class CliApplication
{
    private async Task<WorkItemListingContext> DescribeListingAsync(TrackerConfig config,
        ListWorkItemsRequest request, int count, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> statuses;
        try
        {
            statuses = await tracker.Backend(config).WorkflowStatusesAsync(config, cancellationToken);
        }
        catch (Exception exception) when (exception is TrackerException or IOException or UnauthorizedAccessException)
        {
            statuses = [];
        }
        return new(statuses, statuses.Count > 0 ? "configured" : "unknown",
            request.ArchiveScope.ToString().ToLowerInvariant(), request.Status, request.Fields,
            request.Limit, count, request.Limit is { } limit && count >= limit ? "possibly-truncated" : "complete");
    }
}
