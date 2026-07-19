using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkspaceExecutionLockTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-workspace-lock-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Same_canonical_workspace_is_exclusive_and_released_on_dispose()
    {
        var workspace = Path.Combine(root, "repo");
        var lockRoot = Path.Combine(root, "locks");
        Directory.CreateDirectory(workspace);
        var managerA = new FileWorkspaceExecutionLock(lockRoot);
        var managerB = new FileWorkspaceExecutionLock(lockRoot);
        var first = await managerA.AcquireAsync(workspace, CancellationToken.None);

        var busy = await Assert.ThrowsAsync<TrackerException>(async () =>
            await managerB.AcquireAsync(
                Path.Combine(workspace, "..", "repo", "."),
                CancellationToken.None));

        Assert.Equal("WORKSPACE_BUSY", busy.Code);
        Assert.Equal(Path.GetFullPath(workspace), busy.Details["workspacePath"]);

        await first.DisposeAsync();
        await using var recovered = await managerB.AcquireAsync(
            workspace,
            CancellationToken.None);
    }

    [Fact]
    public async Task Existing_lock_file_without_an_owner_is_reusable()
    {
        var workspace = Path.Combine(root, "repo");
        var lockRoot = Path.Combine(root, "locks");
        Directory.CreateDirectory(workspace);
        var manager = new FileWorkspaceExecutionLock(lockRoot);

        var first = await manager.AcquireAsync(workspace, CancellationToken.None);
        await first.DisposeAsync();
        Assert.Single(Directory.EnumerateFiles(lockRoot, "*.lock"));

        await using var second = await manager.AcquireAsync(workspace, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
