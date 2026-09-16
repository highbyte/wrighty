using System.Security.Cryptography;
using System.Text.Json;
using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Actions;

public sealed record WorkflowBatchCandidate(
    string Id, string Title, string StateVersion, WorkflowActionState Before, string Consequence);

public sealed record WorkflowBatchPreview(
    string Id, string Action, string ConfigurationVersion, DateTimeOffset CreatedAt,
    IReadOnlyList<WorkflowBatchCandidate> Candidates, int SelectedCount, int EligibleCount)
{
    public DateTimeOffset ExpiresAt => CreatedAt + WorkflowBatchPolicy.PreviewLifetime;
    public bool Limited => EligibleCount > Candidates.Count;
    public bool StartsWorker { get; } = false;
}

public sealed record WorkflowBatchItemResult(
    string Id, string Outcome, string? Code = null, WorkflowActionResult? Applied = null,
    bool MutationMayHaveApplied = false);

public sealed record WorkflowBatchRecord(
    WorkflowBatchPreview Preview, string State, IReadOnlyList<WorkflowBatchItemResult> Items,
    string? ActiveItemId = null, string? StopCode = null)
{
    public bool HasIssues => StopCode is not null || Items.Any(item => item.Outcome != "applied");
}

public static class WorkflowBatchPolicy
{
    public const int MaximumCandidates = 100;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(5);

    public static string ConfigurationVersion(TrackerConfig config) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            config, config.SourcePath, config.SourceRevision
        })));

    public static bool IsItemConflict(string code) => code is
        "BATCH_ITEM_INELIGIBLE" or "WORK_ITEM_NOT_FOUND" or "WORK_ITEM_ARCHIVED" or "ITEM_ARCHIVED" or
        "CLAIM_NOT_OWNER" or "CLAIM_REQUIRED" or "CLAIM_HELD" or "CLAIM_HELD_BY_LOCAL_CLAIMANT" or
        "CLAIM_STALE" or "CLAIM_TOKEN_REQUIRED" or "UPDATE_CONFLICT" or "WEB_CLAIM_GENERATION_STALE" or
        "WORKER_ITEM_INELIGIBLE" or "ACTION_STATE_CHANGED" or "WORKFLOW_STATE_INVALID" or
        "WORKER_RECOVERY_PENDING" or "WORKER_ITEM_NOT_PAUSED" or "RESUME_SESSION_CHANGED" or
        "SESSION_LAUNCH_NOT_ALLOWED" or "RESUME_ADDRESS_UNAVAILABLE" or "RESUME_ADDRESS_NOT_LOCAL" or
        "RESUME_WORKTREE_ABSENT" or "STATUS_MOVE_NOT_ALLOWED";
}
