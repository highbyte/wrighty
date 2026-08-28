using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Web;

internal sealed record HostedWorkerProjectedState(
    WebHostedWorkerState State,
    string? ItemId,
    string? Agent);

internal static class HostedWorkerEventProjection
{
    public static HostedWorkerProjectedState Apply(
        WebHostedWorkerState state,
        string? itemId,
        string? agent,
        WorkerEvent value)
    {
        var preparing = value.Type == "preparing";
        var running = value.Type is "started" or "resumed" or "retry-started" or
            "handoff-started" or "requirements-assessment-started" or "running" or "session";
        var terminal = value.Type is "finished" or "needs-attention" or "failed" or
            "fenced" or "timed-out" or "rejected" or "retry-scheduled" or "interrupted";
        var stopping = state is WebHostedWorkerState.Draining or
            WebHostedWorkerState.StoppingNow or WebHostedWorkerState.Finalizing;
        state = ProjectAmbientState(state, value.Type, stopping);

        if ((preparing || running) && value.ItemId is not null)
        {
            if (!stopping)
                state = WebHostedWorkerState.Running;
            itemId = value.ItemId;
            agent = value.Agent;
        }
        else if (terminal)
        {
            itemId = null;
            agent = null;
            if (state == WebHostedWorkerState.StoppingNow)
                state = WebHostedWorkerState.Finalizing;
            else if (!stopping)
                state = WebHostedWorkerState.Running;
        }
        return new HostedWorkerProjectedState(state, itemId, agent);
    }

    private static WebHostedWorkerState ProjectAmbientState(
        WebHostedWorkerState state,
        string eventType,
        bool stopping)
    {
        if (stopping)
            return state;
        return eventType switch
        {
            "workspace-busy" => WebHostedWorkerState.WaitingForWorkspace,
            "idle" or "no-item" => WebHostedWorkerState.Running,
            _ => state
        };
    }

    public static string Level(WorkerEvent value) =>
        WorkerEventClassifier.Classify(value.Type) switch
        {
            WorkerEventSemantic.Success => "success",
            WorkerEventSemantic.Warning => "warning",
            WorkerEventSemantic.Danger => "danger",
            WorkerEventSemantic.Muted => "muted",
            _ => "info"
        };

    public static string? SafeEventMessage(WorkerEvent value) => value.Type switch
    {
        "idle" or "no-item" or "retry-scheduled" or "workspace-busy" or
            "agent-unavailable" or "provider-unavailable" => SafeMessage(value.Message),
        "preparing" => "The worker is preparing the item for its agent.",
        "started" => "The agent session started.",
        "resumed" => "The retained agent session resumed.",
        "running" or "session" => "The agent session is running.",
        "finished" => "The item finished.",
        "needs-attention" => "The item needs operator attention.",
        "failed" => "The agent session failed.",
        "fenced" => "The worker lost claim ownership.",
        "timed-out" => "The agent session timed out.",
        "rejected" => "The agent session was rejected.",
        "interrupted" => value.Outcome == AgentOutcome.InterruptedByOperator
            ? "The operator stopped the agent session; item finalization completed."
            : "The web host interrupted the agent session; item finalization completed.",
        _ => null
    };

    public static string? SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var safe = new string(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        return safe.Length <= 100 ? safe : safe[..100];
    }

    public static string? SafeMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var safe = new string(value.Trim().Where(character =>
            character is '\t' or '\r' or '\n' || !char.IsControl(character)).ToArray());
        return safe.Length <= 500 ? safe : $"{safe[..499]}…";
    }

    public static Highbyte.Wrighty.Workers.WorkspaceMode WorkspaceMode(string? value) =>
        value?.ToLowerInvariant() switch
    {
        "worktree" => Highbyte.Wrighty.Workers.WorkspaceMode.Worktree,
        "shared" => Highbyte.Wrighty.Workers.WorkspaceMode.Shared,
        _ => Highbyte.Wrighty.Workers.WorkspaceMode.Current
    };
}
