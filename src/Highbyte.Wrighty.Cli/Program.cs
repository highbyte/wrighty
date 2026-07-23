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
        IWorkerIdentityProvider identity = new WorkerIdentityProvider(paths);
        IClock clock = new SystemClock();
        ITrackerConfigStore configStore = new TrackerConfigLoader();
        ITrackerConfigLoader configLoader = configStore;
        IExecutableResolver executableResolver = new PathExecutableResolver();
        IGhProcess process = new GhProcess(executableResolver);
        var api = new GhApi(process);
        IProjectClient projects = new GitHubProjectClient(api, cache);
        var git = new GitProcess(executableResolver);
        IRepositoryDiscovery repositoryDiscovery = new GitRepositoryDiscovery(git);
        IGitHubInitializationClient githubInitialization = new GitHubInitializationClient(api);
        var githubResolver = new GitHubWorkItemAddressResolver();
        IClaimService claims = new GitHubClaimService(
            api, identity, clock, githubResolver, new JsonSessionRecordCache(paths));
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
        ITrackerBackendRegistry backendRegistry = new TrackerBackendRegistry(
            [githubBackend, localBackend]);
        var tracker = new TrackerService(backendRegistry);
        var initialization = new TrackerInitializationService(
            configStore,
            repositoryDiscovery,
            githubInitialization,
            projects,
            backendRegistry);
        IGitHubIssueFormScaffolder issueForms = new GitHubIssueFormScaffolder(
            repositoryDiscovery,
            git);
        IGitHubIssueFormPublisher issueFormPublisher = new GitHubIssueFormPublisher(git);
        var worker = new WorkerService(
            tracker,
            new AgentProcessRunner(executableResolver),
            new GitWorkspaceManager(executableResolver),
            [new ClaudeAgentAdapter(), new CodexAgentAdapter(), new CopilotAgentAdapter()],
            executables: executableResolver,
            workspaceExecutionLock: new FileWorkspaceExecutionLock(),
            skillAvailability: new FileWorkerSkillAvailability(executableResolver),
            hostLabelProvider: hostLabel,
            providerAvailabilityStore: new JsonProviderAvailabilityStore(paths));
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
            new GitWorkspaceInventory(executableResolver));
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
            userSettings: userSettings);
        return await application.InvokeAsync(args);
    }
}
