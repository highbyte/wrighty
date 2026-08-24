using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Resolves an agent name to its discovery adapter, mirroring
/// <see cref="AgentExecutionCapabilities.ForAgent"/> so a caller with a name and no adapter in hand
/// can still ask.
///
/// Results are cached for the life of the process. A probe spawns a vendor CLI and waits for it, so
/// re-asking once per command would make an interactive config session noticeably slower for an
/// answer that does not change while an operator is typing. The cache is keyed by agent and holds
/// returned failures too: a machine without codex installed should not pay for that discovery
/// repeatedly. A task that throws or is canceled is evicted so a transient caller cancellation
/// cannot poison discovery for the rest of the process lifetime.
/// </summary>
public sealed class AgentModelDiscoveries(
    Func<string, IAgentModelDiscovery?> resolve,
    IAgentRuntimeCatalog? runtimes = null)
{
    public AgentModelDiscoveries(
        IExecutableResolver executables,
        IAgentRuntimeCatalog? runtimes = null)
        : this(agent => ForAgent(agent, executables), runtimes)
    {
    }

    public AgentModelDiscoveries(
        AgentRegistry registry,
        IAgentRuntimeCatalog? runtimes = null)
        : this(agent => registry.Find(agent)?.ModelDiscovery, runtimes)
    {
    }

    /// <summary>Explicit adapters, for a caller assembling its own set.</summary>
    public AgentModelDiscoveries(IEnumerable<IAgentModelDiscovery> adapters)
        : this(Lookup(adapters))
    {
    }

    private static Func<string, IAgentModelDiscovery?> Lookup(
        IEnumerable<IAgentModelDiscovery> adapters)
    {
        var byAgent = adapters.ToDictionary(
            adapter => adapter.Agent, StringComparer.OrdinalIgnoreCase);
        return agent => byAgent.GetValueOrDefault(agent);
    }

    private readonly Dictionary<string, Task<AgentModelCatalog>> cached =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);

    public static IAgentModelDiscovery? ForAgent(string? agent, IExecutableResolver executables) =>
        agent?.Trim().ToLowerInvariant() switch
        {
            "claude" => new ClaudeModelDiscovery(executables),
            "codex" => new CodexModelDiscovery(executables),
            "copilot" => new CopilotModelDiscovery(executables),
            _ => null
        };

    /// <summary>
    /// Never returns null: an agent Wrighty does not support yields an unavailable catalog, like
    /// any other reason the question could not be answered. Cancellation and unexpected adapter
    /// exceptions propagate, but are not cached.
    /// </summary>
    public async Task<AgentModelCatalog> DiscoverAsync(
        string agent, CancellationToken cancellationToken)
    {
        var normalized = agent.Trim().ToLowerInvariant();
        if (runtimes is not null && !runtimes.Snapshot().IsInstalled(normalized))
        {
            return AgentModelCatalog.Unavailable(
                normalized, ModelDiscoveryFailure.NotInstalled, DateTimeOffset.UtcNow);
        }
        await gate.WaitAsync(cancellationToken);
        Task<AgentModelCatalog> discovery;
        try
        {
            if (!cached.TryGetValue(normalized, out var existing))
            {
                existing = resolve(normalized) is { } adapter
                    ? adapter.DiscoverAsync(cancellationToken)
                    : Task.FromResult(AgentModelCatalog.Unavailable(
                        normalized, ModelDiscoveryFailure.NotInstalled));
                cached[normalized] = existing;
            }

            discovery = existing;
        }
        finally
        {
            gate.Release();
        }

        // Awaited outside the lock: a probe takes seconds, and holding the gate across it would
        // serialize discovery of three vendors that have no reason to wait for each other.
        try
        {
            return await discovery;
        }
        catch
        {
            // The cached task inherits the cancellation token supplied by the first caller. If
            // that caller disconnects, retaining its canceled task would make every later request
            // fail immediately even though a fresh probe could succeed.
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                if (cached.TryGetValue(normalized, out var current) &&
                    ReferenceEquals(current, discovery))
                {
                    cached.Remove(normalized);
                }
            }
            finally
            {
                gate.Release();
            }

            throw;
        }
    }
}
