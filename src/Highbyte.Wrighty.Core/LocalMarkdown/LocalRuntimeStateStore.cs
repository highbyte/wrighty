using System.Text.Json;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.LocalMarkdown;

internal sealed record LocalClaimRecord(
    string InstallationId,
    string ClaimantId,
    string ClaimToken,
    string? Agent,
    string? SessionId,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    string ClaimantKind,
    string? WorkspacePath = null,
    string? Branch = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAddress =>
        !string.IsNullOrWhiteSpace(Agent) ||
        !string.IsNullOrWhiteSpace(SessionId) ||
        !string.IsNullOrWhiteSpace(WorkspacePath);
}

internal sealed record LocalWorkItemRuntime(
    string InstallationId,
    SessionAddress? Session,
    LastRunRecord? LastRun,
    PendingDispatch? PendingDispatch,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastClaimExpiresAt,
    // Hashes and identifiers describing what the recorded session was given; never content.
    // Optional and last so state written by an earlier build still deserializes.
    ApprovedContext.SessionContextMetadata? Context = null,
    // The last run's structured report, kept whether or not it was published. Publishing is a
    // choice about a shared surface; losing an agent's account because nobody wanted it commented
    // on the issue would discard the only record of what it decided.
    ApprovedContext.AgentRunReport? LastReport = null);

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
    public Dictionary<int, LocalWorkItemRuntime> Items { get; init; } = [];

    public LocalClaimRecord? ActiveClaim(int id, DateTimeOffset now) =>
        Claims.TryGetValue(id, out var claim) && claim.ExpiresAt > now ? claim : null;

    public LocalClaimRecord? Claim(int id) => Claims.GetValueOrDefault(id);

    public LocalWorkItemRuntime? Runtime(int id) => Items.GetValueOrDefault(id);

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
        var previous = Items.GetValueOrDefault(id);
        var sameSession = previous is not null &&
            string.Equals(previous.Session?.Id, claim.SessionId, StringComparison.Ordinal);
        Items[id] = new LocalWorkItemRuntime(
            claim.InstallationId,
            new SessionAddress(
                claim.Agent,
                claim.SessionId,
                claim.WorkspacePath ?? (sameSession ? previous!.Session?.WorkspacePath : null),
                claim.Branch ?? previous?.Session?.Branch),
            sameSession ? previous!.LastRun : null,
            sameSession ? previous!.PendingDispatch : null,
            now,
            claim.ExpiresAt,
            // Carried forward unconditionally, unlike the run outcome. The context is written by the
            // launch immediately before the process starts, at which point the vendor has not yet
            // reported the session id it will use — so gating it on session-id equality discards it
            // the moment that id lands, which is every run. It is superseded only by the next launch
            // that resolves one, and every launch records before it spawns, so a session can never
            // end up holding a context that some other launch resolved.
            previous?.Context,
            // Gated on the session, like the run outcome: a report describes one run, and carrying
            // it onto a different session would attribute an account to work it never saw.
            sameSession ? previous!.LastReport : null);
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
        DateTimeOffset endedAt,
        AgentFailure? failure)
    {
        var previous = Items.GetValueOrDefault(id);
        Items[id] = previous is null
            ? new LocalWorkItemRuntime(
                string.Empty,
                null,
                new LastRunRecord(outcome, endedAt, finalMessage, failure),
                null,
                endedAt,
                null)
            : previous with
            {
                LastRun = new LastRunRecord(outcome, endedAt, finalMessage, failure),
                UpdatedAt = endedAt
            };
    }

    /// <summary>
    /// Records what the launch supplied to the session. Overwrite-only and merge-onto-existing, and
    /// it creates a minimal record when none exists — a launch can resolve a context before any
    /// claim metadata has been written, and the run that established the context must not be the
    /// one unable to prove what it supplied.
    /// </summary>
    public void RecordSessionContext(
        int id,
        ApprovedContext.SessionContextMetadata context,
        DateTimeOffset now)
    {
        var previous = Items.GetValueOrDefault(id);
        Items[id] = previous is null
            ? new LocalWorkItemRuntime(string.Empty, null, null, null, now, null, context)
            : previous with { Context = context, UpdatedAt = now };
    }

    /// <summary>Records the run's structured report, whether or not it was published anywhere.</summary>
    public void RecordRunReport(int id, ApprovedContext.AgentRunReport report, DateTimeOffset now)
    {
        var previous = Items.GetValueOrDefault(id);
        Items[id] = previous is null
            ? new LocalWorkItemRuntime(string.Empty, null, null, null, now, null, null, report)
            : previous with { LastReport = report, UpdatedAt = now };
    }

    public bool RecordPendingDispatch(int id, PendingDispatch dispatch, DateTimeOffset updatedAt)
    {
        PreserveSession(id, Claim(id), updatedAt);
        if (Items.GetValueOrDefault(id) is not { } previous)
            return false;
        Items[id] = previous with { PendingDispatch = dispatch, UpdatedAt = updatedAt };
        return true;
    }

    public void ClearPendingDispatch(int id, DateTimeOffset updatedAt)
    {
        if (Items.GetValueOrDefault(id) is not { PendingDispatch: not null } previous)
            return;
        Items[id] = previous with { PendingDispatch = null, UpdatedAt = updatedAt };
    }
}

internal static class LocalRuntimeStateStore
{
    public const string FileName = ".wrighty-runtime-v1.json";
    private const string LegacyFileName = ".runtime-state.json";

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
            // TODO(post-1.0): Remove pre-overhaul runtime-file detection once pre-1.0 stores are
            // no longer expected. This guard is intentionally read-only and must never migrate
            // legacy runtime state into the current schema.
            var legacyPath = Path.Combine(root, LegacyFileName);
            if (File.Exists(legacyPath))
            {
                throw new TrackerException(
                    "STORE_SCHEMA_UNSUPPORTED",
                    "Wrighty found an unsupported Local Markdown runtime state file. " +
                    $"Unsupported file: '{legacyPath}'. " +
                    "Remove or rename the listed file, then retry. " +
                    "Wrighty will create current state as needed.",
                    5,
                    new Dictionary<string, object?>
                    {
                        ["path"] = legacyPath,
                        ["unsupportedFiles"] = new[] { legacyPath }
                    });
            }

            return new LocalRuntimeState();
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var state = JsonSerializer.Deserialize<LocalRuntimeState>(json, JsonOptions);
            if (state is null || state.Version != 1)
            {
                throw Invalid(path, $"unsupported runtime version '{state?.Version}'.");
            }

            if (state.Items.Any(entry =>
                    entry.Value is null ||
                    entry.Value.PendingDispatch is { IsValid: false }))
                throw new JsonException("Invalid work-item runtime entry.");
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
            $"The local runtime file '{path}' is invalid: {message} " +
            "Claims cannot be arbitrated from a corrupt runtime state. Restore or delete the file; " +
            "deleting it releases every live local claim.",
            3,
            new Dictionary<string, object?> { ["path"] = path },
            cause);
}
