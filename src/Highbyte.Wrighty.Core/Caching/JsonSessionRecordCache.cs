using System.Text.Json;

namespace Highbyte.Wrighty.Caching;

/// <summary>
/// A recorded agent session address for one work item. Stored machine-locally so the resume
/// address survives claim release, expiry, and remote claim-history cleanup. Recovery is only
/// meaningful on the installation that recorded the workspace and vendor session.
/// </summary>
public sealed record CachedSessionRecord(
    string? AgentType,
    string? SessionId,
    string? WorkspacePath,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastClaimExpiresAt,
    string? Branch = null);

public interface ISessionRecordCache
{
    Task<CachedSessionRecord?> GetAsync(string key, CancellationToken cancellationToken);

    Task PutAsync(string key, CachedSessionRecord value, CancellationToken cancellationToken);
}

public sealed class JsonSessionRecordCache(CachePaths paths) : ISessionRecordCache
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<CachedSessionRecord?> GetAsync(string key, CancellationToken cancellationToken)
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
        CachedSessionRecord value,
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

    private async Task<SessionCacheFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.SessionCachePath))
        {
            return new SessionCacheFile();
        }

        try
        {
            await using var stream = File.OpenRead(paths.SessionCachePath);
            var file = await JsonSerializer.DeserializeAsync<SessionCacheFile>(
                stream,
                JsonOptions,
                cancellationToken);
            return file is { Version: SchemaVersion } ? file : new SessionCacheFile();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new SessionCacheFile();
        }
    }

    private async Task WriteAsync(SessionCacheFile file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var temporaryPath = $"{paths.SessionCachePath}.{Guid.NewGuid():N}.tmp";
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

            File.Move(temporaryPath, paths.SessionCachePath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed class SessionCacheFile
    {
        public int Version { get; init; } = SchemaVersion;

        public Dictionary<string, CachedSessionRecord> Entries { get; init; } =
            new(StringComparer.Ordinal);
    }
}
