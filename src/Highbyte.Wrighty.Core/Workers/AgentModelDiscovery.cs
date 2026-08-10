namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Whether a model accepts a reasoning-effort setting.
///
/// Three states rather than two, because the vendors genuinely differ in what they will tell us:
/// claude reports it for every model, codex reports it for every model, and copilot reports it only
/// for the session's *current* model. Collapsing <see cref="Unknown"/> into <see cref="No"/> would
/// invent a refusal the vendor never made; collapsing it into <see cref="Yes"/> would invent a
/// guarantee.
///
/// This distinction is the direct lesson of <see cref="AgentExecutionCapability.SupportedEfforts"/>,
/// which was read as a promise because its type gave no way to say "I do not know".
/// </summary>
public enum EffortSupport
{
    Unknown,
    Yes,
    No
}

/// <summary>
/// One model a locally installed agent reports it can run.
///
/// Everything except <see cref="Id"/> is optional, because this record is assembled from three
/// unrelated vendor protocols that each answer a different subset. A field is null when the vendor
/// did not say — never because Wrighty decided a default on its behalf.
/// </summary>
/// <param name="Id">
/// What the vendor's <c>--model</c> argument expects. This is the only field that must be present:
/// without it the entry cannot be acted on.
/// </param>
/// <param name="DisplayName">The vendor's own label, for presentation only. Never matched against.</param>
/// <param name="ResolvedId">
/// The concrete model behind a rolling alias, where the vendor discloses it — claude reports
/// <c>opus</c> resolving to <c>claude-opus-5</c>. Recording both is what lets an operator see that
/// two profiles they believe differ actually resolve to the same model.
/// </param>
/// <param name="Effort">Whether this model accepts a reasoning-effort setting at all.</param>
/// <param name="SupportedEfforts">
/// The levels this model accepts, when the vendor enumerates them. Empty is not "none": consult
/// <see cref="Effort"/> first, because an empty list accompanies both <see cref="EffortSupport.No"/>
/// and <see cref="EffortSupport.Unknown"/>.
/// </param>
/// <param name="DefaultEffort">
/// The level the vendor would use if Wrighty passed none. Worth surfacing because adopting any
/// profile silently overrides it, and an operator cannot weigh that without knowing it.
/// </param>
/// <param name="RelativeCost">
/// The vendor's own cost multiplier as it writes it — copilot publishes <c>0.33x</c>, <c>6x</c>,
/// <c>9x</c>. Kept as the vendor's string rather than parsed into a number: it is a label to show an
/// operator, not a quantity to compute with, and Wrighty must not begin ranking models by it.
/// </param>
public sealed record AgentModel(
    string Id,
    string? DisplayName = null,
    string? ResolvedId = null,
    EffortSupport Effort = EffortSupport.Unknown,
    IReadOnlyList<string>? SupportedEfforts = null,
    string? DefaultEffort = null,
    string? RelativeCost = null)
{
    public IReadOnlyList<string> Efforts => SupportedEfforts ?? [];

    /// <summary>
    /// Whether this model is known to refuse <paramref name="effort"/>. False when unknown, so a
    /// caller that cannot learn the answer refuses nothing — the permissive direction, matching the
    /// existing capability gate.
    /// </summary>
    public bool Rejects(string effort) =>
        Effort == EffortSupport.No ||
        (Effort == EffortSupport.Yes && Efforts.Count > 0 &&
         !Efforts.Contains(effort, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Why a discovery attempt produced nothing.</summary>
public enum ModelDiscoveryFailure
{
    /// <summary>Nothing went wrong; the vendor answered.</summary>
    None,

    /// <summary>The agent's executable is not on this machine. Expected, not an error.</summary>
    NotInstalled,

    /// <summary>The vendor answered, but demanded sign-in first.</summary>
    NotAuthenticated,

    /// <summary>The probe exceeded its budget and its process was killed.</summary>
    TimedOut,

    /// <summary>
    /// The vendor answered in a shape this adapter does not recognize. Expected eventually rather
    /// than exceptional: <c>codex app-server</c> is marked experimental by codex itself, and
    /// claude's control protocol is not a published contract.
    /// </summary>
    Unrecognized,

    /// <summary>The process could not be started, or died without a usable answer.</summary>
    Unavailable
}

/// <summary>
/// What one agent reports it can run on this machine, or why that could not be learned.
///
/// A failed discovery is an ordinary outcome carrying a reason, never an exception. Callers use it
/// to enrich a choice the operator can already make by hand; none of them may be blocked by it.
/// </summary>
/// <param name="Agent">Normalized agent name.</param>
/// <param name="Models">What the vendor listed. Empty whenever <paramref name="Failure"/> is set.</param>
/// <param name="Failure">Why the list is empty, or <see cref="ModelDiscoveryFailure.None"/>.</param>
/// <param name="CurrentModelId">
/// The model this agent would use with no explicit selection. Separate from
/// <see cref="AgentModel.DefaultEffort"/>: this is which model, that is how hard it thinks.
/// </param>
/// <param name="DiscoveredAt">
/// When the vendor was asked. Present so a cached result can be aged out, and so a stale answer can
/// be shown as stale rather than as current — the same reason a recorded selection stamps its CLI
/// version.
/// </param>
public sealed record AgentModelCatalog(
    string Agent,
    IReadOnlyList<AgentModel> Models,
    ModelDiscoveryFailure Failure = ModelDiscoveryFailure.None,
    string? CurrentModelId = null,
    DateTimeOffset? DiscoveredAt = null)
{
    public bool Succeeded => Failure == ModelDiscoveryFailure.None;

    public static AgentModelCatalog Unavailable(
        string agent, ModelDiscoveryFailure failure, DateTimeOffset? at = null) =>
        new(agent, [], failure, DiscoveredAt: at);

    /// <summary>
    /// Finds a model by the identifier a profile mapping would carry. Case-insensitive, and matches
    /// the resolved identifier too, so a mapping pinned to <c>claude-opus-5</c> still finds the
    /// entry the vendor listed under the alias <c>opus</c>.
    /// </summary>
    public AgentModel? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Models.FirstOrDefault(model =>
                string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.ResolvedId, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Asks one locally installed agent what it can run, without starting an inference turn.
///
/// Implemented per vendor rather than behind a shared client, because the three protocols have
/// nothing in common — a control-request over the stream-json channel, an app-server JSONL exchange,
/// and an ACP JSON-RPC session. The shared abstraction is this result, not the transport.
/// </summary>
public interface IAgentModelDiscovery
{
    string Agent { get; }

    /// <summary>
    /// Never throws for a vendor-side problem: an unreachable, unauthenticated, hung, or
    /// unrecognized agent returns a catalog carrying the reason. Only cancellation propagates.
    /// </summary>
    Task<AgentModelCatalog> DiscoverAsync(CancellationToken cancellationToken);
}
