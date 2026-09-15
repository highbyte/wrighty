using System.Security.Cryptography;
using System.Text.Json;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Actions;

public sealed record WorkflowActionState(
    string? Status, string OperationalStatus, bool AutomaticExecutionAllowed, string? DispatchState);

public sealed record WorkflowActionResult(
    string ItemId, string Action, string Outcome, DateTimeOffset ObservedAt,
    string StateVersion, WorkflowActionState Before, WorkflowActionState After)
{
    public bool StartsWorker { get; } = false;
}

/// <summary>Implemented only by backends that can revalidate and mutate under one authority boundary.</summary>
public interface IWorkflowActionBackend
{
    Task<WorkflowActionResult> ExecuteWorkflowActionAsync(
        TrackerConfig config, WorkItemId id, string action, string expectedVersion,
        CancellationToken cancellationToken);
}

public sealed class WorkflowActionService(TrackerService tracker)
{
    public async Task<WorkflowActionResult> ExecuteAsync(
        TrackerConfig config, WorkItemId id, string action, string? expectedVersion,
        CancellationToken cancellationToken)
    {
        EnsureSupported(action);
        if (tracker.Backend(config) is not IWorkflowActionBackend backend)
            throw new TrackerException("NOT_SUPPORTED", "This backend does not support board workflow execution.", 3);
        var current = await tracker.GetOperationalAsync(config, id, cancellationToken);
        return await backend.ExecuteWorkflowActionAsync(
            config, id, action, expectedVersion ?? Version(config, current), cancellationToken);
    }

    public static void EnsureSupported(string action)
    {
        if (action is not ("queue" or "send-back" or "resume"))
            throw new TrackerException("ACTION_EXECUTION_UNSUPPORTED",
                "Only queue, send-back, and resume support workflow execution.", 2);
    }

    public static OperationalAction Select(TrackerConfig config, WorkItemOperationalState state, string action)
    {
        EnsureSupported(action);
        return OperationalActionResolver.BoardActions(new(config, state, DateTimeOffset.UtcNow,
            state.Session?.WorkspacePath is { } path && Directory.Exists(path)))
            .Single(value => value.Name == action);
    }

    public static void Validate(TrackerConfig config, WorkItemOperationalState state,
        string action, string expectedVersion)
    {
        var selected = Select(config, state, action);
        if (selected.UnavailableCode is { } code)
            throw new TrackerException(code, selected.UnavailableReason!, 6);
        if (!string.Equals(expectedVersion, Version(config, state), StringComparison.Ordinal))
            throw new TrackerException("ACTION_STATE_CHANGED",
                "The item, claim, session, or configuration changed. Inspect the action again before retrying.", 6);
    }

    // An observation fingerprint detects changes; it contains no credentials and grants no authority.
    public static string Version(TrackerConfig config, WorkItemOperationalState state) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { config, state })));

    public static WorkflowActionState Describe(WorkItemOperationalState state) => new(
        state.Item.Status, state.OperationalStatus, state.Item.AutomaticExecutionAllowed, state.Item.DispatchState);

    public static WorkItemOperationalState Operational(TrackerConfig config, WorkItemOperationalSnapshot snapshot) => new(
        snapshot.Item, snapshot.Claim, snapshot.Session,
        OperationalStatuses.Resolve(snapshot.Item, snapshot.Claim, snapshot.Session,
            config.DefaultPickFrom, config.DefaultFinishTo));
}
