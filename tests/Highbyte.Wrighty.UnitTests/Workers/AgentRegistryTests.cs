using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class AgentRegistryTests
{
    [Fact]
    public void Built_ins_have_one_canonical_identity_and_complete_declared_services()
    {
        var registry = BuiltInAgentRegistry.Create(new MissingExecutables());

        Assert.Equal(["claude", "codex", "copilot"], registry.Ids);
        Assert.Equal(registry.Ids, registry.ExecutionAdapters.Select(value => value.Agent));
        Assert.All(registry.Integrations, integration =>
        {
            Assert.True(integration.Descriptor.Capabilities.HasFlag(
                AgentCapabilities.WorkerExecution));
            Assert.NotNull(integration.ExecutionAdapter);
            Assert.NotNull(integration.ModelDiscovery);
            Assert.NotNull(integration.SessionExporter);
            Assert.NotNull(integration.ContextDetector);
            Assert.NotNull(integration.Descriptor.SkillTarget);
            Assert.NotNull(integration.Descriptor.Projection);
            Assert.Equal(
                integration.Descriptor.Id,
                integration.ExecutionAdapter!.Agent,
                ignoreCase: true);
            var handle = integration.ExecutionAdapter.CreateSessionHandle(
                new WorkItemId("local:registry"),
                "claim-generation");
            var interactive = integration.ExecutionAdapter.BuildInteractiveInvocation(
                handle,
                new Workspace(Path.GetTempPath()));
            Assert.Equal(integration.Descriptor.ExecutableName, interactive.Executable);
            var desktop = integration.ExecutionAdapter.BuildDesktopLaunch(handle);
            Assert.Equal(
                integration.Descriptor.LocalLaunch!.DesktopScheme,
                desktop.Uri!.Scheme);
            Assert.Equal(
                integration.Descriptor.LocalLaunch.DesktopApplication,
                desktop.RequiredApplication);
        });
    }

    [Fact]
    public void Lookup_is_case_insensitive_but_descriptors_remain_canonical()
    {
        var registry = BuiltInAgentRegistry.Create(new MissingExecutables());

        Assert.Same(BuiltInAgentRegistry.Claude, registry.Find(" CLAUDE ")!.Descriptor);
        Assert.Null(registry.Find("other"));
        Assert.False(registry.IsSupported("auto"));
    }

    [Fact]
    public void Reserved_and_duplicate_ids_fail_at_composition()
    {
        var reserved = BuiltInAgentRegistry.Claude with { Id = "other" };
        Assert.Throws<ArgumentException>(() => new AgentRegistry(
            [new AgentIntegration(reserved, new ClaudeAgentAdapter())]));

        Assert.Throws<ArgumentException>(() => new AgentRegistry(
        [
            Integration(BuiltInAgentRegistry.Claude, new ClaudeAgentAdapter()),
            Integration(BuiltInAgentRegistry.Claude, new ClaudeAgentAdapter())
        ]));
    }

    [Fact]
    public void Declared_capabilities_and_service_ids_must_agree()
    {
        Assert.Throws<ArgumentException>(() => new AgentRegistry(
        [
            new AgentIntegration(
                BuiltInAgentRegistry.Claude,
                new ClaudeAgentAdapter(),
                ModelDiscovery: null,
                SessionExporter: new ClaudeSessionExporter())
        ]));

        Assert.Throws<ArgumentException>(() => new AgentRegistry(
        [
            new AgentIntegration(
                BuiltInAgentRegistry.Claude,
                new CodexAgentAdapter(),
                new ClaudeModelDiscovery(new MissingExecutables()),
                new ClaudeSessionExporter())
        ]));

        Assert.Throws<ArgumentException>(() => new AgentRegistry(
        [
            new AgentIntegration(
                BuiltInAgentRegistry.Claude,
                new ClaudeAgentAdapter(),
                new ClaudeModelDiscovery(new MissingExecutables()),
                new ClaudeSessionExporter(),
                new EnvironmentAgentContextDetector("codex", ["CODEX_THREAD_ID"]))
        ]));
    }

    [Fact]
    public void Shared_skill_targets_must_have_identical_destination_metadata()
    {
        var first = BuiltInAgentRegistry.Codex with
        {
            Capabilities = AgentCapabilities.SkillInstallation,
            Projection = null,
            LocalLaunch = null
        };
        var second = BuiltInAgentRegistry.Copilot with
        {
            Capabilities = AgentCapabilities.SkillInstallation,
            SkillTarget = first.SkillTarget! with { RelativeDirectory = ".other/skills/wrighty" },
            Projection = null,
            LocalLaunch = null
        };

        Assert.Throws<ArgumentException>(() => new AgentRegistry(
        [
            new AgentIntegration(first),
            new AgentIntegration(second)
        ]));
    }

    [Fact]
    public void A_fourth_execution_integration_flows_through_generic_runtime_and_capability_paths()
    {
        var descriptor = new AgentDescriptor(
            "future-agent",
            "Future Agent",
            "Example",
            "future-agent",
            AgentCapabilities.WorkerExecution |
            AgentCapabilities.Resume |
            AgentCapabilities.InteractiveCli |
            AgentCapabilities.DesktopLaunch,
            LocalLaunch: new AgentLocalLaunch(
                "Future Desktop",
                "future",
                AgentDesktopOperatingSystems.MacOS));
        var registry = new AgentRegistry(
            [new AgentIntegration(descriptor, new FutureAgentAdapter())]);
        var runtimes = new AgentRuntimeCatalog(
            registry,
            new InstalledExecutable("future-agent"));

        Assert.True(registry.IsSupported("FUTURE-AGENT"));
        Assert.Equal("future-agent", Assert.Single(registry.ExecutionAdapters).Agent);
        Assert.True(runtimes.Snapshot().IsInstalled("future-agent"));
        Assert.Equal(
            "future-agent",
            AgentExecutionCapabilities.ForAgent("future-agent", registry)!.Agent);
        var adapter = registry.GetRequired("future-agent").ExecutionAdapter!;
        var handle = adapter.CreateSessionHandle(
            new WorkItemId("local:future"),
            "claim-generation");
        var workspace = new Workspace(Path.GetTempPath());
        var item = new WorkItemDetail(
            new WorkItemId("local:future"),
            "Future item",
            "Future body",
            null,
            "Todo",
            null,
            false);
        Assert.Equal(
            "future-agent",
            adapter.BuildStart(
                item,
                handle,
                workspace,
                AgentPermissionProfile.Workspace).Executable);
        Assert.Equal("future-agent", adapter.BuildCheck(handle, workspace).Executable);
        Assert.Equal(
            "future-agent",
            adapter.BuildResume(
                handle,
                workspace,
                WorkerPrompt.ForResume(item.Id),
                AgentPermissionProfile.Workspace).Executable);
        Assert.Null(registry.GetRequired("future-agent").ModelDiscovery);
        Assert.Null(registry.GetRequired("future-agent").SessionExporter);
        Assert.Null(registry.GetRequired("future-agent").ContextDetector);

        string? application = null;
        string? scheme = null;
        var launcher = new LocalAgentSessionLauncher(
            new InstalledExecutable("future-agent"),
            new LocalAgentLaunchPlatform(
                LocalAgentOperatingSystem.MacOS,
                (candidateApplication, candidateScheme) =>
                {
                    application = candidateApplication;
                    scheme = candidateScheme;
                    return true;
                },
                (_, _, _, _, _, _) => Task.FromResult(
                    new SessionLaunchResult(SessionLaunchStatus.Launched)),
                (_, _) => Task.FromResult(
                    new SessionLaunchResult(SessionLaunchStatus.Launched))),
            registry);

        var localCapabilities = launcher.GetCapabilities("future-agent");

        Assert.True(localCapabilities.CanLaunchCli);
        Assert.True(localCapabilities.CanLaunchDesktop);
        Assert.Equal("Future Desktop", application);
        Assert.Equal("future", scheme);
    }

    [Fact]
    public void A_fourth_agent_flows_through_host_drain_and_interruption_projection()
    {
        using var draining = new WorkerRunControl();
        draining.RequestDrain();

        var active = WorkerInstanceEventProjection.Project(
            new WorkerEvent("started", "local:future", "future-agent"),
            draining);

        Assert.NotNull(active);
        Assert.Equal("local:future", active.ItemId);
        Assert.Equal("future-agent", active.Agent);
        Assert.Equal(WorkerInstanceState.Draining, active.State);

        using var interrupted = new WorkerRunControl();
        interrupted.RequestInterrupt(WorkerInterruptionReason.OperatorStopNow);
        var terminal = WorkerInstanceEventProjection.Project(
            new WorkerEvent("interrupted", "local:future", "future-agent"),
            interrupted);

        Assert.NotNull(terminal);
        Assert.Null(terminal.ItemId);
        Assert.Null(terminal.Agent);
        Assert.Equal(WorkerInstanceState.Finalizing, terminal.State);
    }

    [Fact]
    public void Production_concrete_adapter_construction_is_owned_by_the_registry()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src");
        var registryPath = Path.GetFullPath(Path.Combine(
            sourceRoot,
            "Highbyte.Wrighty.Core",
            "Workers",
            "AgentRegistry.cs"));
        var forbidden = new[]
        {
            "new ClaudeAgentAdapter(",
            "new CodexAgentAdapter(",
            "new CopilotAgentAdapter("
        };

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFullPath(path), registryPath, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: index + 1, Text: line))
                .Where(value => forbidden.Any(value.Text.Contains)))
            .Select(value => $"{Path.GetRelativePath(RepositoryRoot(), value.Path)}:{value.Line}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Production_does_not_reintroduce_a_fixed_three_agent_collection()
    {
        var sourceRoot = Path.Combine(RepositoryRoot(), "src");
        var forbidden = new[]
        {
            "new[] { \"claude\", \"codex\", \"copilot\" }",
            "[\"claude\", \"codex\", \"copilot\"]"
        };

        var offenders = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: index + 1, Text: line))
                .Where(value => forbidden.Any(value.Text.Contains)))
            .Select(value => $"{Path.GetRelativePath(RepositoryRoot(), value.Path)}:{value.Line}")
            .ToArray();

        Assert.Empty(offenders);
    }

    private static AgentIntegration Integration(
        AgentDescriptor descriptor,
        IAgentAdapter adapter) => new(
            descriptor,
            adapter,
            new ClaudeModelDiscovery(new MissingExecutables()),
            new ClaudeSessionExporter());

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wrighty.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Wrighty repository root.");
    }

    private sealed class MissingExecutables : IExecutableResolver
    {
        public string Resolve(string executableName) =>
            throw new FileNotFoundException("missing", executableName);

        public bool TryResolve(string executableName, out string? executablePath)
        {
            executablePath = null;
            return false;
        }
    }

    private sealed class InstalledExecutable(string installed) : IExecutableResolver
    {
        public string Resolve(string executableName) =>
            TryResolve(executableName, out var path)
                ? path!
                : throw new FileNotFoundException("missing", executableName);

        public bool TryResolve(string executableName, out string? executablePath)
        {
            executablePath = string.Equals(
                executableName, installed, StringComparison.OrdinalIgnoreCase)
                ? $"/tools/{installed}"
                : null;
            return executablePath is not null;
        }
    }

}
