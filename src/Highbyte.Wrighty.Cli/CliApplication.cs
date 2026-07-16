using System.CommandLine;
using Highbyte.Wrighty.Cli.Output;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Cli.Skills;
using Highbyte.Wrighty.Web;

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
    string workingDirectory)
{
    private readonly OutputWriter writer = new(output, error);

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
        root.Subcommands.Add(BuildReleaseCommand());
        root.Subcommands.Add(BuildArchiveCommand(archive: true));
        root.Subcommands.Add(BuildArchiveCommand(archive: false));
        root.Subcommands.Add(BuildPickCommand());
        root.Subcommands.Add(BuildFinishCommand());
        root.Subcommands.Add(BuildWebCommand());
        root.Subcommands.Add(BuildSkillCommand());
        return root;
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
                parseResult.GetResult(noLinkRepository)?.Tokens.Count > 0,
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

                var items = await tracker.ListAsync(
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
                await writer.WriteItemsAsync(
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
                var item = await tracker.GetAsync(config, id, cancellationToken);
                await writer.WriteDetailAsync(
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
        var fields = FieldOption("Set a Local Markdown custom field as name=value; repeat for multiple fields.");
        var json = JsonOption();
        var command = new Command("create", "Create and track a real work item");
        command.Options.Add(title);
        command.Options.Add(body);
        command.Options.Add(bodyFile);
        command.Options.Add(status);
        command.Options.Add(priority);
        command.Options.Add(creationAttemptId);
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
                        ParseFields(parseResult.GetValue(fields), allowDeletion: true)),
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
        var command = new Command("move", "Move a claimed work item to another status");
        command.Arguments.Add(idArgument);
        command.Arguments.Add(statusArgument);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var result = await tracker.UpdateAsync(
                    config,
                    id,
                    WorkItemPatch.StatusOnly(parseResult.GetValue(statusArgument)!),
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
        var title = new Option<string?>("--title") { Description = "New single-line work-item title." };
        var body = new Option<string?>("--body") { Description = "New markdown work-item body." };
        var bodyFile = new Option<string?>("--body-file")
        {
            Description = "Read the new markdown body from a file, or from stdin with '-'."
        };
        var status = new Option<string?>("--status") { Description = "New workflow status." };
        var priority = new Option<string?>("--priority") { Description = "New work-item priority." };
        var clearPriority = new Option<bool>("--clear-priority")
        {
            Description = "Clear the work-item priority."
        };
        var fields = FieldOption("Set a Local Markdown custom field as name=value; use name= to delete; repeat as needed.");
        var json = JsonOption();
        var command = new Command("edit", "Edit a claimed work item");
        command.Arguments.Add(idArgument);
        command.Options.Add(title);
        command.Options.Add(body);
        command.Options.Add(bodyFile);
        command.Options.Add(status);
        command.Options.Add(priority);
        command.Options.Add(clearPriority);
        command.Options.Add(fields);
        command.Options.Add(json);
        var options = new EditOptionSet(
            idArgument,
            title,
            body,
            bodyFile,
            status,
            priority,
            clearPriority,
            fields,
            json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            config => ExecuteEditAsync(config, parseResult, options, cancellationToken),
            cancellationToken));
        return command;
    }

    private async Task ExecuteEditAsync(
        TrackerConfig config,
        ParseResult parseResult,
        EditOptionSet options,
        CancellationToken cancellationToken)
    {
        var bodySpecified = parseResult.GetResult(options.Body) is not null;
        var bodyFileSpecified = parseResult.GetResult(options.BodyFile) is not null;
        var prioritySpecified = parseResult.GetResult(options.Priority) is not null;
        var clearPriority = parseResult.GetValue(options.ClearPriority);
        EnsureCompatiblePriorityOptions(prioritySpecified, clearPriority);

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
        var id = tracker.ResolveId(config, parseResult.GetValue(options.Id)!);
        var result = await tracker.UpdateAsync(config, id, patch, cancellationToken);
        await writer.WriteUpdateAsync(
            result,
            move: false,
            parseResult.GetValue(options.Json),
            value => tracker.FormatShort(config, value));
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
        bool clearPriority) => new(
        OptionalString(parseResult, options.Title),
        bodySpecified
            ? OptionalValue<string>.From(body)
            : OptionalValue<string>.Unspecified,
        OptionalString(parseResult, options.Status),
        OptionalPriority(parseResult, options.Priority, prioritySpecified, clearPriority),
        parseResult.GetResult(options.Fields) is not null
            ? OptionalValue<IReadOnlyDictionary<string, string?>>.From(
                ParseFields(parseResult.GetValue(options.Fields), allowDeletion: true))
            : OptionalValue<IReadOnlyDictionary<string, string?>>.Unspecified);

    private static OptionalValue<string> OptionalString(
        ParseResult parseResult,
        Option<string?> option) =>
        parseResult.GetResult(option) is not null
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
                    cancellationToken);
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
        var command = new Command("release", "Release a claim owned by this installation");
        command.Arguments.Add(idArgument);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                await tracker.ReleaseAsync(config, id, cancellationToken);
                await writer.WriteReleaseAsync(
                    id,
                    tracker.FormatShort(config, id),
                    parseResult.GetValue(json));
            },
            cancellationToken));
        return command;
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
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var result = archive
                    ? await tracker.ArchiveAsync(config, id, cancellationToken)
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
                var item = await tracker.PickAsync(
                    config,
                    parseResult.GetValue(from),
                    parseResult.GetValue(to),
                    agentContext,
                    cancellationToken);
                await writer.WritePickedAsync(
                    item,
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
        var command = new Command(
            "finish",
            "Move a claimed work item to its completion status and release the claim");
        command.Arguments.Add(idArgument);
        command.Options.Add(status);
        command.Options.Add(json);
        command.SetAction(async (parseResult, cancellationToken) => await ExecuteAsync(
            parseResult.GetValue(json),
            async config =>
            {
                var id = tracker.ResolveId(config, parseResult.GetValue(idArgument)!);
                var result = await tracker.FinishAsync(
                    config,
                    id,
                    parseResult.GetValue(status),
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
        AgentOptionSet options)
    {
        var context = agentContextProvider.Resolve(new AgentContextInput(
            parseResult.GetValue(options.AgentType),
            parseResult.GetValue(options.SessionId),
            parseResult.GetValue(options.Disabled),
            parseResult.GetValue(options.ClaimantKind)));
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
        new Option<string?>("--agent-type")
        {
            Description = "Agent runtime family to publish: codex, claude, copilot, or other."
        },
        new Option<string?>("--session-id")
        {
            Description = "Opaque agent conversation identifier to publish in the claim."
        },
        new Option<bool>("--no-agent-context")
        {
            Description = "Do not publish claimant, agent type, or session metadata."
        });

    private static void AddAgentOptions(Command command, AgentOptionSet options)
    {
        command.Options.Add(options.ClaimantKind);
        command.Options.Add(options.AgentType);
        command.Options.Add(options.SessionId);
        command.Options.Add(options.Disabled);
    }

    private sealed record AgentOptionSet(
        Option<string?> ClaimantKind,
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
