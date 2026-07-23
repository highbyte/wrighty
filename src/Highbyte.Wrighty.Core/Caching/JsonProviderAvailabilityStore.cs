using System.Collections.Concurrent;
using System.Text.Json;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Caching;

public sealed class JsonProviderAvailabilityStore(CachePaths paths)
    : IProviderAvailabilityStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim processGate = ProcessGates.GetOrAdd(
        Path.GetFullPath(paths.ProviderAvailabilityPath),
        _ => new SemaphoreSlim(1, 1));

    public async Task<ProviderAvailability?> GetAsync(
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

    public async Task<ProviderAvailability> OpenAsync(
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
            var record = new StoredProviderAvailability(
                key,
                ProviderAvailabilityState.UnavailableUntil,
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
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));

        await processGate.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var file = await ReadAsync(cancellationToken);
            var key = Key(agentType);
            if (!file.Entries.TryGetValue(key, out var current) ||
                current.State == ProviderAvailabilityState.Available)
                return null;
            if (current.State == ProviderAvailabilityState.UnavailableUntil &&
                current.UnavailableUntil is { } unavailableUntil &&
                unavailableUntil > observedAt)
                return null;
            if (current.State == ProviderAvailabilityState.ProbeDue &&
                current.ProbeLeaseExpiresAt is { } leaseExpiresAt &&
                leaseExpiresAt > observedAt)
                return null;

            var lease = new ProviderProbeLease(
                key,
                Guid.NewGuid().ToString("N"),
                observedAt + leaseDuration);
            file.Entries[key] = current with
            {
                State = ProviderAvailabilityState.ProbeDue,
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

    public async Task CloseAsync(
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
            file.Entries[key] = new StoredProviderAvailability(
                key,
                ProviderAvailabilityState.Available,
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
            var key = Key(lease.AgentType);
            if (!file.Entries.TryGetValue(key, out var current) ||
                current.State != ProviderAvailabilityState.ProbeDue ||
                !string.Equals(current.ProbeLeaseId, lease.LeaseId, StringComparison.Ordinal))
                return;
            file.Entries[key] = current with
            {
                State = ProviderAvailabilityState.UnavailableUntil,
                UnavailableUntil = observedAt,
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
                    paths.ProviderAvailabilityLockPath,
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
                    "PROVIDER_AVAILABILITY_BUSY",
                    "The machine-local provider availability store is busy.",
                    9,
                    new Dictionary<string, object?>
                    {
                        ["path"] = paths.ProviderAvailabilityPath
                    },
                    exception);
            }
        }
    }

    private async Task<ProviderAvailabilityFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.ProviderAvailabilityPath))
            return new ProviderAvailabilityFile();
        try
        {
            var json = await File.ReadAllTextAsync(
                paths.ProviderAvailabilityPath,
                cancellationToken);
            var file = JsonSerializer.Deserialize<ProviderAvailabilityFile>(json, JsonOptions);
            if (file is null || file.Version != SchemaVersion)
                throw new JsonException("Unsupported provider availability schema.");
            Validate(file);
            return file;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TrackerException(
                "PROVIDER_AVAILABILITY_CORRUPT",
                "The machine-local provider availability record could not be read safely. " +
                "Wrighty will not launch an automatic provider run until it is repaired.",
                9,
                new Dictionary<string, object?>
                {
                    ["path"] = paths.ProviderAvailabilityPath
                },
                exception);
        }
    }

    private static void Validate(ProviderAvailabilityFile file)
    {
        foreach (var (key, record) in file.Entries)
        {
            if (record is null ||
                string.IsNullOrWhiteSpace(key) ||
                !string.Equals(key, record.AgentType, StringComparison.OrdinalIgnoreCase) ||
                record.ConsecutiveFailures < 0 ||
                record.State == ProviderAvailabilityState.UnavailableUntil &&
                record.UnavailableUntil is null ||
                record.State == ProviderAvailabilityState.ProbeDue &&
                (string.IsNullOrWhiteSpace(record.ProbeLeaseId) ||
                 record.ProbeLeaseExpiresAt is null))
                throw new JsonException("Invalid provider availability entry.");
        }
    }

    private async Task WriteAsync(
        ProviderAvailabilityFile file,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var temporaryPath = $"{paths.ProviderAvailabilityPath}.{Guid.NewGuid():N}.tmp";
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
            File.Move(temporaryPath, paths.ProviderAvailabilityPath, true);
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

    private static ProviderAvailability Project(StoredProviderAvailability record) => new(
        record.AgentType,
        record.State,
        record.Reason,
        record.State == ProviderAvailabilityState.ProbeDue
            ? record.ProbeLeaseExpiresAt
            : record.UnavailableUntil,
        record.Confidence,
        record.ConsecutiveFailures,
        record.UpdatedAt);

    private sealed class ProviderAvailabilityFile
    {
        public int Version { get; init; } = SchemaVersion;

        public Dictionary<string, StoredProviderAvailability> Entries { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record StoredProviderAvailability(
        string AgentType,
        ProviderAvailabilityState State,
        string? Reason,
        DateTimeOffset? UnavailableUntil,
        AgentFailureConfidence Confidence,
        int ConsecutiveFailures,
        DateTimeOffset UpdatedAt,
        string? ProbeLeaseId = null,
        DateTimeOffset? ProbeLeaseExpiresAt = null);
}
