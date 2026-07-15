using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.LocalMarkdown;

internal sealed class LocalStoreLock : IAsyncDisposable
{
    private readonly FileStream stream;

    private LocalStoreLock(FileStream stream) => this.stream = stream;

    public static async Task<LocalStoreLock> AcquireAsync(
        string root,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".lock");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new LocalStoreLock(new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous));
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }
            catch (IOException exception)
            {
                throw new TrackerException(
                    "STORE_BUSY",
                    $"The local Wrighty store '{root}' is busy.",
                    9,
                    new Dictionary<string, object?> { ["path"] = root },
                    exception);
            }
        }
    }

    public async ValueTask DisposeAsync() => await stream.DisposeAsync();
}
