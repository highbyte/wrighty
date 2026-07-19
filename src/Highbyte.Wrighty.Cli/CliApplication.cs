using System.CommandLine;
using Highbyte.Wrighty.Cli.Output;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Cli.Skills;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;
using System.Text.Json;

namespace Highbyte.Wrighty.Cli;

public sealed class CliApplication(
    ITrackerConfigLoader configLoader,
    TrackerInitializationService initialization,
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
    Func<DateTimeOffset>? clock = null)
{
    private readonly OutputWriter writer = new(output, error, clock);
    private readonly Func<bool> isInputRedirected = inputIsRedirected ?? (() => Console.IsInputRedirected);
    private readonly IWorkItemTextEditor editor = workItemEditor ?? new SystemWorkItemTextEditor();

    public Task<int> InvokeAsync(string[] args)
    {
        return BuildRootCommand().Parse(args).InvokeAsync();
    }

    private RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Wrighty: local-first work coordination with pluggable backends");
        root.Subcommands.Add(BuildInitCommand());
        root.Subcommands.Add(BuildListCommand());
        root.Subcommands.Add(BuildGetCommand());
        root.Subcommands.Add(BuildCreationAttemptCommand());
        root.Subcommands.Add(BuildCreateCommand());
        root.Subcommands.Add(BuildImportCommand());
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
        root.Subcommands.Add(BuildFinishCommand());
        root.Subcommands.Add(BuildWebCommand());
        root.Subcommands.Add(BuildSkillCommand());
        return root;
    }

    private Command BuildResumeCommand()
    {
        var idArgument = WorkItemIdArgument();
        var json = JsonOption();
        var command = new Command("resume-command",
            "Print the recorded workspace and vendor command for an active claim");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) => ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var claim = await tracker.GetClaimOwnershipAsync(config, id, cancellationToken);
                if (claim.SessionId is null || claim.WorkspacePath is null || claim.AgentType is null)
                    throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                        $"Claim '{tracker.FormatShort(config, id)}' does not have a complete agent session address.", 5);
                IAgentAdapter adapter = claim.AgentType switch
                {
                    "claude" => new ClaudeAgentAdapter(),
                    "codex" => new CodexAgentAdapter(),
                    "copilot" => new CopilotAgentAdapter(),
                    _ => throw new TrackerException("AGENT_UNSUPPORTED",
                        $"Unsupported recorded agent '{claim.AgentType}'.", 3)
                };
                var resume = adapter.BuildInteractiveCommand(
                    new SessionHandle(claim.SessionId),
                    new Workspace(claim.WorkspacePath),
                    TrackerEnvironment(config));
                if (parseResult.GetValue(json))
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        version = 1,
                        result = new
                        {
                            id = id.Value,
                            claim.AgentType,
                            claim.SessionId,
                            claim.WorkspacePath,
                            command = resume
                        }
                    }));
                else
                    await output.WriteLineAsync(resume);
            }, cancellationToken));
        return command;
    }

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
        var keepWorkspace = new Option<bool>("--keep-workspace")
        {
            Description = "Retain a successful worktree so its completed agent session can be reviewed interactively."
        };
        var check = new Option<bool>("--check") { Description = "Run a read-only vendor probe and verify its session handle." };
        var yes = new Option<bool>("--yes") { Description = "Acknowledge live worker risk without prompting." };
        var from = new Option<string?>("--from") { Description = "Status to pick from." };
        var to = new Option<string?>("--to") { Description = "Status to move claimed items to." };
        var json = JsonOption();
        var command = new Command("worker", "Autonomously process explicitly eligible work items");
        foreach (var option in new Option[] { agent, once, maxItems, workspaceMode, filters, idleTimeout,
                     itemTimeout, onFenced, claimantId, claimantKind, dryRun, item, resume, fresh, keepWorkspace,
                     from, to, json })
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
                parseResult.GetValue(keepWorkspace)),
            cancellationToken,
            parseResult.GetValue(check),
            parseResult.GetValue(yes),
            parseResult.GetValue(item),
            parseResult.GetValue(resume),
            parseResult.GetValue(fresh),
            parseResult.GetValue(workspaceMode)));
        return command;
    }

    private async Task<int> ExecuteWorkerAsync(WorkerOptions options, CancellationToken cancellationToken,
        bool checkOnly,
        bool yes,
        string? item,
        bool requireResume,
        bool requireFresh,
        string? workspaceModeOverride)
    {
        try
        {
            if (workerService is null)
                throw new TrackerException("WORKER_UNAVAILABLE", "Worker services are not configured.", 7);
            var config = await configLoader.LoadAsync(workingDirectory, cancellationToken);
            options = options with
            {
                WorkspaceMode = ResolveWorkspaceMode(
                    workspaceModeOverride,
                    config.EffectiveWorker.WorkspaceMode)
            };
            ValidateWorkerInvocation(checkOnly, item, requireResume, requireFresh);
            if (checkOnly)
            {
                await workerService.CheckAsync(options.Agent ?? config.EffectiveWorker.DefaultAgent,
                    workingDirectory,
                    value => WriteWorkerEventAsync(value, options.Json), cancellationToken);
                return 0;
            }
            await WriteMissingAgentNoticeAsync(config, options, item);
            var intent = ResolveWorkerIntent(requireResume, requireFresh);
            var callerContext = item is null
                ? null
                : agentContextProvider.Resolve(new AgentContextInput());
            if (!await PreflightWorkerAsync(
                    config, options, item, intent, yes, cancellationToken))
                return 0;
            var summary = await RunWorkerAsync(
                config, options, item, intent, callerContext?.ClaimToken, cancellationToken);
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
        bool requireFresh)
    {
        if (requireResume && requireFresh)
            throw new TrackerException("ARGUMENT_INVALID",
                "--resume cannot be combined with --fresh.", 2);
        if ((requireResume || requireFresh) && item is null)
            throw new TrackerException("ARGUMENT_INVALID",
                "--resume and --fresh require --item <id>.", 2);
        if (checkOnly && item is not null)
            throw new TrackerException("ARGUMENT_INVALID",
                "--check cannot be combined with --item.", 2);
    }

    private async Task WriteMissingAgentNoticeAsync(
        TrackerConfig config,
        WorkerOptions options,
        string? item)
    {
        if (item is not null ||
            !string.IsNullOrWhiteSpace(options.Agent) ||
            !string.IsNullOrWhiteSpace(config.EffectiveWorker.DefaultAgent))
            return;
        await WriteWorkerEventAsync(
            new WorkerEvent(
                "info",
                Message: "No default worker agent is configured; only items with " +
                         "wrighty-agent can run. Set --agent <vendor> or " +
                         "worker.defaultAgent in .wrighty.json to provide a fallback."),
            options.Json);
    }

    private static WorkerItemIntent ResolveWorkerIntent(bool requireResume, bool requireFresh)
    {
        if (requireResume)
            return WorkerItemIntent.Resume;
        return requireFresh ? WorkerItemIntent.Fresh : WorkerItemIntent.Auto;
    }

    private async Task<bool> PreflightWorkerAsync(
        TrackerConfig config,
        WorkerOptions options,
        string? item,
        WorkerItemIntent intent,
        bool yes,
        CancellationToken cancellationToken)
    {
        if (item is not null)
        {
            await workerService!.PreflightItemAsync(
                config, options, workingDirectory, tracker.ResolveId(config, item), intent,
                value => WriteWorkerEventAsync(value, options.Json), cancellationToken);
        }
        else if (!options.DryRun)
        {
            var hasWork = await workerService!.PreflightAsync(
                config, options, workingDirectory,
                value => WriteWorkerEventAsync(value, options.Json), cancellationToken);
            if (!hasWork && options.Once)
                return false;
        }
        await ConfirmWorkerExecutionAsync(options, yes, cancellationToken);
        return true;
    }

    private Task<WorkerRunSummary> RunWorkerAsync(
        TrackerConfig config,
        WorkerOptions options,
        string? item,
        WorkerItemIntent intent,
        string? claimToken,
        CancellationToken cancellationToken) =>
        item is null
            ? workerService!.RunAsync(
                config, options, workingDirectory,
                value => WriteWorkerEventAsync(value, options.Json), cancellationToken)
            : workerService!.RunItemAsync(
                config, options, workingDirectory, tracker.ResolveId(config, item), intent, claimToken,
                value => WriteWorkerEventAsync(value, options.Json), cancellationToken);

    private async Task ConfirmWorkerExecutionAsync(
        WorkerOptions options,
        bool yes,
        CancellationToken cancellationToken)
    {
        if (options.DryRun)
            return;

        await error.WriteLineAsync(
            "warning: live worker execution may start unattended agents with broad tool permissions " +
            "that may execute commands and modify files.");
        if (options.WorkspaceMode == WorkspaceMode.Shared)
            await error.WriteLineAsync(
                "warning: shared workspace mode allows multiple agents to concurrently modify, stage, " +
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

    private async Task WriteWorkerEventAsync(WorkerEvent value, bool json)
    {
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
            return;
        }
        await WriteHumanWorkerEventAsync(value);
    }

    private async Task WriteHumanWorkerEventAsync(WorkerEvent value)
    {
        // Renewal remains available to JSON consumers, while the human stream uses the periodic
        // running heartbeat to avoid printing two operational lines at renewal half-life.
        if (value.Type == "renewed")
            return;
        if (value.Type == "running")
        {
            await output.WriteLineAsync(
                $"{value.OccurredAt:O} running: {value.ItemId ?? "-"}" +
                $"{(value.Agent is null ? "" : $" [{value.Agent}]")}" +
                $"{(value.Message is null ? "" : $" — {value.Message}")}");
            return;
        }
        var argv = value.Arguments is null ? "" : $" argv={string.Join(" ", value.Arguments.Select(QuoteArg))}";
        await output.WriteLineAsync(
            $"{value.Type}: {value.ItemId ?? "-"}{(value.Agent is null ? "" : $" [{value.Agent}]")}" +
            $"{(value.WorkspacePath is null ? "" : $" in {value.WorkspacePath}")}{argv}" +
            $"{(value.Message is null ? "" : $" — {value.Message}")}");
        if (value.SessionId is not null)
            await output.WriteLineAsync($"  session: {value.SessionId}");
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
            foreach (var command in action.Commands)
                await output.WriteLineAsync($"      {command}");
            await output.WriteLineAsync($"      {action.Description}");
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
            Description = "Loopback port to listen on; 0 selects an available port.",
            DefaultValueFactory = _ => 0
        };
        var noOpen = new Option<bool>("--no-open")
        {
            Description = "Do not open the default browser after the server starts."
        };
        var command = new Command("web", "Start the embedded Wrighty web server");
        command.Options.Add(port);
        command.Options.Add(noOpen);
        command.SetAction((parseResult, cancellationToken) => ExecuteWebAsync(
            parseResult.GetValue(port),
            !parseResult.GetValue(noOpen),
            cancellationToken));
        return command;
    }

    private async Task<int> ExecuteWebAsync(
        int port,
        bool openBrowser,
        CancellationToken cancellationToken)
    {
        try
        {
            if (port is < 0 or > 65535)
            {
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    "--port must be between 0 and 65535.",
                    2);
            }

            await webServer.RunAsync(
                new WebServerOptions(port, openBrowser),
                output,
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
        var configPath = new Option<string?>("--config")
        {
            Description = "Configuration file to read or create."
        };
        var check = new Option<bool>("--check")
        {
            Description = "Validate local configuration and remote Project schema without changing either."
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
        command.Options.Add(configPath);
        command.Options.Add(check);
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
                parseResult.GetValue(priorities) is { Length: > 0 } priorityValues ? priorityValues : null),
            parseResult.GetValue(json),
            cancellationToken));
        return command;
    }

    private async Task<int> ExecuteInitializationAsync(
        TrackerInitializationRequest request,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await initialization.InitializeAsync(
                workingDirectory,
                request,
                cancellationToken);
            await writer.WriteInitializationAsync(result, request.CheckOnly, json);
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
                await writer.WriteOperationalDetailAsync(
                    item,
                    parseResult.GetValue(json),
                    value => tracker.FormatShort(config, value));
            },
            cancellationToken));
        return command;
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
                        parseResult.GetValue(workerAgent)),
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
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the import plan without writing files." };
        var maps = new Option<string[]>("--map") { Description = "Map a managed field to a source key, for example status=state." };
        var forceStatus = new Option<string?>("--force-status") { Description = "Use one configured status for every imported file." };
        var json = JsonOption();
        var command = new Command("import", "Import Markdown files into a Local Markdown store");
        command.Arguments.Add(paths);
        command.Options.Add(recursive);
        command.Options.Add(archive);
        command.Options.Add(move);
        command.Options.Add(dryRun);
        command.Options.Add(maps);
        command.Options.Add(forceStatus);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                if (tracker.Backend(config) is not ILocalMarkdownImportBackend importer)
                {
                    throw new TrackerException("NOT_SUPPORTED", "Import is supported only by the Local Markdown backend.", 3);
                }

                var resolvedPaths = (parseResult.GetValue(paths) ?? [])
                    .Select(path => Path.GetFullPath(path, workingDirectory))
                    .ToArray();
                var result = await importer.ImportAsync(
                    config,
                    new LocalMarkdownImportRequest(
                        resolvedPaths,
                        parseResult.GetValue(recursive),
                        parseResult.GetValue(archive),
                        parseResult.GetValue(move),
                        parseResult.GetValue(dryRun),
                        ParseMappings(parseResult.GetValue(maps)),
                        parseResult.GetValue(forceStatus)),
                    cancellationToken);
                await writer.WriteImportAsync(result, parseResult.GetValue(json));
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
            session.AgentType,
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
        var preferredAgentSpecified =
            WasSpecified(parseResult, options.WorkerAgent) ||
            WasSpecified(parseResult, options.ClearAgent);
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
            preferredAgentSpecified
                ? OptionalValue<string?>.From(parseResult.GetValue(options.ClearAgent)
                    ? null : parseResult.GetValue(options.WorkerAgent))
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
        var overrideClaimant = new Option<bool>("--override") { Description = "Release another claimant on this installation." };
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
                await tracker.ReleaseAsync(config, id, new ClaimHandle(context, context.ClaimToken),
                    parseResult.GetValue(overrideClaimant), cancellationToken);
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
        var command = new Command("takeover", "Explicitly take over a same-installation claim");
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
            await WriteResumeCommandsAsync(config, id, result);
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

    private async Task WriteResumeCommandsAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimResult claim)
    {
        if (ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) == ClaimantKind.Agent)
        {
            await output.WriteLineAsync("Interactive resume:");
            await output.WriteLineAsync(BuildClaimResumeCommand(config, claim));
        }
        await output.WriteLineAsync("Headless worker resume:");
        await output.WriteLineAsync(BuildClaimWorkerResumeCommand(config, id, claim));
    }

    private static string BuildClaimResumeCommand(TrackerConfig config, ClaimResult claim)
    {
        if (claim.ClaimantId is null || claim.ClaimToken is null ||
            claim.SessionId is null || claim.WorkspacePath is null || claim.AgentType is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                "The taken-over claim does not have a complete agent session address.", 5);
        IAgentAdapter adapter = claim.AgentType switch
        {
            "claude" => new ClaudeAgentAdapter(),
            "codex" => new CodexAgentAdapter(),
            "copilot" => new CopilotAgentAdapter(),
            _ => throw new TrackerException("AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{claim.AgentType}'.", 3)
        };
        var environment = TrackerEnvironment(config);
        environment["WRIGHTY_CLAIMANT_ID"] = claim.ClaimantId;
        environment["WRIGHTY_CLAIM_TOKEN"] = claim.ClaimToken;
        return adapter.BuildInteractiveCommand(
            new SessionHandle(claim.SessionId),
            new Workspace(claim.WorkspacePath),
            environment);
    }

    private static string BuildClaimWorkerResumeCommand(
        TrackerConfig config,
        WorkItemId id,
        ClaimResult claim)
    {
        if (claim.ClaimantId is null || claim.ClaimToken is null || claim.WorkspacePath is null ||
            claim.SessionId is null || claim.AgentType is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                "The taken-over claim does not have a complete agent session address.", 5);
        var configPrefix = string.IsNullOrWhiteSpace(config.SourcePath)
            ? string.Empty
            : $"{TrackerConfigLoader.ConfigPathEnvironmentVariable}=" +
              $"{ShellQuote(Path.GetFullPath(config.SourcePath))} ";
        return $"cd {ShellQuote(claim.WorkspacePath)} && " +
               configPrefix +
               $"WRIGHTY_CLAIMANT_ID={ShellQuote(claim.ClaimantId)} " +
               $"WRIGHTY_CLAIM_TOKEN={ShellQuote(claim.ClaimToken)} " +
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
                    ["agentType"] = ownership.AgentType,
                    ["expiresAt"] = ownership.ExpiresAt
                });
        await output.WriteLineAsync($"Current claimant: {ownership.ClaimantKind} {ownership.AgentType ?? ""} {ownership.ClaimantId} until {ownership.ExpiresAt:O}");
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

        var detected = agentContextProvider.Resolve(new AgentContextInput());
        return detected.Warning is null && detected.AgentType is "codex" or "claude" or "copilot"
            ? detected.AgentType
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
            parseResult.GetValue(options.AgentType),
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
            Description = "Do not publish claimant, agent type, or session metadata."
        });

    private static void AddAgentOptions(Command command, AgentOptionSet options)
    {
        command.Options.Add(options.ClaimantKind);
        command.Options.Add(options.ClaimantId);
        command.Options.Add(options.ClaimToken);
        command.Options.Add(options.AgentType);
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
        new Option<bool>("--no-auto") { Description = "Remove this item from autonomous worker eligibility." },
        new Option<string?>("--agent") { Description = "Preferred worker vendor: claude, codex, or copilot." },
        new Option<bool>("--clear-agent") { Description = "Clear the preferred worker vendor." },
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
        WasSpecified(parseResult, options.Fields);

    private static bool WasSpecified<T>(ParseResult parseResult, Option<T> option) =>
        parseResult.GetResult(option) is { Implicit: false };

    private sealed record AgentOptionSet(
        Option<string?> ClaimantKind,
        Option<string?> ClaimantId,
        Option<string?> ClaimToken,
        Option<string?> AgentType,
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
