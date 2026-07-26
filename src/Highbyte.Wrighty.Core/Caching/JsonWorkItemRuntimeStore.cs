using System.Text.Json;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Caching;

/// <summary>
/// A recorded agent session address for one work item. Stored machine-locally so the resume
/// address survives claim release, expiry, and remote claim-history cleanup. Recovery is only
/// meaningful on the installation that recorded the workspace and vendor session.
/// </summary>
public sealed record StoredWorkItemRuntime(
    SessionAddress? Session,
    LastRunRecord? LastRun,
    PendingDispatch? PendingDispatch,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastClaimExpiresAt,
    // What the recorded session was given, as hashes and identifiers only — never content. Optional
    // and last so a file written by an earlier build still deserializes; such an entry simply has no
    // manifest, which blocks unattended resume rather than guessing what that agent holds.
    ApprovedContext.SessionContextMetadata? Context = null);

public interface IWorkItemRuntimeStore
{
    Task<StoredWorkItemRuntime?> GetAsync(string key, CancellationToken cancellationToken);

    Task PutAsync(string key, StoredWorkItemRuntime value, CancellationToken cancellationToken);
}

public sealed class JsonWorkItemRuntimeStore(CachePaths paths) : IWorkItemRuntimeStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<StoredWorkItemRuntime?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var file = await ReadAsync(cancellationToken);
            return file.Entries.GetValueOrDefault(key);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PutAsync(
        string key,
        StoredWorkItemRuntime value,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var file = await ReadAsync(cancellationToken);
            file.Entries[key] = value;
            await WriteAsync(file, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorkItemRuntimeFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.WorkItemRuntimePath))
        {
            // TODO(post-1.0): Remove pre-overhaul cache-file detection once pre-1.0 caches are no
            // longer expected. This guard is intentionally read-only and must never migrate data.
            if (File.Exists(paths.LegacySessionPath))
                throw UnsupportedSchema(paths.LegacySessionPath);
            return new WorkItemRuntimeFile();
        }

        try
        {
            var json = await File.ReadAllTextAsync(paths.WorkItemRuntimePath, cancellationToken);
            var file = JsonSerializer.Deserialize<WorkItemRuntimeFile>(json, JsonOptions);
            if (file is null || file.Version != SchemaVersion ||
                file.Entries.Any(entry =>
                    string.IsNullOrWhiteSpace(entry.Key) ||
                    entry.Value is null ||
                    entry.Value.PendingDispatch is { IsValid: false }))
                throw new JsonException("Unsupported or invalid work-item runtime schema.");
            return file;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TrackerException(
                "WORK_ITEM_RUNTIME_CORRUPT",
                "The machine-local work-item runtime record could not be read safely.",
                9,
                new Dictionary<string, object?> { ["path"] = paths.WorkItemRuntimePath },
                exception);
        }
    }

    private static TrackerException UnsupportedSchema(string path) => new(
        "STORE_SCHEMA_UNSUPPORTED",
        "Wrighty found an unsupported machine-local runtime state file. " +
        $"Unsupported file: '{path}'. " +
        "Remove or rename the listed file, then retry. Wrighty will create current state as needed.",
        5,
        new Dictionary<string, object?>
        {
            ["path"] = path,
            ["unsupportedFiles"] = new[] { path }
        });

    private async Task WriteAsync(WorkItemRuntimeFile file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var temporaryPath = $"{paths.WorkItemRuntimePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, file, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, paths.WorkItemRuntimePath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed class WorkItemRuntimeFile
    {
        public int Version { get; init; } = SchemaVersion;

        public Dictionary<string, StoredWorkItemRuntime> Entries { get; init; } =
            new(StringComparer.Ordinal);
    }
}
