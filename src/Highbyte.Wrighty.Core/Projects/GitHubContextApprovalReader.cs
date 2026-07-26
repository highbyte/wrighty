using System.Globalization;
using System.Text.Json;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;

namespace Highbyte.Wrighty.Projects;

/// <summary>
/// Reads the Project single-select field that approves an item's content.
///
/// Only an exact, schema-resolved approved option counts. Unset, unknown, and anything that merely
/// looks affirmative all fail closed as "needs review" — this field decides whether an unattended
/// agent may act on issue text, so an unrecognised value must never read as consent.
///
/// The instant used as the cutoff is the field VALUE's own <c>updatedAt</c>, not the item's or the
/// issue's. That distinction was measured rather than assumed: writing an unrelated Project field
/// leaves this one alone, so an ordinary policy edit cannot silently manufacture content approval.
///
/// Writing the option already held does not advance it either, on any path, which is why approving
/// again has to move the field away and back. The Projects UI enforces this incidentally — it gives
/// no way to re-select the current value, so renewing approval there means clearing the field and
/// setting it again. It also means the field goes on displaying "approved" after an edit that
/// invalidated it, which is why the refusal message has to name the remedy and why the optional
/// edit workflow resets the field rather than relying on the maintainer noticing.
/// </summary>
public sealed class GitHubContextApprovalReader(GhApi api)
{
    /// <summary>The exact option that approves content. Compared case-insensitively but not fuzzily.</summary>
    public const string ApprovedOption = "Approved";

    /// <summary>The exact option meaning the content still needs a maintainer's review.</summary>
    public const string NeedsReviewOption = "Needs review";

    public static IReadOnlyList<(string Name, string Description, string Color)> Options { get; } =
    [
        (NeedsReviewOption, "The current title, body, and discussion still need review", "GRAY"),
        (ApprovedOption, "The current title, body, and reviewed discussion may be given to an agent", "GREEN")
    ];

    private const string ApprovalQuery = """
        query($owner: String!, $repo: String!, $number: Int!, $field: String!) {
          repository(owner: $owner, name: $repo) {
            issue(number: $number) {
              projectItems(first: 20, includeArchived: true) {
                nodes {
                  project { number }
                  approval: fieldValueByName(name: $field) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name updatedAt }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>
    /// Resolves the approval for one issue. Returns <see cref="ContextApproval.NotApproved"/>
    /// whenever approval cannot be established — including when the field is missing, the item is
    /// not on the configured Project, or the value carries no timestamp to use as a cutoff.
    /// </summary>
    public async Task<ContextApproval> ReadAsync(
        TrackerConfig config,
        string owner,
        string repository,
        int number,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(config.GitHubHost, ApprovalQuery, new
        {
            owner,
            repo = repository,
            number,
            field = config.ContextApprovalField
        }, cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("repository", out var repositoryNode) ||
            repositoryNode.ValueKind != JsonValueKind.Object ||
            !repositoryNode.TryGetProperty("issue", out var issue) ||
            issue.ValueKind != JsonValueKind.Object)
            return ContextApproval.NotApproved;

        foreach (var item in issue.GetProperty("projectItems").GetProperty("nodes").EnumerateArray())
        {
            // An issue can sit on several Projects. Only the configured one carries authority; a
            // stray board with a same-named field must not be able to approve anything.
            if (!item.TryGetProperty("project", out var project) ||
                project.ValueKind != JsonValueKind.Object ||
                !project.TryGetProperty("number", out var projectNumber) ||
                projectNumber.GetInt32() != config.ProjectNumber)
                continue;

            if (!item.TryGetProperty("approval", out var approval) ||
                approval.ValueKind != JsonValueKind.Object)
                return ContextApproval.NotApproved;

            var name = approval.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
            if (!string.Equals(name, ApprovedOption, StringComparison.OrdinalIgnoreCase))
                return ContextApproval.NotApproved;

            // An approved option with no readable timestamp cannot bind a revision, so it approves
            // nothing. This is the one case where the field looks affirmative and still must not be
            // treated as consent.
            if (!approval.TryGetProperty("updatedAt", out var updatedAt) ||
                updatedAt.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(updatedAt.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var approvedAt))
                return ContextApproval.NotApproved;

            // The same instant serves both roles by design: it is when the title and body were
            // approved, and the cutoff before which comments are covered by that same gesture.
            return new ContextApproval(
                ContextApprovalSource.ProjectField,
                approvedAt,
                approvedAt);
        }

        return ContextApproval.NotApproved;
    }
}
