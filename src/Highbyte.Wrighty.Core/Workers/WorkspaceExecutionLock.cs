using System.Security.Cryptography;
using System.Text;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Workers;

public interface IWorkspaceExecutionLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        string workspacePath,
        CancellationToken cancellationToken);
}

public sealed class FileWorkspaceExecutionLock(string? lockRoot = null) : IWorkspaceExecutionLock
{
    private readonly string root = lockRoot ?? Path.Combine(
        Path.GetTempPath(),
        "wrighty-workspace-locks",
        UserScope());

    public ValueTask<IAsyncDisposable> AcquireAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = CanonicalPath(workspacePath);
        string lockPath;
        try
        {
            Directory.CreateDirectory(root);
            var key = OperatingSystem.IsWindows()
                ? canonicalPath.ToUpperInvariant()
                : canonicalPath;
            var digest = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            lockPath = Path.Combine(root, $"{digest}.lock");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new TrackerException(
                "WORKSPACE_LOCK_FAILED",
                $"Could not prepare the worker execution lock for '{canonicalPath}': {exception.Message}",
                7,
                new Dictionary<string, object?> { ["workspacePath"] = canonicalPath },
                exception);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new TrackerException(
                "WORKSPACE_BUSY",
                $"Another Wrighty worker is already using '{canonicalPath}'. " +
                "Wait for it to finish, use --workspace-mode worktree for isolation, " +
                "or explicitly accept unsafe concurrency with --workspace-mode shared.",
                7,
                new Dictionary<string, object?> { ["workspacePath"] = canonicalPath },
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new TrackerException(
                "WORKSPACE_LOCK_FAILED",
                $"Could not acquire the worker execution lock for '{canonicalPath}': {exception.Message}",
                7,
                new Dictionary<string, object?> { ["workspacePath"] = canonicalPath },
                exception);
        }

        try
        {
            var metadata = Encoding.UTF8.GetBytes(
                $"workspace={canonicalPath}{Environment.NewLine}" +
                $"processId={Environment.ProcessId}{Environment.NewLine}");
            stream.SetLength(0);
            stream.Write(metadata);
            stream.Flush();
            return ValueTask.FromResult<IAsyncDisposable>(new WorkspaceExecutionLease(stream));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            return new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                   ?? fullPath;
        }
        catch (IOException)
        {
            return fullPath;
        }
    }

    private static string UserScope()
    {
        var identity = $"{Environment.UserDomainName}\0{Environment.UserName}";
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
    }

    private sealed class WorkspaceExecutionLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class NoOpWorkspaceExecutionLock : IWorkspaceExecutionLock
{
    public static NoOpWorkspaceExecutionLock Instance { get; } = new();

    public ValueTask<IAsyncDisposable> AcquireAsync(
        string workspacePath,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IAsyncDisposable>(NoOpLease.Instance);

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static NoOpLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
