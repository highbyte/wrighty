using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Reads explicit controls from reactions on the latest unresolved Wrighty status comment.
///
/// The first read locates the comment by its strict report-identity marker. Its ID is then retained
/// in the
/// session continuation state, while this process retains its last reading and both endpoint ETags.
/// Checks run at most once a minute and unchanged conditional reads do not spend primary REST
/// quota. This separate freshness path is required: live probing established that adding a reaction
/// advances neither the issue's nor the comment's <c>updatedAt</c>.
/// </summary>
public sealed class GitHubControlReactionProvider(
    GhApi api,
    GitHubWorkItemAddressResolver addresses,
    IGitHubViewerIdentity viewerIdentity,
    Func<DateTimeOffset>? clock = null,
    TimeSpan? pollingInterval = null) : ITrustedControlReactionProvider
{
    private const string ApiVersion = "2022-11-28";
    internal static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, PollCache> polls = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan pollEvery = pollingInterval ?? DefaultPollingInterval;

    public async Task<TrustedControlReactionReading?> ReadAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentRunReport latestReport,
        SessionContinuationState state,
        WorkerContinuationConfig continuation,
        CancellationToken cancellationToken)
    {
        if (latestReport.ObservedDisposition != RunReportDisposition.NeedsAttention)
            return null;

        var address = addresses.Decode(id, config);
        var cache = polls.GetOrAdd(
            $"{address.Host}\n{id.Value}", static _ => new PollCache());
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(cache.ReportId, latestReport.ReportId, StringComparison.Ordinal))
                cache.Reset(latestReport.ReportId);

            var observedAt = now();
            if (cache.LastPolledAt is { } last &&
                observedAt - last < pollEvery &&
                cache.Reading is { } cached)
            {
                return cached;
            }

            var viewer = await viewerIdentity.GetLoginAsync(address.Host, cancellationToken);
            if (string.IsNullOrWhiteSpace(viewer))
                throw new TrackerException(
                    ExecutionContextResult.Codes.AuthorizationUnavailable,
                    "Wrighty's GitHub identity could not be established, so a status-comment " +
                    "reaction cannot be trusted.",
                    10);

            var comment = await ReadReportCommentAsync(
                config, id, latestReport, state, address, viewer, cache, cancellationToken);
            var commentId = comment.GetProperty("id").GetRawText();
            var reportRevision = Instant(comment, "updated_at")
                ?? throw InvalidReport(latestReport.ReportId, "has no usable update timestamp");
            var reactions = await ReadReactionsAsync(
                address, commentId, cache, cancellationToken);
            var reading = Evaluate(
                config, latestReport, continuation, commentId, reportRevision, reactions);

            cache.LastPolledAt = observedAt;
            cache.Reading = reading;
            return reading;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private async Task<JsonElement> ReadReportCommentAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentRunReport report,
        SessionContinuationState state,
        GitHubWorkItemAddress address,
        string viewer,
        PollCache cache,
        CancellationToken cancellationToken)
    {
        var focusedCommentId = cache.Comment is { } remembered
            ? remembered.GetProperty("id").GetRawText()
            : string.Equals(state.ControlReportId, report.ReportId, StringComparison.Ordinal) &&
              !string.IsNullOrWhiteSpace(state.ControlReportCommentId)
                ? state.ControlReportCommentId
                : null;
        if (!string.IsNullOrWhiteSpace(focusedCommentId))
        {
            var endpoint =
                $"repos/{address.Owner}/{address.Repository}/issues/comments/{focusedCommentId}";
            var response = await api.GetVersionedConditionalAsync(
                address.Host, endpoint, ApiVersion, cache.CommentETag, cancellationToken);
            var comment = response.NotModified
                ? cache.Comment ?? throw InvalidReport(
                    report.ReportId, "returned not-modified before its comment was cached")
                : response.Body ?? throw InvalidReport(
                    report.ReportId, "returned no comment content");
            ValidateReportComment(comment, id, report, viewer);
            cache.Comment = comment.Clone();
            cache.CommentETag = response.ETag;
            return comment;
        }

        var commentsEndpoint =
            $"repos/{address.Owner}/{address.Repository}/issues/{address.IssueNumber}/comments?per_page=100";
        using var pages = await api.GetVersionedPaginatedAsync(
            address.Host, commentsEndpoint, ApiVersion, cancellationToken);
        var matches = new List<JsonElement>();
        foreach (var page in pages.RootElement.EnumerateArray())
            foreach (var candidate in page.EnumerateArray())
            {
                if (!TryMatchReport(candidate, id, report, viewer)) continue;
                matches.Add(candidate.Clone());
            }

        if (matches.Count != 1)
            throw InvalidReport(
                report.ReportId,
                matches.Count == 0
                    ? "could not be found as one strict Wrighty-authored comment"
                    : "matched more than one strict Wrighty-authored comment");

        cache.Comment = matches[0].Clone();
        cache.CommentETag = null;
        return matches[0];
    }

    private async Task<IReadOnlyList<JsonElement>> ReadReactionsAsync(
        GitHubWorkItemAddress address,
        string commentId,
        PollCache cache,
        CancellationToken cancellationToken)
    {
        var endpoint =
            $"repos/{address.Owner}/{address.Repository}/issues/comments/{commentId}/reactions?per_page=100";
        if (cache.ReactionsRequirePagination)
        {
            using var pages = await api.GetVersionedPaginatedAsync(
                address.Host, endpoint, ApiVersion, cancellationToken);
            cache.Reactions = FlattenPages(pages.RootElement);
            return cache.Reactions;
        }

        var response = await api.GetVersionedConditionalAsync(
            address.Host, endpoint, ApiVersion, cache.ReactionsETag, cancellationToken);
        if (response.NotModified)
        {
            cache.ReactionsETag = response.ETag;
            return cache.Reactions ?? throw new TrackerException(
                "GH_RESPONSE_INVALID",
                "GitHub returned not-modified before the report reactions were cached.",
                10);
        }

        if (HasNextPage(response.Link))
        {
            // A single ETag describes only the first page. Once a report exceeds one page, keep
            // paging it unconditionally rather than letting a change on a later page go unseen.
            using var pages = await api.GetVersionedPaginatedAsync(
                address.Host, endpoint, ApiVersion, cancellationToken);
            cache.Reactions = FlattenPages(pages.RootElement);
            cache.ReactionsETag = null;
            cache.ReactionsRequirePagination = true;
            return cache.Reactions;
        }

        if (response.Body is not { ValueKind: JsonValueKind.Array } body)
        {
            throw new TrackerException(
                "GH_RESPONSE_INVALID",
                "GitHub returned malformed report reaction content.",
                10);
        }

        cache.Reactions = body.EnumerateArray().Select(value => value.Clone()).ToArray();
        cache.ReactionsETag = response.ETag;
        return cache.Reactions;
    }

    private static IReadOnlyList<JsonElement> FlattenPages(JsonElement pages)
    {
        if (pages.ValueKind != JsonValueKind.Array)
            throw InvalidReactionPages();

        var reactions = new List<JsonElement>();
        foreach (var page in pages.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Array)
                throw InvalidReactionPages();
            reactions.AddRange(page.EnumerateArray().Select(value => value.Clone()));
        }
        return reactions;
    }

    private static TrackerException InvalidReactionPages() => new(
        "GH_RESPONSE_INVALID",
        "GitHub returned malformed paginated report reaction content.",
        10);

    private static bool HasNextPage(string? link) =>
        link?.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase) == true;

    private static TrustedControlReactionReading Evaluate(
        TrackerConfig config,
        AgentRunReport latestReport,
        WorkerContinuationConfig continuation,
        string commentId,
        DateTimeOffset reportRevision,
        IReadOnlyList<JsonElement> reactions)
    {
        var candidates = new List<TrustedContinuationEvent>();
        foreach (var reaction in reactions)
        {
            var actor = reaction.GetProperty("user").GetProperty("login").GetString();
            if (string.IsNullOrWhiteSpace(actor) ||
                !config.TrustedCommentAuthors.Any(name =>
                    string.Equals(name, actor, StringComparison.OrdinalIgnoreCase)))
                continue;

            var createdAt = Instant(reaction, "created_at");
            // Strictly after: a reaction left before a report edit did not accept the report now
            // visible at that stable comment ID.
            if (createdAt is null || createdAt <= reportRevision) continue;

            var content = reaction.GetProperty("content").GetString();
            TrustedContinuationKind? kind = ReactionKinds.Matches(
                content, continuation.ResumeReaction)
                    ? TrustedContinuationKind.Continue
                    : ReactionKinds.Matches(content, continuation.CompletionReaction)
                        ? TrustedContinuationKind.CompletionRequested
                        : null;
            if (kind is null) continue;

            candidates.Add(new TrustedContinuationEvent(
                reaction.GetProperty("id").GetRawText(),
                TrustedContinuationSource.Reaction,
                actor,
                createdAt.Value,
                Kind: kind.Value));
        }

        if (candidates.Count == 0)
            return new TrustedControlReactionReading(
                latestReport.ReportId, commentId, reportRevision);

        var latestAt = candidates.Max(value => value.CreatedAt);
        var latest = candidates.Where(value => value.CreatedAt == latestAt).ToArray();
        if (latest.Select(value => value.Kind).Distinct().Count() > 1)
            return new TrustedControlReactionReading(
                latestReport.ReportId,
                commentId,
                reportRevision,
                Reason: "Conflicting trusted control reactions were created at the same latest " +
                        "instant on the current Wrighty status comment. Remove one or add the intended " +
                        "reaction again so the choice is unambiguous.");

        // Multiple actors choosing the same control at the same second agree. Pick by immutable
        // reaction ID so every host names the same trigger.
        var selected = latest.OrderBy(value => value.StableId, StringComparer.Ordinal).Last();
        return new TrustedControlReactionReading(
            latestReport.ReportId, commentId, reportRevision, selected);
    }

    private static void ValidateReportComment(
        JsonElement candidate,
        WorkItemId id,
        AgentRunReport report,
        string viewer)
    {
        if (!TryMatchReport(candidate, id, report, viewer))
            throw InvalidReport(
                report.ReportId,
                "no longer matches its cached strict marker, item, run, report, and author");
    }

    private static bool TryMatchReport(
        JsonElement candidate,
        WorkItemId id,
        AgentRunReport report,
        string viewer)
    {
        var body = candidate.TryGetProperty("body", out var bodyValue)
            ? bodyValue.GetString()
            : null;
        var author = candidate.TryGetProperty("user", out var user) &&
                     user.TryGetProperty("login", out var login)
            ? login.GetString()
            : null;
        return string.Equals(author, viewer, StringComparison.OrdinalIgnoreCase) &&
               WrightyCommentClassifier.HasHandoverMarker(body) &&
               WrightyCommentClassifier.TryParseSessionReport(body, out var marker) &&
               WrightyCommentClassifier.BelongsTo(marker, id) &&
               string.Equals(marker.RunId, report.RunId, StringComparison.Ordinal) &&
               string.Equals(marker.ReportId, report.ReportId, StringComparison.Ordinal);
    }

    private static DateTimeOffset? Instant(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    private static TrackerException InvalidReport(string reportId, string reason) => new(
        ExecutionContextResult.Codes.ReadFailed,
        $"Wrighty status comment for report '{reportId}' {reason}; no control reaction was accepted.",
        10);

    private sealed class PollCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? ReportId { get; private set; }
        public DateTimeOffset? LastPolledAt { get; set; }
        public JsonElement? Comment { get; set; }
        public string? CommentETag { get; set; }
        public IReadOnlyList<JsonElement>? Reactions { get; set; }
        public string? ReactionsETag { get; set; }
        public bool ReactionsRequirePagination { get; set; }
        public TrustedControlReactionReading? Reading { get; set; }

        public void Reset(string reportId)
        {
            ReportId = reportId;
            LastPolledAt = null;
            Comment = null;
            CommentETag = null;
            Reactions = null;
            ReactionsETag = null;
            ReactionsRequirePagination = false;
            Reading = null;
        }
    }
}
