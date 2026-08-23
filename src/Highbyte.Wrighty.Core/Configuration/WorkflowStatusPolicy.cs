namespace Highbyte.Wrighty.Configuration;

/// <summary>
/// Shared workflow-role rules derived from the configured status vocabulary. Keeping this in the
/// configuration layer ensures the web console and direct configuration edits agree about which
/// statuses are active workflow states rather than terminal archive candidates.
/// </summary>
public static class WorkflowStatusPolicy
{
    public static string? InferBacklogStatus(
        TrackerConfig config,
        IEnumerable<string> statuses) =>
        statuses.FirstOrDefault(status =>
            !Matches(status, config.DefaultPickFrom) &&
            !Matches(status, config.DefaultPickTo) &&
            !Matches(status, config.DefaultFinishTo));

    /// <summary>
    /// Returns why <paramref name="status"/> cannot trigger automatic archiving, or null when it
    /// is not one of Wrighty's known non-terminal workflow roles.
    /// </summary>
    public static string? ArchiveRestriction(
        TrackerConfig config,
        IEnumerable<string> statuses,
        string status)
    {
        if (Matches(status, config.DefaultPickFrom))
            return "the worker-pick source (defaultPickFrom)";
        if (Matches(status, config.DefaultPickTo))
            return "the active-work destination (defaultPickTo)";

        var ordered = statuses as IReadOnlyList<string> ?? statuses.ToArray();
        var backlog = ordered.Count > 0 && Matches(ordered[0], config.DefaultPickFrom)
            ? ordered[0]
            : InferBacklogStatus(config, ordered);
        return Matches(status, backlog) ? "the backlog status" : null;
    }

    private static bool Matches(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
