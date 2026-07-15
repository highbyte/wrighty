using System.Text.Json;

namespace Highbyte.Wrighty.Caching;

public sealed class JsonNodeIdCache(CachePaths paths) : INodeIdCache
{
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ProjectMetadata?> GetAsync(string key, CancellationToken cancellationToken)
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
        ProjectMetadata value,
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

    public async Task InvalidateAsync(string key, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var file = await ReadAsync(cancellationToken);
            if (file.Entries.Remove(key))
            {
                await WriteAsync(file, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NodeCacheFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.NodeCachePath))
        {
            return new NodeCacheFile();
        }

        try
        {
            await using var stream = File.OpenRead(paths.NodeCachePath);
            var file = await JsonSerializer.DeserializeAsync<NodeCacheFile>(
                stream,
                JsonOptions,
                cancellationToken);
            return file is { Version: SchemaVersion } ? file : new NodeCacheFile();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new NodeCacheFile();
        }
    }

    private async Task WriteAsync(NodeCacheFile file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Root);
        var temporaryPath = $"{paths.NodeCachePath}.{Guid.NewGuid():N}.tmp";
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

            File.Move(temporaryPath, paths.NodeCachePath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed class NodeCacheFile
    {
        public int Version { get; init; } = SchemaVersion;

        public Dictionary<string, ProjectMetadata> Entries { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
