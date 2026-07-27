using System.Text.Json;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;

namespace Highbyte.Wrighty.UnitTests.GitHub;

/// <summary>
/// The reader's contract is completeness. A conversation read that stopped early cannot be told
/// apart from one where a comment was deleted — and deletion is detected precisely by a comment
/// being absent — so a short read would look like tampering, or hide a comment that should have
/// blocked the launch. Every incomplete case here must raise rather than return what it collected.
/// </summary>
public class GitHubConversationReaderTests
{
    private sealed class QueueGhProcess(params string[] responses) : IGhProcess
    {
        private readonly Queue<string> responses = new(responses);

        public List<string?> Inputs { get; } = [];

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
        {
            Inputs.Add(standardInput);
            return Task.FromResult(new GhProcessResult(0, responses.Dequeue(), string.Empty));
        }
    }

    private static string Comment(
        long id,
        string author = "octocat",
        string body = "Please also handle the empty case.",
        string createdAt = "2026-07-26T10:00:00Z",
        string? lastEditedAt = null,
        bool minimized = false,
        string? reactions = null,
        int reactionTotal = 0) =>
        $$"""
        {
          "databaseId": {{id}},
          "author": {{(author is null ? "null" : $"{{\"login\":\"{author}\"}}")}},
          "authorAssociation": "MEMBER",
          "body": {{JsonSerializer.Serialize(body)}},
          "createdAt": "{{createdAt}}",
          "lastEditedAt": {{(lastEditedAt is null ? "null" : $"\"{lastEditedAt}\"")}},
          "url": "https://github.com/owner/repo/issues/42#issuecomment-{{id}}",
          "isMinimized": {{(minimized ? "true" : "false")}},
          "minimizedReason": {{(minimized ? "\"outdated\"" : "null")}},
          "reactions": { "totalCount": {{reactionTotal}}, "nodes": [{{reactions ?? ""}}] }
        }
        """;

    private static string Page(
        string comments,
        bool hasNextPage = false,
        string? endCursor = null,
        string title = "Add retry handling",
        string body = "The worker should retry once.",
        string? lastEditedAt = null,
        int bodyEdits = 0,
        string? titleChanges = null,
        int titleChangeTotal = 0) =>
        $$"""
        { "data": { "repository": { "issue": {
          "title": {{JsonSerializer.Serialize(title)}},
          "body": {{JsonSerializer.Serialize(body)}},
          "url": "https://github.com/owner/repo/issues/42",
          "createdAt": "2026-07-26T09:00:00Z",
          "lastEditedAt": {{(lastEditedAt is null ? "null" : $"\"{lastEditedAt}\"")}},
          "userContentEdits": { "totalCount": {{bodyEdits}} },
          "titleChanges": { "totalCount": {{titleChangeTotal}}, "nodes": [{{titleChanges ?? ""}}] },
          "comments": {
            "pageInfo": { "hasNextPage": {{(hasNextPage ? "true" : "false")}},
                          "endCursor": {{(endCursor is null ? "null" : $"\"{endCursor}\"")}} },
            "nodes": [{{comments}}]
          }
        } } } }
        """;

    private static Task<GitHubConversation> Read(params string[] responses) =>
        new GitHubConversationReader(new GhApi(new QueueGhProcess(responses)))
            .ReadAsync("github.com", "owner", "repo", 42, CancellationToken.None);

    [Fact]
    public async Task ASinglePageIsRead()
    {
        var conversation = await Read(Page(Comment(1)));

        Assert.Equal("Add retry handling", conversation.Title);
        Assert.Equal("The worker should retry once.", conversation.Body);
        Assert.Single(conversation.Comments);
        Assert.Equal("1", conversation.Comments[0].StableId);
        Assert.Equal("octocat", conversation.Comments[0].Author);
        Assert.Equal("MEMBER", conversation.Comments[0].AuthorAssociation);
    }

    [Fact]
    public async Task EveryPageIsFollowed()
    {
        var conversation = await Read(
            Page(Comment(1), hasNextPage: true, endCursor: "c1"),
            Page(Comment(2), hasNextPage: true, endCursor: "c2"),
            Page(Comment(3)));

        Assert.Equal(["1", "2", "3"], conversation.Comments.Select(c => c.StableId));
    }

