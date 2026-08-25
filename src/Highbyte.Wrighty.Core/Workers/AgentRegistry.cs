using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.AgentContext;

namespace Highbyte.Wrighty.Workers;

[Flags]
public enum AgentCapabilities
{
    None = 0,
    WorkerExecution = 1 << 0,
    Resume = 1 << 1,
    ModelDiscovery = 1 << 2,
    SessionExport = 1 << 3,
    SkillInstallation = 1 << 4,
    ContextDetection = 1 << 5,
    InteractiveCli = 1 << 6,
    DesktopLaunch = 1 << 7,
    GitHubProjection = 1 << 8
}

/// <summary>
/// One physical Agent Skill destination. Several agents may share a target without inventing a
/// synthetic supported-agent identity.
/// </summary>
public sealed record AgentSkillTarget(
    string Id,
    string RelativeDirectory,
    bool RequiresInvocationPolicy = false);

/// <summary>The stable GitHub Project presentation for one built-in agent.</summary>
public sealed record AgentProjection(
    string OptionName,
    string ProjectionDescription,
    string PolicyDescription,
    string Color,
    int ProjectionOrder,
    int PolicyOrder);

[Flags]
public enum AgentDesktopOperatingSystems
{
    None = 0,
    MacOS = 1 << 0,
    Windows = 1 << 1,
    Linux = 1 << 2
}

/// <summary>The allowlisted Desktop application and URI scheme for one agent.</summary>
public sealed record AgentLocalLaunch(
    string DesktopApplication,
    string DesktopScheme,
    AgentDesktopOperatingSystems DesktopOperatingSystems,
    DesktopSessionSupport DesktopSessionSupport = DesktopSessionSupport.Supported);

/// <summary>
/// Dependency-free identity and presentation metadata for a built-in agent. Runtime installation
/// and readiness deliberately do not live here: they are host facts supplied by
/// <see cref="IAgentRuntimeCatalog"/>.
/// </summary>
public sealed record AgentDescriptor(
    string Id,
    string DisplayName,
    string VendorName,
    string ExecutableName,
    AgentCapabilities Capabilities,
    AgentSkillTarget? SkillTarget = null,
    AgentProjection? Projection = null,
    AgentLocalLaunch? LocalLaunch = null);

/// <summary>
/// Binds stable identity to the process-scoped services Wrighty can use for that agent.
/// Optional services remain explicit rather than being manufactured by parallel name switches.
/// </summary>
public sealed record AgentIntegration(
    AgentDescriptor Descriptor,
    IAgentAdapter? ExecutionAdapter = null,
    IAgentModelDiscovery? ModelDiscovery = null,
    IAgentSessionExporter? SessionExporter = null,
    IAgentContextDetector? ContextDetector = null);

