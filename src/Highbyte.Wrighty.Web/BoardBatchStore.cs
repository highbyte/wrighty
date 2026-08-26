using System.Collections.Concurrent;
using System.Security.Cryptography;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Web;

public enum BoardBatchAction
{
    Queue,
    Dequeue,
    Resume
}

public sealed record BoardBatchCandidate(
    string Id,
    string DisplayId,
    string Title);

public sealed record BoardBatchIntent(
    string Id,
    BoardBatchAction Action,
    string ConfigurationRevision,
    DateTimeOffset CreatedAt,
    IReadOnlyList<BoardBatchCandidate> Candidates,
    int EligibleCount,
    int ShownCount);

public sealed record BoardBatchItemResult(
    string Id,
    string DisplayId,
    bool Succeeded,
    bool Skipped,
    string? Reason = null,
    bool Aborted = false);

public sealed record BoardBatchResult(
    string IntentId,
    BoardBatchAction Action,
    DateTimeOffset CompletedAt,
    IReadOnlyList<BoardBatchItemResult> Items,
    string? AbortReason = null)
{
    public int SucceededCount => Items.Count(item => item.Succeeded);

    public int SkippedCount => Items.Count(item => item.Skipped && !item.Aborted);

    public int FailedCount => Items.Count(item => !item.Succeeded && !item.Skipped && !item.Aborted);

    public int AbortedCount => Items.Count(item => item.Aborted);

    public int NotProcessedCount => Items.Count(item => !item.Succeeded);

    public bool HasIssues => AbortReason is not null || NotProcessedCount > 0;
}

/// <summary>
/// Process-local storage for frozen Board batch intents and their idempotent results. Intents
/// contain only bounded display data and canonical item IDs; claim/session credentials and
/// backend payloads never enter this cache.
/// </summary>
public sealed class BoardBatchStore(
    TimeProvider? timeProvider = null,
    TimeSpan? intentLifetime = null,
    int maximumEntries = 512)
{
    public const int MaximumCandidates = 100;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan lifetime = intentLifetime ?? TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object latestLock = new();
    private BoardBatchResult? latestResult;

    public BoardBatchIntent Create(
        BoardBatchAction action,
        string? configurationRevision,
        IReadOnlyList<BoardBatchCandidate> candidates,
        int eligibleCount,
        int shownCount)
    {
        Purge();
        var frozen = candidates
            .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Take(MaximumCandidates)
            .ToArray();
        var intent = new BoardBatchIntent(
            RandomNumberGenerator.GetHexString(32).ToLowerInvariant(),
            action,
            configurationRevision ?? string.Empty,
            clock.GetUtcNow(),
            frozen,
            eligibleCount,
            shownCount);
        entries[intent.Id] = new Entry(intent);
        EnforceBound();
        return intent;
    }

    public BoardBatchResult? LatestResult
    {
        get
        {
            lock (latestLock)
                return latestResult;
        }
    }

    public async Task<BoardBatchResult> ExecuteAsync(
        string? intentId,
        string? configurationRevision,
        Func<BoardBatchIntent, Task<BoardBatchResult>> execute)
    {
        var entry = Find(intentId);
        await entry.Gate.WaitAsync(CancellationToken.None);
        try
        {
            if (entry.Result is { } completed)
                return completed;
            if (clock.GetUtcNow() - entry.Intent.CreatedAt > lifetime)
            {
                entries.TryRemove(entry.Intent.Id, out _);
                throw InvalidIntent(
                    "BOARD_BATCH_EXPIRED",
                    "This batch preview expired. Refresh the Board and review the current items again.");
            }
            if (!string.Equals(
                    entry.Intent.ConfigurationRevision,
                    configurationRevision ?? string.Empty,
                    StringComparison.Ordinal))
            {
                throw InvalidIntent(
                    "BOARD_BATCH_CONFIG_CHANGED",
                    "Wrighty's configuration changed after this preview. Refresh the Board and review the batch again.");
            }

            var result = await execute(entry.Intent);
            entry.Result = result;
            lock (latestLock)
                latestResult = result.HasIssues ? result : null;
            return result;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public bool Dismiss(string? intentId)
    {
        if (string.IsNullOrWhiteSpace(intentId))
            return false;
        lock (latestLock)
        {
            if (!string.Equals(latestResult?.IntentId, intentId, StringComparison.Ordinal))
                return false;
            latestResult = null;
            return true;
        }
    }

    private Entry Find(string? intentId)
    {
        if (string.IsNullOrWhiteSpace(intentId) || intentId.Length > 128 ||
            !entries.TryGetValue(intentId, out var entry))
        {
            throw InvalidIntent(
                "BOARD_BATCH_UNKNOWN",
                "This batch preview is no longer available. Refresh the Board and review the current items again.");
        }
        Purge();
        return entry;
    }

    private void Purge()
    {
        var cutoff = clock.GetUtcNow() - lifetime;
        foreach (var pair in entries)
        {
            if (pair.Value.Result is null && pair.Value.Intent.CreatedAt < cutoff)
                entries.TryRemove(pair.Key, out _);
        }
    }

    private void EnforceBound()
    {
        var overflow = entries.Count - maximumEntries;
        if (overflow <= 0)
            return;
        foreach (var entry in entries.Values
                     .OrderBy(value => value.Result is null ? 0 : 1)
                     .ThenBy(value => value.Intent.CreatedAt)
                     .Take(overflow))
        {
            entries.TryRemove(entry.Intent.Id, out _);
        }
    }

    private static TrackerException InvalidIntent(string code, string message) =>
        new(code, message, 6);

    private sealed class Entry(BoardBatchIntent intent)
    {
        public BoardBatchIntent Intent { get; } = intent;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public BoardBatchResult? Result { get; set; }
    }
}
