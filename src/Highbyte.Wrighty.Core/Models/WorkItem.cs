using Highbyte.Wrighty.GitHub;

namespace Highbyte.Wrighty.Models;

public sealed record GitHubProjectItem(
    GitHubWorkItemAddress Address,
    WorkItemSummary Summary,
    string IssueNodeId,
    string ProjectItemId,
    string? CreationAttemptId = null)
{
    public int Number => Address.IssueNumber;

    public string Title => Summary.Title;

    public string? Url => Summary.Url;

    public string? Status => Summary.Status;

    public string? Priority => Summary.Priority;
}
