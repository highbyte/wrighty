using System.Globalization;
using System.Text.Json;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.GitHub;

/// <summary>
/// Reads one issue's complete conversation: base content, the evidence binding it to an approval,
/// and every comment with its provenance and reactions.
///
/// Completeness is the contract. A conversation read that stopped early is indistinguishable from
/// one where a comment was deleted, and deletion is detected precisely by a comment being absent —
/// so a short read would silently look like tampering, or worse, hide a pending comment and let a
/// launch proceed. Every failure here raises rather than returning what it managed to collect.
/// </summary>
public sealed class GitHubConversationReader(GhApi api)
{
    /// <summary>
    /// Comments per page. GitHub caps nested connections, and reactions are fetched inline per
    /// comment, so this stays well below the limit rather than trading round-trips for a query the
    /// server may reject outright.
    /// </summary>
    private const int CommentPageSize = 50;

    /// <summary>
    /// Reactions fetched per comment. A comment with more than this many is not silently truncated
    /// — it raises, because a missing reaction could be the one authorised decision that would have
    /// resolved the comment, and quietly dropping it would leave the comment looking undecided.
    /// </summary>
    private const int ReactionPageSize = 100;

    private const string ApiErrorCode = "GH_API_ERROR";
    private const string CreatedAtField = "createdAt";

    private const string ConversationQuery = """
        query($owner: String!, $repo: String!, $number: Int!, $comments: Int!, $reactions: Int!, $cursor: String) {
          repository(owner: $owner, name: $repo) {
            issue(number: $number) {
              title
              body
              url
              createdAt
              lastEditedAt
              titleChanges: timelineItems(last: 100, itemTypes: [RENAMED_TITLE_EVENT]) {
                nodes { ... on RenamedTitleEvent { createdAt } }
              }
              comments(first: $comments, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  databaseId
                  author { login }
                  authorAssociation
                  body
                  createdAt
                  lastEditedAt
                  url
                  isMinimized
                  minimizedReason
                  reactions(first: $reactions) {
                    totalCount
                    nodes {
                      id
                      content
                      createdAt
                      user { login }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<GitHubConversation> ReadAsync(
        string host,
        string owner,
        string repository,
        int number,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        var comments = new List<GitHubComment>();
        JsonElement issue = default;
        var first = true;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = await api.GraphQlAsync(host, ConversationQuery, new
            {
                owner,
                repo = repository,
                number,
                comments = CommentPageSize,
                reactions = ReactionPageSize,
                cursor
            }, cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("repository", out var repositoryNode) ||
                repositoryNode.ValueKind == JsonValueKind.Null ||
                !repositoryNode.TryGetProperty("issue", out var issueNode) ||
                issueNode.ValueKind == JsonValueKind.Null)
                throw new TrackerException(
                    ApiErrorCode,
                    $"Issue {owner}/{repository}#{number} could not be read.",
                    10);

            // The base content is taken from the first page only. Later pages are re-reads of the
            // same issue, and letting a mid-read edit change the title or body underneath the loop
            // would produce a snapshot that never existed at any single moment.
            if (first)
            {
                issue = issueNode.Clone();
                first = false;
            }

            var connection = issueNode.GetProperty("comments");
            foreach (var node in connection.GetProperty("nodes").EnumerateArray())
                comments.Add(ReadComment(node, owner, repository, number));

            var pageInfo = connection.GetProperty("pageInfo");
            cursor = pageInfo.GetProperty("hasNextPage").GetBoolean()
                ? pageInfo.GetProperty("endCursor").GetString()
                    ?? throw new TrackerException(
                        ApiErrorCode,
                        $"Issue {owner}/{repository}#{number} reported another page of comments " +
                        "without a cursor to fetch it.",
                        10)
                : null;
        }
        while (cursor is not null);

        return new GitHubConversation(
            issue.GetProperty("title").GetString() ?? string.Empty,
            issue.GetProperty("body").GetString() ?? string.Empty,
            issue.GetProperty("url").GetString() ?? string.Empty,
            Instant(issue, CreatedAtField) ?? DateTimeOffset.MinValue,
            Instant(issue, "lastEditedAt"),
            LatestTitleChange(issue.GetProperty("titleChanges")),
            comments);
    }

    private static GitHubComment ReadComment(
        JsonElement node, string owner, string repository, int number)
    {
        var id = node.GetProperty("databaseId");
        if (id.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new TrackerException(
                ApiErrorCode,
                $"A comment on {owner}/{repository}#{number} has no stable identifier, so it " +
                "cannot be approved or compared across reads.",
                10);

        var reactions = node.GetProperty("reactions");
        var reactionCount = reactions.GetProperty("totalCount").GetInt32();
        if (reactionCount > ReactionPageSize)
            throw new TrackerException(
                ApiErrorCode,
                $"Comment {id} on {owner}/{repository}#{number} has {reactionCount} reactions, " +
                $"above the {ReactionPageSize} this read retrieves. Refusing rather than deciding " +
                "the comment from an incomplete set.",
                10);

        return new GitHubComment(
            id.GetRawText(),
            // A deleted account reports a null author. It is named rather than left blank so the
            // provenance line in a prompt cannot silently look like it came from nobody.
            Login(node, "author") ?? "(unknown)",
            Text(node, "authorAssociation"),
            Instant(node, CreatedAtField) ?? DateTimeOffset.MinValue,
            Instant(node, "lastEditedAt"),
            Text(node, "url") ?? string.Empty,
            node.GetProperty("body").GetString() ?? string.Empty,
            node.GetProperty("isMinimized").GetBoolean(),
            Text(node, "minimizedReason"),
            ReadReactions(reactions));
    }

    private static List<GitHubReaction> ReadReactions(JsonElement reactions)
    {
        var result = new List<GitHubReaction>();
        foreach (var node in reactions.GetProperty("nodes").EnumerateArray())
        {
            var actor = Login(node, "user");
            // A reaction whose actor cannot be resolved can never satisfy an approver policy, so it
            // is dropped here rather than carried forward as an undecidable decision.
            if (actor is null) continue;
            result.Add(new GitHubReaction(
                node.GetProperty("id").GetString() ?? string.Empty,
                actor,
                Text(node, "content") ?? string.Empty,
                Instant(node, CreatedAtField) ?? DateTimeOffset.MinValue));
        }
        return result;
    }

    /// <summary>
    /// The most recent title rename, or null when the title has never changed. This is the only
    /// observable evidence of a title edit: it advances neither the issue's edit timestamp nor its
    /// user-content edit history.
    /// </summary>
    private static DateTimeOffset? LatestTitleChange(JsonElement titleChanges)
    {
        DateTimeOffset? latest = null;
        foreach (var node in titleChanges.GetProperty("nodes").EnumerateArray())
        {
            if (Instant(node, CreatedAtField) is not { } at) continue;
            if (latest is null || at > latest) latest = at;
        }
        return latest;
    }

    private static string? Login(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.GetProperty("login").GetString()
            : null;

    private static string? Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Instant(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
