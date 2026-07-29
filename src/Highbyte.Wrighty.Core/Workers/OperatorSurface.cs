using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Workers;

/// <summary>Where an operator meets an item when they are not at the Wrighty CLI.</summary>
public enum OperatorSurfaceKind
{
    /// <summary>The local web dashboard, which is Local Markdown only.</summary>
    Dashboard,

    /// <summary>A GitHub issue and its Project fields.</summary>
    GitHubIssue
}

/// <summary>
/// How an operator reaches an item outside the CLI, and what they can do there.
///
/// Handover guidance used to infer this from whether the item had a URL, which is why a GitHub
/// reader was told the dashboard is "Local Markdown only" — true, and no use to them. The two
/// backends differ in ways that change the advice rather than only its wording: only GitHub has a
/// discussion to append a clarification to, and only GitHub has an approval field whose name the
/// repository can change. Guidance that names a field has to name the configured one.
/// </summary>
public sealed record OperatorSurface(
    OperatorSurfaceKind Kind,
    string? ItemUrl = null,
    string ContextApprovalField = "",
    string ApprovedOption = "",
    string DispatchStateField = "")
{
    /// <summary>
    /// Whether a clarification can be added without rewriting what the agent already holds.
    ///
    /// This is the difference that matters after a paused run. Appending to a discussion is
    /// additive, so any worker may carry it to the session; rewriting the description is not, so
    /// only a run an operator names for that item may proceed across it.
    /// </summary>
    public bool HasDiscussion => Kind == OperatorSurfaceKind.GitHubIssue;

    public static OperatorSurface For(TrackerConfig config, string? itemUrl) =>
        itemUrl is { Length: > 0 }
            ? new OperatorSurface(
                OperatorSurfaceKind.GitHubIssue,
                itemUrl,
                config.ContextApprovalField,
                Projects.GitHubContextApprovalReader.ApprovedOption,
                config.DispatchStateField)
            : new OperatorSurface(OperatorSurfaceKind.Dashboard);
}
