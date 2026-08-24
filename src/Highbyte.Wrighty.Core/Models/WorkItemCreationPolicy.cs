using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Models;

/// <summary>
/// Keeps newly authored work at an entry point in the workflow. Import and adoption have separate
/// semantics because they preserve work that may already be active or complete.
/// </summary>
public static class WorkItemCreationPolicy
{
    public static IReadOnlyList<string> AllowedStatuses(
        TrackerConfig config,
        IEnumerable<string> statuses) =>
        statuses.Where(status => Restriction(config, status) is null).ToArray();

    public static string? Restriction(TrackerConfig config, string? status)
    {
        if (Matches(status, config.DefaultPickTo))
            return "the active-work destination (defaultPickTo)";
        if (Matches(status, config.DefaultFinishTo))
            return "the completion destination (defaultFinishTo)";
        if (config.Archive.OnStatuses.Any(candidate => Matches(status, candidate)))
            return "an archive-triggering terminal status";
        return null;
    }

    public static void EnsureAllowed(TrackerConfig config, string status)
    {
        if (Restriction(config, status) is not { } restriction)
            return;

        throw new TrackerException(
            "ARGUMENT_INVALID",
            $"Status '{status}' cannot be used to create a work item because it is {restriction}. " +
            "Create the item in a backlog or worker-queue status, then advance it through the workflow.",
            2);
    }

    private static bool Matches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
