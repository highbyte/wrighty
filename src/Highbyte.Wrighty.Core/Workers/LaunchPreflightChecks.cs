using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Refusal codes emitted by the built-in launch checks. They are stable strings because operator
/// tooling, worker events, and documentation all key off them.
/// </summary>
public static class LaunchPreflightCodes
{
    /// <summary>Authoritative Project worker policy changed after the item was selected.</summary>
    public const string PolicyChanged = "LAUNCH_POLICY_CHANGED";

    /// <summary>The item resolves to a different agent than the one holding the claim.</summary>
    public const string AgentChanged = "LAUNCH_AGENT_CHANGED";

    /// <summary>The effective spawned-agent permission profile cannot be resolved.</summary>
    public const string PermissionsUnavailable = "LAUNCH_PERMISSIONS_UNAVAILABLE";
}

/// <summary>
/// Revalidates authoritative Project worker policy (plan 029) against a freshly read item, so a
/// policy change between selection and launch cannot reach a vendor process.
/// </summary>
/// <remarks>
/// Only fresh launches are gated. A resume re-enters a session that already exists on this
/// installation; refusing it here would strand a live workspace rather than protect anything, and
/// resume ownership is enforced by the claim protocol instead.
/// </remarks>
public sealed class WorkerPolicyLaunchCheck(Func<string, bool> agentIsSupported)
    : ILaunchPreflightCheck
{
    public string Name => "worker-policy";

    public bool AppliesTo(LaunchStage stage, LaunchKind kind) =>
        stage == LaunchStage.PostClaim && kind == LaunchKind.Fresh;

    public ValueTask<LaunchPreflightDecision> EvaluateAsync(
        LaunchPreflightRequest request,
        CancellationToken cancellationToken)
    {
        var decision = WorkerPolicyGate.Evaluate(
            request.Detail,
            request.Options,
            request.Config.EffectiveWorker.DefaultAgent,
            agentIsSupported);
        if (!decision.Eligible)
            return ValueTask.FromResult(LaunchPreflightDecision.Refuse(
                LaunchPreflightCodes.PolicyChanged,
                "Authoritative Project policy changed after claim: " +
                WorkerPolicyGate.Describe(decision.Reason) + ".",
                [$"policy-reason={decision.Reason}"]));

        if (!string.Equals(decision.Agent, request.Agent, StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(LaunchPreflightDecision.Refuse(
                LaunchPreflightCodes.AgentChanged,
                "Authoritative Project policy now selects a different agent than the one " +
                "holding this claim.",
                [$"claimed-agent={request.Agent}", $"policy-agent={decision.Agent}"]));

        return ValueTask.FromResult(LaunchPreflightDecision.Admit(
            [$"policy-agent={decision.Agent}"]));
    }
}

/// <summary>
/// Resolves the effective spawned-agent permission profile (plan 025) before the launch commits to
/// a workspace. An unresolvable profile is a refusal, never a silent fallback: guessing here would
/// decide how much privilege an unattended agent receives.
/// </summary>
public sealed class AgentPermissionLaunchCheck(
    Func<string, bool> agentIsSupported,
    Func<TrackerConfig, string, AgentPermissions> describe)
    : ILaunchPreflightCheck
{
    public string Name => "agent-permissions";

    public bool AppliesTo(LaunchStage stage, LaunchKind kind) => stage == LaunchStage.PostClaim;

    public ValueTask<LaunchPreflightDecision> EvaluateAsync(
        LaunchPreflightRequest request,
        CancellationToken cancellationToken)
    {
        if (!agentIsSupported(request.Agent))
            return ValueTask.FromResult(LaunchPreflightDecision.Refuse(
                LaunchPreflightCodes.PermissionsUnavailable,
                $"Agent '{request.Agent}' is not supported by this installation.",
                [$"agent={request.Agent}"]));
        try
        {
            var permissions = describe(request.Config, request.Agent);
            return ValueTask.FromResult(LaunchPreflightDecision.Admit(
            [
                $"permission-profile={permissions.ProfileName}",
                $"permission-enforcement={permissions.Enforcement.ToString().ToLowerInvariant()}"
            ]));
        }
        catch (TrackerException exception) when (exception.Code == "CONFIG_INVALID")
        {
            // Fail before the workspace exists rather than at BuildStart, so a misconfigured
            // profile does not leave a freshly created worktree behind.
            return ValueTask.FromResult(LaunchPreflightDecision.Refuse(
                LaunchPreflightCodes.PermissionsUnavailable,
                exception.Message,
                [$"agent={request.Agent}"]));
        }
    }
}
