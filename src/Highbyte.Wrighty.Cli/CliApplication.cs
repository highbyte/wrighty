using System.CommandLine;
using Highbyte.Wrighty.Cli.Output;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Importing;
using Highbyte.Wrighty.Cli.Skills;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;
using System.Text.Json;

namespace Highbyte.Wrighty.Cli;

public sealed partial class CliApplication(
    ITrackerConfigLoader configLoader,
    ITrackerInitializationService initialization,
    TrackerService tracker,
    IAgentExecutionContextProvider agentContextProvider,
    ISkillManager skillManager,
    IWrightyWebServer webServer,
    TextReader input,
    TextWriter output,
    TextWriter error,
    string workingDirectory,
    WorkerService? workerService = null,
    Func<bool>? inputIsRedirected = null,
    IWorkItemTextEditor? workItemEditor = null,
    Func<DateTimeOffset>? clock = null,
    TerminalCapabilities? terminalCapabilities = null,
    IGitHubIssueFormScaffolder? issueFormScaffolder = null,
    IGitHubIssueFormPublisher? issueFormPublisher = null,
    IWorkspaceInventory? workspaceInventory = null,
    Settings.UserSettingsStore? userSettings = null,
    IProviderCapacityStore? providerCapacityStore = null,
    Func<TrackerConfig, IExecutionContextProvider?>? executionContextProviders = null,
    GitHub.IGitHubViewerIdentity? viewerIdentity = null,
    IAgentRuntimeCatalog? runtimeCatalog = null,
    ILocalAgentSessionLauncher? localAgentLauncher = null,
    IRepositoryConfigurationService? repositoryConfiguration = null,
    IWorkerInstanceRegistry? workerInstanceRegistry = null,
    IContextApprovalService? contextApprovalService = null)
{
    private readonly OutputWriter writer = new(output, error, clock);
    private readonly Func<bool> isInputRedirected = inputIsRedirected ?? (() => Console.IsInputRedirected);
    private readonly IWorkItemTextEditor editor = workItemEditor ?? new SystemWorkItemTextEditor();
    private readonly TerminalCapabilities terminals = terminalCapabilities ?? TerminalCapabilities.Plain;
    private readonly IGitHubIssueFormScaffolder? forms = issueFormScaffolder;
    private readonly IGitHubIssueFormPublisher? formPublisher = issueFormPublisher;
    private readonly IProviderCapacityStore providerCapacity =
        providerCapacityStore ?? NoOpProviderCapacityStore.Instance;
    private readonly IAgentRuntimeCatalog? runtimes = runtimeCatalog;
    private readonly ILocalAgentSessionLauncher localLauncher =
        localAgentLauncher ??
        new LocalAgentSessionLauncher(new Highbyte.Wrighty.Processes.PathExecutableResolver());
    private readonly IRepositoryConfigurationService? repositoryConfigurations =
        repositoryConfiguration;
    private readonly IWorkerInstanceRegistry workerInstances =
        workerInstanceRegistry ?? NoOpWorkerInstanceRegistry.Instance;
    private readonly IContextApprovalService? contextApproval =
        contextApprovalService ??
        (executionContextProviders is null
            ? null
            : new ContextApprovalService(tracker, executionContextProviders));

    public Task<int> InvokeAsync(string[] args, CancellationToken cancellationToken = default)
    {
        // The token is the process's shutdown signal. Everything cancellation-driven below —
        // worker loop unwinding, claim release, worker-instance record removal — hangs off the
        // tokens System.CommandLine derives from this one, so a caller that never cancels it
        // (tests, or a host with its own lifetime) simply gets no shutdown path, which is also
        // the observed failure when nothing translated SIGINT into it.
        return BuildRootCommand().Parse(args).InvokeAsync(cancellationToken: cancellationToken);
    }

    private RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Wrighty: local-first work coordination with pluggable backends");
        root.Subcommands.Add(BuildInitCommand());
        root.Subcommands.Add(BuildListCommand());
        root.Subcommands.Add(BuildStatusCommand());
        root.Subcommands.Add(BuildGetCommand());
        root.Subcommands.Add(BuildContextCommand());
        root.Subcommands.Add(BuildApproveCommand());
        root.Subcommands.Add(BuildApprovalWorkflowCommand());
        root.Subcommands.Add(BuildProviderCommand());
        root.Subcommands.Add(BuildCreationAttemptCommand());
        root.Subcommands.Add(BuildCreateCommand());
        root.Subcommands.Add(BuildImportCommand());
        root.Subcommands.Add(BuildAdoptCommand());
        root.Subcommands.Add(BuildMoveCommand());
        root.Subcommands.Add(BuildEditCommand());
        root.Subcommands.Add(BuildClaimCommand());
        root.Subcommands.Add(BuildTakeoverCommand());
        root.Subcommands.Add(BuildResumeCommand());
        root.Subcommands.Add(BuildReleaseCommand());
        root.Subcommands.Add(BuildRequeueCommand());
        root.Subcommands.Add(BuildArchiveCommand(archive: true));
        root.Subcommands.Add(BuildArchiveCommand(archive: false));
        root.Subcommands.Add(BuildPickCommand());
        root.Subcommands.Add(BuildWorkerCommand());
        root.Subcommands.Add(BuildWorkspacesCommand());
        root.Subcommands.Add(BuildFinishCommand());
        root.Subcommands.Add(BuildWebCommand());
        root.Subcommands.Add(BuildSkillCommand());
        root.Subcommands.Add(BuildConfigCommand());
        return root;
    }

    private Command BuildConfigCommand()
    {
        var command = new Command("config", "Inspect and manage scoped Wrighty configuration");
        command.Subcommands.Add(BuildConfigShowCommand());
        command.Subcommands.Add(BuildConfigUserCommand());
        command.Subcommands.Add(BuildConfigRepositoryCommand());
        command.Subcommands.Add(BuildConfigProfileCommand());
        return command;
    }

    private Command BuildConfigUserCommand()
    {
        var command = new Command("user", "Manage user-scoped settings");
        command.Subcommands.Add(BuildConfigUserShowCommand());
        var host = new Command("host", "Manage the symbolic machine host label");
        host.Subcommands.Add(BuildConfigUserHostSetCommand());
        host.Subcommands.Add(BuildConfigUserHostClearCommand());
        command.Subcommands.Add(host);
        return command;
    }

    private Command BuildConfigUserHostSetCommand()
    {
        var label = new Argument<string>("label")
        {
            Description = "Symbolic host label published in GitHub handovers."
        };
        var command = new Command("set", "Set the symbolic host label");
        command.Arguments.Add(label);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(false, async () =>
            {
                var value = parseResult.GetValue(label)!;
                if (string.IsNullOrWhiteSpace(value))
                    throw new TrackerException("ARGUMENT_INVALID", "Host label cannot be empty.", 2);
                var store = RequireUserSettings();
                var settings = await store.LoadAsync(cancellationToken);
                await store.SaveAsync(settings with { HostLabel = value.Trim() }, cancellationToken);
                await output.WriteLineAsync($"Host label set to '{value.Trim()}'.");
            }));
        return command;
    }

    private Command BuildConfigUserHostClearCommand()
    {
        var command = new Command("clear", "Clear the symbolic host label");
        command.SetAction((_, cancellationToken) =>
            ExecuteConfigurationCommandAsync(false, async () =>
            {
                var store = RequireUserSettings();
                var settings = await store.LoadAsync(cancellationToken);
                await store.SaveAsync(settings with { HostLabel = null }, cancellationToken);
                await output.WriteLineAsync(
                    $"Host label cleared; GitHub handovers will show '{Settings.HostLabelProvider.AnonymousLabel}'.");
            }));
        return command;
    }

    private Command BuildConfigRepositoryCommand()
    {
        var command = new Command("repository", "Inspect or change repository .wrighty.json policy");
        command.Subcommands.Add(BuildConfigRepositoryShowCommand());
        command.Subcommands.Add(BuildConfigRepositoryCheckCommand());
        command.Subcommands.Add(BuildConfigRepositoryWorkflowCommand());
        command.Subcommands.Add(BuildConfigRepositoryArchiveCommand());
        command.Subcommands.Add(BuildConfigRepositoryWorkerCommand());
        command.Subcommands.Add(BuildConfigRepositoryCompletionCommand());
        command.Subcommands.Add(BuildConfigRepositoryWebCommand());
        command.Subcommands.Add(BuildConfigRepositoryProfilesCommand());
        return command;
    }

    private Command BuildConfigRepositoryShowCommand()
    {
        var config = ConfigPathOption();
        var json = JsonOption();
        var command = new Command("show", "Show effective repository configuration");
        command.Options.Add(config);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(parseResult.GetValue(json), async () =>
            {
                var explicitPath = parseResult.GetValue(config);
                var snapshot = await RequireRepositoryConfigurations().ReadAsync(
                    workingDirectory,
                    explicitPath,
                    cancellationToken);
                await WriteRepositoryConfigurationAsync(
                    snapshot,
                    parseResult.GetValue(json),
                    RepositoryConfigurationResolution(explicitPath));
            }));
        return command;
    }

    private Command BuildConfigRepositoryCheckCommand()
    {
        var config = ConfigPathOption();
        var json = JsonOption();
        var command = new Command("check", "Validate repository configuration without remote mutation");
        command.Options.Add(config);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(parseResult.GetValue(json), async () =>
            {
                var snapshot = await RequireRepositoryConfigurations().ReadAsync(
                    workingDirectory,
                    parseResult.GetValue(config),
                    cancellationToken);
                if (parseResult.GetValue(json))
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        result = RepositoryConfigurationDto(
                            snapshot,
                            RepositoryConfigurationResolution(parseResult.GetValue(config)))
                    }, ConfigurationJsonOptions));
                }
                else
                {
                    await output.WriteLineAsync($"Configuration valid: {snapshot.SourcePath}");
                    await output.WriteLineAsync($"Revision: {snapshot.Revision}");
                    await output.WriteLineAsync($"Schema: {snapshot.SchemaVersion}");
                    if (snapshot.RequiresCanonicalizationApproval)
                        await output.WriteLineAsync(
                            "warning: A typed save will normalize comments or trailing commas and requires --yes.");
                }
            }));
        return command;
    }

    private Command BuildConfigRepositoryWorkflowCommand()
    {
        var group = new Command("workflow", "Manage ordinary workflow defaults");
        var pickFrom = new Option<string?>("--pick-from");
        var pickTo = new Option<string?>("--pick-to");
        var finishTo = new Option<string?>("--finish-to");
        var command = new Command("set-defaults", "Set typed workflow defaults");
        command.Options.Add(pickFrom);
        command.Options.Add(pickTo);
        command.Options.Add(finishTo);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var mutation = new WorkflowDefaultsMutation(
                parseResult.GetValue(pickFrom),
                parseResult.GetValue(pickTo),
                parseResult.GetValue(finishTo));
            if (mutation.PickFrom is null && mutation.PickTo is null && mutation.FinishTo is null)
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "Provide at least one workflow default to change.",
                    2);
            return ExecuteConfigurationMutationAsync(
                parseResult,
                common,
                mutation,
                cancellationToken);
        });
        group.Subcommands.Add(command);
        return group;
    }

    private Command BuildConfigRepositoryArchiveCommand()
    {
        var group = new Command("archive", "Manage archive policy");
        var statuses = new Option<string[]>("--on-status")
        {
            Description = "Status that archives an item; repeatable. Omit all values to clear."
        };
        var command = new Command("set", "Set statuses that trigger archival");
        command.Options.Add(statuses);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationMutationAsync(
                parseResult,
                common,
                new ArchivePolicyMutation(parseResult.GetValue(statuses) ?? []),
                cancellationToken));
        group.Subcommands.Add(command);
        return group;
    }

    private Command BuildConfigRepositoryWorkerCommand()
    {
        var group = new Command("worker", "Manage ordinary worker defaults");
        var defaultAgent = new Option<string?>("--default-agent");
        var clearAgent = new Option<bool>("--clear-default-agent");
        var workspaceMode = new Option<string?>("--workspace-mode");
        var command = new Command("set", "Set worker default agent or workspace mode");
        command.Options.Add(defaultAgent);
        command.Options.Add(clearAgent);
        command.Options.Add(workspaceMode);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var selectedAgent = parseResult.GetValue(defaultAgent);
            var clearing = parseResult.GetValue(clearAgent);
            if (clearing && selectedAgent is not null)
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "--default-agent and --clear-default-agent cannot be combined.",
                    2);
            var mode = parseResult.GetValue(workspaceMode);
            if (!clearing && selectedAgent is null && mode is null)
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "Provide --default-agent, --clear-default-agent, or --workspace-mode.",
                    2);
            return ExecuteConfigurationMutationAsync(
                parseResult,
                common,
                new WorkerDefaultsMutation(
                    clearing || selectedAgent is not null,
                    clearing ? null : selectedAgent,
                    mode),
                cancellationToken);
        });
        group.Subcommands.Add(command);
        return group;
    }

    private Command BuildConfigRepositoryCompletionCommand()
    {
        var group = new Command("completion", "Manage worker completion policy");
        var commit = new Option<string?>("--commit");
        var integration = new Option<string?>("--integration");
        var command = new Command("set", "Set typed completion policy");
        command.Options.Add(commit);
        command.Options.Add(integration);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var selectedCommit = parseResult.GetValue(commit);
            var selectedIntegration = parseResult.GetValue(integration);
            if (selectedCommit is null && selectedIntegration is null)
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "Provide --commit or --integration.",
                    2);
            return ExecuteConfigurationMutationAsync(
                parseResult,
                common,
                new CompletionPolicyMutation(selectedCommit, selectedIntegration),
                cancellationToken);
        });
        group.Subcommands.Add(command);
        return group;
    }

    private Command BuildConfigRepositoryWebCommand()
    {
        var group = new Command("web", "Manage repository web policy");
        var protect = new Option<bool?>("--protect-non-human-claims")
        {
            Description = "Whether ordinary web edits protect non-human claims."
        };
        var command = new Command("set", "Set typed repository web policy");
        command.Options.Add(protect);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var selected = parseResult.GetValue(protect);
            if (selected is null)
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "Provide --protect-non-human-claims true|false.",
                    2);
            return ExecuteConfigurationMutationAsync(
                parseResult,
                common,
                new WebPolicyMutation(selected.Value),
                cancellationToken);
        });
        group.Subcommands.Add(command);
        return group;
    }

    private sealed record ConfigurationMutationOptions(
        Option<string?> Config,
        Option<string?> Revision,
        Option<bool> DryRun,
        Option<bool> Json,
        Option<bool> Yes);

    private ConfigurationMutationOptions AddMutationOptions(Command command)
    {
        var options = new ConfigurationMutationOptions(
            ConfigPathOption(),
            new Option<string?>("--revision")
            {
                Description = "Require this exact raw-file revision."
            },
            new Option<bool>("--dry-run")
            {
                Description = "Show the typed change without writing."
            },
            JsonOption(),
            new Option<bool>("--yes")
            {
                Description = "Approve canonicalization warnings."
            });
        command.Options.Add(options.Config);
        command.Options.Add(options.Revision);
        command.Options.Add(options.DryRun);
        command.Options.Add(options.Json);
        command.Options.Add(options.Yes);
        return options;
    }

    private Task<int> ExecuteConfigurationMutationAsync(
        ParseResult parseResult,
        ConfigurationMutationOptions options,
        RepositoryConfigurationMutation mutation,
        CancellationToken cancellationToken) =>
        ExecuteConfigurationCommandAsync(parseResult.GetValue(options.Json), async () =>
        {
            var service = RequireRepositoryConfigurations();
            var before = await service.ReadAsync(
                workingDirectory,
                parseResult.GetValue(options.Config),
                cancellationToken);
            var expectedRevision = parseResult.GetValue(options.Revision) ?? before.Revision;
            var result = await service.MutateAsync(
                before.SourcePath,
                expectedRevision,
                mutation,
                parseResult.GetValue(options.Yes),
                parseResult.GetValue(options.DryRun),
                cancellationToken);
            await WriteConfigurationMutationAsync(result, parseResult.GetValue(options.Json));
        });

    private IRepositoryConfigurationService RequireRepositoryConfigurations() =>
        repositoryConfigurations ?? throw new TrackerException(
            "CONFIGURATION_MANAGEMENT_UNAVAILABLE",
            "Repository configuration management is not configured in this Wrighty build.",
            7);

    private static Option<string?> ConfigPathOption() => new("--config")
    {
        Description = "Explicit repository configuration path."
    };

    private static readonly JsonSerializerOptions ConfigurationJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

    private async Task WriteRepositoryConfigurationAsync(
        RepositoryConfigurationSnapshot snapshot,
        bool json,
        string resolution)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                result = RepositoryConfigurationDto(snapshot, resolution)
            }, ConfigurationJsonOptions));
            return;
        }

        await WriteRepositoryConfigurationHumanAsync(snapshot, resolution);
    }

    private async Task WriteConfigurationMutationAsync(
        RepositoryConfigurationMutationResult result,
        bool json)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                result = new
                {
                    saved = result.Saved,
                    restartRequired = result.RestartRequired,
                    sourcePath = result.After.SourcePath,
                    previousRevision = result.Before.Revision,
                    revision = result.After.Revision,
                    changes = result.Changes
                }
            }, ConfigurationJsonOptions));
            return;
        }

        var summary = "Dry run; configuration was not written.";
        if (result.Saved)
            summary = $"Saved {result.After.SourcePath}.";
        else if (result.Changes.Count == 0)
            summary = "No configuration values changed.";
        await output.WriteLineAsync(summary);
        foreach (var change in result.Changes)
            await output.WriteLineAsync(
                $"  {change.Id}: {JsonSerializer.Serialize(change.Before)} -> " +
                JsonSerializer.Serialize(change.After));
        if (result.RestartRequired)
            await output.WriteLineAsync(
                "Restart continuous workers and this web process before relying on the new policy.");
    }

    private static object RepositoryConfigurationDto(
        RepositoryConfigurationSnapshot snapshot,
        string resolution) => new
        {
            sourcePath = snapshot.SourcePath,
            exists = true,
            resolution,
            revision = snapshot.Revision,
            schemaVersion = snapshot.SchemaVersion,
            schemaVersionWasExplicit = snapshot.SchemaVersionWasExplicit,
            containsComments = snapshot.ContainsComments,
            containsTrailingCommas = snapshot.ContainsTrailingCommas,
            legacyProperties = snapshot.LegacyProperties ?? [],
            settings = snapshot.Settings
        };

    private static object MissingRepositoryConfigurationDto(
        string sourcePath,
        string resolution) => new
        {
            sourcePath,
            exists = false,
            resolution
        };

    private Command BuildConfigShowCommand()
    {
        var config = ConfigPathOption();
        var json = JsonOption();
        var command = new Command("show", "Show effective user and repository configuration");
        command.Options.Add(config);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(parseResult.GetValue(json), () =>
                WriteAggregateConfigurationAsync(
                    parseResult.GetValue(config),
                    parseResult.GetValue(json),
                    cancellationToken)));
        return command;
    }

    private async Task<WorkerConfig?> TryLoadWorkerConfigAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return (await configLoader.LoadAsync(
                workingDirectory,
                cancellationToken)).EffectiveWorker;
        }
        catch (TrackerException exception) when (exception.Code == "CONFIG_NOT_FOUND")
        {
            // User-scoped settings remain inspectable outside an initialized tracker. In that
            // case every repository-scoped experimental integration stays disabled.
            return null;
        }
    }

    private AgentLaunchCapabilityView[] AgentLaunchCapabilities(WorkerConfig? worker)
    {
        if (runtimes is null)
            return [];
        return runtimes.Snapshot().Agents.Select(runtime =>
        {
            var local = localLauncher.GetCapabilities(runtime.Agent);
            var experimentalEnabled =
                worker?.AllowsExperimentalDesktopSession(runtime.Agent) == true;
            var desktopSupport = runtime.Agent switch
            {
                "codex" or "copilot" => "supported",
                "claude" when experimentalEnabled => "experimental-enabled",
                "claude" => "experimental-disabled",
                _ => "unavailable"
            };
            var canOpenDesktop =
                local.CanLaunchDesktop &&
                desktopSupport is "supported" or "experimental-enabled";
            string? desktopUnavailableReason = local.DesktopUnavailableReason;
            if (canOpenDesktop)
                desktopUnavailableReason = null;
            else if (desktopSupport == "experimental-disabled")
                desktopUnavailableReason =
                    "Opening recorded Claude sessions in Desktop is experimental and is not enabled.";
            return new AgentLaunchCapabilityView(
                runtime.Agent,
                runtime.Installed && local.CanLaunchCli,
                canOpenDesktop,
                desktopSupport,
                local.CliUnavailableReason,
                desktopUnavailableReason);
        }).ToArray();
    }

    private sealed record AgentLaunchCapabilityView(
        [property: System.Text.Json.Serialization.JsonPropertyName("agent")]
        string Agent,
        [property: System.Text.Json.Serialization.JsonPropertyName("openCli")]
        bool OpenCli,
        [property: System.Text.Json.Serialization.JsonPropertyName("openDesktop")]
        bool OpenDesktop,
        [property: System.Text.Json.Serialization.JsonPropertyName("desktopSessionSupport")]
        string DesktopSessionSupport,
        [property: System.Text.Json.Serialization.JsonPropertyName("cliUnavailableReason")]
        string? CliUnavailableReason,
        [property: System.Text.Json.Serialization.JsonPropertyName("desktopUnavailableReason")]
        string? DesktopUnavailableReason);

    private Command BuildConfigUserShowCommand()
    {
        var json = JsonOption();
        var command = new Command("show", "Show effective user-scoped configuration");
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(parseResult.GetValue(json), async () =>
            {
                var store = RequireUserSettings();
                var settings = await store.LoadAsync(cancellationToken);
                if (parseResult.GetValue(json))
                {
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        result = UserConfigurationDto(store, settings)
                    }, ConfigurationJsonOptions));
                }
                else
                {
                    await WriteUserConfigurationHumanAsync(store, settings);
                }
            }));
        return command;
    }

    private async Task WriteAggregateConfigurationAsync(
        string? explicitPath,
        bool json,
        CancellationToken cancellationToken)
    {
        var userStore = RequireUserSettings();
        var user = await userStore.LoadAsync(cancellationToken);
        var resolution = RepositoryConfigurationResolution(explicitPath);
        var repositoryPath = repositoryConfigurations is null
            ? Path.Combine(workingDirectory, TrackerConfigLoader.FileName)
            : repositoryConfigurations.ResolvePath(workingDirectory, explicitPath);
        RepositoryConfigurationSnapshot? repository = null;
        if (repositoryConfigurations is not null)
        {
            try
            {
                repository = await repositoryConfigurations.ReadPathAsync(
                    repositoryPath,
                    cancellationToken);
            }
            catch (TrackerException exception) when (
                exception.Code == "CONFIG_NOT_FOUND" &&
                string.Equals(resolution, "discovery", StringComparison.Ordinal))
            {
                // An aggregate view remains useful outside an initialized repository. Explicit
                // selections are errors because the caller asked Wrighty to inspect that exact file.
            }
        }
        else if (!string.IsNullOrWhiteSpace(explicitPath))
            throw new TrackerException(
                "CONFIGURATION_MANAGEMENT_UNAVAILABLE",
                "Repository configuration management is not configured in this Wrighty build.",
                7);
        var worker = repository?.StoredConfiguration.EffectiveWorker ??
            await TryLoadWorkerConfigAsync(cancellationToken);
        var launchCapabilities = AgentLaunchCapabilities(worker);

        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                result = new
                {
                    user = UserConfigurationDto(userStore, user),
                    repository = repository is null
                        ? MissingRepositoryConfigurationDto(repositoryPath, resolution)
                        : RepositoryConfigurationDto(repository, resolution),
                    agentLaunch = launchCapabilities
                }
            }, ConfigurationJsonOptions));
            return;
        }

        await WriteUserConfigurationHumanAsync(userStore, user);
        await output.WriteLineAsync();
        if (repository is null)
        {
            await output.WriteLineAsync("Repository configuration");
            await output.WriteLineAsync($"  File: {repositoryPath}");
            await output.WriteLineAsync("  Status: not found");
            await output.WriteLineAsync($"  Resolution: {resolution}");
        }
        else
        {
            await WriteRepositoryConfigurationHumanAsync(repository, resolution);
        }
        await WriteAgentLaunchCapabilitiesHumanAsync(launchCapabilities);
    }

    private async Task WriteAgentLaunchCapabilitiesHumanAsync(
        IReadOnlyCollection<AgentLaunchCapabilityView> launchCapabilities)
    {
        if (launchCapabilities.Count == 0)
            return;

        await output.WriteLineAsync();
        await output.WriteLineAsync("Local agent launch");
        foreach (var capability in launchCapabilities)
        {
            await output.WriteLineAsync(
                $"  {capability.Agent}: CLI " +
                $"{(capability.OpenCli ? "available" : "unavailable")}; Desktop " +
                $"{capability.DesktopSessionSupport} " +
                $"{(capability.OpenDesktop ? "available" : "unavailable")}");
        }
    }

    private async Task WriteUserConfigurationHumanAsync(
        Settings.UserSettingsStore store,
        Settings.UserSettings settings)
    {
        var effectiveHost = EffectiveHost(settings);
        await output.WriteLineAsync("User configuration");
        await output.WriteLineAsync($"  File: {store.SourcePath}");
        await output.WriteLineAsync(store.AwaitingMigration
            ? $"  Status: reading {Path.GetFileName(store.LegacySourcePath)}; " +
              "it will be migrated on the next change and left in place"
            : store.Exists
                ? "  Status: present"
                : "  Status: not present; defaults in effect");
        await output.WriteLineAsync(
            $"  hostLabel: {effectiveHost}" +
            (string.IsNullOrWhiteSpace(settings.HostLabel) ? " (default)" : string.Empty));
    }

    private async Task WriteRepositoryConfigurationHumanAsync(
        RepositoryConfigurationSnapshot snapshot,
        string resolution)
    {
        await output.WriteLineAsync("Repository configuration");
        await output.WriteLineAsync($"  File: {snapshot.SourcePath}");
        await output.WriteLineAsync("  Status: present");
        await output.WriteLineAsync($"  Resolution: {resolution}");
        await output.WriteLineAsync($"  Schema: {snapshot.SchemaVersion}");
        await output.WriteLineAsync($"  Revision: {snapshot.Revision}");

        var settings = snapshot.Settings
            .Where(setting => !string.Equals(
                setting.Id,
                "schemaVersion",
                StringComparison.Ordinal))
            .GroupBy(setting => ConfigurationGroup(setting.Id));
        foreach (var group in settings)
        {
            await output.WriteLineAsync();
            await output.WriteLineAsync($"  {group.Key}");
            foreach (var setting in group)
            {
                await output.WriteLineAsync(
                    $"    {setting.Id}: {RenderConfigurationValue(setting.EffectiveValue)}" +
                    (string.Equals(
                        setting.DefaultSource,
                        "wrighty-default",
                        StringComparison.Ordinal)
                        ? " (default)"
                        : string.Empty));
            }
        }

        if (snapshot.RequiresCanonicalizationApproval)
            await output.WriteLineAsync(
                "  Warning: a typed save will normalize comments or trailing commas and requires --yes.");
    }

    private static object UserConfigurationDto(
        Settings.UserSettingsStore store,
        Settings.UserSettings settings)
    {
        var effectiveHost = EffectiveHost(settings);
        return new
        {
            sourcePath = store.SourcePath,
            exists = store.Exists,
            settings = new
            {
                hostLabel = new
                {
                    storedValue = settings.HostLabel,
                    effectiveValue = effectiveHost,
                    source = string.IsNullOrWhiteSpace(settings.HostLabel)
                        ? "wrighty-default"
                        : "user"
                }
            }
        };
    }

    private static string EffectiveHost(Settings.UserSettings settings) =>
        string.IsNullOrWhiteSpace(settings.HostLabel)
            ? Settings.HostLabelProvider.AnonymousLabel
            : settings.HostLabel.Trim();

    private static string RepositoryConfigurationResolution(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return "argument";
        return string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(TrackerConfigLoader.ConfigPathEnvironmentVariable))
            ? "discovery"
            : "environment";
    }

    private static string ConfigurationGroup(string id)
    {
        if (id.StartsWith("worker.", StringComparison.Ordinal))
            return "Worker";
        if (id.StartsWith("web.", StringComparison.Ordinal))
            return "Web";
        if (id.StartsWith("github.", StringComparison.Ordinal))
            return "GitHub";
        if (id.StartsWith("localMarkdown.", StringComparison.Ordinal))
            return "Local Markdown";
        if (id.StartsWith("archive.", StringComparison.Ordinal) ||
            id.StartsWith("default", StringComparison.Ordinal) ||
            string.Equals(id, "leaseMinutes", StringComparison.Ordinal))
            return "Workflow";
        return "General";
    }

    private static string RenderConfigurationValue(object? value) => value switch
    {
        null => "(not set)",
        string text => text,
        bool boolean => boolean ? "true" : "false",
        _ => JsonSerializer.Serialize(value)
    };

    private Settings.UserSettingsStore RequireUserSettings() =>
        userSettings ?? throw new TrackerException(
            "SETTINGS_UNAVAILABLE",
            "User settings are not configured in this Wrighty build.",
            7);

    private Command BuildResumeCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var exec = new Option<bool>("--exec")
        {
            Description = "Launch the recorded session directly instead of printing the command " +
                          "(macOS/Linux; interactive). Cannot be combined with --json."
        };
        var command = new Command("resume-command",
            "Print (or, with --exec, launch) the recorded workspace and vendor command for an item's session");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        command.Options.Add(exec);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            parseResult.GetValue(json),
            config => ResumeCommandAsync(
                config,
                parseResult.GetValue(idArgument)!,
                parseResult.GetValue(exec),
                parseResult.GetValue(json),
                cancellationToken),
            cancellationToken));
        return command;
    }

    private async Task ResumeCommandAsync(
        TrackerConfig config, string idText, bool exec, bool json, CancellationToken cancellationToken)
    {
        if (exec && json)
            throw new TrackerException("ARGUMENT_INVALID",
                "--exec and --json cannot be used together.", 2);
        var id = tracker.ResolveId(config, idText);
        // Read the durable session, not the claim: after an item is finished or its claim released,
        // the address survives on the session record (this is exactly the guided-completion case)
        // even though the claim no longer carries it.
        var state = await tracker.GetOperationalAsync(config, id, cancellationToken);
        var session = state.Session;
        if (session?.SessionId is null || session.WorkspacePath is null || session.Agent is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Item '{tracker.FormatShort(config, id)}' does not have a complete recorded agent session address.", 5);
        // The recorded worktree must still exist to resume into it. When it has been removed
        // (cleaned up after completion, or on another host), refuse rather than print a command that
        // would fail on `cd` into a directory that is gone.
        if (!Directory.Exists(session.WorkspacePath))
            throw new TrackerException("RESUME_WORKTREE_ABSENT",
                $"The recorded worktree for '{tracker.FormatShort(config, id)}' is no longer present at " +
                $"{session.WorkspacePath}; it was removed or is on another host, so the session cannot be resumed here.", 5);
        var adapter = ResumeAdapterFor(session.Agent);
        var handle = new SessionHandle(session.SessionId);
        var workspace = new Workspace(session.WorkspacePath);
        var environment = TrackerEnvironment(config);
        var resume = adapter.BuildInteractiveCommand(handle, workspace, environment);
        var invocation = adapter.BuildInteractiveInvocation(handle, workspace, environment);
        if (exec)
        {
            await localLauncher.ExecuteAsync(invocation, cancellationToken);
            return;
        }

        if (json)
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                version = 1,
                result = new
                {
                    id = id.Value,
                    session.Agent,
                    session.SessionId,
                    session.WorkspacePath,
                    command = resume,
                    executable = invocation.Executable,
                    arguments = invocation.Arguments,
                    workingDirectory = invocation.WorkingDirectory,
                    environment = invocation.Environment
                }
            }));
        else
            await output.WriteLineAsync(resume);
    }

    private static IAgentAdapter ResumeAdapterFor(string agentType) => agentType switch
    {
        "claude" => new ClaudeAgentAdapter(),
        "codex" => new CodexAgentAdapter(),
        "copilot" => new CopilotAgentAdapter(),
        _ => throw new TrackerException("AGENT_UNSUPPORTED",
            $"Unsupported recorded agent '{agentType}'.", 3)
    };

    private Command BuildWorkerCommand()
    {
        var agent = new Option<string?>("--agent") { Description = "Vendor to run: claude, codex, or copilot." };
        var once = new Option<bool>("--once") { Description = "Process at most one item and exit." };
        var maxItems = new Option<int?>("--max-items") { Description = "Stop after processing this many items." };
        var workspaceMode = new Option<string?>("--workspace-mode")
        {
            Description = "Override worker.workspaceMode: current (exclusive), shared (unsafe), or worktree (isolated)."
        };
        var filters = new Option<string[]>("--filter") { Description = "Extra eligibility filter (key=value); repeatable." };
        var idleTimeout = new Option<string?>("--idle-timeout") { Description = "Exit after this long without eligible work." };
        var itemTimeout = new Option<string>("--item-timeout")
        {
            Description = "Per-item process and hard lease-renewal budget.",
            DefaultValueFactory = _ => "60m"
        };
        var onFenced = new Option<string>("--on-fenced")
        {
            Description = "Action after takeover or lease loss: kill or detach.",
            DefaultValueFactory = _ => "kill"
        };
        var claimantId = new Option<string?>("--claimant-id") { Description = "Stable automation run identity." };
        var claimantKind = new Option<string>("--claimant-kind")
        {
            Description = "Worker claim attribution: agent or automation.",
            DefaultValueFactory = _ => "agent"
        };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print eligible invocations; claim and spawn nothing." };
        var item = new Option<string?>("--item")
        {
            Description = "Process one exact item, automatically resuming a recoverable session or starting new."
        };
        var resume = new Option<bool>("--resume")
        {
            Description = "Require --item to resume an existing recorded agent session."
        };
        var fresh = new Option<bool>("--fresh")
        {
            Description = "Require --item to start a new agent session; fail if the item is actively claimed."
        };
        var handoff = new Option<bool>("--handoff")
        {
            Description = "Require --item to hand the recorded session's work to a different agent " +
                          "as a new session in the retained workspace; --agent names the target, " +
                          "otherwise the first available configured fallback is used."
        };
        var keepWorkspace = new Option<bool>("--keep-workspace")
        {
            Description = "Retain a successful worktree so its completed agent session can be reviewed interactively."
        };
        var profile = new Option<string?>("--profile")
        {
            Description = "Execution profile for fresh launches in this run. Highest precedence: " +
                          "overrides the item's profile and the repository default."
        };
        var check = new Option<bool>("--check") { Description = "Run a read-only vendor probe and verify its session handle." };
        var yes = new Option<bool>("--yes") { Description = "Acknowledge live worker risk without prompting." };
        var from = new Option<string?>("--from") { Description = "Status to pick from." };
        var to = new Option<string?>("--to") { Description = "Status to move claimed items to." };
        var color = new Option<string>("--color")
        {
            Description = "Human output color: auto, always, or never. JSON is always unstyled.",
            DefaultValueFactory = _ => "auto"
        };
        var json = JsonOption();
        var command = new Command("worker", "Autonomously process explicitly eligible work items");
        foreach (var option in new Option[] { agent, once, maxItems, workspaceMode, filters, idleTimeout,
                     itemTimeout, onFenced, claimantId, claimantKind, dryRun, item, resume, fresh,
                     handoff, keepWorkspace, profile, from, to, color, json })
            command.Options.Add(option);
        command.Options.Add(check);
        command.Options.Add(yes);
        command.SetAction((parseResult, cancellationToken) => ExecuteWorkerAsync(
            new WorkerOptions(
                parseResult.GetValue(agent),
                parseResult.GetValue(once),
                parseResult.GetValue(maxItems),
                WorkspaceMode.Current,
                ParseWorkerFilters(parseResult.GetValue(filters)),
                ParseDuration(parseResult.GetValue(idleTimeout), "--idle-timeout", optional: true),
                ParseDuration(parseResult.GetValue(itemTimeout), "--item-timeout", optional: false)!.Value,
                ParseFencedAction(parseResult.GetValue(onFenced)!),
                parseResult.GetValue(claimantId),
                parseResult.GetValue(claimantKind)!,
                parseResult.GetValue(dryRun),
                parseResult.GetValue(json),
                parseResult.GetValue(from),
                parseResult.GetValue(to),
                parseResult.GetValue(keepWorkspace),
                parseResult.GetValue(profile)),
            cancellationToken,
            parseResult.GetValue(check),
            parseResult.GetValue(yes),
            parseResult.GetValue(item),
            parseResult.GetValue(resume),
            parseResult.GetValue(fresh),
            parseResult.GetValue(handoff),
            parseResult.GetValue(workspaceMode),
            parseResult.GetValue(color)!));
        return command;
    }

    private Command BuildProviderCommand()
    {
        var command = new Command(
            "provider",
            "Inspect and actively probe installation-local provider capacity");
        command.Subcommands.Add(BuildProviderProbeCommand());
        return command;
    }

    private Command BuildProviderProbeCommand()
    {
        var agent = new Argument<string>("agent")
        {
            Description = "Provider agent to probe: claude, codex, or copilot."
        };
        var yes = new Option<bool>("--yes")
        {
            Description = "Acknowledge that the probe starts the vendor CLI and may consume usage."
        };
        var json = JsonOption();
        var command = new Command(
            "probe",
            "Probe provider capacity now without claiming a work item");
        command.Arguments.Add(agent);
        command.Options.Add(yes);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var selected = parseResult.GetValue(agent)!;
                await ConfirmProviderProbeAsync(
                    selected,
                    parseResult.GetValue(yes),
                    parseResult.GetValue(json),
                    cancellationToken);
                await (workerService ?? throw new TrackerException(
                        "WORKER_UNAVAILABLE",
                        "Provider capacity probing is not configured.",
                        7))
                    .ProbeProviderAsync(
                        config,
                        selected,
                        workingDirectory,
                        value => WriteWorkerEventAsync(
                            value,
                            parseResult.GetValue(json),
                            WorkerColorMode.Auto),
                        cancellationToken);
            },
            cancellationToken));
        return command;
    }

    private async Task ConfirmProviderProbeAsync(
        string agent,
        bool yes,
        bool json,
        CancellationToken cancellationToken)
    {
        if (!yes && (json || isInputRedirected()))
            throw new TrackerException(
                "PROVIDER_PROBE_CONFIRMATION_REQUIRED",
                "Provider capacity probing requires --yes in JSON or non-interactive mode.",
                2);
        await error.WriteLineAsync(
            $"warning: probing {agent} starts a small vendor request and may consume subscription usage; " +
            "it does not claim or modify a work item.");
        if (yes)
            return;
        await output.WriteAsync($"Probe {agent} capacity now? [y/N] ");
        var answer = await input.ReadLineAsync(cancellationToken);
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "PROVIDER_PROBE_CONFIRMATION_REQUIRED",
                "Provider capacity probing was cancelled.",
                2);
    }

    private async Task<int> ExecuteWorkerAsync(WorkerOptions options, CancellationToken cancellationToken,
        bool checkOnly,
        bool yes,
        string? item,
        bool requireResume,
        bool requireFresh,
        bool requireHandoff,
        string? workspaceModeOverride,
        string colorValue)
    {
        try
        {
            var colorMode = ParseWorkerColorMode(colorValue);
            if (workerService is null)
                throw new TrackerException("WORKER_UNAVAILABLE", "Worker services are not configured.", 7);
            var config = await configLoader.LoadAsync(workingDirectory, cancellationToken);
            options = options with
            {
                WorkspaceMode = ResolveWorkspaceMode(
                    workspaceModeOverride,
                    config.EffectiveWorker.WorkspaceMode)
            };
            ValidateWorkerInvocation(
                checkOnly, item, requireResume, requireFresh, requireHandoff, options.Profile);
            if (checkOnly)
            {
                await workerService.CheckAsync(options.Agent ?? config.EffectiveWorker.DefaultAgent,
                    workingDirectory,
                    value => WriteWorkerEventAsync(value, options.Json, colorMode), cancellationToken);
                return 0;
            }
            await WriteMissingAgentNoticeAsync(config, options, item, colorMode);
            var intent = ResolveWorkerIntent(requireResume, requireFresh, requireHandoff);
            var callerContext = item is null
                ? null
                : agentContextProvider.Resolve(new AgentContextInput());
            if (!await PreflightWorkerAsync(
                    config, options, item, intent, yes, colorMode, cancellationToken))
                return 0;
            var summary = await RunWorkerAsync(
                config, options, item, intent, callerContext?.ClaimToken, colorMode, cancellationToken);
            return summary.ExitCode;
        }
        catch (TrackerException exception)
        {
            return await writer.WriteErrorAsync(exception, options.Json);
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception)
        {
            return await writer.WriteErrorAsync(new TrackerException(
                "UNEXPECTED_ERROR", exception.Message, innerException: exception), options.Json);
        }
    }

    private static void ValidateWorkerInvocation(
        bool checkOnly,
        string? item,
        bool requireResume,
        bool requireFresh,
        bool requireHandoff,
        string? profile)
    {
        // A vendor-native session keeps the model it started with; copilot goes further and gives a
        // resumed session's model precedence over configuration. Silently ignoring --profile here
        // would tell the operator they had changed something when they had not.
        if (profile is not null && requireResume)
            throw new TrackerException("ARGUMENT_INVALID",
                "--profile cannot be combined with --resume: a recorded session keeps the model and " +
                "effort it started with. Use --fresh to start a new session under this profile, or " +
                "--handoff to continue the work under a different agent.", 2);
        if ((requireResume ? 1 : 0) + (requireFresh ? 1 : 0) + (requireHandoff ? 1 : 0) > 1)
            throw new TrackerException("ARGUMENT_INVALID",
                "--resume, --fresh, and --handoff cannot be combined.", 2);
        if ((requireResume || requireFresh || requireHandoff) && item is null)
            throw new TrackerException("ARGUMENT_INVALID",
                "--resume, --fresh, and --handoff require --item <id>.", 2);
        if (checkOnly && item is not null)
            throw new TrackerException("ARGUMENT_INVALID",
                "--check cannot be combined with --item.", 2);
    }

    private async Task WriteMissingAgentNoticeAsync(
        TrackerConfig config,
        WorkerOptions options,
        string? item,
        WorkerColorMode colorMode)
    {
        if (item is not null ||
            !string.IsNullOrWhiteSpace(options.Agent) ||
            !string.IsNullOrWhiteSpace(config.EffectiveWorker.DefaultAgent))
            return;
        await WriteWorkerEventAsync(
            new WorkerEvent(
                "info",
                Message: "No default worker agent is configured; only items with " +
                         "an item agent policy can run. Set --agent <vendor> or " +
                         "worker.defaultAgent in .wrighty.json to provide a fallback."),
            options.Json,
            colorMode);
    }

    private static WorkerItemIntent ResolveWorkerIntent(
        bool requireResume, bool requireFresh, bool requireHandoff)
    {
        if (requireResume)
            return WorkerItemIntent.Resume;
        if (requireHandoff)
            return WorkerItemIntent.Handoff;
        return requireFresh ? WorkerItemIntent.Fresh : WorkerItemIntent.Auto;
    }

    private async Task<bool> PreflightWorkerAsync(
        TrackerConfig config,
        WorkerOptions options,
        string? item,
        WorkerItemIntent intent,
        bool yes,
        WorkerColorMode colorMode,
        CancellationToken cancellationToken)
    {
        if (item is not null)
        {
            await workerService!.PreflightItemAsync(
                config, options, workingDirectory, tracker.ResolveId(config, item), intent,
                value => WriteWorkerEventAsync(value, options.Json, colorMode), cancellationToken);
        }
        else if (!options.DryRun)
        {
            var hasWork = await workerService!.PreflightAsync(
                config, options, workingDirectory,
                value => WriteWorkerEventAsync(value, options.Json, colorMode), cancellationToken);
            if (!hasWork && options.Once)
                return false;
        }
        await ConfirmWorkerExecutionAsync(config, options, yes, colorMode, cancellationToken);
        return true;
    }

    private async Task<WorkerRunSummary> RunWorkerAsync(
        TrackerConfig config,
        WorkerOptions options,
        string? item,
        WorkerItemIntent intent,
        string? claimToken,
        WorkerColorMode colorMode,
        CancellationToken cancellationToken)
    {
        var selection = new WorkerItemSelection(item, intent, claimToken);
        Func<WorkerEvent, Task> ordinaryOutput =
            value => WriteWorkerEventAsync(value, options.Json, colorMode);
        if (options.DryRun)
            return await RunWorkerServiceAsync(
                config, options, selection, ordinaryOutput, cancellationToken);

        var configPath = config.SourcePath is null
            ? Path.Combine(workingDirectory, TrackerConfigLoader.FileName)
            : Path.GetFullPath(config.SourcePath);
        var revision = config.SourceRevision ??
            (File.Exists(configPath)
            ? await RepositoryConfigurationService.RevisionAsync(configPath, cancellationToken)
            : string.Empty);
        var registration = await RegisterWorkerAsync(
            configPath, revision, options, selection, cancellationToken);
        await using var registrationScope = registration;
        var warningState = new WorkerRegistryWarningState();
        return await RunWorkerServiceAsync(
            config,
            options,
            selection,
            value => WriteRegisteredWorkerEventAsync(
                value,
                registration,
                warningState,
                options,
                colorMode,
                cancellationToken),
            cancellationToken);
    }

    private async Task<IWorkerInstanceRegistration> RegisterWorkerAsync(
        string configPath,
        string revision,
        WorkerOptions options,
        WorkerItemSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await workerInstances.RegisterAsync(
                configPath,
                revision,
                WorkerInvocationSummary(options, selection.Item, selection.Intent),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync(
                $"warning: Local worker status could not be registered: {exception.Message}");
            return await NoOpWorkerInstanceRegistry.Instance.RegisterAsync(
                configPath,
                revision,
                string.Empty,
                cancellationToken);
        }
    }

    private async Task WriteRegisteredWorkerEventAsync(
        WorkerEvent value,
        IWorkerInstanceRegistration registration,
        WorkerRegistryWarningState warningState,
        WorkerOptions options,
        WorkerColorMode colorMode,
        CancellationToken cancellationToken)
    {
        var running = value.Type is "started" or "resumed" or "running" or "session";
        var terminal = value.Type is "finished" or "needs-attention" or "failed" or "fenced"
            or "timed-out" or "rejected" or "retry-scheduled";
        try
        {
            if (running && value.ItemId is not null)
            {
                await registration.UpdateAsync(
                    value.ItemId,
                    WorkerInstanceState.RunningItem,
                    cancellationToken);
            }
            else if (terminal)
            {
                await registration.UpdateAsync(
                    null,
                    WorkerInstanceState.Idle,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            if (!warningState.Written)
            {
                warningState.Written = true;
                await error.WriteLineAsync(
                    $"warning: Local worker status could not be updated: {exception.Message}");
            }
        }
        await WriteWorkerEventAsync(value, options.Json, colorMode);
    }

    private Task<WorkerRunSummary> RunWorkerServiceAsync(
        TrackerConfig config,
        WorkerOptions options,
        WorkerItemSelection selection,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken) =>
        selection.Item is null
            ? workerService!.RunAsync(
                config, options, workingDirectory, emit, cancellationToken)
            : workerService!.RunItemAsync(
                config,
                options,
                workingDirectory,
                tracker.ResolveId(config, selection.Item),
                selection.Intent,
                selection.ClaimToken,
                emit,
                cancellationToken);

    private sealed record WorkerItemSelection(
        string? Item,
        WorkerItemIntent Intent,
        string? ClaimToken);

    private sealed class WorkerRegistryWarningState
    {
        public bool Written { get; set; }
    }

    private static string WorkerInvocationSummary(
        WorkerOptions options,
        string? item,
        WorkerItemIntent intent)
    {
        var values = new List<string> { "wrighty worker" };
        if (item is not null)
            values.Add($"--item {item}");
        if (options.Once)
            values.Add("--once");
        if (options.MaxItems is { } maximum)
            values.Add($"--max-items {maximum}");
        if (options.Agent is { } agent)
            values.Add($"--agent {agent}");
        if (item is not null && intent != WorkerItemIntent.Auto)
            values.Add($"--{intent.ToString().ToLowerInvariant()}");
        values.Add($"--workspace-mode {options.WorkspaceMode.ToString().ToLowerInvariant()}");
        return string.Join(' ', values);
    }

    private async Task ConfirmWorkerExecutionAsync(
        TrackerConfig config,
        WorkerOptions options,
        bool yes,
        WorkerColorMode colorMode,
        CancellationToken cancellationToken)
    {
        if (options.DryRun)
            return;

        var styler = new WorkerTerminalStyler(terminals, colorMode);
        await error.WriteLineAsync(
            $"{styler.WarningPrefix()} live worker execution may start unattended agents that " +
            "execute commands and modify files on this machine.");
        await WriteEffectivePermissionsAsync(config, options, styler);
        if (options.WorkspaceMode == WorkspaceMode.Shared)
            await error.WriteLineAsync(
                $"{styler.WarningPrefix()} shared workspace mode allows multiple agents to concurrently modify, stage, " +
                "or commit the same files; Wrighty cannot detect or resolve these conflicts.");
        if (yes)
            return;
        if (options.Json || isInputRedirected())
            throw new TrackerException(
                "WORKER_CONFIRMATION_REQUIRED",
                "Live worker execution requires --yes in JSON or non-interactive mode.",
                2);

        await output.WriteAsync("Continue? [y/N] ");
        var answer = await input.ReadLineAsync(cancellationToken);
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "WORKER_CONFIRMATION_REQUIRED",
                "Live worker execution was cancelled.",
                2);
    }

    /// <summary>
    /// States the effective per-agent permission posture before a live run. A profile a vendor
    /// cannot enforce is called out explicitly, so the operator is never left believing a run is
    /// confined to the workspace when it is not.
    /// </summary>
    private async Task WriteEffectivePermissionsAsync(
        TrackerConfig config,
        WorkerOptions options,
        WorkerTerminalStyler styler)
    {
        foreach (var permissions in workerService!.DescribeAgentPermissions(config, options.Agent))
        {
            var prefix = permissions.IsWeakerThanRequested ||
                         permissions.Enforcement == AgentPermissionEnforcement.Unrestricted
                ? $"{styler.WarningPrefix()} "
                : string.Empty;
            await error.WriteLineAsync(
                $"{prefix}{permissions.Agent} permission profile: {permissions.ProfileName} — " +
                permissions.Summary);
        }
    }

    private async Task WriteWorkerEventAsync(
        WorkerEvent value,
        bool json,
        WorkerColorMode colorMode)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return;
        }
        await WriteHumanWorkerEventAsync(value, colorMode);
    }

    private async Task WriteHumanWorkerEventAsync(
        WorkerEvent value,
        WorkerColorMode colorMode)
    {
        var styler = new WorkerTerminalStyler(terminals, colorMode);
        // Renewal remains available to JSON consumers, while the human stream uses the periodic
        // running heartbeat to avoid printing two operational lines at renewal half-life.
        if (value.Type == "renewed")
            return;
        if (value.Type == "running")
        {
            await output.WriteLineAsync(
                $"{value.OccurredAt:O} {styler.EventPrefix(value.Type)} {value.ItemId ?? "-"}" +
                $"{(value.Agent is null ? "" : $" [{value.Agent}]")}" +
                $"{(value.Message is null ? "" : $" — {value.Message}")}");
            return;
        }
        var argv = value.Arguments is null ? "" : $" argv={string.Join(" ", value.Arguments.Select(QuoteArg))}";
        await output.WriteLineAsync(
            $"{styler.EventPrefix(value.Type)} {value.ItemId ?? "-"}{(value.Agent is null ? "" : $" [{value.Agent}]")}" +
            $"{(value.WorkspacePath is null ? "" : $" in {value.WorkspacePath}")}{argv}" +
            $"{(value.Message is null ? "" : $" — {value.Message}")}");
        if (value.SessionId is not null)
            await output.WriteLineAsync($"  session: {value.SessionId}");
        if (value.Branch is not null)
            await output.WriteLineAsync($"  branch: {value.Branch}");
        if (value.ClaimExpiresAt is not null)
            await output.WriteLineAsync($"  claim expires: {value.ClaimExpiresAt:O}");
        if (value.ReviewCommand is not null)
            await output.WriteLineAsync($"  review: {value.ReviewCommand}");
        await WriteOperatorActionsAsync(value.OperatorActions);
    }

    private async Task WriteOperatorActionsAsync(
        IReadOnlyList<WorkerOperatorAction>? actions)
    {
        if (actions is not { Count: > 0 })
            return;
        await output.WriteLineAsync("  What you can do next:");
        foreach (var action in actions)
        {
            await output.WriteLineAsync($"    {action.Scenario}:");
            await output.WriteLineAsync($"      {action.Description}");
            foreach (var command in action.Commands)
                await output.WriteLineAsync($"      $ {command}");
            if (!string.IsNullOrWhiteSpace(action.AgentPrompt))
            {
                await output.WriteLineAsync("      then paste into the opened session:");
                await output.WriteLineAsync($"        {action.AgentPrompt}");
            }
        }
    }

    private static string QuoteArg(string value)
    {
        if (value.Length > 0 && value.All(ch =>
                char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':' or '=' or ','))
            return value;
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static WorkspaceMode ParseWorkspaceMode(string value) => value.ToLowerInvariant() switch
    {
        "current" => WorkspaceMode.Current,
        "shared" => WorkspaceMode.Shared,
        "worktree" => WorkspaceMode.Worktree,
        _ => throw new TrackerException("ARGUMENT_INVALID",
            "--workspace-mode must be current, shared, or worktree.", 2)
    };

    private static WorkspaceMode ResolveWorkspaceMode(
        string? commandLineValue,
        string? configuredValue) =>
        ParseWorkspaceMode(commandLineValue ?? configuredValue ?? "current");

    private static FencedAction ParseFencedAction(string value) => value.ToLowerInvariant() switch
    {
        "kill" => FencedAction.Kill,
        "detach" => FencedAction.Detach,
        _ => throw new TrackerException("ARGUMENT_INVALID", "--on-fenced must be kill or detach.", 2)
    };

    private static WorkerColorMode ParseWorkerColorMode(string value) => value.ToLowerInvariant() switch
    {
        "auto" => WorkerColorMode.Auto,
        "always" => WorkerColorMode.Always,
        "never" => WorkerColorMode.Never,
        _ => throw new TrackerException(
            "ARGUMENT_INVALID",
            "--color must be auto, always, or never.",
            2)
    };

    private static IReadOnlyDictionary<string, string> ParseWorkerFilters(string[]? values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1 ||
                !result.TryAdd(value[..separator], value[(separator + 1)..]))
                throw new TrackerException("ARGUMENT_INVALID",
                    $"Invalid or duplicate --filter '{value}'; expected key=value.", 2);
        }
        return result;
    }

    private static TimeSpan? ParseDuration(string? value, string option, bool optional)
    {
        if (string.IsNullOrWhiteSpace(value))
            return optional ? null : throw new TrackerException("ARGUMENT_INVALID", $"{option} is required.", 2);
        var suffix = value[^1];
        var multiplier = suffix switch { 's' => 1d, 'm' => 60d, 'h' => 3600d, _ => 0d };
        var number = multiplier == 0 ? value : value[..^1];
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            throw new TrackerException("ARGUMENT_INVALID",
                $"{option} must be a positive duration such as 30s, 15m, or 2h.", 2);
        return TimeSpan.FromSeconds(amount * (multiplier == 0 ? 1 : multiplier));
    }

    private Command BuildWebCommand()
    {
        var port = new Option<int>("--port")
        {
            Description = "TCP port to listen on; 0 selects an available port.",
            DefaultValueFactory = _ => 0
        };
        var noOpen = new Option<bool>("--no-open")
        {
            Description = "Do not open the default browser after the server starts."
        };
        var bind = new Option<string?>("--bind")
        {
            Description = "Bind one specific local-interface IP instead of loopback; HTTP remains plaintext."
        };
        var allowHost = new Option<string[]>("--allow-host")
        {
            Description = "Additional exact Host name to accept; repeatable."
        };
        var auth = new Option<string>("--auth")
        {
            Description = "Use token authentication (default); none grants every reachable client full access.",
            DefaultValueFactory = _ => "token"
        };
        var persistToken = new Option<bool>("--persist-token")
        {
            Description = "Reuse a managed per-tracker launch token across server restarts."
        };
        var tokenFile = new Option<string?>("--token-file")
        {
            Description = "Use a persistent launch token at this explicit path."
        };
        var rotateToken = new Option<bool>("--rotate-token")
        {
            Description = "Replace the selected persistent launch token before starting."
        };
        var publicUrl = new Option<string?>("--public-url")
        {
            Description = "Exact external http or https proxy origin; does not change the bind address."
        };
        var command = new Command(
            "web",
            "Start the loopback dashboard with token-gated protected routes by default");
        command.Options.Add(port);
        command.Options.Add(noOpen);
        command.Options.Add(bind);
        command.Options.Add(allowHost);
        command.Options.Add(auth);
        command.Options.Add(persistToken);
        command.Options.Add(tokenFile);
        command.Options.Add(rotateToken);
        command.Options.Add(publicUrl);
        command.SetAction((parseResult, cancellationToken) => ExecuteWebAsync(
            new WebServerOptions(
                parseResult.GetValue(port),
                !parseResult.GetValue(noOpen),
                parseResult.GetValue(bind),
                parseResult.GetValue(allowHost) ?? [],
                parseResult.GetValue(auth) ?? "token",
                parseResult.GetValue(persistToken),
                parseResult.GetValue(tokenFile),
                parseResult.GetValue(rotateToken),
                parseResult.GetValue(publicUrl)),
            cancellationToken));
        return command;
    }

    private async Task<int> ExecuteWebAsync(
        WebServerOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.Port is < 0 or > 65535)
            {
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "--port must be between 0 and 65535.",
                    2);
            }

            await webServer.RunAsync(
                options,
                output,
                error,
                cancellationToken);
            return 0;
        }
        catch (TrackerException exception)
        {
            return await writer.WriteErrorAsync(exception, json: false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            return await writer.WriteErrorAsync(
                new TrackerException(
                    "UNEXPECTED_ERROR",
                    exception.Message,
                    innerException: exception),
                json: false);
        }
    }

    private Command BuildInitCommand()
    {
        var backend = new Option<string?>("--backend")
        {
            Description = "Backend to initialize: github or local-markdown."
        };
        var repository = new Option<string?>("--repository")
        {
            Description = "GitHub repository in OWNER/REPOSITORY format."
        };
        var githubHost = new Option<string?>("--github-host")
        {
            Description = "GitHub hostname; inferred from a discovered remote or defaults to github.com."
        };
        var remote = new Option<string?>("--remote")
        {
            Description = "Git remote used for first-time repository discovery; defaults to origin."
        };
        var projectOwner = new Option<string?>("--project-owner")
        {
            Description = "User or organization that owns the Project; defaults to the repository owner."
        };
        var projectNumber = new Option<int?>("--project-number")
        {
            Description = "Existing owner-relative GitHub Project number."
        };
        var projectTitle = new Option<string?>("--project-title")
        {
            Description = "Exact Project title to reuse or create during first-time setup."
        };
        var noLinkRepository = new Option<bool>("--no-link-repository")
        {
            Description = "Do not link the Project from the repository's Projects tab."
        };
        var trustedCommentAuthor = new Option<string[]>("--trusted-comment-author")
        {
            Description = "GitHub login whose comments count as approved without a separate " +
                          "approval step. Repeatable. Anyone you grant write access can edit that " +
                          "author's comments, and the edited text would be approved too.",
            AllowMultipleArgumentsPerToken = true
        };
        var contextApprover = new Option<string[]>("--context-approver")
        {
            Description = "GitHub login whose +1/-1 reactions include or exclude pending " +
                          "comments. Repeatable.",
            AllowMultipleArgumentsPerToken = true
        };
        var defaultAgent = new Option<string?>("--default-agent")
        {
            Description = "Default worker agent: claude, codex, copilot, auto, or none."
        };
        var configPath = new Option<string?>("--config")
        {
            Description = "Configuration file to read or create."
        };
        var check = new Option<bool>("--check")
        {
            Description = "Validate local configuration and remote Project schema without changing either."
        };
        var createView = new Option<bool>("--create-view")
        {
            Description = "Create the canonical Wrighty Board and Wrighty Attention views for an existing Project when they are missing."
        };
        var skipIssueForms = new Option<bool>("--skip-issue-forms")
        {
            Description = "Do not create recommended Wrighty issue forms or template-chooser configuration."
        };
        var publishIssueForms = new Option<bool>("--publish-issue-forms")
        {
            Description = "Stage, commit, and push only the Wrighty-managed issue forms. Use with --yes for automation."
        };
        var yes = new Option<bool>("--yes")
        {
            Description = "Approve and execute the complete initialization plan without prompting."
        };
        var localPath = new Option<string?>("--local-path")
        {
            Description = "Local Markdown store path, relative to the configuration file by default."
        };
        var statuses = new Option<string[]>("--status")
        {
            Description = "Allowed local workflow status; repeat for multiple values."
        };
        var priorities = new Option<string[]>("--priority")
        {
            Description = "Allowed local priority; repeat for multiple values."
        };
        var json = JsonOption();
        var command = new Command("init", "Create or validate Wrighty configuration and backend resources");
        command.Options.Add(backend);
        command.Options.Add(repository);
        command.Options.Add(githubHost);
        command.Options.Add(remote);
        command.Options.Add(projectOwner);
        command.Options.Add(projectNumber);
        command.Options.Add(projectTitle);
        command.Options.Add(noLinkRepository);
        command.Options.Add(trustedCommentAuthor);
        command.Options.Add(contextApprover);
        command.Options.Add(defaultAgent);
        command.Options.Add(configPath);
        command.Options.Add(check);
        command.Options.Add(createView);
        command.Options.Add(skipIssueForms);
        command.Options.Add(publishIssueForms);
        command.Options.Add(yes);
        command.Options.Add(localPath);
        command.Options.Add(statuses);
        command.Options.Add(priorities);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteInitializationAsync(
            new TrackerInitializationRequest(
                parseResult.GetValue(repository),
                parseResult.GetValue(githubHost),
                parseResult.GetValue(remote),
                parseResult.GetValue(projectOwner),
                parseResult.GetValue(projectNumber),
                parseResult.GetValue(projectTitle),
                parseResult.GetValue(noLinkRepository),
                WasSpecified(parseResult, noLinkRepository),
                parseResult.GetValue(configPath),
                parseResult.GetValue(check),
                parseResult.GetValue(backend),
                parseResult.GetValue(localPath),
                parseResult.GetValue(statuses) is { Length: > 0 } statusValues ? statusValues : null,
                parseResult.GetValue(priorities) is { Length: > 0 } priorityValues ? priorityValues : null,
                parseResult.GetValue(createView),
                parseResult.GetValue(skipIssueForms),
                parseResult.GetValue(publishIssueForms),
                parseResult.GetValue(trustedCommentAuthor) is { Length: > 0 } trustedValues
                    ? trustedValues
                    : null,
                parseResult.GetValue(defaultAgent),
                WasSpecified(parseResult, defaultAgent),
                parseResult.GetValue(contextApprover) is { Length: > 0 } approverValues
                    ? approverValues
                    : null),
            parseResult.GetValue(json),
            parseResult.GetValue(yes),
            cancellationToken));
        return command;
    }

    /// <summary>
    /// Offers the authenticated login both context authorities during interactive GitHub setup:
    /// trusted comment author (own comments count as approved) and context approver (+1/-1
    /// reactions decide other people's pending comments).
    ///
    /// One question for both, deliberately: for the maintainer running init they are two halves of
    /// the same intent — "my judgement decides what an agent reads" — and a second prompt would
    /// read as ceremony. The configuration keeps two separate lists, so a team that wants the
    /// authorities split still edits them independently.
    ///
    /// Asked here rather than left to the configuration file because the alternative is
    /// discovering the settings after being made to move an approval field for a comment you
    /// wrote yourself. The default is no, and the prompt carries the consequence rather than
    /// deferring it to documentation: naming an author also accepts every edit a write-access
    /// collaborator makes to that author's comments.
    ///
    /// Silent when the answer cannot be a considered one — JSON, --yes, redirected input, an
    /// explicit flag for either list, a non-GitHub backend, or an identity that could not be
    /// resolved.
    /// </summary>
    /// <summary>
    /// Offers automatic cross-agent handoff during interactive setup: when an agent runs out of
    /// usage mid-item, its work continues under another installed agent instead of waiting out
    /// the quota — a core reason to pool several local AI subscriptions through Wrighty.
    ///
    /// Asked only when more than one supported agent is installed; with a single vendor there is
    /// no viable target and the question would be noise. The prompt defaults to yes, unlike the
    /// authority offer above: the consent that matters — which vendors may run this repository's
    /// work — was already given by installing and configuring the agents, and what remains is a
    /// preference about spending another subscription's quota automatically.
    ///
    /// Silent when the answer cannot be a considered one — JSON, --yes, redirected input, or
    /// check-only. Applied only when a new configuration is created; an existing configuration
    /// keeps whatever it says.
    /// </summary>
    private async Task<TrackerInitializationRequest> OfferCrossAgentHandoffAsync(
        TrackerInitializationRequest request,
        bool json,
        bool yes,
        CancellationToken cancellationToken)
    {
        if (request.CheckOnly || json || yes || isInputRedirected())
            return request;
        var installed = runtimes?.Snapshot().InstalledAgents ?? [];
        if (installed.Count < 2)
            return request;
        // Same rule as the authority offer: only settings the configuration has no opinion on
        // are offered, so a rerun never re-asks — and a bare Enter on this [Y/n] prompt can
        // never flip a recovery policy someone deliberately configured.
        var existing = await TryLoadExistingConfigurationAsync(request, cancellationToken);
        if (existing?.Worker?.UsageFailure is not null)
            return request;

        await output.WriteLineAsync(
            $"{JoinAgentNames(installed)} are installed. When one runs out of usage mid-item, " +
            "Wrighty can hand the work to another of them automatically: a new session in the " +
            "same retained workspace, briefed with a bounded summary of the previous run.");
        await output.WriteAsync(
            "Hand work to another installed agent automatically when one runs out of usage? " +
            "[Y/n] ");
        var answer = await input.ReadLineAsync(cancellationToken);
        var declined = string.Equals(answer, "n", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer, "no", StringComparison.OrdinalIgnoreCase);
        return declined ? request : request with { AllowCrossAgentHandoff = true };
    }

    /// <summary>
    /// The configuration init would be rerun over, or null for a first-time init.
    ///
    /// Prompts resolve the backend from this first and the request second, exactly as the
    /// initialization service does: a rerun in a configured repository keeps that backend, so
    /// backend-specific questions must follow it rather than what the folder looks like. Without
    /// that, a Local Markdown store in a repository with a GitHub remote is asked GitHub-only
    /// questions — approval authorities its backend has no concept of.
    /// </summary>
    private async Task<TrackerConfig?> TryLoadExistingConfigurationAsync(
        TrackerInitializationRequest request,
        CancellationToken cancellationToken)
    {
        if (configLoader is not ITrackerConfigStore store)
            return null;
        return await store.TryLoadPathAsync(
            store.ResolvePath(workingDirectory, request.ConfigPath), cancellationToken);
    }

    private async Task<TrackerInitializationRequest> OfferContextAuthorityAsync(
        TrackerInitializationRequest request,
        bool json,
        bool yes,
        CancellationToken cancellationToken)
    {
        if (request.TrustedCommentAuthors is not null ||
            request.ContextApprovers is not null ||
            request.CheckOnly ||
            json ||
            yes ||
            isInputRedirected() ||
            viewerIdentity is null)
            return request;
        var existing = await TryLoadExistingConfigurationAsync(request, cancellationToken);
        if (!string.Equals(
                existing?.Backend ?? request.Backend ?? "github",
                "github",
                StringComparison.OrdinalIgnoreCase))
            return request;
        // Already decided: a rerun adopts settings the configuration has no opinion on, and
        // re-asking a recorded one would invite an accidental change.
        if (existing is not null &&
            (existing.TrustedCommentAuthors.Count > 0 || existing.ContextApprovers.Count > 0))
            return request;

        var login = await viewerIdentity.GetLoginAsync(
            request.GitHubHost ?? "github.com", cancellationToken);
        if (string.IsNullOrWhiteSpace(login))
            return request;

        await output.WriteLineAsync(
            $"Comments by '{login}' can count as approved without a separate approval step, and " +
            $"'{login}' can decide other people's pending comments with reactions: a +1 includes " +
            "one, a -1 excludes it.");
        await output.WriteLineAsync(
            "Anyone you grant write access to this repository can edit that author's comments, and " +
            "the edited text would be approved too.");
        await output.WriteAsync(
            $"Trust '{login}' as comment author and context approver? [y/N] ");
        var answer = await input.ReadLineAsync(cancellationToken);
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
            ? request with { TrustedCommentAuthors = [login], ContextApprovers = [login] }
            : request;
    }

    private async Task<int> ExecuteInitializationAsync(
        TrackerInitializationRequest request,
        bool json,
        bool yes,
        CancellationToken cancellationToken)
    {
        try
        {
            request = await ResolveInitializationDefaultAgentAsync(
                request,
                json,
                yes,
                cancellationToken);
            request = await OfferContextAuthorityAsync(request, json, yes, cancellationToken);
            request = await OfferCrossAgentHandoffAsync(request, json, yes, cancellationToken);
            var result = await initialization.InitializeAsync(
                workingDirectory,
                request,
                (plan, confirmationToken) => ConfirmInitializationAsync(
                    plan,
                    json,
                    yes,
                    confirmationToken),
                cancellationToken);
            var scaffold = await ScaffoldIssueFormsAsync(
                result,
                request,
                cancellationToken);
            result = scaffold.Result;
            result = await PublishIssueFormsAsync(
                result,
                request,
                scaffold.ManagedPaths,
                json,
                yes,
                cancellationToken);
            await writer.WriteInitializationAsync(
                result,
                request.CheckOnly,
                json,
                runtimes?.Snapshot());
            return 0;
        }
        catch (TrackerException exception)
        {
            return await writer.WriteErrorAsync(exception, json);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            return await writer.WriteErrorAsync(
                new TrackerException("UNEXPECTED_ERROR", exception.Message, innerException: exception),
                json);
        }
    }

    private async Task<TrackerInitializationRequest> ResolveInitializationDefaultAgentAsync(
        TrackerInitializationRequest request,
        bool json,
        bool yes,
        CancellationToken cancellationToken)
    {
        if (request.DefaultAgentSpecified)
            return ResolveExplicitDefaultAgent(request);
        if (request.CheckOnly || json || yes || isInputRedirected() ||
            runtimes is null || configLoader is not ITrackerConfigStore store)
        {
            return request;
        }

        var configPath = store.ResolvePath(workingDirectory, request.ConfigPath);
        var existing = await store.TryLoadPathAsync(configPath, cancellationToken);
        var snapshot = runtimes.Snapshot();
        if (existing is not null)
        {
            await WriteExistingDefaultAgentAsync(existing, snapshot);
            return request;
        }

        return await ResolveNewInitializationDefaultAgentAsync(
            request,
            snapshot,
            cancellationToken);
    }

    /// <summary>Agent display names as prose: "Claude and Codex", "Claude, Codex and Copilot".</summary>
    private static string JoinAgentNames(IReadOnlyList<AgentRuntime> agents)
    {
        var names = agents.Select(runtime => AgentDisplayName(runtime.Agent)).ToArray();
        return names.Length switch
        {
            0 => "No agent",
            1 => names[0],
            _ => $"{string.Join(", ", names[..^1])} and {names[^1]}"
        };
    }

    /// <summary>
    /// The agents this host can actually run, named with the executable each resolved to.
    ///
    /// Printed on every interactive init, including a rerun over an existing configuration where
    /// the numbered selection prompt does not appear. "Which agents does Wrighty see here?" is a
    /// question init is expected to answer, and naming only the configured default leaves an
    /// operator guessing whether the others were found at all.
    /// </summary>
    private async Task WriteDetectedAgentsAsync(AgentRuntimeSnapshot snapshot)
    {
        if (snapshot.InstalledAgents.Count == 0)
        {
            await output.WriteLineAsync("No supported local AI agent CLI was found on PATH.");
            await output.WriteLineAsync(
                "Supported executables: " +
                $"{string.Join(", ", snapshot.Agents.Select(value => value.ExecutableName))}.");
            return;
        }

        await output.WriteLineAsync("Local AI agent CLIs found:");
        var width = snapshot.InstalledAgents
            .Max(runtime => AgentDisplayName(runtime.Agent).Length);
        foreach (var runtime in snapshot.InstalledAgents)
        {
            await output.WriteLineAsync(
                $"  {AgentDisplayName(runtime.Agent).PadRight(width)}   {runtime.ExecutablePath}");
        }
    }

    private async Task WriteExistingDefaultAgentAsync(
        TrackerConfig existing,
        AgentRuntimeSnapshot snapshot)
    {
        await WriteDetectedAgentsAsync(snapshot);
        var configured = existing.EffectiveWorker.DefaultAgent;
        if (configured is null)
        {
            await output.WriteLineAsync(
                "Configured default worker agent: none. Rerun with --default-agent <agent> to change it.");
            return;
        }

        var state = snapshot.IsInstalled(configured) ? "installed" : "not installed locally";
        await output.WriteLineAsync(
            $"Configured default worker agent: {configured} ({state}). " +
            "Rerun with --default-agent <agent> to change it.");
    }

    private async Task<TrackerInitializationRequest> ResolveNewInitializationDefaultAgentAsync(
        TrackerInitializationRequest request,
        AgentRuntimeSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var installed = snapshot.InstalledAgents;
        if (installed.Count == 0)
        {
            await WriteNoInstalledAgentsAsync(snapshot);
            return request with { DefaultAgent = null, DefaultAgentSpecified = true };
        }

        await WriteInstalledAgentChoicesAsync(installed);
        var selected = await ReadDefaultAgentChoiceAsync(installed, cancellationToken);
        return request with
        {
            DefaultAgent = selected?.Agent,
            DefaultAgentSpecified = true
        };
    }

    private async Task WriteNoInstalledAgentsAsync(AgentRuntimeSnapshot snapshot)
    {
        await output.WriteLineAsync("No supported local AI agent CLI was found on PATH.");
        await output.WriteLineAsync(
            "Wrighty can still initialize the tracker, but autonomous worker runs will be unavailable.");
        await output.WriteLineAsync(
            $"Supported executables: {string.Join(", ", snapshot.Agents.Select(value => value.ExecutableName))}.");
        await output.WriteLineAsync("Default worker agent: none");
        await output.WriteLineAsync(
            "Install a supported agent CLI, then rerun wrighty init --default-agent <agent>.");
        await output.WriteLineAsync(
            "Install its Wrighty skill with wrighty skill install --agent auto.");
    }

    private async Task WriteInstalledAgentChoicesAsync(
        IReadOnlyList<AgentRuntime> installed)
    {
        await output.WriteLineAsync("Local AI agent CLIs found:");
        var width = Math.Max(
            installed.Max(runtime => AgentDisplayName(runtime.Agent).Length),
            "None".Length);
        for (var index = 0; index < installed.Count; index++)
        {
            var runtime = installed[index];
            await output.WriteLineAsync(
                $"  {index + 1}. {AgentDisplayName(runtime.Agent).PadRight(width)}   " +
                $"{runtime.ExecutablePath}");
        }
        await output.WriteLineAsync(
            $"  {installed.Count + 1}. {"None".PadRight(width)}   " +
            "leave worker.defaultAgent unset");
        await output.WriteAsync(
            $"Default worker agent [{AgentDisplayName(installed[0].Agent)}]: ");
    }

    private async Task<AgentRuntime?> ReadDefaultAgentChoiceAsync(
        IReadOnlyList<AgentRuntime> installed,
        CancellationToken cancellationToken)
    {
        var answer = await input.ReadLineAsync(cancellationToken);
        if (answer is null)
            throw new TrackerException(
                "INITIALIZATION_CANCELLED",
                "Default worker agent selection was cancelled; nothing was changed.",
                2);
        var choice = answer.Trim();
        if (choice.Length == 0)
            return installed[0];
        if (string.Equals(choice, "none", StringComparison.OrdinalIgnoreCase) ||
            choice == (installed.Count + 1).ToString())
            return null;
        if (int.TryParse(choice, out var number) &&
            number >= 1 &&
            number <= installed.Count)
            return installed[number - 1];

        return installed.FirstOrDefault(runtime =>
                   string.Equals(runtime.Agent, choice, StringComparison.OrdinalIgnoreCase))
               ?? throw new TrackerException(
                   "ARGUMENT_INVALID",
                   "Choose an installed agent by name or number, or choose none.",
                   2);
    }

    private TrackerInitializationRequest ResolveExplicitDefaultAgent(
        TrackerInitializationRequest request)
    {
        var value = request.DefaultAgent?.Trim().ToLowerInvariant();
        if (value == "none")
            return request with { DefaultAgent = null };
        if (value is not ("claude" or "codex" or "copilot" or "auto"))
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--default-agent must be claude, codex, copilot, auto, or none.",
                2);
        if (runtimes is null)
        {
            if (value == "auto")
                throw new TrackerException(
                    "AGENT_DISCOVERY_UNAVAILABLE",
                    "Automatic local agent discovery is not configured in this Wrighty host.",
                    7);
            return request with { DefaultAgent = value };
        }

        var snapshot = runtimes.Snapshot();
        if (value == "auto")
        {
            if (snapshot.InstalledAgents.Count != 1)
                throw new TrackerException(
                    "DEFAULT_AGENT_AMBIGUOUS",
                    $"--default-agent auto requires exactly one installed supported agent; found " +
                    $"{snapshot.InstalledAgents.Count}: " +
                    $"{string.Join(", ", snapshot.InstalledAgents.Select(runtime => runtime.Agent))}.",
                    2,
                    new Dictionary<string, object?>
                    {
                        ["installedAgents"] = snapshot.InstalledAgents
                            .Select(runtime => runtime.Agent)
                            .ToArray()
                    });
            return request with { DefaultAgent = snapshot.InstalledAgents[0].Agent };
        }

        var selected = snapshot.Find(value);
        if (selected is null)
            throw new TrackerException(
                "AGENT_UNSUPPORTED",
                $"This Wrighty build does not register a '{value}' agent adapter.",
                2);
        if (!selected.Installed)
            throw new TrackerException(
                "AGENT_NOT_INSTALLED",
                $"--default-agent {value} requires the '{selected.ExecutableName}' executable on PATH.",
                7,
                new Dictionary<string, object?>
                {
                    ["agent"] = value,
                    ["executable"] = selected.ExecutableName,
                    ["availableAgents"] = snapshot.InstalledAgents
                        .Select(runtime => runtime.Agent)
                        .ToArray()
                });
        return request with { DefaultAgent = value };
    }

    private static string AgentDisplayName(string agent) =>
        agent.Length == 0 ? "Agent" : $"{char.ToUpperInvariant(agent[0])}{agent[1..]}";

    private async Task<(TrackerInitializationResult Result, IReadOnlyList<string> ManagedPaths)> ScaffoldIssueFormsAsync(
        TrackerInitializationResult result,
        TrackerInitializationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CheckOnly ||
            !string.Equals(result.Config.Backend, "github", StringComparison.OrdinalIgnoreCase) ||
            forms is null)
        {
            return (result, []);
        }

        var actions = result.Actions.ToList();
        if (request.SkipIssueForms)
        {
            actions.Add("Wrighty worker issue-form creation was skipped by request.");
            return (result with { Actions = actions }, []);
        }

        var scaffold = await forms.ScaffoldAsync(
            workingDirectory,
            result.Config,
            request.Remote ?? "origin",
            cancellationToken);
        actions.AddRange(scaffold.Actions);
        return (result with { Changed = result.Changed || scaffold.ChangedPaths.Count > 0, Actions = actions }, scaffold.ManagedPaths);
    }

    private async Task<TrackerInitializationResult> PublishIssueFormsAsync(
        TrackerInitializationResult result,
        TrackerInitializationRequest request,
        IReadOnlyList<string> managedPaths,
        bool json,
        bool yes,
        CancellationToken cancellationToken)
    {
        if (request.CheckOnly || request.SkipIssueForms || managedPaths.Count == 0)
        {
            return result;
        }

        var pendingPaths = formPublisher is null
            ? managedPaths
            : await formPublisher.FindPendingAsync(
                workingDirectory,
                managedPaths,
                cancellationToken);
        if (pendingPaths.Count == 0)
        {
            return result;
        }

        var publish = request.PublishIssueForms;
        if (!publish && !yes && !json && !isInputRedirected())
        {
            await output.WriteAsync(
                "Stage, commit, and push the pending Wrighty issue-form changes? [y/N] ");
            var answer = await input.ReadLineAsync(cancellationToken);
            publish = string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
        }

        var actions = result.Actions.ToList();
        if (!publish)
        {
            actions.Add(
                "Wrighty issue forms remain uncommitted. Review and publish them, or rerun init with --yes --publish-issue-forms.");
            return result with { Actions = actions };
        }

        if (formPublisher is null)
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                "Automatic issue-form publication is unavailable in this Wrighty host.",
                3);
        }

        actions.AddRange(await formPublisher.PublishAsync(
            workingDirectory,
            pendingPaths,
            request.Remote ?? "origin",
            cancellationToken));
        return result with { Actions = actions };
    }

    private async Task ConfirmInitializationAsync(
        TrackerInitializationPlan plan,
        bool json,
        bool yes,
        CancellationToken cancellationToken)
    {
        if (yes)
        {
            return;
        }

        if (!json)
        {
            await WriteInitializationPlanAsync(plan);
        }

        if (json || isInputRedirected())
        {
            throw new TrackerException(
                "INIT_CONFIRMATION_REQUIRED",
                "Initialization requires --yes in JSON or non-interactive mode. No changes were made.",
                2,
                new Dictionary<string, object?> { ["plan"] = plan });
        }

        await output.WriteAsync("Continue? [y/N] ");
        var answer = await input.ReadLineAsync(cancellationToken);
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "INIT_CONFIRMATION_REQUIRED",
                "Initialization was cancelled. No changes were made.",
                2,
                new Dictionary<string, object?> { ["plan"] = plan });
        }
    }

    private async Task WriteInitializationPlanAsync(TrackerInitializationPlan plan)
    {
        await output.WriteLineAsync("Wrighty initialization plan:");
        await output.WriteLineAsync($"Backend: {plan.Backend}");
        await WriteInitializationTargetAsync(plan);
        await output.WriteLineAsync($"Configuration: {(plan.CreateConfiguration ? "create" : "use")} {plan.ConfigPath}");
        if (plan.WorkerDefaultAgentIncluded)
            await output.WriteLineAsync(
                $"Worker default agent: {plan.WorkerDefaultAgent ?? "none"}");
        await WriteInitializationStepsAsync("Planned actions:", plan.Steps);
        if (plan.ManualFollowUp.Count > 0)
        {
            await WriteInitializationStepsAsync(
                "Manual follow-up after initialization:",
                plan.ManualFollowUp);
        }

        await WriteInitializationOverridesAsync(plan);
        await output.WriteLineAsync("  --check                  Validate without writing");
        await output.WriteLineAsync("  --yes                    Execute this plan without prompting");
    }

    private async Task WriteInitializationTargetAsync(TrackerInitializationPlan plan)
    {
        if (plan.Repository is not null)
        {
            await output.WriteLineAsync($"Repository: {plan.Repository}");
            var project = plan.CreateProject
                ? $"create '{plan.ProjectTitle}' for {plan.ProjectOwner}"
                : $"use {plan.ProjectOwner}/{plan.ProjectNumber} ({plan.ProjectTitle})";
            await output.WriteLineAsync($"Project: {project}");
        }
        else
        {
            await output.WriteLineAsync($"Store: {plan.LocalStorePath}");
        }
    }

    private async Task WriteInitializationStepsAsync(
        string heading,
        IReadOnlyList<string> steps)
    {
        await output.WriteLineAsync(heading);
        foreach (var step in steps)
        {
            await output.WriteLineAsync($"- {step}");
        }
    }

    private async Task WriteInitializationOverridesAsync(TrackerInitializationPlan plan)
    {
        await output.WriteLineAsync("Common overrides:");
        await output.WriteLineAsync(
            "  --default-agent AGENT    Set claude, codex, copilot, auto, or none");
        if (string.Equals(plan.Backend, "github", StringComparison.OrdinalIgnoreCase))
        {
            if (plan.CreateConfiguration)
            {
                await output.WriteLineAsync("  --backend local-markdown  Initialize a Local Markdown tracker instead");
                await output.WriteLineAsync("  --project-number N       Use an existing Project");
                await output.WriteLineAsync("  --project-title TITLE    Change the new Project title");
                await output.WriteLineAsync("  --no-link-repository     Skip repository linking");
            }
            else
            {
                await output.WriteLineAsync("  --create-view            Create Wrighty Board and Wrighty Attention views when missing");
            }
            await output.WriteLineAsync("  --skip-issue-forms       Skip local worker issue forms");
            await output.WriteLineAsync("  --publish-issue-forms    Commit and push only Wrighty issue forms");
        }
        else
        {
            if (plan.CreateConfiguration)
            {
                await output.WriteLineAsync("  --backend github --repository OWNER/REPOSITORY");
                await output.WriteLineAsync("                            Initialize a GitHub tracker instead");
            }
            await output.WriteLineAsync("  --local-path PATH        Change the Local Markdown store path");
            await output.WriteLineAsync("  --status NAME            Configure a workflow status; repeat as needed");
            await output.WriteLineAsync("  --priority NAME          Configure a priority; repeat as needed");
        }
    }

    /*
     * All non-init commands require an existing configuration. Initialization has a dedicated
     * path above because it can create that configuration.
     */

    private Command BuildListCommand()
    {
        var status = new Option<string?>("--status")
        {
            Description = "Only list items with this workflow status."
        };
        var limit = new Option<int?>("--limit")
        {
            Description = "Maximum number of items to return."
        };
        var compact = new Option<bool>("--compact")
        {
            Description = "Emit stable token-efficient output."
        };
        var json = JsonOption();
        var archived = new Option<bool>("--archived")
        {
            Description = "List archived items only."
        };
        var includeArchived = new Option<bool>("--include-archived")
        {
            Description = "List active and archived items."
        };
        var fields = FieldOption("Only list items whose custom field exactly matches name=value; repeat for AND semantics.");
        var command = new Command("list", "List work items from the configured tracker");
        command.Options.Add(status);
        command.Options.Add(limit);
        command.Options.Add(compact);
        command.Options.Add(archived);
        command.Options.Add(includeArchived);
        command.Options.Add(fields);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                if (parseResult.GetValue(compact) && parseResult.GetValue(json))
                {
                    throw new TrackerException(
                        "ARGUMENT_INVALID",
                        "--compact and --json cannot be used together.",
                        2);
                }

                if (parseResult.GetValue(archived) && parseResult.GetValue(includeArchived))
                {
                    throw new TrackerException(
                        "ARGUMENT_INVALID",
                        "--archived and --include-archived cannot be used together.",
                        2);
                }

                var items = await tracker.ListOperationalAsync(
                    config,
                    new ListWorkItemsRequest(
                        parseResult.GetValue(status),
                        parseResult.GetValue(limit),
                        parseResult.GetValue(archived)
                            ? ArchiveScope.Archived
                            : parseResult.GetValue(includeArchived)
                                ? ArchiveScope.All
                                : ArchiveScope.Active,
                        ParseFields(parseResult.GetValue(fields), allowDeletion: false)
                            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal)),
                    cancellationToken);
                await writer.WriteOperationalItemsAsync(
                    items,
                    parseResult.GetValue(compact),
                    parseResult.GetValue(json),
                    id => tracker.FormatShort(config, id));
            },
            cancellationToken));
        return command;
    }

    private Command BuildApproveCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var command = new Command(
            "approve",
            "Re-approve an item's context: reset to needs-review, approve, and report the " +
            "resulting revision");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                // Both moves, deliberately: approval is an instant — the batch cutoff is the
                // moment the field lands on Approved — and re-selecting the value it already
                // holds moves nothing. The cycle is what picks up pending comments.
                var service = contextApproval
                    ?? throw new TrackerException(
                        "CONTEXT_APPROVAL_UNSUPPORTED",
                        $"The '{config.Backend}' backend cannot assemble an approved context.",
                        3);
                // Report what the cycle produced through the same read a launch would perform, so
                // the digest and counts shown are exactly what the next run acts on.
                var result = await service.ApproveAsync(config, id, cancellationToken);
                await writer.WriteApprovedContextAsync(
                    id,
                    result,
                    config.EffectiveWorker.EffectiveContext.ToLimits(),
                    parseResult.GetValue(json));
            },
            cancellationToken));
        return command;
    }

    private Command BuildApprovalWorkflowCommand()
    {
        var idArgument = WorkItemIdArgument();
        var invalidate = new Command(
            "invalidate",
            "Reset an item's base context approval to needs-review after a title or body edit");
        invalidate.Arguments.Add(idArgument);
        invalidate.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            json: false,
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var service = contextApproval
                    ?? throw new TrackerException(
                        "CONTEXT_APPROVAL_UNSUPPORTED",
                        $"The '{config.Backend}' backend has no context approval surface.",
                        3);
                var disposition = await service.InvalidateAsync(config, id, cancellationToken);
                await output.WriteLineAsync(disposition ==
                    ContextApprovalInvalidationDisposition.ResetToNeedsReview
                        ? $"{id} context approval reset to needs-review."
                        : $"{id} retained its newer context approval; no reset was needed.");
            },
            cancellationToken));

        var approval = new Command(
            "approval",
            "Manage context approval from trusted automation workflows");
        approval.Subcommands.Add(invalidate);
        return approval;
    }

    private Command BuildContextCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var prompt = new Option<bool>("--prompt")
        {
            Description = "Print the prompt a fresh agent launch would be given, in full."
        };
        var revision = new Option<string?>("--revision")
        {
            Description = "Serve only this exact context revision; refuse if the approved context " +
                          "has moved since. For an agent recovering a context it has lost."
        };
        var command = new Command(
            "context",
            "Show what an unattended agent would be given for one item, or why it would be refused");
        command.Arguments.Add(idArgument);
        command.Options.Add(prompt);
        command.Options.Add(revision);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var provider = executionContextProviders?.Invoke(config)
                    ?? throw new TrackerException(
                        ExecutionContextResult.Codes.Unsupported,
                        $"The '{config.Backend}' backend cannot assemble an approved context.",
                        3);

                // Read-only and explicitly diagnostic: this never claims, launches, or mutates.
                // The configured limits, so what this reports is what a launch would apply rather
                // than a default the repository has overridden.
                var limits = config.EffectiveWorker.EffectiveContext.ToLimits();
                var result = await provider.GetAsync(
                    config, id, ContextReadPurpose.Diagnostics, limits, cancellationToken);

                // A pinned revision serves one thing only: the context this run was launched with.
                //
                // The point is what it refuses. An agent that has lost its context needs the
                // requirements back, but it must not be able to acquire a *different* set — a
                // newer approval, an edited description, comments nobody has decided on. Serving
                // only on an exact digest match means the answer is either the approved content
                // this run already had, or nothing.
                //
                // A mismatch is not an error to work around. It means the approved context moved
                // while the run was in flight, so what the agent holds is superseded and the run
                // should stop rather than continue against requirements no one approved for it.
                // A pinned revision serves one thing only: the context this run was launched
                // with, or nothing. See PinnedContextRetrieval for why that refusal is the point.
                if (parseResult.GetValue(revision) is { Length: > 0 } pinned)
                {
                    var served = PinnedContextRetrieval.Serve(result, pinned);
                    if (!served.Served)
                        throw new TrackerException(
                            served.RefusalCode!, served.RefusalMessage!, 5);

                    await output.WriteLineAsync(ExecutionPromptRenderer.ForFreshLaunch(
                        served.Snapshot!, WorkerPrompt.OperatingInstructions(id)));
                    return;
                }

                // --prompt prints the approved content, which the summary deliberately does not:
                // the summary is for routine checking and lands in terminals and logs, whereas this
                // is an operator explicitly asking to read what an agent would be told. Refusals
                // still print as a summary, because there is no prompt to show for a run that
                // would not start.
                if (parseResult.GetValue(prompt) && result.Snapshot is { } approved)
                {
                    await output.WriteLineAsync(ExecutionPromptRenderer.ForFreshLaunch(
                        approved, WorkerPrompt.OperatingInstructions(id)));
                    return;
                }

                await writer.WriteApprovedContextAsync(id, result, limits, parseResult.GetValue(json));
            },
            cancellationToken));
        return command;
    }

    private Command BuildGetCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var command = new Command("get", "Get one tracked work item");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var item = await tracker.GetOperationalAsync(config, id, cancellationToken);
                var workspaceStatus = item.Session?.WorkspacePath is { } workspacePath &&
                                      workspaceInventory is { } inventory
                    ? await inventory.GetStatusAsync(
                        workingDirectory, workspacePath, item.Session.Branch, cancellationToken)
                    : null;
                await writer.WriteOperationalDetailAsync(
                    item,
                    parseResult.GetValue(json),
                    value => tracker.FormatShort(config, value),
                    workspaceStatus);
            },
            cancellationToken));
        return command;
    }

    private Command BuildStatusCommand()
    {
        var json = JsonOption();
        var command = new Command(
            "status",
            "Show what needs attention: blocked, retained, active, and queued items");
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            parseResult.GetValue(json),
            config => StatusAsync(config, parseResult.GetValue(json), cancellationToken),
            cancellationToken));
        return command;
    }

    private async Task StatusAsync(TrackerConfig config, bool json, CancellationToken cancellationToken)
    {
        var items = await tracker.ListOperationalAsync(
            config,
            new ListWorkItemsRequest(null, null, ArchiveScope.Active),
            cancellationToken);
        // Bounded, machine-local git probes only for items with a retained worktree in the
        // needs-attention/completed/paused groups — the same posture as `wrighty workspaces`.
        var workspaceStatuses = new Dictionary<string, WorkspaceStatusResult>(StringComparer.Ordinal);
        if (workspaceInventory is { } inventory)
        {
            foreach (var value in items)
            {
                if (value.Session is not { HasRecordedWorktree: true } session)
                    continue;
                if (value.OperationalStatus is not (OperationalStatuses.NeedsAttention
                    or OperationalStatuses.Completed
                    or OperationalStatuses.PausedSession))
                    continue;
                workspaceStatuses[value.Item.Id.Value] = await inventory.GetStatusAsync(
                    workingDirectory, session.WorkspacePath!, session.Branch, cancellationToken);
            }
        }

        await writer.WriteStatusAsync(
            items,
            workspaceStatuses,
            config.Worker?.Completion?.Integration,
            json,
            id => tracker.FormatShort(config, id),
            new StatusOutputContext(
                (await providerCapacity.ListAsync(cancellationToken))
                    .Where(value => value.State != ProviderCapacityState.Available)
                    .ToArray(),
                config.SourcePath is null
                    ? []
                    : await workerInstances.ListAsync(config.SourcePath, cancellationToken),
                config.SourceRevision ??
                (config.SourcePath is { } configurationPath && File.Exists(configurationPath)
                    ? await RepositoryConfigurationService.RevisionAsync(
                        configurationPath,
                        cancellationToken)
                    : null)));
    }

    private Command BuildCreateCommand()
    {
        var title = new Option<string?>("--title")
        {
            Description = "Required single-line work-item title."
        };
        var body = new Option<string?>("--body")
        {
            Description = "Markdown work-item body."
        };
        var bodyFile = new Option<string?>("--body-file")
        {
            Description = "Read the markdown body from a file, or from stdin with '-'."
        };
        var status = new Option<string?>("--status")
        {
            Description = "Initial workflow status; defaults to defaultPickFrom."
        };
        var priority = new Option<string?>("--priority")
        {
            Description = "Initial work-item priority."
        };
        var creationAttemptId = new Option<string?>("--creation-attempt-id")
        {
            Description = "UUID identifying this logical creation attempt across retries."
        };
        var auto = new Option<bool>("--auto") { Description = "Opt this item into autonomous worker processing." };
        var workerAgent = new Option<string?>("--agent") { Description = "Preferred worker vendor: claude, codex, or copilot." };
        var createProfile = ExecutionProfileOption();
        var fields = FieldOption("Set a Local Markdown custom field as name=value; repeat for multiple fields.");
        var json = JsonOption();
        var command = new Command("create", "Create and track a real work item");
        command.Options.Add(title);
        command.Options.Add(body);
        command.Options.Add(bodyFile);
        command.Options.Add(status);
        command.Options.Add(priority);
        command.Options.Add(creationAttemptId);
        command.Options.Add(auto);
        command.Options.Add(workerAgent);
        command.Options.Add(createProfile);
        command.Options.Add(fields);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var titleValue = parseResult.GetValue(title);
                if (titleValue is null)
                {
                    throw new TrackerException(
                        "ARGUMENT_INVALID",
                        "--title is required.",
                        2);
                }

                var bodyValue = await ReadBodyAsync(
                    parseResult.GetValue(body),
                    parseResult.GetValue(bodyFile),
                    cancellationToken);

                var result = await tracker.CreateAsync(
                    config,
                    new CreateWorkItemRequest(
                        titleValue,
                        bodyValue ?? string.Empty,
                        parseResult.GetValue(status),
                        parseResult.GetValue(priority),
                        ParseFields(parseResult.GetValue(fields), allowDeletion: true),
                        parseResult.GetValue(auto),
                        parseResult.GetValue(workerAgent),
                        NormalizeExecutionProfile(parseResult.GetValue(createProfile))),
                    parseResult.GetValue(creationAttemptId),
                    cancellationToken);
                await writer.WriteCreateAsync(
                    result,
                    parseResult.GetValue(json),
                    id => tracker.FormatShort(config, id));
            },
            cancellationToken));
        return command;
    }

    private Command BuildImportCommand()
    {
        var paths = new Argument<string[]>("path")
        {
            Description = "Markdown file or directory to import; repeat for multiple paths."
        };
        var recursive = new Option<bool>("--recursive") { Description = "Search directories recursively." };
        var archive = new Option<bool>("--archive") { Description = "Import into the archive." };
        var move = new Option<bool>("--move") { Description = "Delete sources only after the complete batch is verified and committed." };
        var inPlace = new Option<bool>("--in-place") { Description = "Normalize unmanaged Markdown already below the configured local items or archive directory." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the import plan without writing files." };
        var maps = new Option<string[]>("--map") { Description = "Map a managed field to a source key, for example status=state." };
        var forceStatus = new Option<string?>("--force-status") { Description = "Use one configured status for every imported file." };
        var creationAttemptId = new Option<string?>("--creation-attempt-id") { Description = "UUID identifying this GitHub import across retries." };
        var preserveCustomFields = new Option<bool>("--preserve-custom-fields") { Description = "Preserve custom YAML in the shared fenced body block for GitHub." };
        var fromStore = new Option<string?>("--from-store") { Description = "Copy a configured tracker corpus; currently local-markdown to GitHub." };
        var includeArchived = new Option<bool>("--include-archived") { Description = "Include archived Local Markdown items in whole-store import." };
        var mapStatus = new Option<string[]>("--map-status") { Description = "Map a source Status to a GitHub Status as source=target; repeatable." };
        var mapPriority = new Option<string[]>("--map-priority") { Description = "Map a source Priority to a GitHub Priority as source=target; repeatable." };
        var copyAsReleased = new Option<bool>("--copy-as-released") { Description = "Copy content from claimed source items without claim, session, or workspace state." };
        var allowUnmappedReferences = new Option<bool>("--allow-unmapped-references") { Description = "Preserve ambiguous local #N references and record warnings in the manifest." };
        var stopOnError = new Option<bool>("--stop-on-error") { Description = "Stop whole-store execution after the first incomplete item." };
        var manifest = new Option<string?>("--manifest") { Description = "Whole-store import manifest path." };
        var json = JsonOption();
        var command = new Command(
            "import",
            "Create backend-native identities from Markdown documents or an explicit source store");
        command.Arguments.Add(paths);
        command.Options.Add(recursive);
        command.Options.Add(archive);
        command.Options.Add(move);
        command.Options.Add(inPlace);
        command.Options.Add(dryRun);
        command.Options.Add(maps);
        command.Options.Add(forceStatus);
        command.Options.Add(creationAttemptId);
        command.Options.Add(preserveCustomFields);
        command.Options.Add(fromStore);
        command.Options.Add(includeArchived);
        command.Options.Add(mapStatus);
        command.Options.Add(mapPriority);
        command.Options.Add(copyAsReleased);
        command.Options.Add(allowUnmappedReferences);
        command.Options.Add(stopOnError);
        command.Options.Add(manifest);
        command.Options.Add(json);
        var options = new ImportCommandOptions(
            paths, recursive, archive, move, inPlace, dryRun, maps, forceStatus,
            creationAttemptId, preserveCustomFields, fromStore, includeArchived,
            mapStatus, mapPriority, copyAsReleased, allowUnmappedReferences,
            stopOnError, manifest, json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteImportCommandAsync(parseResult, options, cancellationToken));
        return command;
    }

    private Task<int> ExecuteImportCommandAsync(
        ParseResult parseResult,
        ImportCommandOptions options,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            parseResult.GetValue(options.Json),
            config => ExecuteImportAsync(config, parseResult, options, cancellationToken),
            cancellationToken);

    private async Task ExecuteImportAsync(
        TrackerConfig config,
        ParseResult parseResult,
        ImportCommandOptions options,
        CancellationToken cancellationToken)
    {
        var fromStore = parseResult.GetValue(options.FromStore);
        if (fromStore is not null)
        {
            await ExecuteWholeStoreImportAsync(
                config, parseResult, options, fromStore, cancellationToken);
            return;
        }

        RejectWholeStoreOptionsWithoutSource(parseResult, options);
        var paths = (parseResult.GetValue(options.Paths) ?? [])
            .Select(path => Path.GetFullPath(path, workingDirectory))
            .ToArray();
        if (tracker.Backend(config) is ILocalMarkdownImportBackend localImporter)
        {
            await ExecuteLocalImportAsync(
                config, parseResult, options, paths, localImporter, cancellationToken);
            return;
        }

        await ExecuteGitHubImportAsync(
            config, parseResult, options, paths, cancellationToken);
    }

    private async Task ExecuteWholeStoreImportAsync(
        TrackerConfig config,
        ParseResult parseResult,
        ImportCommandOptions options,
        string fromStore,
        CancellationToken cancellationToken)
    {
        ValidateWholeStoreArguments(parseResult, options, fromStore);
        var service = new WholeStoreImportService(tracker);
        var summary = await service.RunAsync(
            config,
            new WholeStoreImportOptions(
                parseResult.GetValue(options.IncludeArchived),
                parseResult.GetValue(options.DryRun),
                parseResult.GetValue(options.CopyAsReleased),
                parseResult.GetValue(options.AllowUnmappedReferences),
                parseResult.GetValue(options.StopOnError),
                ParseValueMappings(parseResult.GetValue(options.MapStatus), "--map-status"),
                ParseValueMappings(parseResult.GetValue(options.MapPriority), "--map-priority"),
                parseResult.GetValue(options.Manifest) is { } manifest
                    ? Path.GetFullPath(manifest, workingDirectory)
                    : null),
            cancellationToken);
        await writer.WriteWholeStoreImportAsync(summary, parseResult.GetValue(options.Json));
        if (summary.Failed > 0)
        {
            throw new TrackerException(
                "IMPORT_INCOMPLETE",
                $"{summary.Failed} whole-store import item(s) remain incomplete; rerun with manifest '{summary.ManifestPath}'.",
                10,
                new Dictionary<string, object?>
                {
                    ["manifestPath"] = summary.ManifestPath,
                    ["failed"] = summary.Failed
                });
        }
    }

    private static void ValidateWholeStoreArguments(
        ParseResult parseResult,
        ImportCommandOptions options,
        string fromStore)
    {
        if (!string.Equals(fromStore, "local-markdown", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                $"Unsupported --from-store value '{fromStore}'; expected local-markdown.",
                3);
        }
        if ((parseResult.GetValue(options.Paths) ?? []).Length > 0 ||
            parseResult.GetValue(options.Recursive) ||
            parseResult.GetValue(options.Archive) ||
            parseResult.GetValue(options.Move) ||
            parseResult.GetValue(options.InPlace) ||
            parseResult.GetValue(options.ForceStatus) is not null ||
            (parseResult.GetValue(options.Maps) ?? []).Length > 0 ||
            parseResult.GetValue(options.CreationAttemptId) is not null ||
            parseResult.GetValue(options.PreserveCustomFields))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--from-store cannot be combined with document paths or standalone import options.",
                2);
        }
    }

    private static void RejectWholeStoreOptionsWithoutSource(
        ParseResult parseResult,
        ImportCommandOptions options)
    {
        if (parseResult.GetValue(options.IncludeArchived) ||
            (parseResult.GetValue(options.MapStatus) ?? []).Length > 0 ||
            (parseResult.GetValue(options.MapPriority) ?? []).Length > 0 ||
            parseResult.GetValue(options.CopyAsReleased) ||
            parseResult.GetValue(options.AllowUnmappedReferences) ||
            parseResult.GetValue(options.StopOnError) ||
            parseResult.GetValue(options.Manifest) is not null)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "Whole-store options require --from-store local-markdown.",
                2);
        }
    }

    private async Task ExecuteGitHubImportAsync(
        TrackerConfig config,
        ParseResult parseResult,
        ImportCommandOptions options,
        string[] paths,
        CancellationToken cancellationToken)
    {
        ValidateGitHubImportArguments(config, parseResult, options, paths);
        var source = await MarkdownImportPlanner.PlanFileAsync(
            paths[0],
            ParseMappings(parseResult.GetValue(options.Maps)),
            parseResult.GetValue(options.ForceStatus),
            cancellationToken);
        if (source.CustomFieldNames.Count > 0 &&
            !parseResult.GetValue(options.PreserveCustomFields))
        {
            throw new TrackerException(
                "IMPORT_FIELDS_UNSUPPORTED",
                $"GitHub import source contains unsupported custom fields: {string.Join(", ", source.CustomFieldNames)}. Use --preserve-custom-fields to encode them in the shared round-trip block.",
                3,
                new Dictionary<string, object?>
                {
                    ["path"] = source.Path,
                    ["fields"] = source.CustomFieldNames
                });
        }
        var body = source.CustomFieldsYaml is not null
            ? MarkdownImportPlanner.AppendCustomFieldBlock(source.Body, source.CustomFieldsYaml)
            : source.Body;
        if (parseResult.GetValue(options.DryRun))
        {
            await writer.WritePortableImportPlanAsync(
                source,
                source.Status ?? config.DefaultPickFrom,
                parseResult.GetValue(options.Json));
            return;
        }

        var created = await tracker.CreateAsync(
            config,
            new CreateWorkItemRequest(source.Title, body, source.Status, source.Priority),
            parseResult.GetValue(options.CreationAttemptId),
            cancellationToken);
        await writer.WriteCreateAsync(
            created,
            parseResult.GetValue(options.Json),
            id => tracker.FormatShort(config, id));
    }

    private static void ValidateGitHubImportArguments(
        TrackerConfig config,
        ParseResult parseResult,
        ImportCommandOptions options,
        string[] paths)
    {
        if (!string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                $"Import is not supported by backend '{config.Backend}'.",
                3);
        }
        if (parseResult.GetValue(options.InPlace))
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                "--in-place is supported only by the Local Markdown backend.",
                3);
        }
        if (parseResult.GetValue(options.Move) ||
            parseResult.GetValue(options.Archive) ||
            parseResult.GetValue(options.Recursive) ||
            paths.Length != 1 ||
            Directory.Exists(paths.SingleOrDefault()))
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                "The first GitHub import increment accepts exactly one Markdown file and is copy-only; --move, --archive, directories, and --recursive are not supported.",
                3);
        }
    }

    private async Task ExecuteLocalImportAsync(
        TrackerConfig config,
        ParseResult parseResult,
        ImportCommandOptions options,
        string[] paths,
        ILocalMarkdownImportBackend importer,
        CancellationToken cancellationToken)
    {
        var result = await importer.ImportAsync(
            config,
            new LocalMarkdownImportRequest(
                paths,
                parseResult.GetValue(options.Recursive),
                parseResult.GetValue(options.Archive),
                parseResult.GetValue(options.Move),
                parseResult.GetValue(options.DryRun),
                ParseMappings(parseResult.GetValue(options.Maps)),
                parseResult.GetValue(options.ForceStatus),
                parseResult.GetValue(options.InPlace)),
            cancellationToken);
        await writer.WriteImportAsync(result, parseResult.GetValue(options.Json));
    }

    private sealed record ImportCommandOptions(
        Argument<string[]> Paths,
        Option<bool> Recursive,
        Option<bool> Archive,
        Option<bool> Move,
        Option<bool> InPlace,
        Option<bool> DryRun,
        Option<string[]> Maps,
        Option<string?> ForceStatus,
        Option<string?> CreationAttemptId,
        Option<bool> PreserveCustomFields,
        Option<string?> FromStore,
        Option<bool> IncludeArchived,
        Option<string[]> MapStatus,
        Option<string[]> MapPriority,
        Option<bool> CopyAsReleased,
        Option<bool> AllowUnmappedReferences,
        Option<bool> StopOnError,
        Option<string?> Manifest,
        Option<bool> Json);

    private Command BuildAdoptCommand()
    {
        var references = new Argument<string[]>("issue-ref")
        {
            Description = "Existing GitHub issue number, owner/repository#number, or issue URL."
        };
        var status = new Option<string?>("--status")
        {
            Description = "Set Status; new Project items otherwise use defaultPickFrom."
        };
        var priority = new Option<string?>("--priority")
        {
            Description = "Set Priority; otherwise preserve it or leave it unset."
        };
        var auto = new Option<bool>("--auto")
        {
            Description = "Explicitly authorize autonomous worker processing."
        };
        var agent = new Option<string?>("--agent")
        {
            Description = "Preferred worker vendor; does not imply --auto."
        };
        var json = JsonOption();
        var command = new Command(
            "adopt",
            "Enroll existing backend-native objects while preserving their identities");
        command.Arguments.Add(references);
        command.Options.Add(status);
        command.Options.Add(priority);
        command.Options.Add(auto);
        command.Options.Add(agent);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var values = parseResult.GetValue(references) ?? [];
                if (values.Length == 0)
                {
                    throw new TrackerException(
                        "ARGUMENT_INVALID",
                        "At least one issue reference is required.",
                        2);
                }
                var agentPolicy = parseResult.GetValue(agent);
                if (agentPolicy is not null &&
                    agentPolicy.ToLowerInvariant() is not ("claude" or "codex" or "copilot"))
                {
                    throw new TrackerException(
                        "ARGUMENT_INVALID",
                        "--agent must be claude, codex, or copilot.",
                        2);
                }

                var results = new List<AdoptWorkItemResult>();
                foreach (var reference in values)
                {
                    results.Add(await tracker.AdoptAsync(
                        config,
                        reference,
                        new AdoptWorkItemOptions(
                            parseResult.GetValue(status),
                            parseResult.GetValue(priority),
                            parseResult.GetValue(auto),
                            agentPolicy),
                        cancellationToken));
                }
                await writer.WriteAdoptAsync(
                    results,
                    parseResult.GetValue(json),
                    id => tracker.FormatShort(config, id));
            },
            cancellationToken));
        return command;
    }

    private static IReadOnlyDictionary<string, string> ParseMappings(string[]? values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1 ||
                !result.TryAdd(value[..separator], value[(separator + 1)..]))
            {
                throw new TrackerException("ARGUMENT_INVALID", $"Invalid or duplicate --map value '{value}'; expected target=source-key.", 2);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseValueMappings(
        string[]? values,
        string option)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 ||
                separator == value.Length - 1 ||
                !result.TryAdd(
                    value[..separator].Trim(),
                    value[(separator + 1)..].Trim()))
            {
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    $"Invalid or duplicate {option} value '{value}'; expected source=target.",
                    2);
            }
        }
        return result;
    }

    private Command BuildCreationAttemptCommand()
    {
        var parent = new Command(
            "creation-attempt",
            "Generate identifiers used to make work-item creation retry-safe");
        var json = JsonOption();
        var create = new Command("new", "Generate a new Creation attempt ID");
        create.Options.Add(json);
        create.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteCreationAttemptAsync(
                    CreationAttempt.NormalizeOrCreate(null),
                    parseResult.GetValue(json));
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 130;
            }
            catch (Exception exception)
            {
                return await writer.WriteErrorAsync(
                    new TrackerException(
                        "UNEXPECTED_ERROR",
                        exception.Message,
                        innerException: exception),
                    parseResult.GetValue(json));
            }
        });
        parent.Subcommands.Add(create);
        return parent;
    }

    private Command BuildMoveCommand()
    {
        var idArgument = WorkItemIdArgument();
        var statusArgument = new Argument<string>("status")
        {
            Description = "Destination workflow status."
        };
        var json = JsonOption();
        var claimant = AgentOptions();
        var command = new Command("move", "Move a claimed work item to another status");
        command.Arguments.Add(idArgument);
        command.Arguments.Add(statusArgument);
        command.Options.Add(json);
        AddAgentOptions(command, claimant);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var context = await ResolveAgentContextAsync(parseResult, claimant);
                var result = await tracker.UpdateAsync(
                    config,
                    id,
                    WorkItemPatch.StatusOnly(parseResult.GetValue(statusArgument)!),
                    expectedRevision: null,
                    new ClaimHandle(context, context.ClaimToken),
                    cancellationToken);
                await writer.WriteUpdateAsync(
                    result,
                    move: true,
                    parseResult.GetValue(json),
                    value => tracker.FormatShort(config, value));
            },
            cancellationToken));
        return command;
    }

    private Command BuildEditCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var options = EditOptions(idArgument, json);
        var takeover = new Option<bool>("--takeover")
        {
            Description = "Acquire or take over a human editing claim when necessary."
        };
        var yes = new Option<bool>("--yes")
        {
            Description = "With --takeover, confirm displacement of an active claimant without prompting."
        };
        var requeue = new Option<bool>("--requeue")
        {
            Description = "After saving, preserve the recorded agent session and queue it for a continuous worker."
        };
        var command = new Command(
            "edit",
            "Edit a claimed work item; optionally acquire or take over a human editing claim");
        var claimant = AgentOptions();
        command.Arguments.Add(idArgument);
        AddEditOptions(command, options);
        command.Options.Add(takeover);
        command.Options.Add(yes);
        command.Options.Add(requeue);
        AddAgentOptions(command, claimant);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            config => ExecuteEditAsync(
                config,
                parseResult,
                options,
                takeover,
                yes,
                requeue,
                claimant,
                cancellationToken),
            cancellationToken));
        return command;
    }

    private async Task ExecuteEditAsync(
        TrackerConfig config,
        ParseResult parseResult,
        EditOptionSet options,
        Option<bool> takeover,
        Option<bool> yes,
        Option<bool> requeue,
        AgentOptionSet claimantOptions,
        CancellationToken cancellationToken)
    {
        var hasDirectEdit = HasEditOptions(parseResult, options);
        var useEditor = !hasDirectEdit;
        if (useEditor && parseResult.GetValue(options.Json))
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "Interactive editing cannot be combined with --json. Supply edit options for a " +
                "non-interactive JSON operation.",
                2);
        if (useEditor)
            editor.Validate();

        var patch = hasDirectEdit
            ? await ParseEditPatchAsync(parseResult, options, cancellationToken)
            : new WorkItemPatch(
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string?>.Unspecified);
        var id = tracker.ResolveId(config, parseResult.GetValue(options.Id)!);
        var currentItem = useEditor
            ? await tracker.GetAsync(config, id, cancellationToken)
            : null;
        var context = await ResolveAgentContextAsync(
            parseResult,
            claimantOptions,
            parseResult.GetValue(takeover) ? "human" : null);
        ClaimResult? editingClaim = null;
        if (parseResult.GetValue(takeover))
        {
            if (context.EffectiveClaimantKind != ClaimantKind.Human)
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "edit --takeover is a human workflow; use --claimant-kind human.",
                    2);
            editingClaim = await EnsureHumanEditingClaimAsync(
                config,
                id,
                context,
                parseResult.GetValue(yes),
                parseResult.GetValue(options.Json),
                cancellationToken);
            context = context with
            {
                ClaimantId = editingClaim.ClaimantId,
                ClaimToken = editingClaim.ClaimToken
            };
        }

        if (useEditor)
        {
            var edited = await editor.EditAsync(
                currentItem!.Title, currentItem.Body, cancellationToken);
            patch = patch with
            {
                Title = OptionalValue<string>.From(edited.Title),
                Body = OptionalValue<string>.From(edited.Body)
            };
        }
        var result = await tracker.UpdateAsync(config, id, patch, null,
            new ClaimHandle(context, context.ClaimToken), cancellationToken);
        if (parseResult.GetValue(requeue))
        {
            await tracker.RequeueAsync(
                config,
                id,
                new ClaimHandle(context, context.ClaimToken),
                cancellationToken);
            await writer.WriteRequeueAsync(
                id,
                tracker.FormatShort(config, id),
                parseResult.GetValue(options.Json));
        }
        else
        {
            await writer.WriteUpdateAsync(
                result,
                move: false,
                parseResult.GetValue(options.Json),
                value => tracker.FormatShort(config, value));
            if (editingClaim is not null && !parseResult.GetValue(options.Json))
            {
                await output.WriteLineAsync(
                    $"The human editing claim remains active until {editingClaim.ExpiresAt:O}.");
                await output.WriteLineAsync(
                    $"Continue headlessly: wrighty worker --item {ShellQuote(id.Value)} --yes");
            }
        }
    }

    private Command BuildRequeueCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var claimant = AgentOptions();
        var command = new Command(
            "requeue",
            "End the current claim while preserving its recorded agent session for a continuous worker");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        AddAgentOptions(command, claimant);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var context = await ResolveAgentContextAsync(parseResult, claimant);
                await tracker.RequeueAsync(
                    config,
                    id,
                    new ClaimHandle(context, context.ClaimToken),
                    cancellationToken);
                await writer.WriteRequeueAsync(
                    id,
                    tracker.FormatShort(config, id),
                    parseResult.GetValue(json));
            },
            cancellationToken));
        return command;
    }

    private async Task<ClaimResult> EnsureHumanEditingClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext humanContext,
        bool yes,
        bool json,
        CancellationToken cancellationToken)
    {
        var ownership = await tracker.GetClaimOwnershipAsync(config, id, cancellationToken);
        if (ownership.State == ClaimOwnershipState.HeldByOther)
            throw new TrackerException(
                "CLAIM_NOT_OWNER",
                $"Work item '{id}' has an active claim from another Wrighty installation until " +
                $"{ownership.ExpiresAt:O}; it cannot be taken over here.",
                6);

        if (ownership.State == ClaimOwnershipState.OwnedByCurrent)
        {
            if (string.Equals(ownership.ClaimantId, humanContext.ClaimantId, StringComparison.Ordinal) &&
                humanContext.ClaimToken is not null)
            {
                try
                {
                    return await tracker.ClaimAsync(
                        config,
                        id,
                        humanContext,
                        cancellationToken,
                        humanContext.ClaimToken);
                }
                catch (TrackerException exception) when (exception.Code == "CLAIM_STALE")
                {
                    // An explicit --takeover may recover a lost or stale handle, but only after
                    // the same confirmation required for any other active claimant.
                }
            }

            await ConfirmClaimTransferAsync(
                "takeover for editing", id, config, yes, json, cancellationToken, ownership);
            return await tracker.TakeoverAsync(
                config, id, humanContext, humanContext.ClaimToken, cancellationToken);
        }

        var session = await tracker.GetAgentSessionAsync(config, id, cancellationToken);
        if (session is not { HasAddress: true })
            return await tracker.ClaimAsync(config, id, humanContext, cancellationToken);
        if (!session.FromCurrentInstallation)
            throw new TrackerException(
                "RESUME_ADDRESS_NOT_LOCAL",
                $"Work item '{id}' has a recorded agent session from another Wrighty installation. " +
                "Its session cannot be preserved by a local editing claim.",
                5);
        if (!session.IsComplete)
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' has incomplete agent-session metadata. Editing takeover will " +
                "not discard it; repair or explicitly release that session first.",
                5);

        var recoveryContext = new AgentExecutionContext(
            session.Agent,
            session.SessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: $"agent:cli-edit-recover:{Guid.NewGuid():N}");
        var recovered = await tracker.ClaimAsync(
            config, id, recoveryContext, cancellationToken);
        recovered = await tracker.RenewClaimAsync(
            config,
            id,
            new ClaimHandle(recoveryContext, recovered.ClaimToken),
            session.WorkspacePath,
            session.SessionId,
            cancellationToken);
        return await tracker.TakeoverAsync(
            config, id, humanContext, recovered.ClaimToken, cancellationToken);
    }

    private async Task<WorkItemPatch> ParseEditPatchAsync(
        ParseResult parseResult,
        EditOptionSet options,
        CancellationToken cancellationToken)
    {
        var bodySpecified = WasSpecified(parseResult, options.Body);
        var bodyFileSpecified = WasSpecified(parseResult, options.BodyFile);
        var prioritySpecified = WasSpecified(parseResult, options.Priority);
        var clearPriority = parseResult.GetValue(options.ClearPriority);
        EnsureCompatiblePriorityOptions(prioritySpecified, clearPriority);
        if (parseResult.GetValue(options.Auto) && parseResult.GetValue(options.NoAuto))
            throw new TrackerException("ARGUMENT_INVALID", "--auto and --no-auto cannot be used together.", 2);
        if (parseResult.GetValue(options.Profile) is not null && parseResult.GetValue(options.ClearProfile))
        {
            throw new TrackerException("ARGUMENT_INVALID",
                "--profile and --clear-profile cannot be combined.", 2);
        }

        if (parseResult.GetValue(options.WorkerAgent) is not null && parseResult.GetValue(options.ClearAgent))
            throw new TrackerException("ARGUMENT_INVALID", "--agent and --clear-agent cannot be used together.", 2);

        var bodyValue = await ReadBodyAsync(
            bodySpecified ? parseResult.GetValue(options.Body) : null,
            bodyFileSpecified ? parseResult.GetValue(options.BodyFile) : null,
            cancellationToken);
        var patch = BuildEditPatch(
            parseResult,
            options,
            bodyValue,
            bodySpecified || bodyFileSpecified,
            prioritySpecified,
            clearPriority);
        return patch;
    }

    private static void EnsureCompatiblePriorityOptions(
        bool prioritySpecified,
        bool clearPriority)
    {
        if (prioritySpecified && clearPriority)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--priority and --clear-priority cannot be used together.",
                2);
        }
    }

    private static WorkItemPatch BuildEditPatch(
        ParseResult parseResult,
        EditOptionSet options,
        string? body,
        bool bodySpecified,
        bool prioritySpecified,
        bool clearPriority)
    {
        var automationSpecified =
            WasSpecified(parseResult, options.Auto) ||
            WasSpecified(parseResult, options.NoAuto);
        var agentPolicySpecified =
            WasSpecified(parseResult, options.WorkerAgent) ||
            WasSpecified(parseResult, options.ClearAgent);
        var profileSpecified =
            WasSpecified(parseResult, options.Profile) ||
            WasSpecified(parseResult, options.ClearProfile);
        return new WorkItemPatch(
            OptionalString(parseResult, options.Title),
            bodySpecified
                ? OptionalValue<string>.From(body)
                : OptionalValue<string>.Unspecified,
            OptionalString(parseResult, options.Status),
            OptionalPriority(parseResult, options.Priority, prioritySpecified, clearPriority),
            WasSpecified(parseResult, options.Fields)
                ? OptionalValue<IReadOnlyDictionary<string, string?>>.From(
                    ParseFields(parseResult.GetValue(options.Fields), allowDeletion: true))
                : OptionalValue<IReadOnlyDictionary<string, string?>>.Unspecified,
            automationSpecified
                ? OptionalValue<bool>.From(parseResult.GetValue(options.Auto))
                : OptionalValue<bool>.Unspecified,
            agentPolicySpecified
                ? OptionalValue<string?>.From(parseResult.GetValue(options.ClearAgent)
                    ? null : parseResult.GetValue(options.WorkerAgent))
                : OptionalValue<string?>.Unspecified,
            OptionalValue<string?>.Unspecified,
            profileSpecified
                ? OptionalValue<string?>.From(parseResult.GetValue(options.ClearProfile)
                    ? null : NormalizeExecutionProfile(parseResult.GetValue(options.Profile)))
                : OptionalValue<string?>.Unspecified);
    }

    private static OptionalValue<string> OptionalString(
        ParseResult parseResult,
        Option<string?> option) =>
        WasSpecified(parseResult, option)
            ? OptionalValue<string>.From(parseResult.GetValue(option))
            : OptionalValue<string>.Unspecified;

    private static OptionalValue<string?> OptionalPriority(
        ParseResult parseResult,
        Option<string?> priority,
        bool prioritySpecified,
        bool clearPriority)
    {
        if (clearPriority)
        {
            return OptionalValue<string?>.From(null);
        }

        return prioritySpecified
            ? OptionalValue<string?>.From(parseResult.GetValue(priority))
            : OptionalValue<string?>.Unspecified;
    }

    private Command BuildClaimCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var agentOptions = AgentOptions();
        var command = new Command("claim", "Claim one work item");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        AddAgentOptions(command, agentOptions);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var agentContext = await ResolveAgentContextAsync(parseResult, agentOptions);
                var result = await tracker.ClaimAsync(
                    config,
                    id,
                    agentContext,
                    cancellationToken,
                    agentContext.ClaimToken);
                await writer.WriteClaimAsync(
                    id,
                    tracker.FormatShort(config, id),
                    result,
                    parseResult.GetValue(json));
            },
            cancellationToken));
        return command;
    }

    private Command BuildReleaseCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var claimant = AgentOptions();
        var overrideClaimant = new Option<bool>("--override")
        {
            Description = "Escape hatch: clear another claimant's claim on this installation " +
                          "without taking it over."
        };
        var yes = new Option<bool>("--yes") { Description = "Confirm override release without prompting." };
        var command = new Command("release", "Release a claim owned by this installation");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        command.Options.Add(overrideClaimant);
        command.Options.Add(yes);
        AddAgentOptions(command, claimant);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var context = await ResolveAgentContextAsync(parseResult, claimant);
                if (parseResult.GetValue(overrideClaimant))
                    await ConfirmClaimTransferAsync("override release", id, config,
                        parseResult.GetValue(yes), parseResult.GetValue(json), cancellationToken);
                // `wrighty release` ends a claim. It is not a statement about what should happen
                // to the item next, so a recorded decision — a queued resume, a scheduled retry —
                // outlives it. `wrighty requeue` is the command that sets one.
                await tracker.ReleaseAsync(config, id, new ClaimHandle(context, context.ClaimToken),
                    parseResult.GetValue(overrideClaimant), DispatchStateOnRelease.Preserve,
                    cancellationToken);
                await writer.WriteReleaseAsync(
                    id,
                    tracker.FormatShort(config, id),
                    parseResult.GetValue(json));
            },
            cancellationToken));
        return command;
    }

    private Command BuildTakeoverCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var yes = new Option<bool>("--yes") { Description = "Confirm takeover without prompting." };
        var printResume = new Option<bool>("--print-resume-command")
        {
            Description = "Print an environment-prefixed vendor resume command after takeover."
        };
        var claimant = AgentOptions();
        var command = new Command(
            "takeover",
            "Take over a same-installation claim directly. Prefer 'wrighty edit <id> --takeover' " +
            "to clarify an item or 'wrighty worker --item <id>' to continue its session.");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        command.Options.Add(yes);
        command.Options.Add(printResume);
        AddAgentOptions(command, claimant);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            config => ExecuteTakeoverAsync(
                config, parseResult, idArgument, json, yes, printResume, claimant,
                cancellationToken),
            cancellationToken));
        return command;
    }

    private async Task ExecuteTakeoverAsync(
        TrackerConfig config,
        ParseResult parseResult,
        Argument<string> idArgument,
        Option<bool> json,
        Option<bool> yes,
        Option<bool> printResume,
        AgentOptionSet claimant,
        CancellationToken cancellationToken)
    {
        var print = parseResult.GetValue(printResume);
        var jsonOutput = parseResult.GetValue(json);
        if (print && jsonOutput)
            throw new TrackerException("ARGUMENT_INVALID",
                "--print-resume-command cannot be combined with --json.", 2);
        var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
        var context = await ResolveAgentContextAsync(parseResult, claimant);
        var ownership = await tracker.GetClaimOwnershipAsync(config, id, cancellationToken);
        EnsureTakeoverAvailable(id, ownership);

        ClaimResult result;
        if (ownership.ClaimantId == context.ClaimantId &&
            ownership.State == ClaimOwnershipState.OwnedByCurrent &&
            context.ClaimToken is not null)
        {
            result = await tracker.TakeoverAsync(
                config, id, context, context.ClaimToken, cancellationToken);
        }
        else
        {
            await ConfirmClaimTransferAsync(
                "takeover", id, config, parseResult.GetValue(yes), jsonOutput,
                cancellationToken, ownership);
            result = await tracker.TakeoverAsync(
                config, id, context, context.ClaimToken, cancellationToken);
        }

        await writer.WriteClaimAsync(id, tracker.FormatShort(config, id), result, jsonOutput);
        if (print)
            await WriteResumeCommandsAsync(config, id, result, cancellationToken);
    }

    private static void EnsureTakeoverAvailable(
        WorkItemId id,
        ClaimOwnershipResult ownership)
    {
        if (ownership.State != ClaimOwnershipState.Unclaimed)
            return;
        throw new TrackerException(
            "CLAIM_NOT_FOUND",
            $"Work item '{id}' has no active claim. Takeover is no longer possible " +
            "after the prior claim expires or is released. Recover its recorded session " +
            "when available, otherwise start a new session, with: " +
            $"wrighty worker --item {ShellQuote(id.Value)} --yes",
            5);
    }

    /// <summary>
    /// Prints the commands that resume the session this takeover now owns.
    ///
    /// The directory they run in comes from the recorded session rather than from the claim result.
    /// A claim result carries whatever the claim marker held, and a marker is issue-comment content
    /// — trusted now only because of who wrote the comment, which is a weaker guarantee than a
    /// machine-local record. These commands `cd` somewhere and start an agent there, so that one
    /// field is worth taking from the strongest source available.
    ///
    /// The agent and session id stay as the takeover reported them: they are usually the operator's
    /// own <c>--agent-type</c> and <c>--session-id</c>, an unknown agent is refused against the
    /// known adapters, and a wrong session id makes the vendor fail to find a session rather than
    /// run somewhere unintended.
    /// </summary>
    private async Task WriteResumeCommandsAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimResult claim,
        CancellationToken cancellationToken)
    {
        var recorded = await tracker.GetAgentSessionAsync(config, id, cancellationToken);
        var workspace = recorded?.WorkspacePath;
        if (claim.ClaimantId is null || claim.ClaimToken is null ||
            claim.SessionId is null || claim.Agent is null || workspace is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                "The taken-over claim does not have a complete agent session address.", 5);

        if (ClaimantKinds.FromStorageValue(claim.ClaimantKind) == ClaimantKind.Agent)
        {
            await output.WriteLineAsync("Interactive resume:");
            await output.WriteLineAsync(BuildClaimResumeCommand(config, claim, workspace));
        }
        await output.WriteLineAsync("Headless worker resume:");
        await output.WriteLineAsync(BuildClaimWorkerResumeCommand(config, id, claim, workspace));
    }

    private static string BuildClaimResumeCommand(
        TrackerConfig config, ClaimResult claim, string workspace)
    {
        var environment = TrackerEnvironment(config);
        environment["WRIGHTY_CLAIMANT_ID"] = claim.ClaimantId!;
        environment["WRIGHTY_CLAIM_TOKEN"] = claim.ClaimToken!;
        return ResumeAdapterFor(claim.Agent!).BuildInteractiveCommand(
            new SessionHandle(claim.SessionId!),
            new Workspace(workspace),
            environment);
    }

    private static string BuildClaimWorkerResumeCommand(
        TrackerConfig config,
        WorkItemId id,
        ClaimResult claim,
        string workspace)
    {
        var configPrefix = string.IsNullOrWhiteSpace(config.SourcePath)
            ? string.Empty
            : $"{TrackerConfigLoader.ConfigPathEnvironmentVariable}=" +
              $"{ShellQuote(Path.GetFullPath(config.SourcePath))} ";
        return $"cd {ShellQuote(workspace)} && " +
               configPrefix +
               $"WRIGHTY_CLAIMANT_ID={ShellQuote(claim.ClaimantId!)} " +
               $"WRIGHTY_CLAIM_TOKEN={ShellQuote(claim.ClaimToken!)} " +
               $"wrighty worker --item {ShellQuote(id.Value)} --resume --yes";
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static Dictionary<string, string> TrackerEnvironment(TrackerConfig config)
    {
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(config.SourcePath))
            environment[TrackerConfigLoader.ConfigPathEnvironmentVariable] =
                Path.GetFullPath(config.SourcePath);
        return environment;
    }

    private async Task ConfirmClaimTransferAsync(string action, WorkItemId id, TrackerConfig config,
        bool yes, bool json, CancellationToken cancellationToken,
        Highbyte.Wrighty.Claims.ClaimOwnershipResult? known = null)
    {
        if (yes) return;
        var ownership = known ?? await tracker.GetClaimOwnershipAsync(config, id, cancellationToken);
        if (json || Console.IsInputRedirected)
            throw new TrackerException("CLAIM_CONFIRMATION_REQUIRED",
                $"{action} of '{tracker.FormatShort(config, id)}' requires --yes in JSON or non-interactive mode.", 2,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["claimantId"] = ownership.ClaimantId,
                    ["claimantKind"] = ownership.ClaimantKind,
                    ["agent"] = ownership.Agent,
                    ["expiresAt"] = ownership.ExpiresAt
                });
        await output.WriteLineAsync($"Current claimant: {ownership.ClaimantKind} {ownership.Agent ?? ""} {ownership.ClaimantId} until {ownership.ExpiresAt:O}");
        await output.WriteLineAsync("Warning: the previous claimant may still have work in progress. This does not stop its process.");
        await output.WriteLineAsync(config.Backend == "github"
            ? "GitHub writes already in flight may land after takeover; Wrighty detects but cannot roll them back."
            : "A mutation already holding the Local Markdown lock may finish first; after takeover returns, old handles are fenced.");
        await output.WriteAsync($"Confirm {action} of {tracker.FormatShort(config, id)}? [y/N] ");
        var answer = await input.ReadLineAsync(cancellationToken);
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("CLAIM_CONFIRMATION_REQUIRED", $"{action} was cancelled.", 2);
    }

    private Command BuildArchiveCommand(bool archive)
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var name = archive ? "archive" : "unarchive";
        var command = new Command(
            name,
            archive
                ? "Archive a claimed work item and release its claim"
                : "Restore an archived work item to active views");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        var claimant = AgentOptions();
        if (archive) AddAgentOptions(command, claimant);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var context = archive ? await ResolveAgentContextAsync(parseResult, claimant) : null;
                var result = archive
                    ? await tracker.ArchiveAsync(config, id, new ClaimHandle(context!, context!.ClaimToken), cancellationToken)
                    : await tracker.UnarchiveAsync(config, id, cancellationToken);
                await writer.WriteArchiveAsync(
                    result,
                    parseResult.GetValue(json),
                    value => tracker.FormatShort(config, value));
            },
            cancellationToken));
        return command;
    }

    private Command BuildPickCommand()
    {
        var from = new Option<string?>("--from")
        {
            Description = "Status to pick from; defaults to defaultPickFrom."
        };
        var to = new Option<string?>("--to")
        {
            Description = "Status to move to after claiming; defaults to defaultPickTo."
        };
        var json = JsonOption();
        var agentOptions = AgentOptions();
        var command = new Command("pick", "Claim the highest-priority available item");
        command.Options.Add(from);
        command.Options.Add(to);
        command.Options.Add(json);
        AddAgentOptions(command, agentOptions);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var agentContext = await ResolveAgentContextAsync(parseResult, agentOptions);
                var picked = await tracker.PickWithClaimAsync(
                    config,
                    parseResult.GetValue(from),
                    parseResult.GetValue(to),
                    agentContext,
                    cancellationToken);
                await writer.WritePickedAsync(
                    picked,
                    parseResult.GetValue(json),
                    id => tracker.FormatShort(config, id));
            },
            cancellationToken));
        return command;
    }

    private Command BuildFinishCommand()
    {
        var idArgument = WorkItemIdArgument();
        var status = new Option<string?>("--status")
        {
            Description = "Completion status; defaults to defaultFinishTo."
        };
        var json = JsonOption();
        var claimant = AgentOptions();
        var command = new Command(
            "finish",
            "Move a claimed work item to its completion status and release the claim");
        command.Arguments.Add(idArgument);
        command.Options.Add(status);
        command.Options.Add(json);
        AddAgentOptions(command, claimant);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var context = await ResolveAgentContextAsync(parseResult, claimant);
                var result = await tracker.FinishAsync(
                    config,
                    id,
                    parseResult.GetValue(status),
                    new ClaimHandle(context, context.ClaimToken),
                    cancellationToken);
                await writer.WriteFinishAsync(
                    result,
                    parseResult.GetValue(json),
                    value => tracker.FormatShort(config, value));
            },
            cancellationToken));
        return command;
    }

    private Command BuildSkillCommand()
    {
        var parent = new Command("skill", "Install and validate agent skills for the Wrighty CLI");
        parent.Subcommands.Add(BuildSkillOperationCommand("install"));
        parent.Subcommands.Add(BuildSkillOperationCommand("check"));
        parent.Subcommands.Add(BuildSkillOperationCommand("update"));
        return parent;
    }

    private Command BuildSkillOperationCommand(string operation)
    {
        var agent = new Option<string>("--agent")
        {
            Description = "Agent host: auto, codex, claude, copilot, or all.",
            DefaultValueFactory = _ => "auto"
        };
        var scope = new Option<string>("--scope")
        {
            Description = "Installation scope: project or user.",
            DefaultValueFactory = _ => "project"
        };
        var projectDirectory = new Option<string?>("--project-dir")
        {
            Description = "Project installation root; defaults to the Git root or current directory."
        };
        var force = new Option<bool>("--force")
        {
            Description = "Replace locally modified files in a recognized installation."
        };
        var checkTracker = new Option<bool>("--check-tracker")
        {
            Description = "Also validate the Wrighty configuration and backend read-only."
        };
        var json = JsonOption();
        var command = new Command(operation, $"{char.ToUpperInvariant(operation[0])}{operation[1..]} the Wrighty agent skill");
        command.Options.Add(agent);
        command.Options.Add(scope);
        command.Options.Add(projectDirectory);
        if (operation is "install" or "update") command.Options.Add(force);
        if (operation == "check") command.Options.Add(checkTracker);
        command.Options.Add(json);
        var options = new SkillOptionSet(
            agent,
            scope,
            projectDirectory,
            force,
            checkTracker,
            json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteSkillOperationAsync(operation, parseResult, options, cancellationToken));
        return command;
    }

    private async Task<int> ExecuteSkillOperationAsync(
        string operation,
        ParseResult parseResult,
        SkillOptionSet options,
        CancellationToken cancellationToken)
    {
        var useJson = parseResult.GetValue(options.Json);
        try
        {
            var agent = ResolveSkillAgent(parseResult.GetValue(options.Agent)!);
            var scope = ParseSkillScope(parseResult.GetValue(options.Scope)!);
            var projectPath = parseResult.GetValue(options.ProjectDirectory);
            var results = await RunSkillOperationAsync(
                operation,
                agent,
                scope,
                projectPath,
                parseResult.GetValue(options.Force),
                cancellationToken);
            await ValidateTrackerForSkillCheckAsync(
                operation,
                parseResult.GetValue(options.CheckTracker),
                cancellationToken);
            await writer.WriteSkillOperationsAsync(results, operation, useJson);
            return 0;
        }
        catch (TrackerException exception)
        {
            return await writer.WriteErrorAsync(exception, useJson);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            return await writer.WriteErrorAsync(
                new TrackerException("UNEXPECTED_ERROR", exception.Message, innerException: exception),
                useJson);
        }
    }

    private string ResolveSkillAgent(string agent)
    {
        if (!string.Equals(agent, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return agent;
        }

        if (runtimes is not null)
        {
            var snapshot = runtimes.Snapshot();
            if (snapshot.InstalledAgents.Count == 0)
                throw new TrackerException(
                    "SKILL_AGENT_NOT_INSTALLED",
                    "No supported local agent CLI was found on PATH. Supported executables: " +
                    $"{string.Join(", ", snapshot.Agents.Select(runtime => runtime.ExecutableName))}. " +
                    "Use --agent claude, codex, copilot, or all to choose targets explicitly.",
                    2,
                    new Dictionary<string, object?>
                    {
                        ["supportedAgents"] = snapshot.Agents
                            .Select(runtime => runtime.Agent)
                            .ToArray()
                    });
            return string.Join(",", snapshot.InstalledAgents.Select(runtime => runtime.Agent));
        }

        var detected = agentContextProvider.Resolve(new AgentContextInput());
        return detected.Warning is null && detected.Agent is "codex" or "claude" or "copilot"
            ? detected.Agent
            : "auto";
    }

    private static SkillScope ParseSkillScope(string scope) => scope.ToLowerInvariant() switch
    {
        "project" => SkillScope.Project,
        "user" => SkillScope.User,
        _ => throw new TrackerException(
            "ARGUMENT_INVALID",
            "--scope must be project or user.",
            2)
    };

    private Task<IReadOnlyList<SkillOperationResult>> RunSkillOperationAsync(
        string operation,
        string agent,
        SkillScope scope,
        string? projectPath,
        bool force,
        CancellationToken cancellationToken) => operation switch
        {
            "install" => skillManager.InstallAsync(
                agent, scope, workingDirectory, projectPath, force, cancellationToken),
            "check" => skillManager.CheckAsync(
                agent, scope, workingDirectory, projectPath, cancellationToken),
            _ => skillManager.UpdateAsync(
                agent, scope, workingDirectory, projectPath, force, cancellationToken)
        };

    private async Task ValidateTrackerForSkillCheckAsync(
        string operation,
        bool checkTracker,
        CancellationToken cancellationToken)
    {
        if (operation != "check" || !checkTracker)
        {
            return;
        }

        var config = await configLoader.LoadAsync(workingDirectory, cancellationToken);
        await tracker.InitializeAsync(config, checkOnly: true, cancellationToken);
    }

    private Command BuildWorkspacesCommand()
    {
        var json = JsonOption();
        var command = new Command(
            "workspaces",
            "List retained worker worktrees and branches for this repository");
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            config => ExecuteWorkspacesListAsync(
                config, parseResult.GetValue(json), cancellationToken),
            cancellationToken));

        var idArgument = WorkItemIdArgument();
        var cleanupJson = JsonOption();
        var cleanupForce = new Option<bool>("--force")
        {
            Description = "Discard uncommitted changes and unmerged commits (git worktree remove " +
                "--force / branch -D). Never overrides an active claim."
        };
        var cleanup = new Command(
            "cleanup",
            "Remove an item's clean worktree and delete its merged worker branch. " +
            "Dirty worktrees and unmerged branches are refused by git; pass --force to discard them.");
        cleanup.Arguments.Add(idArgument);
        cleanup.Options.Add(cleanupJson);
        cleanup.Options.Add(cleanupForce);
        cleanup.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(cleanupJson),
            config => ExecuteWorkspaceCleanupAsync(
                config, parseResult.GetRequiredValue(idArgument),
                parseResult.GetValue(cleanupJson), parseResult.GetValue(cleanupForce),
                cancellationToken),
            cancellationToken));
        command.Subcommands.Add(cleanup);
        return command;
    }

    private IWorkspaceInventory RequireWorkspaceInventory() =>
        workspaceInventory ?? throw new TrackerException(
            "WORKSPACE_ERROR", "Workspace inventory is not available in this host.", 7);

    private async Task ExecuteWorkspacesListAsync(
        TrackerConfig config,
        bool json,
        CancellationToken cancellationToken)
    {
        var inventory = RequireWorkspaceInventory();
        var root = GitWorkspaceManager.ResolveWorktreeRoot(config.Worker, workingDirectory);
        var workspaces = await inventory.ListAsync(workingDirectory, root, cancellationToken);
        var items = await tracker.ListOperationalAsync(
            config, new ListWorkItemsRequest(null, null, ArchiveScope.All), cancellationToken);
        var entries = workspaces
            .Select(workspace => (workspace, ItemFor(items, workspace)))
            .ToList();
        await writer.WriteWorkspacesAsync(entries, json);
    }

    private static string? ItemFor(
        IReadOnlyList<WorkItemOperationalState> items,
        WorkerWorkspaceInfo workspace) =>
        items.FirstOrDefault(item =>
                (item.Session?.WorkspacePath is { } path &&
                 string.Equals(Path.GetFullPath(path), workspace.Path, StringComparison.Ordinal)) ||
                (workspace.Branch is not null &&
                 string.Equals(item.Session?.Branch, workspace.Branch, StringComparison.Ordinal)))
            ?.Item.Id.Value;

    private async Task ExecuteWorkspaceCleanupAsync(
        TrackerConfig config,
        string id,
        bool json,
        bool force,
        CancellationToken cancellationToken)
    {
        var inventory = RequireWorkspaceInventory();
        var resolved = tracker.ResolveId(config, id);
        var state = await tracker.GetOperationalAsync(config, resolved, cancellationToken);
        // An active claim is a coordination guarantee, not a git-cleanliness one: --force never
        // overrides it, because forcing could yank a workspace from under a live worker or editor.
        if (state.Claim.State != ClaimOwnershipState.Unclaimed)
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{resolved}' has an active claim; a worker or editor may still be " +
                "using the workspace. Wait for release or expiry before cleanup.",
                6);
        if (string.IsNullOrWhiteSpace(state.Session?.WorkspacePath) &&
            string.IsNullOrWhiteSpace(state.Session?.Branch))
            throw new TrackerException(
                "WORKSPACE_NOT_FOUND",
                $"Work item '{resolved}' has no recorded workspace or worker branch.",
                5);

        var (workspaceRemoved, branchDeleted) = await inventory.CleanupAsync(
            workingDirectory, state.Session?.WorkspacePath, state.Session?.Branch,
            cancellationToken, force);
        await writer.WriteWorkspaceCleanupAsync(
            resolved,
            tracker.FormatShort(config, resolved),
            state.Session?.WorkspacePath,
            state.Session?.Branch,
            workspaceRemoved,
            branchDeleted,
            json);
    }

    private async Task<int> ExecuteAsync(
        bool json,
        Func<TrackerConfig, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = await configLoader.LoadAsync(workingDirectory, cancellationToken);
            await action(config);
            return 0;
        }
        catch (TrackerException exception)
        {
            return await writer.WriteErrorAsync(exception, json);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            return await writer.WriteErrorAsync(
                new TrackerException("UNEXPECTED_ERROR", exception.Message, innerException: exception),
                json);
        }
    }

    private async Task<int> ExecuteConfigurationCommandAsync(
        bool json,
        Func<Task> action)
    {
        try
        {
            await action();
            return 0;
        }
        catch (TrackerException exception)
        {
            return await writer.WriteErrorAsync(exception, json);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            return await writer.WriteErrorAsync(
                new TrackerException("UNEXPECTED_ERROR", exception.Message, innerException: exception),
                json);
        }
    }

    private static Option<bool> JsonOption()
    {
        return new Option<bool>("--json")
        {
            Description = "Emit a versioned JSON response."
        };
    }

    private static Option<string[]> FieldOption(string description) => new("--field")
    {
        Description = description
    };

    private static IReadOnlyDictionary<string, string?> ParseFields(
        string[]? values,
        bool allowDeletion)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
            {
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    $"Invalid --field value '{value}'; expected name=value.",
                    2);
            }

            var name = value[..separator];
            LocalMarkdownReservedFields.ValidateCustomFieldName(name);
            if (!result.TryAdd(name, value[(separator + 1)..] is { Length: > 0 } fieldValue
                    ? fieldValue
                    : allowDeletion ? null : string.Empty))
            {
                throw new TrackerException("ARGUMENT_INVALID", $"Custom field '{name}' was specified more than once.", 2);
            }
        }

        return result;
    }

    private static Argument<string> WorkItemIdArgument()
    {
        return new Argument<string>("id")
        {
            Description = "Backend work-item ID, shorthand, or issue URL."
        };
    }

    private async Task<string?> ReadBodyAsync(
        string? body,
        string? bodyFile,
        CancellationToken cancellationToken)
    {
        if (body is not null && bodyFile is not null)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "--body and --body-file cannot be used together.",
                2);
        }

        if (bodyFile is null)
        {
            return body;
        }

        try
        {
            return bodyFile == "-"
                ? await input.ReadToEndAsync(cancellationToken)
                : await File.ReadAllTextAsync(
                    Path.GetFullPath(bodyFile, workingDirectory),
                    cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                $"Could not read body file '{bodyFile}': {exception.Message}",
                2,
                innerException: exception);
        }
    }

    private async Task<AgentExecutionContext> ResolveAgentContextAsync(
        ParseResult parseResult,
        AgentOptionSet options,
        string? defaultClaimantKind = null)
    {
        var context = agentContextProvider.Resolve(new AgentContextInput(
            parseResult.GetValue(options.Agent),
            parseResult.GetValue(options.SessionId),
            parseResult.GetValue(options.Disabled),
            parseResult.GetValue(options.ClaimantKind) ?? defaultClaimantKind,
            parseResult.GetValue(options.ClaimantId),
            parseResult.GetValue(options.ClaimToken)));
        if (context.Warning is not null)
        {
            await error.WriteLineAsync($"warning: {context.Warning}");
        }

        return context;
    }

    private static AgentOptionSet AgentOptions() => new(
        new Option<string?>("--claimant-kind")
        {
            Description = "Claimant kind to publish: agent, human, automation, or unknown."
        },
        new Option<string?>("--claimant-id")
        {
            Description = "Opaque claimant-session identifier; defaults to WRIGHTY_CLAIMANT_ID or detected session."
        },
        new Option<string?>("--claim-token")
        {
            Description = "Expected claim generation; defaults to WRIGHTY_CLAIM_TOKEN."
        },
        new Option<string?>("--agent-type")
        {
            Description = "Agent runtime family to publish: codex, claude, copilot, or other."
        },
        new Option<string?>("--session-id")
        {
            Description = "Opaque agent conversation identifier to publish in the claim."
        },
        new Option<bool>("--no-claimant-context")
        {
            Description = "Do not publish claimant, agent, or session metadata."
        });

    private static void AddAgentOptions(Command command, AgentOptionSet options)
    {
        command.Options.Add(options.ClaimantKind);
        command.Options.Add(options.ClaimantId);
        command.Options.Add(options.ClaimToken);
        command.Options.Add(options.Agent);
        command.Options.Add(options.SessionId);
        command.Options.Add(options.Disabled);
    }

    private static EditOptionSet EditOptions(
        Argument<string> id,
        Option<bool> json) => new(
        id,
        new Option<string?>("--title") { Description = "New single-line work-item title." },
        new Option<string?>("--body") { Description = "New markdown work-item body." },
        new Option<string?>("--body-file")
        {
            Description = "Read the new markdown body from a file, or from stdin with '-'."
        },
        new Option<string?>("--status") { Description = "New workflow status." },
        new Option<string?>("--priority") { Description = "New work-item priority." },
        new Option<bool>("--clear-priority") { Description = "Clear the work-item priority." },
        new Option<bool>("--auto") { Description = "Opt this item into autonomous worker processing." },
        new Option<bool>("--no-auto") { Description = "Change this item to manual-only execution." },
        new Option<string?>("--agent") { Description = "Preferred worker vendor: claude, codex, or copilot." },
        new Option<bool>("--clear-agent") { Description = "Use the repository-default agent policy." },
        ExecutionProfileOption(),
        new Option<bool>("--clear-profile")
        {
            Description = "Use the repository-default execution profile."
        },
        FieldOption("Set a Local Markdown custom field as name=value; use name= to delete; repeat as needed."),
        json);

    private static void AddEditOptions(Command command, EditOptionSet options)
    {
        command.Options.Add(options.Title);
        command.Options.Add(options.Body);
        command.Options.Add(options.BodyFile);
        command.Options.Add(options.Status);
        command.Options.Add(options.Priority);
        command.Options.Add(options.ClearPriority);
        command.Options.Add(options.Auto);
        command.Options.Add(options.NoAuto);
        command.Options.Add(options.WorkerAgent);
        command.Options.Add(options.ClearAgent);
        command.Options.Add(options.Profile);
        command.Options.Add(options.ClearProfile);
        command.Options.Add(options.Fields);
        command.Options.Add(options.Json);
    }

    private static bool HasEditOptions(ParseResult parseResult, EditOptionSet options) =>
        WasSpecified(parseResult, options.Title) ||
        WasSpecified(parseResult, options.Body) ||
        WasSpecified(parseResult, options.BodyFile) ||
        WasSpecified(parseResult, options.Status) ||
        WasSpecified(parseResult, options.Priority) ||
        WasSpecified(parseResult, options.ClearPriority) ||
        WasSpecified(parseResult, options.Auto) ||
        WasSpecified(parseResult, options.NoAuto) ||
        WasSpecified(parseResult, options.WorkerAgent) ||
        WasSpecified(parseResult, options.ClearAgent) ||
        WasSpecified(parseResult, options.Profile) ||
        WasSpecified(parseResult, options.ClearProfile) ||
        WasSpecified(parseResult, options.Fields);

    private static Option<string?> ExecutionProfileOption() =>
        new("--profile")
        {
            Description =
                "Execution profile for this item: a name from the repository's vocabulary, or one " +
                "of Wrighty's built-in economy, balanced, deep tiers. Names a policy, never a model."
        };

    /// <summary>
    /// Checks the shape of a profile name at the CLI boundary so a typo is refused where it was
    /// typed. Whether the repository actually configures the name is settled at resolution, not
    /// here — an operator may legitimately set a profile before adding it to the vocabulary.
    /// </summary>
    private static string? NormalizeExecutionProfile(string? profile)
    {
        if (profile is null)
        {
            return null;
        }

        var trimmed = profile.Trim();
        return Workers.ExecutionProfileResolver.IsValidName(trimmed)
            ? trimmed
            : throw new TrackerException("ARGUMENT_INVALID",
                $"'{profile}' is not a valid execution profile name. Use lowercase words separated " +
                "by dashes, and not a ranking word such as 'best' or 'cheapest'.", 2);
    }

    private static bool WasSpecified<T>(ParseResult parseResult, Option<T> option) =>
        parseResult.GetResult(option) is { Implicit: false };

    private sealed record AgentOptionSet(
        Option<string?> ClaimantKind,
        Option<string?> ClaimantId,
        Option<string?> ClaimToken,
        Option<string?> Agent,
        Option<string?> SessionId,
        Option<bool> Disabled);

    private sealed record EditOptionSet(
        Argument<string> Id,
        Option<string?> Title,
        Option<string?> Body,
        Option<string?> BodyFile,
        Option<string?> Status,
        Option<string?> Priority,
        Option<bool> ClearPriority,
        Option<bool> Auto,
        Option<bool> NoAuto,
        Option<string?> WorkerAgent,
        Option<bool> ClearAgent,
        Option<string?> Profile,
        Option<bool> ClearProfile,
        Option<string[]> Fields,
        Option<bool> Json);

    private sealed record SkillOptionSet(
        Option<string> Agent,
        Option<string> Scope,
        Option<string?> ProjectDirectory,
        Option<bool> Force,
        Option<bool> CheckTracker,
        Option<bool> Json);
}
