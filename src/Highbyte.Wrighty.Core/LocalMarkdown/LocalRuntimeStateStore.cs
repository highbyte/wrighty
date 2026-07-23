using System.Text.Json;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.LocalMarkdown;

internal sealed record LocalClaimRecord(
    string WorkerIdentity,
    string ClaimantId,
    string ClaimToken,
    string? AgentType,
    string? SessionId,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    string ClaimantKind,
    string? WorkspacePath = null,
    string? Branch = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAddress =>
        !string.IsNullOrWhiteSpace(AgentType) ||
        !string.IsNullOrWhiteSpace(SessionId) ||
        !string.IsNullOrWhiteSpace(WorkspacePath);
}

internal sealed record LocalSessionRecord(
    string WorkerIdentity,
    string? AgentType,
    string? SessionId,
    string? WorkspacePath,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastClaimExpiresAt,
    string? Branch = null,
    Claims.RunOutcome? Outcome = null,
    string? FinalMessage = null,
    DateTimeOffset? EndedAt = null);

/// <summary>
/// Machine-local runtime state for one Local Markdown store: the authoritative live claims and
/// the durable per-item agent session records. This state never belongs in the committed work-item
/// documents: Git does not arbitrate local claims, and session addresses are only meaningful on
/// the filesystem that recorded them. All access must happen while holding the store lock.
/// </summary>
internal sealed class LocalRuntimeState
{
    public int Version { get; init; } = 1;
    public Dictionary<int, LocalClaimRecord> Claims { get; init; } = [];
    public Dictionary<int, LocalSessionRecord> Sessions { get; init; } = [];

    public LocalClaimRecord? ActiveClaim(int id, DateTimeOffset now) =>
        Claims.TryGetValue(id, out var claim) && claim.ExpiresAt > now ? claim : null;

    public LocalClaimRecord? Claim(int id) => Claims.GetValueOrDefault(id);

    public LocalSessionRecord? Session(int id) => Sessions.GetValueOrDefault(id);

    /// <summary>
    /// Preserves a claim's recorded session address as the item's durable session record before
    /// the claim is removed or replaced. Session records are overwrite-only: they survive
    /// release, expiry, finish, and archive until a newer address replaces them.
    /// </summary>
    public void PreserveSession(int id, LocalClaimRecord? claim, DateTimeOffset now)
    {
        if (claim is not { HasAddress: true })
        {
            return;
        }

        // The captured run outcome is written separately (RecordRunOutcome) after the run ends.
        // Carry it forward here so a later claim-metadata refresh for the same recorded session
        // does not wipe the "what happened" signal; a different session starts with no outcome.
        var previous = Sessions.GetValueOrDefault(id);
        var sameSession = previous is not null &&
            string.Equals(previous.SessionId, claim.SessionId, StringComparison.Ordinal);
        Sessions[id] = new LocalSessionRecord(
            claim.WorkerIdentity,
            claim.AgentType,
            claim.SessionId,
            claim.WorkspacePath,
            now,
            claim.ExpiresAt,
            claim.Branch ?? previous?.Branch,
            sameSession ? previous!.Outcome : null,
            sameSession ? previous!.FinalMessage : null,
            sameSession ? previous!.EndedAt : null);
    }

    /// <summary>
    /// Records the outcome of the just-ended agent run onto the item's durable session record.
    /// Overwrite-only and merge-onto-existing: the address fields are preserved; only the run
    /// outcome, final message, and end time are set. Creates a minimal record when none exists so
    /// the "what happened" signal survives even after the workspace is cleaned up.
    /// </summary>
    public void RecordRunOutcome(
        int id,
        Claims.RunOutcome outcome,
        string? finalMessage,
        DateTimeOffset endedAt)
    {
        var previous = Sessions.GetValueOrDefault(id);
        Sessions[id] = previous is null
            ? new LocalSessionRecord(
                string.Empty, null, null, null, endedAt, null, null,
                outcome, finalMessage, endedAt)
            : previous with { Outcome = outcome, FinalMessage = finalMessage, EndedAt = endedAt };
    }
}

internal static class LocalRuntimeStateStore
{
    public const string FileName = ".runtime-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string PathFor(string root) => Path.Combine(root, FileName);

    public static async Task<LocalRuntimeState> LoadUnlockedAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = PathFor(root);
        if (!File.Exists(path))
        {
            return new LocalRuntimeState();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<LocalRuntimeState>(
                stream,
                JsonOptions,
                cancellationToken);
            if (state is null || state.Version != 1)
            {
                throw Invalid(path, $"unsupported runtime-state version '{state?.Version}'.");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw Invalid(path, exception.Message, exception);
        }
    }

    public static async Task SaveUnlockedAsync(
        string root,
        LocalRuntimeState state,
        CancellationToken cancellationToken)
    {
        var path = PathFor(root);
        var temporary = Path.Combine(root, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static TrackerException Invalid(string path, string message, Exception? cause = null) =>
        new(
            "LOCAL_STORE_INVALID",
            $"The local runtime-state file '{path}' is invalid: {message} " +
            "Claims cannot be arbitrated from a corrupt runtime state. Restore or delete the file; " +
            "deleting it releases every live local claim.",
            3,
            new Dictionary<string, object?> { ["path"] = path },
            cause);
}