    [Fact]
    public async Task TheCursorIsPassedToTheNextPage()
    {
        var process = new QueueGhProcess(
            Page(Comment(1), hasNextPage: true, endCursor: "cursor-1"),
            Page(Comment(2)));
        await new GitHubConversationReader(new GhApi(process))
            .ReadAsync("github.com", "owner", "repo", 42, CancellationToken.None);

        Assert.Equal(2, process.Inputs.Count);
        Assert.Contains("cursor-1", process.Inputs[1]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaseContentComesFromTheFirstPageOnly()
    {
        // A title edit landing mid-read must not be spliced into a snapshot of the earlier pages;
        // that snapshot never existed at any single moment.
        var conversation = await Read(
            Page(Comment(1), hasNextPage: true, endCursor: "c1", title: "Original"),
            Page(Comment(2), title: "Renamed mid-read"));

        Assert.Equal("Original", conversation.Title);
    }

    [Fact]
    public async Task AMissingCursorOnAFurtherPageRaisesRatherThanTruncating()
    {
        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Read(Page(Comment(1), hasNextPage: true, endCursor: null)));

        Assert.Contains("cursor", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingIssueRaises()
    {
        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Read("""{ "data": { "repository": { "issue": null } } }"""));

        Assert.Contains("could not be read", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACommentWithoutAStableIdRaises()
    {
        var comment = Comment(1).Replace("\"databaseId\": 1", "\"databaseId\": null", StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<TrackerException>(() => Read(Page(comment)));

        Assert.Contains("stable identifier", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoreReactionsThanAreFetchedRaisesRatherThanDeciding()
    {
        // A dropped reaction could be the one authorised decision that would have resolved the
        // comment, which would leave it looking undecided for reasons nobody could see.
        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            Read(Page(Comment(1, reactionTotal: 500))));

        Assert.Contains("reactions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReactionsAreReadWithTheirActorAndTime()
    {
        var reaction = """
            { "id": "REACTION_1", "content": "THUMBS_UP", "createdAt": "2026-07-26T11:00:00Z",
              "user": { "login": "maintainer" } }
            """;
        var conversation = await Read(Page(Comment(1, reactions: reaction, reactionTotal: 1)));

        var actual = Assert.Single(conversation.Comments[0].Reactions);
        Assert.Equal("REACTION_1", actual.Id);
        Assert.Equal("maintainer", actual.Actor);
        Assert.Equal("THUMBS_UP", actual.Content);
        Assert.Equal(new DateTimeOffset(2026, 7, 26, 11, 0, 0, TimeSpan.Zero), actual.CreatedAt);
    }

    [Fact]
    public async Task AReactionFromADeletedAccountIsDropped()
    {
        // It can never satisfy an approver policy, so carrying it forward would only produce an
        // undecidable decision.
        var reaction = """
            { "id": "R1", "content": "THUMBS_UP", "createdAt": "2026-07-26T11:00:00Z", "user": null }
            """;
        var conversation = await Read(Page(Comment(1, reactions: reaction, reactionTotal: 1)));

        Assert.Empty(conversation.Comments[0].Reactions);
    }

    [Fact]
    public async Task ACommentFromADeletedAccountIsNamedRatherThanBlank()
    {
        var comment = Comment(1).Replace("{\"login\":\"octocat\"}", "null", StringComparison.Ordinal);
        var conversation = await Read(Page(comment));

        Assert.Equal("(unknown)", conversation.Comments[0].Author);
    }

    [Fact]
    public async Task AnEditedCommentReportsTheEditAsItsRevision()
    {
        var conversation = await Read(Page(Comment(1, lastEditedAt: "2026-07-26T12:00:00Z")));
        var comment = conversation.Comments[0];

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero), comment.RevisionAt);
        Assert.NotEqual(comment.CreatedAt, comment.RevisionAt);
    }

    [Fact]
    public async Task MinimizedStateIsCarriedThroughToTheEntry()
    {
        var conversation = await Read(Page(Comment(1, minimized: true)));
        var entry = conversation.Comments[0].ToEntry();

        Assert.True(entry.Minimized);
        Assert.Equal("outdated", conversation.Comments[0].MinimizedReason);
        // Hiding advances no timestamp, so the revision is still the creation instant.
        Assert.Equal(conversation.Comments[0].CreatedAt, entry.RevisionAt);
    }

    [Fact]
    public async Task ABodyEditIsBoundThroughTheIssuesEditMetadata()
    {
        var conversation = await Read(Page(Comment(1), lastEditedAt: "2026-07-26T13:00:00Z", bodyEdits: 2));
        var revision = conversation.ToBaseRevision();

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 13, 0, 0, TimeSpan.Zero), revision.BodyLastEditedAt);
        Assert.Equal(2, revision.BodyEditCount);
    }

    [Fact]
    public async Task ATitleEditIsBoundThroughRenameEventsInstead()
    {
        // The title advances neither lastEditedAt nor the edit history, so the rename event is the
        // only evidence there is. Reading the issue's edit metadata alone would miss it entirely.
        var renames = """
            { "createdAt": "2026-07-26T14:00:00Z" }, { "createdAt": "2026-07-26T15:00:00Z" }
            """;
        var conversation = await Read(Page(Comment(1), titleChanges: renames, titleChangeTotal: 2));
        var revision = conversation.ToBaseRevision();

        Assert.Equal(new DateTimeOffset(2026, 7, 26, 15, 0, 0, TimeSpan.Zero), revision.TitleLastRenamedAt);
        Assert.Equal(2, revision.TitleRenameCount);
        Assert.Null(revision.BodyLastEditedAt);
    }

    [Fact]
    public async Task AnUneditedIssueIsCoveredByAnyApproval()
    {
        var revision = (await Read(Page(Comment(1)))).ToBaseRevision();

        Assert.Null(revision.LastChangedAt);
        Assert.True(revision.IsCoveredBy(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task ATitleRenameAfterApprovalIsNotCovered()
    {
        var approved = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var renames = """{ "createdAt": "2026-07-26T14:00:00Z" }""";
        var revision = (await Read(Page(Comment(1), titleChanges: renames, titleChangeTotal: 1)))
            .ToBaseRevision();

        Assert.False(revision.IsCoveredBy(approved));
    }

    [Fact]
    public async Task AnEmptyConversationIsValid()
    {
        var conversation = await Read(Page(""));

        Assert.Empty(conversation.Comments);
        Assert.Equal("Add retry handling", conversation.Title);
    }

    [Fact]
    public async Task CommentsProjectOntoTheBackendNeutralModel()
    {
        var conversation = await Read(Page(Comment(1, body: "exact **markdown** preserved")));
        var entry = conversation.Comments[0].ToEntry();

        Assert.Equal("1", entry.StableId);
        Assert.Equal("exact **markdown** preserved", entry.Body);
        Assert.Equal("MEMBER", entry.AuthorAssociation);
        Assert.Equal("https://github.com/owner/repo/issues/42#issuecomment-1", entry.Url);
    }
}
