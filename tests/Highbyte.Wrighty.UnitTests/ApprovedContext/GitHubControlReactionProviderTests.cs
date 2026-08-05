using System.Text.Json;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

public sealed class GitHubControlReactionProviderTests
{
    private static readonly WorkItemId Id = new("github:owner/repo#42");
    private static readonly DateTimeOffset ReportAt =
        new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1,
        TrustedCommentAuthors = ["operator"]
    };

    private static readonly AgentRunReport Report = new(
        "run-1",
        "report-1",
        "codex",
        RunReportDisposition.NeedsAttention,
        AgentOutcome.Succeeded,
        ReportAt);

    private sealed class Identity : IGitHubViewerIdentity
    {
        public Task<string?> GetLoginAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("wrighty-bot");
    }

    private sealed class QueueGhProcess(params object[] responses) : IGhProcess
    {
        private readonly Queue<object> responses = new(responses);
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            var next = responses.Dequeue();
            return Task.FromResult(next is GhProcessResult result
                ? result
                : new GhProcessResult(0, Assert.IsType<string>(next), string.Empty));
        }
    }

    private static string Marker() =>
        $"{HandoverRenderer.Marker}\n{AgentRunReport.MarkerPrefix}\n" +
        JsonSerializer.Serialize(new
        {
            itemId = Id.Value,
            runId = Report.RunId,
            reportId = Report.ReportId,
            formatVersion = 1
        }) + "\n-->\nreport";

    private static string Comment(string updatedAt = "2026-08-04T10:00:00Z") =>
        JsonSerializer.Serialize(new
        {
            id = 900,
            body = Marker(),
            updated_at = updatedAt,
            user = new { login = "wrighty-bot" }
        });

    private static string LocatedComments() => $"[[{Comment()}]]";

    private static string Reactions(params object[] reactions) =>
        JsonSerializer.Serialize(reactions);

    private static string ReactionPages(params object[][] pages) =>
        JsonSerializer.Serialize(pages);

    private static string Included(
        string body,
        string etag = "\"etag-1\"",
        string? link = null,
        int status = 200) =>
        $"HTTP/2.0 {status} {(status == 304 ? "Not Modified" : "OK")}\n" +
        $"Etag: {etag}\n" +
        (link is null ? string.Empty : $"Link: {link}\n") +
        $"\n{body}";

    private static GhProcessResult NotModified(string etag) =>
        new(1, Included(string.Empty, etag, status: 304), "gh: HTTP 304");

    private static object Reaction(
        long id,
        string content,
        string createdAt,
        string actor = "operator") =>
        new { id, content, created_at = createdAt, user = new { login = actor } };

    private static GitHubControlReactionProvider Provider(
        IGhProcess process,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? pollingInterval = null) =>
        new(
            new GhApi(process), new GitHubWorkItemAddressResolver(), new Identity(),
            clock, pollingInterval);

    [Theory]
    [InlineData("rocket", TrustedContinuationKind.Continue)]
    [InlineData("hooray", TrustedContinuationKind.CompletionRequested)]
    public async Task Configured_reaction_after_the_report_becomes_a_control_event(
        string reaction,
        TrustedContinuationKind expected)
    {
        var process = new QueueGhProcess(
            LocatedComments(),
            Included(Reactions(Reaction(44, reaction, "2026-08-04T10:00:01Z"))));

        var reading = await Provider(process).ReadAsync(
            Config, Id, Report, new SessionContinuationState(),
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.NotNull(reading);
        Assert.Equal("900", reading.ReportCommentId);
        Assert.Equal(expected, reading.Event!.Kind);
        Assert.Equal("reaction:44", reading.Event.ConsumptionKey);
        Assert.Equal("operator", reading.Event.Actor);
    }

    [Fact]
    public async Task Reaction_at_the_report_revision_is_stale_and_ignored()
    {
        var process = new QueueGhProcess(
            LocatedComments(),
            Included(Reactions(Reaction(44, "rocket", "2026-08-04T10:00:00Z"))));

        var reading = await Provider(process).ReadAsync(
            Config, Id, Report, new SessionContinuationState(),
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.Null(reading!.Event);
    }

    [Fact]
    public async Task Standalone_legacy_report_comment_is_not_a_control_surface()
    {
        var legacyBody = Marker().Replace(
            HandoverRenderer.Marker + "\n", string.Empty, StringComparison.Ordinal);
        var located = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = 900,
                body = legacyBody,
                updated_at = "2026-08-04T10:00:00Z",
                user = new { login = "wrighty-bot" }
            }
        });
        var process = new QueueGhProcess($"[{located}]");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(() =>
            Provider(process).ReadAsync(
                Config, Id, Report, new SessionContinuationState(),
                new WorkerContinuationConfig(), CancellationToken.None));

        Assert.Contains("status comment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_latest_controls_fail_closed()
    {
        var process = new QueueGhProcess(
            LocatedComments(),
            Included(Reactions(
                Reaction(44, "rocket", "2026-08-04T10:00:01Z"),
                Reaction(45, "hooray", "2026-08-04T10:00:01Z"))));

        var reading = await Provider(process).ReadAsync(
            Config, Id, Report, new SessionContinuationState(),
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.Null(reading!.Event);
        Assert.Contains("Conflicting", reading.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cached_report_identity_uses_the_focused_comment_path()
    {
        var process = new QueueGhProcess(
            Included(Comment(), "\"comment-1\""),
            Included(
                Reactions(Reaction(44, "rocket", "2026-08-04T10:00:01Z")),
                "\"reactions-1\""));
        var state = new SessionContinuationState(
            ControlReportId: Report.ReportId,
            ControlReportCommentId: "900",
            ControlReportRevisionAt: ReportAt);

        var reading = await Provider(process).ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.NotNull(reading!.Event);
        Assert.Contains(
            process.Calls[0], value => value.Contains("issues/comments/900", StringComparison.Ordinal));
        Assert.DoesNotContain(process.Calls[0], value => value.Contains("issues/42/comments"));
    }

    [Fact]
    public async Task Cached_reading_is_reused_until_the_reaction_poll_interval_elapses()
    {
        var current = ReportAt;
        var process = new QueueGhProcess(
            Included(Comment(), "\"comment-1\""),
            Included(Reactions(), "\"reactions-1\""));
        var provider = Provider(process, () => current);
        var state = new SessionContinuationState(
            ControlReportId: Report.ReportId,
            ControlReportCommentId: "900",
            ControlReportRevisionAt: ReportAt);

        var first = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);
        current = current.AddSeconds(59);
        var second = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(2, process.Calls.Count);
    }

    [Fact]
    public async Task Unchanged_conditional_reads_reuse_the_cached_comment_and_reactions()
    {
        var current = ReportAt;
        var process = new QueueGhProcess(
            Included(Comment(), "\"comment-1\""),
            Included(
                Reactions(Reaction(44, "rocket", "2026-08-04T10:00:01Z")),
                "\"reactions-1\""),
            NotModified("\"comment-1\""),
            NotModified("\"reactions-1\""));
        var provider = Provider(process, () => current);
        var state = new SessionContinuationState(
            ControlReportId: Report.ReportId,
            ControlReportCommentId: "900",
            ControlReportRevisionAt: ReportAt);

        var first = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);
        current = current.AddMinutes(1);
        var second = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Contains(
            process.Calls[2], value => value == "If-None-Match: \"comment-1\"");
        Assert.Contains(
            process.Calls[3], value => value == "If-None-Match: \"reactions-1\"");
    }

    [Fact]
    public async Task Edited_report_rechecks_cached_reactions_against_the_new_revision()
    {
        var current = ReportAt;
        var process = new QueueGhProcess(
            Included(Comment(), "\"comment-1\""),
            Included(
                Reactions(Reaction(44, "rocket", "2026-08-04T10:00:01Z")),
                "\"reactions-1\""),
            Included(Comment("2026-08-04T10:00:02Z"), "\"comment-2\""),
            NotModified("\"reactions-1\""));
        var provider = Provider(process, () => current);
        var state = new SessionContinuationState(
            ControlReportId: Report.ReportId,
            ControlReportCommentId: "900",
            ControlReportRevisionAt: ReportAt);

        var first = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);
        current = current.AddMinutes(1);
        var second = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.NotNull(first!.Event);
        Assert.Null(second!.Event);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-04T10:00:02Z"),
            second.ReportRevisionAt);
    }

    [Fact]
    public async Task Changed_reaction_response_is_evaluated_when_the_comment_is_unchanged()
    {
        var current = ReportAt;
        var process = new QueueGhProcess(
            Included(Comment(), "\"comment-1\""),
            Included(Reactions(), "\"reactions-1\""),
            NotModified("\"comment-1\""),
            Included(
                Reactions(Reaction(44, "rocket", "2026-08-04T10:00:01Z")),
                "\"reactions-2\""));
        var provider = Provider(process, () => current);
        var state = new SessionContinuationState(
            ControlReportId: Report.ReportId,
            ControlReportCommentId: "900",
            ControlReportRevisionAt: ReportAt);

        var first = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);
        current = current.AddMinutes(1);
        var second = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.Null(first!.Event);
        Assert.Equal(TrustedContinuationKind.Continue, second!.Event!.Kind);
    }

    [Fact]
    public async Task Multi_page_reactions_fall_back_to_complete_unconditional_pagination()
    {
        var current = ReportAt;
        var process = new QueueGhProcess(
            Included(Comment(), "\"comment-1\""),
            Included(
                Reactions(),
                "\"reactions-page-1\"",
                "<https://api.github.com/reactions?page=2>; rel=\"next\""),
            ReactionPages(
                [],
                [Reaction(144, "rocket", "2026-08-04T10:00:01Z")]),
            NotModified("\"comment-1\""),
            ReactionPages(
                [],
                [Reaction(145, "hooray", "2026-08-04T10:00:02Z")]));
        var state = new SessionContinuationState(
            ControlReportId: Report.ReportId,
            ControlReportCommentId: "900",
            ControlReportRevisionAt: ReportAt);
        var provider = Provider(process, () => current);

        var first = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);
        current = current.AddMinutes(1);
        var second = await provider.ReadAsync(
            Config, Id, Report, state,
            new WorkerContinuationConfig(), CancellationToken.None);

        Assert.Equal("reaction:144", first!.Event!.ConsumptionKey);
        Assert.Equal("reaction:145", second!.Event!.ConsumptionKey);
        Assert.Contains("--paginate", process.Calls[2]);
        Assert.Contains("--slurp", process.Calls[2]);
        Assert.Contains("--paginate", process.Calls[4]);
        Assert.DoesNotContain("--include", process.Calls[4]);
    }
}
