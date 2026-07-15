using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbyte.Wrighty.Caching;

namespace Highbyte.Wrighty.Identity;

public sealed class WorkerIdentityProvider(CachePaths paths) : IWorkerIdentityProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<string> GetIdentityAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var installId = await ReadOrCreateInstallIdAsync(cancellationToken);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(installId));
            return Convert.ToHexString(digest)[..12].ToLowerInvariant();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> ReadOrCreateInstallIdAsync(CancellationToken cancellationToken)
    {
        var existing = await TryReadInstallIdAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        if (File.Exists(paths.IdentityPath))
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(10, cancellationToken);
                existing = await TryReadInstallIdAsync(cancellationToken);
                if (existing is not null)
                {
                    return existing;
                }
            }

            // The identity file is regenerable. Remove state that stayed malformed after
            // waiting for any concurrent writer to finish.
            File.Delete(paths.IdentityPath);
        }

        Directory.CreateDirectory(paths.Root);
        var created = Guid.NewGuid().ToString("D");
        try
        {
            await using var stream = new FileStream(
                paths.IdentityPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            await JsonSerializer.SerializeAsync(
                stream,
                new IdentityFile { InstallId = created },
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return created;
        }
        catch (IOException) when (File.Exists(paths.IdentityPath))
        {
            // Another process initialized this installation concurrently. Its identity wins.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var winner = await TryReadInstallIdAsync(cancellationToken);
                if (winner is not null)
                {
                    return winner;
                }

                await Task.Delay(10, cancellationToken);
            }

            throw;
        }
    }

    private async Task<string?> TryReadInstallIdAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.IdentityPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                paths.IdentityPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var identity = await JsonSerializer.DeserializeAsync<IdentityFile>(
                stream,
                JsonOptions,
                cancellationToken);
            return Guid.TryParse(identity?.InstallId, out var installId)
                ? installId.ToString("D")
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private sealed class IdentityFile
    {
        public string InstallId { get; init; } = string.Empty;
    }
}
