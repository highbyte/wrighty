using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Models;
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
            Assert.NotNull(integration.Descriptor.SkillTarget);
            Assert.NotNull(integration.Descriptor.Projection);
            Assert.Equal(
                integration.Descriptor.Id,
                integration.ExecutionAdapter!.Agent,
                ignoreCase: true);
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
            AgentCapabilities.InteractiveCli);
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

    private sealed class FutureAgentAdapter : IAgentAdapter
    {
        private readonly CodexAgentAdapter inner = new();

        public string Agent => "future-agent";

        public string ExecutableName => "future-agent";

        public bool SupportsPreassignedHandle => inner.SupportsPreassignedHandle;

        public AgentPermissions DescribePermissions(AgentPermissionProfile profile) =>
            inner.DescribePermissions(profile) with { Agent = Agent };

        public AgentExecutionCapability DescribeExecutionCapability() =>
            inner.DescribeExecutionCapability() with { Agent = Agent };

        public AgentInvocation BuildStart(
            WorkItemDetail item,
            SessionHandle handle,
            Workspace workspace,
            AgentPermissionProfile permissions,
            string? promptAddendum = null,
            bool requiresUserConfirmation = false,
            ExecutionSelection? selection = null) =>
            inner.BuildStart(
                item,
                handle,
                workspace,
                permissions,
                promptAddendum,
                requiresUserConfirmation,
                selection) with
            {
                Executable = ExecutableName
            };

        public AgentInvocation BuildStartWithPrompt(
            SessionHandle handle,
            Workspace workspace,
            AgentPermissionProfile permissions,
            string prompt,
            ExecutionSelection? selection = null) =>
            inner.BuildStartWithPrompt(handle, workspace, permissions, prompt, selection) with
            {
                Executable = ExecutableName
            };

        public AgentInvocation BuildResumeWithPrompt(
            SessionHandle handle,
            Workspace workspace,
            AgentPermissionProfile permissions,
            string prompt) =>
            inner.BuildResumeWithPrompt(handle, workspace, permissions, prompt) with
            {
                Executable = ExecutableName
            };

        public AgentInvocation BuildResume(
            SessionHandle handle,
            Workspace workspace,
            string prompt,
            AgentPermissionProfile permissions) =>
            inner.BuildResume(handle, workspace, prompt, permissions) with
            {
                Executable = ExecutableName
            };

        public AgentInvocation BuildCheck(SessionHandle handle, Workspace workspace) =>
            inner.BuildCheck(handle, workspace) with { Executable = ExecutableName };

        public LocalAgentInvocation BuildInteractiveInvocation(
            SessionHandle handle,
            Workspace workspace,
            IReadOnlyDictionary<string, string>? environment = null) =>
            inner.BuildInteractiveInvocation(handle, workspace, environment) with
            {
                Executable = ExecutableName
            };

        public DesktopLaunchAddress BuildDesktopLaunch(SessionHandle handle) =>
            inner.BuildDesktopLaunch(handle) with { Vendor = Agent };

        public string BuildInteractiveCommand(
            SessionHandle handle,
            Workspace workspace,
            IReadOnlyDictionary<string, string>? environment = null) =>
            inner.BuildInteractiveCommand(handle, workspace, environment);

        public string? TryExtractSessionId(string outputLine) =>
            inner.TryExtractSessionId(outputLine);

        public Task<AgentRunResult> InterpretAsync(
            Stream stdout,
            int exitCode,
            CancellationToken cancellationToken) =>
            inner.InterpretAsync(stdout, exitCode, cancellationToken);
    }
}
