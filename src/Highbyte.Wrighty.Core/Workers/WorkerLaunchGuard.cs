using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Workers;

/// <summary>Prevents worker-spawned sessions from recursively creating more worker hosts.</summary>
public static class WorkerLaunchGuard
{
    public const string ChildEnvironmentVariable = "WRIGHTY_WORKER_CHILD";

    public static void EnsureAllowed()
    {
        if (Environment.GetEnvironmentVariable(ChildEnvironmentVariable) == "1")
            throw new TrackerException("WORKER_RECURSIVE_LAUNCH",
                "A worker-spawned agent must not start another worker. Let its owning worker manage execution.", 2);
    }
}
