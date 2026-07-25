using System.Collections.Concurrent;
using System.Text.Json;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Caching;

public sealed class JsonProviderCapacityStore(CachePaths paths)
    : IProviderCapacityStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim processGate = ProcessGates.GetOrAdd(
        Path.GetFullPath(paths.ProviderCapacityPath),
        _ => new SemaphoreSlim(1, 1));

    public async Task<IReadOnlyList<ProviderCapacity>> ListAsync(
        CancellationToken cancellationToken)
    {
        await processGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var file = await ReadAsync(cancellationToken);
            return file.Entries.Values
                .Select(Project)
                .OrderBy(value => value.Agent, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            processGate.Release();
        }
    }

    public async Task<ProviderCapacity?> GetAsync(
        string agentType,
        CancellationToken cancellationToken)
    {
        await processGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var file = await ReadAsync(cancellationToken);
            return file.Entries.TryGetValue(Key(agentType), out var record)
                ? Project(record)
                : null;
        }
        finally
        {
            processGate.Release();
        }
    }

    public async Task<ProviderCapacity> RecordUnavailableAsync(
        string agentType,
        string? reason,
        DateTimeOffset unavailableUntil,
        AgentFailureConfidence confidence,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await processGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var file = await ReadAsync(cancellationToken);
            var key = Key(agentType);
            var failures = file.Entries.TryGetValue(key, out var previous)
                ? previous.ConsecutiveFailures + 1
                : 1;
            var record = new StoredProviderCapacity(
                key,
                ProviderCapacityState.UnavailableUntil,
                SanitizeReason(reason),
                unavailableUntil,
                confidence,
                failures,
                observedAt);
            file.Entries[key] = record;
            await WriteAsync(file, cancellationToken);
            return Project(record);
        }
        finally
        {
            processGate.Release();
        }
    }

    public async Task<ProviderProbeLease?> TryAcquireProbeAsync(
        string agentType,
        DateTimeOffset observedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken,
        bool allowBeforeUnavailableUntil = false,
        bool allowWhenAvailable = false)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await processGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var file = await ReadAsync(cancellationToken);
            var key = Key(agentType);
            var exists = file.Entries.TryGetValue(key, out var current);
            if ((!exists || current!.State == ProviderCapacityState.Available) &&
                !allowWhenAvailable)
                return null;
            current ??= new StoredProviderCapacity(
                key,
                ProviderCapacityState.Available,
                null,
                null,
                AgentFailureConfidence.Authoritative,
                0,
                observedAt);
            if (current.State == ProviderCapacityState.UnavailableUntil &&
                current.UnavailableUntil is { } unavailableUntil &&
                unavailableUntil > observedAt &&
                !allowBeforeUnavailableUntil)
                return null;
            if (current.State == ProviderCapacityState.ProbeInProgress &&
                current.ProbeLeaseExpiresAt is { } leaseExpiresAt &&
                leaseExpiresAt > observedAt)
                return null;

            var lease = new ProviderProbeLease(
                key,
                Guid.NewGuid().ToString("N"),
                observedAt + leaseDuration);
            file.Entries[key] = current with
            {
                State = ProviderCapacityState.ProbeInProgress,
                Reason = current.State == ProviderCapacityState.Available
                    ? null
                    : current.Reason,
                UpdatedAt = observedAt,
                ProbeLeaseId = lease.LeaseId,
                ProbeLeaseExpiresAt = lease.ExpiresAt
            };
            await WriteAsync(file, cancellationToken);
            return lease;
        }
        finally
        {
            processGate.Release();
        }
    }

    public async Task RecordAvailableAsync(
        string agentType,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await processGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var file = await ReadAsync(cancellationToken);
            var key = Key(agentType);
            file.Entries[key] = new StoredProviderCapacity(
                key,
                ProviderCapacityState.Available,
                "A provider run completed without a capacity failure.",
                null,
                AgentFailureConfidence.Authoritative,
                0,
                observedAt);
            await WriteAsync(file, cancellationToken);
        }
        finally
        {
            processGate.Release();
        }
    }

    public async Task ReleaseProbeAsync(
        ProviderProbeLease lease,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await processGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var file = await ReadAsync(cancellationToken);
            var key = Key(lease.Agent);
            if (!file.Entries.TryGetValue(key, out var current) ||
                current.State != ProviderCapacityState.ProbeInProgress ||
                !string.Equals(current.ProbeLeaseId, lease.LeaseId, StringComparison.Ordinal))
                return;
            file.Entries[key] = current with
            {
                State = current.UnavailableUntil is null
                    ? ProviderCapacityState.Available
                    : ProviderCapacityState.UnavailableUntil,
                UpdatedAt = observedAt,
                ProbeLeaseId = null,
                ProbeLeaseExpiresAt = null
            };
            await WriteAsync(file, cancellationToken);
        }
        finally
        {
            processGate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    paths.ProviderCapacityLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }
            catch (IOException exception)
            {
                throw new TrackerException(
                    "PROVIDER_CAPACITY_BUSY",
                    "The machine-local provider capacity store is busy.",
                    9,
                    new Dictionary<string, object?>
                    {
                        ["path"] = paths.ProviderCapacityPath
                    },
                    exception);
            }
        }
    }

    private async Task<ProviderCapacityFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.ProviderCapacityPath))
        {
            // TODO(post-1.0): Remove pre-overhaul cache-file detection once pre-1.0 caches are no
            // longer expected. This guard is intentionally read-only and must never migrate data.
            if (File.Exists(paths.LegacyProviderAvailabilityPath))
            {
                throw new TrackerException(
                    "STORE_SCHEMA_UNSUPPORTED",
                    "Wrighty found an unsupported machine-local provider state file. " +
                    $"Unsupported file: '{paths.LegacyProviderAvailabilityPath}'. " +
                    "Remove or rename the listed file, then retry. " +
                    "Wrighty will create current state as needed.",
                    5,
                    new Dictionary<string, object?>
                    {
                        ["path"] = paths.LegacyProviderAvailabilityPath,
                        ["unsupportedFiles"] =
                            new[] { paths.LegacyProviderAvailabilityPath }
                    });
            }
            return new ProviderCapacityFile();
        }
        try
        {
            var json = await File.ReadAllTextAsync(
                paths.ProviderCapacityPath,
                cancellationToken);
            var file = JsonSerializer.Deserialize<ProviderCapacityFile>(json, JsonOptions);
            if (file is null || file.Version != SchemaVersion)
                throw new JsonException("Unsupported provider capacity schema.");
            Validate(file);
            return file;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TrackerException(
                "PROVIDER_CAPACITY_CORRUPT",
                "The machine-local provider capacity record could not be read safely. " +
                "Wrighty will not launch an automatic provider run until it is repaired.",
                9,
                new Dictionary<string, object?>
                {
                    ["path"] = paths.ProviderCapacityPath
                },
                exception);
        }
    }

    private static void Validate(ProviderCapacityFile file)
    {
        foreach (var (key, record) in file.Entries)
        {
            if (record is null ||
                string.IsNullOrWhiteSpace(key) ||
                !string.Equals(key, record.Agent, StringComparison.OrdinalIgnoreCase) ||
                record.ConsecutiveFailures < 0 ||
                record.State == ProviderCapacityState.UnavailableUntil &&
                record.UnavailableUntil is null ||
                record.State == ProviderCapacityState.ProbeInProgress &&
                (string.IsNullOrWhiteSpace(record.ProbeLeaseId) ||
                 record.ProbeLeaseExpiresAt is null))
                throw new JsonException("Invalid provider capacity entry.");
        }
    }

    private async Task WriteAsync(
        ProviderCapacityFile file,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var temporaryPath = $"{paths.ProviderCapacityPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    file,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, paths.ProviderCapacityPath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string Key(string agentType)
    {
        if (string.IsNullOrWhiteSpace(agentType))
            throw new ArgumentException("Agent type is required.", nameof(agentType));
        return agentType.Trim().ToLowerInvariant();
    }

    private static string? SanitizeReason(string? reason)
    {
        var sanitized = AgentFailureClassifier.SanitizeMessage(reason);
        if (sanitized is null)
            return null;
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    private static ProviderCapacity Project(StoredProviderCapacity record) => new(
        record.Agent,
        record.State,
        record.Reason,
        record.State == ProviderCapacityState.ProbeInProgress
            ? record.ProbeLeaseExpiresAt
            : record.UnavailableUntil,
        record.Confidence,
        record.ConsecutiveFailures,
        record.UpdatedAt);

    private sealed class ProviderCapacityFile
    {
        public int Version { get; init; } = SchemaVersion;

        public Dictionary<string, StoredProviderCapacity> Entries { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record StoredProviderCapacity(
        string Agent,
        ProviderCapacityState State,
        string? Reason,
        DateTimeOffset? UnavailableUntil,
        AgentFailureConfidence Confidence,
        int ConsecutiveFailures,
        DateTimeOffset UpdatedAt,
        string? ProbeLeaseId = null,
        DateTimeOffset? ProbeLeaseExpiresAt = null);
}