/// <summary>The authoritative catalogue of built-in agent integrations for one process.</summary>
public sealed class AgentRegistry
{
    private const int MaximumIdLength = 32;
    private static readonly HashSet<string> ReservedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "all",
        "auto",
        "none",
        "other",
        "repository-default"
    };

    private readonly IReadOnlyDictionary<string, AgentIntegration> integrationsById;

    public AgentRegistry(IEnumerable<AgentIntegration> integrations)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        var values = integrations.ToArray();
        if (values.Length == 0)
            throw new ArgumentException("At least one agent integration is required.", nameof(integrations));

        foreach (var integration in values)
            Validate(integration);

        var duplicate = values
            .GroupBy(value => value.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException(
                $"Agent id '{duplicate.Key}' is registered more than once.", nameof(integrations));

        RequireDistinctProjectionOrder(
            values,
            projection => projection.ProjectionOrder,
            "projection");
        RequireDistinctProjectionOrder(
            values,
            projection => projection.PolicyOrder,
            "policy");
        RequireConsistentSkillTargets(values);

        Integrations = Array.AsReadOnly(values
            .OrderBy(value => value.Descriptor.Id, StringComparer.Ordinal)
            .ToArray());
        Descriptors = Array.AsReadOnly(Integrations.Select(value => value.Descriptor).ToArray());
        WorkerDescriptors = Array.AsReadOnly(Descriptors
            .Where(value => value.Capabilities.HasFlag(AgentCapabilities.WorkerExecution))
            .ToArray());
        ExecutionAdapters = Array.AsReadOnly(Integrations
            .Where(value => value.ExecutionAdapter is not null)
            .Select(value => value.ExecutionAdapter!)
            .ToArray());
        Ids = Array.AsReadOnly(Descriptors.Select(value => value.Id).ToArray());
        WorkerIds = Array.AsReadOnly(WorkerDescriptors.Select(value => value.Id).ToArray());
        integrationsById = Integrations.ToDictionary(
            value => value.Descriptor.Id,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AgentIntegration> Integrations { get; }

    public IReadOnlyList<AgentDescriptor> Descriptors { get; }

    public IReadOnlyList<AgentDescriptor> WorkerDescriptors { get; }

    public IReadOnlyList<IAgentAdapter> ExecutionAdapters { get; }

    public IReadOnlyList<string> Ids { get; }

    public IReadOnlyList<string> WorkerIds { get; }

    public string DescribeIds(string conjunction = "or") =>
        FormatIds(Ids, conjunction);

    public string DescribeWorkerIds(string conjunction = "or") =>
        FormatIds(WorkerIds, conjunction);

    public bool IsWorkerAgent(string? id) =>
        Find(id)?.Descriptor.Capabilities.HasFlag(AgentCapabilities.WorkerExecution) == true;

    public bool IsSupported(string? id) => Find(id) is not null;

    public AgentIntegration? Find(string? id)
    {
        var normalized = id?.Trim();
        return string.IsNullOrEmpty(normalized)
            ? null
            : integrationsById.GetValueOrDefault(normalized);
    }

    public AgentIntegration GetRequired(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"Agent '{id}' is not registered.");

    internal static string FormatIds(IReadOnlyList<string> ids, string conjunction = "or") =>
        ids.Count switch
        {
            0 => string.Empty,
            1 => ids[0],
            2 => $"{ids[0]} {conjunction} {ids[1]}",
            _ => $"{string.Join(", ", ids.Take(ids.Count - 1))}, {conjunction} {ids[^1]}"
        };

    private static void Validate(AgentIntegration integration)
    {
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(integration.Descriptor);
        var descriptor = integration.Descriptor;

        if (!ValidId(descriptor.Id))
            throw new ArgumentException(
                $"Agent id '{descriptor.Id}' must be a lowercase token of at most " +
                $"{MaximumIdLength} characters.", nameof(integration));
        if (ReservedIds.Contains(descriptor.Id))
            throw new ArgumentException(
                $"Agent id '{descriptor.Id}' is reserved for selection or attribution.",
                nameof(integration));
        RequireText(descriptor.DisplayName, nameof(descriptor.DisplayName));
        RequireText(descriptor.VendorName, nameof(descriptor.VendorName));
        RequireText(descriptor.ExecutableName, nameof(descriptor.ExecutableName));
        if (descriptor.ExecutableName.Contains('/') || descriptor.ExecutableName.Contains('\\'))
            throw new ArgumentException(
                $"Agent '{descriptor.Id}' must declare an executable name, not a path.",
                nameof(integration));

        ValidateService(
            descriptor,
            AgentCapabilities.WorkerExecution,
            integration.ExecutionAdapter,
            "execution adapter");
        ValidateService(
            descriptor,
            AgentCapabilities.ModelDiscovery,
            integration.ModelDiscovery,
            "model discovery");
        ValidateService(
            descriptor,
            AgentCapabilities.SessionExport,
            integration.SessionExporter,
            "session exporter");
        ValidateService(
            descriptor,
            AgentCapabilities.ContextDetection,
            integration.ContextDetector,
            "context detector");
        ValidateMetadata(
            descriptor,
            AgentCapabilities.SkillInstallation,
            descriptor.SkillTarget,
            "skill target");
        ValidateMetadata(
            descriptor,
            AgentCapabilities.GitHubProjection,
            descriptor.Projection,
            "GitHub projection");
        ValidateMetadata(
            descriptor,
            AgentCapabilities.DesktopLaunch,
            descriptor.LocalLaunch,
            "local launch metadata");

        if ((descriptor.Capabilities & (
                AgentCapabilities.Resume |
                AgentCapabilities.InteractiveCli |
                AgentCapabilities.DesktopLaunch)) != 0 &&
            integration.ExecutionAdapter is null)
        {
            throw new ArgumentException(
                $"Agent '{descriptor.Id}' declares launch/session behavior without an execution adapter.",
                nameof(integration));
        }
        if (descriptor.SkillTarget is { } skillTarget)
        {
            RequireText(skillTarget.Id, nameof(skillTarget.Id));
            RequireText(skillTarget.RelativeDirectory, nameof(skillTarget.RelativeDirectory));
            if (Path.IsPathRooted(skillTarget.RelativeDirectory) ||
                skillTarget.RelativeDirectory.Split(['/', '\\']).Contains("..", StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Agent '{descriptor.Id}' must declare a safe relative skill target.",
                    nameof(integration));
            }
        }
        if (descriptor.Projection is { } projection)
        {
            RequireText(projection.OptionName, nameof(projection.OptionName));
            RequireText(projection.ProjectionDescription, nameof(projection.ProjectionDescription));
            RequireText(projection.PolicyDescription, nameof(projection.PolicyDescription));
            RequireText(projection.Color, nameof(projection.Color));
            if (projection.ProjectionOrder < 0 || projection.PolicyOrder < 0)
                throw new ArgumentException(
                    $"Agent '{descriptor.Id}' projection order cannot be negative.",
                    nameof(integration));
        }
        if (descriptor.LocalLaunch is { } localLaunch)
        {
            RequireText(localLaunch.DesktopApplication, nameof(localLaunch.DesktopApplication));
            RequireText(localLaunch.DesktopScheme, nameof(localLaunch.DesktopScheme));
            if (!Uri.CheckSchemeName(localLaunch.DesktopScheme) ||
                localLaunch.DesktopScheme != localLaunch.DesktopScheme.ToLowerInvariant())
            {
                throw new ArgumentException(
                    $"Agent '{descriptor.Id}' must declare a lowercase URI scheme.",
                    nameof(integration));
            }
            const AgentDesktopOperatingSystems allPlatforms =
                AgentDesktopOperatingSystems.MacOS |
                AgentDesktopOperatingSystems.Windows |
                AgentDesktopOperatingSystems.Linux;
            if (localLaunch.DesktopOperatingSystems == AgentDesktopOperatingSystems.None ||
                (localLaunch.DesktopOperatingSystems & ~allPlatforms) != 0)
            {
                throw new ArgumentException(
                    $"Agent '{descriptor.Id}' must declare at least one supported Desktop platform.",
                    nameof(integration));
            }
            if (localLaunch.DesktopSessionSupport == DesktopSessionSupport.Unavailable)
            {
                throw new ArgumentException(
                    $"Agent '{descriptor.Id}' cannot declare unavailable Desktop metadata.",
                    nameof(integration));
            }
        }

        if (integration.ExecutionAdapter is { } adapter)
        {
            RequireSameId(descriptor.Id, adapter.Agent, "execution adapter");
            if (!string.Equals(
                    descriptor.ExecutableName,
                    adapter.ExecutableName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Agent '{descriptor.Id}' declares executable '{descriptor.ExecutableName}' " +
                    $"but its execution adapter reports '{adapter.ExecutableName}'.",
                    nameof(integration));
            }
        }
        if (integration.ModelDiscovery is { } discovery)
            RequireSameId(descriptor.Id, discovery.Agent, "model discovery");
        if (integration.SessionExporter is { } exporter)
            RequireSameId(descriptor.Id, exporter.Agent, "session exporter");
        if (integration.ContextDetector is { } detector)
            RequireSameId(descriptor.Id, detector.Agent, "context detector");
    }

    private static void ValidateService<T>(
        AgentDescriptor descriptor,
        AgentCapabilities capability,
        T? service,
        string name) where T : class =>
        ValidateMetadata(descriptor, capability, service, name);

    private static void ValidateMetadata<T>(
        AgentDescriptor descriptor,
        AgentCapabilities capability,
        T? value,
        string name) where T : class
    {
        var declared = descriptor.Capabilities.HasFlag(capability);
        if (declared == (value is not null))
            return;
        throw new ArgumentException(
            declared
                ? $"Agent '{descriptor.Id}' declares {capability} without a {name}."
                : $"Agent '{descriptor.Id}' supplies a {name} without declaring {capability}.",
            nameof(descriptor));
    }

    private static void RequireSameId(string expected, string actual, string service)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Agent '{expected}' has a {service} reporting id '{actual}'.");
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty and trimmed.", name);
    }

    private static bool ValidId(string value) =>
        value.Length is > 0 and <= MaximumIdLength &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static void RequireDistinctProjectionOrder(
        IReadOnlyList<AgentIntegration> integrations,
        Func<AgentProjection, int> select,
        string name)
    {
        var duplicate = integrations
            .Select(value => value.Descriptor.Projection)
            .Where(value => value is not null)
            .Select(value => select(value!))
            .GroupBy(value => value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException(
                $"Agent {name} order '{duplicate.Key}' is registered more than once.",
                nameof(integrations));
    }

    private static void RequireConsistentSkillTargets(
        IReadOnlyList<AgentIntegration> integrations)
    {
        var conflictingId = integrations
            .Select(integration => integration.Descriptor.SkillTarget)
            .Where(target => target is not null)
            .Select(target => target!)
            .GroupBy(target => target.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group
                .Distinct()
                .Count() > 1);
        if (conflictingId is not null)
        {
            throw new ArgumentException(
                $"Skill target '{conflictingId.Key}' has conflicting destination metadata.",
                nameof(integrations));
        }

        var conflictingPath = integrations
            .Select(integration => integration.Descriptor.SkillTarget)
            .Where(target => target is not null)
            .Select(target => target!)
            .GroupBy(target => target.RelativeDirectory, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group
                .Select(target => target.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1);
        if (conflictingPath is not null)
        {
            throw new ArgumentException(
                $"Skill destination '{conflictingPath.Key}' is assigned to multiple target ids.",
                nameof(integrations));
        }
    }
}

/// <summary>Builds Wrighty's reviewed, compile-time set of built-in integrations.</summary>
public static class BuiltInAgentRegistry
{
    private const AgentCapabilities Capabilities =
        AgentCapabilities.WorkerExecution |
        AgentCapabilities.Resume |
        AgentCapabilities.ModelDiscovery |
        AgentCapabilities.SessionExport |
        AgentCapabilities.SkillInstallation |
        AgentCapabilities.ContextDetection |
        AgentCapabilities.InteractiveCli |
        AgentCapabilities.DesktopLaunch |
        AgentCapabilities.GitHubProjection;

    public static AgentRegistry Create(
        IExecutableResolver executables,
        string? claudeTranscriptRoot = null,
        string? codexSessionsRoot = null,
        string? copilotSharesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(executables);
        return new AgentRegistry(
        [
            new AgentIntegration(
                Claude,
                new ClaudeAgentAdapter(),
                new ClaudeModelDiscovery(executables),
                new ClaudeSessionExporter(claudeTranscriptRoot),
                new EnvironmentAgentContextDetector(
                    Claude.Id,
                    ["CLAUDE_CODE_REMOTE_SESSION_ID", "CLAUDE_CODE_SESSION_ID"],
                    [
                        new AgentPresenceSignal("CLAUDECODE", ["1"]),
                        new AgentPresenceSignal(
                            "CLAUDE_CODE_REMOTE",
                            ["1", "true", "yes", "on"])
                    ])),
            new AgentIntegration(
                Codex,
                new CodexAgentAdapter(),
                new CodexModelDiscovery(executables),
                new CodexSessionExporter(codexSessionsRoot),
                new EnvironmentAgentContextDetector(Codex.Id, ["CODEX_THREAD_ID"])),
            new AgentIntegration(
                Copilot,
                new CopilotAgentAdapter(shareDirectory: copilotSharesRoot),
                new CopilotModelDiscovery(executables),
                new CopilotSessionExporter(copilotSharesRoot),
                new EnvironmentAgentContextDetector(
                    Copilot.Id,
                    ["COPILOT_AGENT_SESSION_ID"]))
        ]);
    }

    public static AgentDescriptor Claude { get; } = new(
        "claude",
        "Claude",
        "Anthropic",
        "claude",
        Capabilities,
        new AgentSkillTarget("claude", ".claude/skills/wrighty", RequiresInvocationPolicy: true),
        new AgentProjection(
            "Claude",
            "Anthropic Claude Code agent",
            "Use Anthropic Claude Code",
            "ORANGE",
            ProjectionOrder: 1,
            PolicyOrder: 0),
        new AgentLocalLaunch(
            "Claude",
            "claude",
            AgentDesktopOperatingSystems.MacOS | AgentDesktopOperatingSystems.Windows,
            DesktopSessionSupport.Experimental));

    public static AgentDescriptor Codex { get; } = new(
        "codex",
        "Codex",
        "OpenAI",
        "codex",
        Capabilities,
        new AgentSkillTarget("codex-copilot", ".agents/skills/wrighty"),
        new AgentProjection(
            "Codex",
            "OpenAI Codex agent",
            "Use OpenAI Codex",
            "GREEN",
            ProjectionOrder: 0,
            PolicyOrder: 1),
        new AgentLocalLaunch(
            "ChatGPT",
            "codex",
            AgentDesktopOperatingSystems.MacOS | AgentDesktopOperatingSystems.Windows));

    public static AgentDescriptor Copilot { get; } = new(
        "copilot",
        "Copilot",
        "GitHub",
        "copilot",
        Capabilities,
        new AgentSkillTarget("codex-copilot", ".agents/skills/wrighty"),
        new AgentProjection(
            "Copilot",
            "GitHub Copilot agent",
            "Use GitHub Copilot",
            "BLUE",
            ProjectionOrder: 2,
            PolicyOrder: 2),
        new AgentLocalLaunch(
            "GitHub Copilot",
            "ghapp",
            AgentDesktopOperatingSystems.MacOS |
            AgentDesktopOperatingSystems.Windows |
            AgentDesktopOperatingSystems.Linux));

    public static IReadOnlyList<AgentDescriptor> Descriptors { get; } =
        Array.AsReadOnly<AgentDescriptor>([Claude, Codex, Copilot]);

    public static IReadOnlyList<string> Ids { get; } = Array.AsReadOnly(Descriptors
        .Select(value => value.Id)
        .Order(StringComparer.Ordinal)
        .ToArray());

    public static bool IsSupported(string? id) =>
        id is not null && Ids.Contains(id.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string DescribeIds(string conjunction = "or") =>
        AgentRegistry.FormatIds(Ids, conjunction);
}
