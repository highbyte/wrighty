using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

public class GitHubContextApprovalReaderTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 7
    };

    private sealed class QueueGhProcess(params string[] responses) : IGhProcess
    {
        private readonly Queue<string> responses = new(responses);
        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct) =>
            Task.FromResult(new GhProcessResult(0, responses.Dequeue(), string.Empty));
    }

    private static string Response(string? name, string? updatedAt, int projectNumber = 7) =>
        $$"""
        { "data": { "repository": { "issue": { "projectItems": { "nodes": [
          { "project": { "number": {{projectNumber}} },
            "approval": {{(name is null ? "null" : $$"""
              { "name": "{{name}}"{{(updatedAt is null ? "" : $", \"updatedAt\": \"{updatedAt}\"")}} }
              """)}} }
        ] } } } } }
        """;

    private static Task<ContextApproval> Read(string response) =>
        new GitHubContextApprovalReader(new GhApi(new QueueGhProcess(response)))
            .ReadAsync(Config, "owner", "repo", 42, CancellationToken.None);

    [Fact]
    public async Task AnApprovedOptionYieldsItsOwnUpdatedAtAsTheCutoff()
    {
        var approval = await Read(Response("Approved", "2026-07-26T12:00:00Z"));

        Assert.True(approval.IsApproved);
        Assert.Equal(ContextApprovalSource.ProjectField, approval.Source);
        var expected = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, approval.BaseApprovedAt);
        // One gesture, two roles: it approves the title/body and sets the comment cutoff.
        Assert.Equal(expected, approval.BatchCommentCutoff);
    }

    [Theory]
    [InlineData("Needs review")]
    [InlineData("approved-ish")]
    [InlineData("Yes")]
    [InlineData("")]
    public async Task AnyOtherOptionApprovesNothing(string option) =>
        Assert.False((await Read(Response(option, "2026-07-26T12:00:00Z"))).IsApproved);

    [Fact]
    public async Task TheExactOptionIsMatchedCaseInsensitivelyButNotFuzzily()
    {
        Assert.True((await Read(Response("APPROVED", "2026-07-26T12:00:00Z"))).IsApproved);
        Assert.False((await Read(Response("Approved (pending)", "2026-07-26T12:00:00Z"))).IsApproved);
    }

    [Fact]
    public async Task AnUnsetFieldApprovesNothing() =>
        Assert.False((await Read(Response(null, null))).IsApproved);

    [Fact]
    public async Task AnApprovedOptionWithNoTimestampApprovesNothing()
    {
        // The one case where the field looks affirmative and still must not be consent: with no
        // readable instant there is nothing to bind a revision to.
        Assert.False((await Read(Response("Approved", null))).IsApproved);
    }

    [Fact]
    public async Task AnotherProjectsFieldCannotApprove()
    {
        // An issue can sit on several Projects; only the configured one carries authority.
        Assert.False((await Read(Response("Approved", "2026-07-26T12:00:00Z", projectNumber: 99))).IsApproved);
    }

    [Fact]
    public async Task AMissingIssueApprovesNothing() =>
        Assert.False((await Read("""{ "data": { "repository": { "issue": null } } }""")).IsApproved);
}

public class GitHubExecutionContextProviderTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 7
    };

    private static readonly WorkItemId Id = new("github:owner/repo#42");
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 13, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

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

    private const string ApprovalResponse = """
        { "data": { "repository": { "issue": { "projectItems": { "nodes": [
          { "project": { "number": 7 },
            "approval": { "name": "Approved", "updatedAt": "2026-07-26T12:00:00Z" } }
        ] } } } } }
        """;

    private static string ConversationResponse(string comments = "") =>
        $$"""
        { "data": { "repository": { "issue": {
          "title": "Add retry handling",
          "body": "The worker should retry once.",
          "url": "https://github.com/owner/repo/issues/42",
          "createdAt": "2026-07-26T09:00:00Z",
          "lastEditedAt": null,
          "titleChanges": { "nodes": [] },
          "comments": { "pageInfo": { "hasNextPage": false, "endCursor": null },
                        "nodes": [{{comments}}] }
        } } } }
        """;

    private static string CommentNode(string id, string createdAt, string reactions = "", int reactionTotal = 0) =>
        $$"""
        { "databaseId": {{id}}, "author": { "login": "octocat" }, "authorAssociation": "MEMBER",
          "body": "Please also handle the empty case.", "createdAt": "{{createdAt}}",
          "lastEditedAt": null,
          "url": "https://github.com/owner/repo/issues/42#issuecomment-{{id}}",
          "isMinimized": false, "minimizedReason": null,
          "reactions": { "totalCount": {{reactionTotal}}, "nodes": [{{reactions}}] } }
        """;

    private static (GitHubExecutionContextProvider Provider, QueueGhProcess Process) Build(
        IContextApproverPolicy? policy = null, params string[] responses)
    {
        var process = new QueueGhProcess(responses);
        var api = new GhApi(process);
        return (new GitHubExecutionContextProvider(
            new GitHubConversationReader(api),
            new GitHubContextApprovalReader(api),
            new GitHubWorkItemAddressResolver(),
            policy,
            clock: new FixedClock(Now)), process);
    }

    private static Task<ExecutionContextResult> Read(
        IContextApproverPolicy? policy = null, params string[] responses) =>
        Build(policy, responses).Provider
            .GetAsync(Config, Id, ContextReadPurpose.Diagnostics, ContextLimits.Default,
                CancellationToken.None);

    [Fact]
    public async Task AnApprovedIssueWithACoveredCommentProducesASnapshot()
    {
        var result = await Read(null, ApprovalResponse,
            ConversationResponse(CommentNode("1", "2026-07-26T10:00:00Z")));

        Assert.True(result.IsApproved);
        Assert.Equal("Add retry handling", result.Snapshot!.Title);
        Assert.Single(result.Snapshot.Discussion);
        Assert.StartsWith("sha256:", result.Snapshot.Revision.Digest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIssueWithNoApprovalIsRefused()
    {
        var unapproved = """
            { "data": { "repository": { "issue": { "projectItems": { "nodes": [
              { "project": { "number": 7 },
                "approval": { "name": "Needs review", "updatedAt": "2026-07-26T12:00:00Z" } }
            ] } } } } }
            """;
        var result = await Read(null, unapproved, ConversationResponse());

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.ApprovalUnavailable, result.Code);
    }

    [Fact]
    public async Task TheApprovalIsReadBeforeTheConversation()
    {
        // Ordering matters: a comment arriving between the two reads is seen by the comment read
        // and lands after the cutoff, so it is pending and blocks. The other order would let a
        // comment slip in behind an approval that never covered it.
        var (provider, process) = Build(null, ApprovalResponse, ConversationResponse());
        await provider.GetAsync(Config, Id, ContextReadPurpose.PreClaim, ContextLimits.Default,
            CancellationToken.None);

        Assert.Equal(2, process.Inputs.Count);
        Assert.Contains("projectItems", process.Inputs[0]!, StringComparison.Ordinal);
        Assert.Contains("comments(first:", process.Inputs[1]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableConversationRefusesRatherThanReturningAPartialContext()
    {
        var truncated = ConversationResponse(CommentNode("1", "2026-07-26T10:00:00Z"))
            .Replace("\"hasNextPage\": false", "\"hasNextPage\": true", StringComparison.Ordinal);
        var result = await Read(null, ApprovalResponse, truncated);

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.ReadFailed, result.Code);
    }

    [Fact]
    public async Task AnIdThatIsNotAGitHubIssueIsRefused()
    {
        var result = await Build(null).Provider.GetAsync(
            Config, new WorkItemId("local:1"), ContextReadPurpose.Diagnostics,
            ContextLimits.Default, CancellationToken.None);

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.ReadFailed, result.Code);
    }

    [Fact]
    public async Task TheDefaultApproverPolicyAuthorisesNobody()
    {
        // Deliberately useless rather than permissive: with no github.contextApprovers configured,
        // a reaction from anyone must not be able to decide what an unattended agent reads.
        var reaction = """
            { "id": "R1", "content": "THUMBS_UP", "createdAt": "2026-07-26T12:30:00Z",
              "user": { "login": "anyone" } }
            """;
        var result = await Read(null, ApprovalResponse,
            ConversationResponse(CommentNode("1", "2026-07-26T12:20:00Z", reaction, 1)));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
    }

    [Fact]
    public void TheUnavailablePolicyAuthorisesNoActorAtAll()
    {
        var policy = UnavailableApproverPolicy.Instance;
        foreach (var actor in new string?[] { "maintainer", "admin", "", null })
        {
            Assert.False(policy.IsApprover(actor));
            Assert.False(policy.CanExcludeContent(actor));
        }
    }

    // --- configured approvers, through production wiring ----------------------------------------

    private static readonly TrackerConfig ApproverConfig = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 7,
        ContextApprovers = ["Maintainer"]
    };

    private static Task<ExecutionContextResult> ReadWithApprovers(params string[] responses) =>
        Build(null, responses).Provider
            .GetAsync(ApproverConfig, Id, ContextReadPurpose.Diagnostics, ContextLimits.Default,
                CancellationToken.None);

    [Fact]
    public async Task AConfiguredApproversThumbsUpIncludesAPendingComment()
    {
        // No explicit policy: the provider derives one from github.contextApprovers, which is what
        // makes the reaction mechanics reachable in production. The actor's case differs from the
        // configured entry to pin the case-insensitive match.
        var reaction = """
            { "id": "R1", "content": "THUMBS_UP", "createdAt": "2026-07-26T12:30:00Z",
              "user": { "login": "maintainer" } }
            """;
        var result = await ReadWithApprovers(ApprovalResponse,
            ConversationResponse(CommentNode("1", "2026-07-26T12:20:00Z", reaction, 1)));

        Assert.True(result.IsApproved, $"{result.Code}: {result.Message}");
        var decision = Assert.Single(result.Snapshot!.Decisions);
        Assert.Equal(DiscussionDecisionKind.Include, decision.Decision);
        Assert.Equal(DiscussionDecisionSource.Reaction, decision.Source);
        Assert.Single(result.Snapshot.Discussion);
    }

    [Fact]
    public async Task AConfiguredApproversThumbsDownExcludesAPendingComment()
    {
        // Decision 10's documented exclusion workflow, previously a silent no-op: the -1 decides
        // the comment in place and the launch proceeds without it.
        var reaction = """
            { "id": "R1", "content": "THUMBS_DOWN", "createdAt": "2026-07-26T12:30:00Z",
              "user": { "login": "maintainer" } }
            """;
        var result = await ReadWithApprovers(ApprovalResponse,
            ConversationResponse(CommentNode("1", "2026-07-26T12:20:00Z", reaction, 1)));

        Assert.True(result.IsApproved, $"{result.Code}: {result.Message}");
        var decision = Assert.Single(result.Snapshot!.Decisions);
        Assert.Equal(DiscussionDecisionKind.Exclude, decision.Decision);
        Assert.Empty(result.Snapshot.Discussion);
    }

    [Fact]
    public async Task AnUnconfiguredActorsReactionStaysInertWithApproversConfigured()
    {
        var reaction = """
            { "id": "R1", "content": "THUMBS_UP", "createdAt": "2026-07-26T12:30:00Z",
              "user": { "login": "passer-by" } }
            """;
        var result = await ReadWithApprovers(ApprovalResponse,
            ConversationResponse(CommentNode("1", "2026-07-26T12:20:00Z", reaction, 1)));

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
    }

    private sealed class FixedViewer(string? login) : Highbyte.Wrighty.GitHub.IGitHubViewerIdentity
    {
        public Task<string?> GetLoginAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(login);
    }

    [Fact]
    public async Task WithAnIdentityTheApproversStillDecideAndProtocolExclusionHolds()
    {
        // The production wiring: viewer identity resolved, approvers configured. The approver's
        // reaction decides, exactly as without an identity.
        var process = new QueueGhProcess(ApprovalResponse,
            ConversationResponse(CommentNode("1", "2026-07-26T12:20:00Z", """
                { "id": "R1", "content": "THUMBS_UP", "createdAt": "2026-07-26T12:30:00Z",
                  "user": { "login": "maintainer" } }
                """, 1)));
        var provider = new GitHubExecutionContextProvider(
            new GitHubConversationReader(new GhApi(process)),
            new GitHubContextApprovalReader(new GhApi(process)),
            new GitHubWorkItemAddressResolver(),
            clock: new FixedClock(Now),
            viewerIdentity: new FixedViewer("wrighty-bot"));

        var result = await provider.GetAsync(
            ApproverConfig, Id, ContextReadPurpose.Diagnostics, ContextLimits.Default,
            CancellationToken.None);

        Assert.True(result.IsApproved, $"{result.Code}: {result.Message}");
        Assert.Equal(DiscussionDecisionKind.Include, Assert.Single(result.Snapshot!.Decisions).Decision);
    }

    [Fact]
    public async Task WithAnIdentityButNoApproversNobodyDecides()
    {
        var process = new QueueGhProcess(ApprovalResponse,
            ConversationResponse(CommentNode("1", "2026-07-26T12:20:00Z", """
                { "id": "R1", "content": "THUMBS_UP", "createdAt": "2026-07-26T12:30:00Z",
                  "user": { "login": "maintainer" } }
                """, 1)));
        var provider = new GitHubExecutionContextProvider(
            new GitHubConversationReader(new GhApi(process)),
            new GitHubContextApprovalReader(new GhApi(process)),
            new GitHubWorkItemAddressResolver(),
            clock: new FixedClock(Now),
            viewerIdentity: new FixedViewer("wrighty-bot"));

        var result = await provider.GetAsync(
            Config, Id, ContextReadPurpose.Diagnostics, ContextLimits.Default,
            CancellationToken.None);

        Assert.False(result.IsApproved);
        Assert.Equal(ExecutionContextResult.Codes.CommentPending, result.Code);
    }

    [Fact]
    public void TheConfiguredPolicyMatchesLoginsNotBlanks()
    {
        var policy = new ConfiguredApproverPolicy(["Maintainer"], viewerLogin: "wrighty-bot");

        Assert.True(policy.IsApprover("maintainer"));
        Assert.True(policy.IsApprover("MAINTAINER"));
        Assert.False(policy.IsApprover("someone-else"));
        Assert.False(policy.IsApprover(""));
        Assert.False(policy.IsApprover(null));
        // Approval authority and protocol-comment exclusion stay separate concerns: only the
        // account Wrighty posts as hides Wrighty's own comments.
        Assert.False(policy.CanExcludeContent("maintainer"));
        Assert.True(policy.CanExcludeContent("wrighty-bot"));
    }
}
