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
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var paths = new CachePaths(
            Environment.GetEnvironmentVariable("WRIGHTY_CACHE_DIR"));
        INodeIdCache cache = new JsonNodeIdCache(paths);
        IWorkerIdentityProvider identity = new WorkerIdentityProvider(paths);
        IClock clock = new SystemClock();
        ITrackerConfigStore configStore = new TrackerConfigLoader();
        ITrackerConfigLoader configLoader = configStore;
        IExecutableResolver executableResolver = new PathExecutableResolver();
        IGhProcess process = new GhProcess(executableResolver);
        var api = new GhApi(process);
        IProjectClient projects = new GitHubProjectClient(api, cache);
        IRepositoryDiscovery repositoryDiscovery = new GitRepositoryDiscovery(
            new GitProcess(executableResolver));
        IGitHubInitializationClient githubInitialization = new GitHubInitializationClient(api);
        var githubResolver = new GitHubWorkItemAddressResolver();
        IClaimService claims = new GitHubClaimService(api, identity, clock, githubResolver);
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
        var worker = new WorkerService(
            tracker,
            new AgentProcessRunner(executableResolver),
            new GitWorkspaceManager(executableResolver),
            [new ClaudeAgentAdapter(), new CodexAgentAdapter(), new CopilotAgentAdapter()],
            executables: executableResolver,
            workspaceExecutionLock: new FileWorkspaceExecutionLock());
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
            Environment.CurrentDirectory);
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
            worker);
        return await application.InvokeAsync(args);
    }
}
