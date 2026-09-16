using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.LocalMarkdown;

namespace Highbyte.Wrighty.Actions;

/// <summary>
/// Configuration-scoped, cross-process preview journal. Write-ahead item markers prevent
/// replay after interruption; a cache record never substitutes for fresh execution consent.
/// </summary>
public sealed class WorkflowBatchStore(string root, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private const int MaximumEntries = 512;
    private const string CompletedState = "completed";
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<WorkflowBatchPreview> CreateAsync(TrackerConfig config, string action,
        IReadOnlyList<WorkflowBatchCandidate> candidates, int selectedCount, int eligibleCount,
        CancellationToken cancellationToken)
    {
        WorkflowActionService.EnsureSupported(action);
        if (candidates.Count > WorkflowBatchPolicy.MaximumCandidates)
            throw Error("BATCH_TOO_LARGE", "A batch may contain at most 100 items.");
        var scope = Scope(config);
        await using var gate = await LocalStoreLock.AcquireAsync(scope, cancellationToken);
        Purge(scope);
        if (Directory.EnumerateFiles(scope, "*.json").Take(MaximumEntries).Count() >= MaximumEntries)
            throw Error("BATCH_STORE_FULL", "The batch cache is full. Wait for its 24-hour retention period.");
        var preview = new WorkflowBatchPreview(RandomNumberGenerator.GetHexString(32).ToLowerInvariant(),
            action, WorkflowBatchPolicy.ConfigurationVersion(config), clock.GetUtcNow(),
            candidates.ToArray(), selectedCount, eligibleCount);
        Save(Path.Combine(scope, preview.Id + ".json"), new(preview, "preview", []));
        return preview;
    }

    public async Task<WorkflowBatchRecord> ReadAsync(TrackerConfig config, string id,
        CancellationToken cancellationToken)
    {
        var path = RecordPath(config, id);
        await using var gate = await LocalStoreLock.AcquireAsync(Scope(config), cancellationToken);
        return Recover(path, Load(path));
    }

    public async Task<WorkflowBatchRecord> ExecuteAsync(TrackerConfig config, string id,
        Func<WorkflowBatchPreview, WorkflowBatchCandidate, CancellationToken, Task<WorkflowActionResult>> execute,
        CancellationToken cancellationToken)
    {
        var path = RecordPath(config, id);
        await using var gate = await LocalStoreLock.AcquireAsync(Scope(config), cancellationToken);
        var record = Recover(path, Load(path));
        if (record.State == CompletedState) return record;
        if (clock.GetUtcNow() >= record.Preview.ExpiresAt)
            throw Error("BATCH_EXPIRED", "The preview expired. Review a new batch.");
        if (record.Preview.ConfigurationVersion != WorkflowBatchPolicy.ConfigurationVersion(config))
            throw Error("BATCH_CONFIG_CHANGED", "Configuration changed. Review a new batch.");
        record = record with { State = "running" };
        Save(path, record);
        var journal = new Journal(path, record);
        var candidates = record.Preview.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var execution = await WorkflowBatchExecutor.ExecuteAsync(
            record.Preview.Candidates.Select(candidate => candidate.Id).ToArray(),
            async (itemId, token) => await execute(record.Preview, candidates[itemId], token),
            cancellationToken, journal);
        record = record with { State = CompletedState, Items = execution.Items, StopCode = execution.StopCode };
        Save(path, record);
        return record;
    }

    private sealed class Journal(string path, WorkflowBatchRecord record) : IWorkflowBatchProgress
    {
        public void Starting(string id)
        {
            // Persist before the mutation so recovery cannot replay an ambiguous item.
            record = record with { ActiveItemId = id };
            Save(path, record);
        }

        public void Completed(WorkflowBatchItemResult result)
        {
            record = record with { Items = [.. record.Items, result], ActiveItemId = null,
                StopCode = result.Outcome == "failed" ? result.Code : null };
            Save(path, record);
        }
    }

    private static WorkflowBatchRecord Recover(string path, WorkflowBatchRecord record)
    {
        if (record.State != "running") return record;
        if (record.ActiveItemId is { } id)
            record = record with { Items = [.. record.Items,
                new(id, "failed", "BATCH_INTERRUPTED", MutationMayHaveApplied: true)] };
        record = Complete(record with { ActiveItemId = null, StopCode = "BATCH_INTERRUPTED" });
        Save(path, record);
        return record;
    }

    private static WorkflowBatchRecord Complete(WorkflowBatchRecord record)
    {
        var execution = WorkflowBatchExecutor.Complete(
            record.Preview.Candidates.Select(candidate => candidate.Id).ToArray(), record.Items, record.StopCode);
        return record with { State = CompletedState, ActiveItemId = null, Items = execution.Items };
    }

    private void Purge(string scope)
    {
        var cutoff = clock.GetUtcNow() - Retention;
        foreach (var path in Directory.EnumerateFiles(scope, "*.json"))
            if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime) File.Delete(path);
    }

    private static TrackerException Error(string code, string message) => new(code, message, 6);

    private string Scope(TrackerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SourcePath))
            throw Error("BATCH_SCOPE_REQUIRED", "Batch operations require a saved repository configuration.");
        var source = Path.GetFullPath(config.SourcePath);
        if (OperatingSystem.IsWindows()) source = source.ToUpperInvariant();
        return Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
    }

    private string RecordPath(TrackerConfig config, string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigit))
            throw Error("BATCH_UNKNOWN", "The batch preview ID is invalid or unavailable.");
        return Path.Combine(Scope(config), id.ToLowerInvariant() + ".json");
    }

    private static WorkflowBatchRecord Load(string path)
    {
        if (!File.Exists(path)) throw Error("BATCH_UNKNOWN", "This batch is unavailable. Review a new batch.");
        try
        {
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new JsonException();
            var record = JsonSerializer.Deserialize<WorkflowBatchRecord>(File.ReadAllText(path));
            if (record?.Preview?.Candidates is null || record.Items is null ||
                record.State is not ("preview" or "running" or CompletedState) ||
                record.Preview.Id != Path.GetFileNameWithoutExtension(path) ||
                record.Preview.Candidates.Count > WorkflowBatchPolicy.MaximumCandidates ||
                !ValidCandidates(record) ||
                record.Preview.Candidates.Select(item => item.Id).Distinct().Count() != record.Preview.Candidates.Count)
                throw new JsonException();
            WorkflowActionService.EnsureSupported(record.Preview.Action);
            return record;
        }
        catch (JsonException)
        {
            throw Error("BATCH_RECORD_INVALID", "The saved batch is invalid. Inspect item state before creating another preview.");
        }
    }

    private static bool ValidCandidates(WorkflowBatchRecord record)
    {
        var preview = record.Preview;
        if (preview.CreatedAt == default || preview.EligibleCount < preview.Candidates.Count ||
            preview.SelectedCount < preview.EligibleCount || string.IsNullOrWhiteSpace(preview.ConfigurationVersion))
            return false;
        if (preview.Candidates.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                item.StateVersion is not { Length: 64 } || !item.StateVersion.All(char.IsAsciiHexDigit) ||
                item.Before is null || item.Consequence is null || item.Title is null))
            return false;
        var ids = preview.Candidates.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (record.Items.Any(item => item is null || !ids.Contains(item.Id)) ||
            record.Items.Select(item => item.Id).Distinct().Count() != record.Items.Count)
            return false;
        return record.State != "preview" || (record.Items.Count == 0 && record.ActiveItemId is null);
    }

    private static void Save(string path, WorkflowBatchRecord record)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
