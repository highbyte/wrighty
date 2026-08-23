using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Caching;

namespace Highbyte.Wrighty.Workers;

public sealed record PendingWorkerInterruption(
    string RunId,
    string ItemId,
    string Agent,
    WorkerInterruptionReason Reason,
    DateTimeOffset OccurredAt);

public sealed class WorkerInterruptionJournal(CachePaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Write(
        string runId,
        string configurationPath,
        string itemId,
        string agent,
        string? claimToken,
        bool workspacePresent,
        bool sessionPresent,
        WorkerInterruptionReason reason,
        DateTimeOffset occurredAt)
    {
        var itemHash = Hash(itemId)[..16];
        var path = Path.Combine(paths.WorkerInterruptionsRoot, $"{runId}-{itemHash}.json");
        Directory.CreateDirectory(paths.WorkerInterruptionsRoot);
        var record = new Record(
            1,
            runId,
            JsonWorkerInstanceRegistry.ConfigurationPathHash(configurationPath),
            itemId,
            agent,
            claimToken is null ? null : Hash(claimToken),
            workspacePresent,
            sessionPresent,
            reason,
            occurredAt);
        var temporary = Path.Combine(
            paths.WorkerInterruptionsRoot,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.Write("\n"u8);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the journal write failure.
            }
        }
    }

    public static IReadOnlyList<PendingWorkerInterruption> ListPending(
        CachePaths paths,
        string configurationPath)
    {
        if (!Directory.Exists(paths.WorkerInterruptionsRoot))
            return [];
        var configurationHash = JsonWorkerInstanceRegistry.ConfigurationPathHash(configurationPath);
        var values = new List<PendingWorkerInterruption>();
        foreach (var path in Directory.GetFiles(paths.WorkerInterruptionsRoot, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var record = JsonSerializer.Deserialize<Record>(stream, JsonOptions);
                if (record is null || record.ConfigurationPathHash != configurationHash)
                    continue;
                values.Add(new PendingWorkerInterruption(
                    record.RunId,
                    record.ItemId,
                    record.Agent,
                    record.Reason,
                    record.OccurredAt));
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // A malformed breadcrumb is diagnostic only and never authority for recovery.
            }
        }
        return values.OrderByDescending(value => value.OccurredAt).ToArray();
    }

    public static void Complete(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A retained breadcrumb is conservative: status can report that cleanup may need
            // review rather than silently losing the only evidence of an interrupted finalizer.
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Record(
        int Version,
        string RunId,
        string ConfigurationPathHash,
        string ItemId,
        string Agent,
        string? ClaimGenerationFingerprint,
        bool WorkspacePresent,
        bool SessionPresent,
        WorkerInterruptionReason Reason,
        DateTimeOffset OccurredAt);
}
