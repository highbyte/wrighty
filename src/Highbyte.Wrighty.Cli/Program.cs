using System.Collections;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Cli.Skills;
using Highbyte.Wrighty.Cli.Output;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var paths = new CachePaths(
            Environment.GetEnvironmentVariable("WRIGHTY_CACHE_DIR"));
        var userConfigPaths = new Highbyte.Wrighty.Settings.UserConfigPaths(
            Environment.GetEnvironmentVariable("WRIGHTY_CONFIG_DIR"));
        var userSettings = new Highbyte.Wrighty.Settings.UserSettingsStore(userConfigPaths);
        Highbyte.Wrighty.Settings.IHostLabelProvider hostLabel =
            new Highbyte.Wrighty.Settings.HostLabelProvider(userSettings);
        INodeIdCache cache = new JsonNodeIdCache(paths);
        IInstallationIdentityProvider identity = new InstallationIdentityProvider(paths);
        IClock clock = new SystemClock();
        ITrackerConfigStore configStore = new TrackerConfigLoader();
        ITrackerConfigLoader configLoader = configStore;
        IRepositoryConfigurationService repositoryConfiguration =
            new RepositoryConfigurationService(configStore);
        IExecutableResolver executableResolver = new PathExecutableResolver();
        IAgentAdapter[] agentAdapters =
            [new ClaudeAgentAdapter(), new CodexAgentAdapter(),
             new CopilotAgentAdapter(shareDirectory: paths.CopilotSharesRoot)];
        IAgentRuntimeCatalog agentRuntimes =
            new AgentRuntimeCatalog(agentAdapters, executableResolver);
        ILocalAgentSessionLauncher localAgentLauncher =
            new LocalAgentSessionLauncher(executableResolver);
        IGhProcess process = new GhProcess(executableResolver);
        var api = new GhApi(process);
        IProjectClient projects = new GitHubProjectClient(api, cache);
        var git = new GitProcess(executableResolver);
        IRepositoryDiscovery repositoryDiscovery = new GitRepositoryDiscovery(git);
        IGitHubInitializationClient githubInitialization = new GitHubInitializationClient(api);
        var githubResolver = new GitHubWorkItemAddressResolver();
        IClaimService claims = new GitHubClaimService(
            api, identity, clock, githubResolver, new JsonWorkItemRuntimeStore(paths));
        IWorkItemMutationGuard mutationGuard = new ClaimMutationGuard(claims);
        IWorkItemBackend backend = new GitHubWorkItemBackend(
            api,
            projects,
            githubResolver,
            mutationGuard: mutationGuard);
        ITrackerBackend githubBackend = new GitHubTrackerBackend(
            projects,
            claims,
            githubResolver,
            backend);
        ITrackerBackend localBackend = new LocalMarkdownTrackerBackend(identity, clock);
        var backendRegistry = new TrackerBackendRegistry(
            [githubBackend, localBackend]);
        var tracker = new TrackerService(backendRegistry);
        var initialization = new TrackerInitializationService(
            configStore,
            repositoryDiscovery,
            githubInitialization,
            projects,
            backendRegistry);
        IProviderCapacityStore providerCapacity =
            new JsonProviderCapacityStore(paths);
        IWorkerInstanceRegistry workerInstances =
            new JsonWorkerInstanceRegistry(paths);
        IGitHubIssueFormScaffolder issueForms = new GitHubIssueFormScaffolder(
            repositoryDiscovery,
            git);
        IGitHubIssueFormPublisher issueFormPublisher = new GitHubIssueFormPublisher(git);
        // One instance: the login it resolves is fixed for the process, and a lookup per item would
        // be a request per iteration of the worker's polling loop.
        IGitHubViewerIdentity viewerIdentity = new GitHubViewerIdentity(api);
        // Resolved per config, because which provider can assemble an approved context depends on
        // the backend. A backend with no discussion surface still supplies title and body.
        Func<TrackerConfig, IExecutionContextProvider?> executionContextProviders = config =>
            config.Backend switch
            {
                "github" => new GitHubExecutionContextProvider(
                    new GitHubConversationReader(api),
                    new GitHubContextApprovalReader(api),
                    new GitHubWorkItemAddressResolver(),
                    // No explicit approver policy: the provider derives one per read from the
                    // configured github.contextApprovers, and the identity lets Wrighty recognise
                    // its own protocol comments either way.
                    viewerIdentity: viewerIdentity),
                "local-markdown" => new LocalExecutionContextProvider(
                    backendRegistry.Get(config.Backend)),
                _ => null
            };
        // One instance retains the per-report polling interval, ETags, and last good reading. A
        // provider created per item would turn every worker iteration back into an unconditional
        // GitHub read and discard the rate-limit protection.
        ITrustedControlReactionProvider githubControlReactions =
            new GitHubControlReactionProvider(api, githubResolver, viewerIdentity);
        Func<TrackerConfig, ITrustedControlReactionProvider?> controlReactionProviders = config =>
            config.Backend == "github" ? githubControlReactions : null;
        IContextApprovalService contextApproval = new ContextApprovalService(
            tracker,
            executionContextProviders);

        // One instance, registered on the worker: the post-claim stage records what it resolved and
        // the pre-spawn stage compares against it, so both must be the same object.
        var contextLaunchCheck = new ExecutionContextLaunchCheck(
            executionContextProviders,
            config => config.EffectiveWorker.EffectiveContext.ToLimits());

        var worker = new WorkerService(
            tracker,
            new AgentProcessRunner(executableResolver),
            new GitWorkspaceManager(executableResolver),
            agentAdapters,
            executables: executableResolver,
            workspaceExecutionLock: new FileWorkspaceExecutionLock(),
            skillAvailability: new FileWorkerSkillAvailability(executableResolver),
            hostLabelProvider: hostLabel,
            providerCapacityStore: providerCapacity,
            launchPreflightChecks: [contextLaunchCheck],
            runtimeCatalog: agentRuntimes,
            continuations: new TrustedContinuationScan(
                tracker,
                executionContextProviders,
                config => config.EffectiveWorker.EffectiveContext.ToLimits(),
                controlReactionProviders: controlReactionProviders),
            cachePaths: paths,
            userSettings: userSettings,
            agentVersions: new AgentVersionProbe(executableResolver));
        IAgentExecutionContextProvider agentContext = new AgentExecutionContextProvider(
            Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .ToDictionary(
                    entry => (string)entry.Key,
                    entry => entry.Value?.ToString(),
                    StringComparer.Ordinal));
        IWrightyWebServer webServer = new WrightyWebServer(
            configLoader,
            tracker,
            new SystemBrowserLauncher(),
            Environment.CurrentDirectory,
            new WrightyWebServerDependencies(
                new GitWorkspaceInventory(executableResolver),
                providerCapacity,
                worker,
                agentAdapters,
                agentRuntimes,
                localAgentLauncher,
                repositoryConfiguration,
                workerInstances,
                contextApproval,
                new Highbyte.Wrighty.Settings.UserConfigurationService(userSettings)));
        var application = new CliApplication(
            configLoader,
            initialization,
            tracker,
            agentContext,
            SkillManager.CreateDefault(),
            webServer,
            Console.In,
            Console.Out,
            Console.Error,
            Environment.CurrentDirectory,
            worker,
            terminalCapabilities: TerminalCapabilities.Detect(),
            issueFormScaffolder: issueForms,
            issueFormPublisher: issueFormPublisher,
            workspaceInventory: new GitWorkspaceInventory(executableResolver),
            userSettings: userSettings,
            providerCapacityStore: providerCapacity,
            executionContextProviders: executionContextProviders,
            viewerIdentity: viewerIdentity,
            runtimeCatalog: agentRuntimes,
            localAgentLauncher: localAgentLauncher,
            repositoryConfiguration: repositoryConfiguration,
            workerInstanceRegistry: workerInstances,
            contextApprovalService: contextApproval);

        using var shutdown = ShutdownSignals.Register();
        return await application.InvokeAsync(args, shutdown.Token);
    }
}
