using System.CommandLine;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Settings;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

/// <summary>
/// The <c>wrighty config profile</c> command group: your mapping from a shared profile
/// name to concrete vendor model and effort, stored in your user settings on this computer.
///
/// These commands are deliberately user-scoped. The profile *vocabulary* is repository policy and
/// belongs in <c>.wrighty.json</c>; what <c>deep</c> means in vendor terms depends on what this
/// operator has installed and is entitled to, and publishing that to the repository would make one
/// person's entitlement everyone else's problem.
/// </summary>
public sealed partial class CliApplication
{
    private Command BuildConfigProfileCommand()
    {
        var command = new Command(
            ProfileArgumentName, "Manage what each execution profile resolves to for you on this computer");
        command.Subcommands.Add(BuildConfigProfileListCommand());
        command.Subcommands.Add(BuildConfigProfileShowCommand());
        command.Subcommands.Add(BuildConfigProfileSetCommand());
        command.Subcommands.Add(BuildConfigProfileUnsetCommand());
        command.Subcommands.Add(BuildConfigProfileModelsCommand());
        return command;
    }

    private Command BuildConfigProfileListCommand()
    {
        var json = new Option<bool>("--json") { Description = "Emit a versioned JSON response." };
        var command = new Command("list", "List your execution-profile mappings");
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(
                parseResult.GetValue(json),
                () => ListProfilesAsync(parseResult.GetValue(json), cancellationToken)));
        return command;
    }

    private async Task ListProfilesAsync(bool json, CancellationToken cancellationToken)
    {
        var settings = await RequireUserSettings().LoadAsync(cancellationToken);
        if (json)
        {
            await writer.WriteExecutionProfilesAsync(new { profiles = settings.WorkerProfiles });
            return;
        }

        // Built-ins are listed first and always, so "what does balanced mean" is answerable
        // before the operator has configured anything.
        foreach (var profile in BuiltInExecutionProfiles.Names)
        {
            await output.WriteLineAsync($"{profile} (built-in)");
            await WriteBuiltInAgentsAsync(settings, profile);
        }

        var extra = settings.WorkerProfiles.Keys
            .Where(name => !BuiltInExecutionProfiles.IsBuiltIn(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in extra)
        {
            await output.WriteLineAsync(profile);
            await WriteAgentsAsync(FindAgents(settings, profile)!);
        }

        await output.WriteLineAsync(
            "\nBuilt-in tiers set reasoning effort only; each vendor's own default model " +
            "still applies. Name a model to pin one.");
    }

    private async Task WriteBuiltInAgentsAsync(UserSettings settings, string profile)
    {
        foreach (var agent in ProfileAgents)
        {
            var user = settings.FindMapping(profile, agent);
            var mapping = user is { IsEmpty: false }
                ? user
                : BuiltInExecutionProfiles.Find(profile, RequireExecutionCapability(agent));
            var origin = user is { IsEmpty: false } ? " [overridden here]" : string.Empty;
            await output.WriteLineAsync(mapping is null
                ? $"  {agent}: unavailable"
                : $"  {agent}: {DescribeMapping(mapping)}{origin}");
        }
    }

    private async Task WriteAgentsAsync(IReadOnlyDictionary<string, ExecutionProfileMapping> agents)
    {
        foreach (var (agent, mapping) in agents.OrderBy(
                     entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"  {agent}: {DescribeMapping(mapping)}");
        }
    }

    /// <summary>
    /// Looks a profile up by name without depending on the dictionary's comparer, which a JSON
    /// round-trip does not preserve. That has been lost twice; an explicit comparison cannot be.
    /// </summary>
    private static IReadOnlyDictionary<string, ExecutionProfileMapping>? FindAgents(
        UserSettings settings, string profile) =>
        settings.WorkerProfiles.FirstOrDefault(entry =>
            string.Equals(entry.Key, profile, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// <c>wrighty config profile models</c> — what each installed agent says it can run here.
    ///
    /// Read-only and purely informational, so an operator can pin a model they have rather than one
    /// they remember. It asks the vendors directly, which takes a moment per agent and can fail;
    /// both are reported plainly rather than hidden behind an empty list.
    /// </summary>
    private Command BuildConfigProfileModelsCommand()
    {
        var agent = new Option<string?>("--agent")
        {
            Description = $"Ask one agent only: {agents.DescribeWorkerIds()}."
        };
        var json = new Option<bool>("--json") { Description = "Emit a versioned JSON response." };
        var command = new Command("models", "List the models each installed agent can run here");
        command.Options.Add(agent);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(
                parseResult.GetValue(json),
                () => ListModelsAsync(
                    parseResult.GetValue(agent), parseResult.GetValue(json), cancellationToken)));
        return command;
    }

    private async Task ListModelsAsync(
        string? agent, bool json, CancellationToken cancellationToken)
    {
        var discoveries = Discoveries ?? throw new TrackerException(
            "MODEL_DISCOVERY_UNAVAILABLE",
            "Model discovery is not configured in this Wrighty build.", 7);
        var agents = agent is null
            ? ProfileAgents
            : [NormalizeProfileAgent(agent)];

        var catalogs = new List<AgentModelCatalog>();
        foreach (var name in agents)
        {
            catalogs.Add(await discoveries.DiscoverAsync(name, cancellationToken));
        }

        if (json)
        {
            await writer.WriteExecutionProfilesAsync(new { agents = catalogs });
            return;
        }

        foreach (var catalog in catalogs)
        {
            await WriteCatalogAsync(catalog);
        }

        await output.WriteLineAsync(
            "\nPin one with 'wrighty config profile set <profile> --agent <agent> --model <id>'. " +
            "Wrighty reads this from your computer and stores nothing about your account.");
    }

    private async Task WriteCatalogAsync(AgentModelCatalog catalog)
    {
        if (!catalog.Succeeded)
        {
            var unknown = catalog.Failure == ModelDiscoveryFailure.NotInstalled
                ? string.Empty
                : "; models unknown";
            await output.WriteLineAsync(
                $"{catalog.Agent} {DescribeFailure(catalog.Failure)}{unknown}");
            return;
        }

        await output.WriteLineAsync(catalog.Agent);
        foreach (var model in catalog.Models)
        {
            var marks = new List<string>();
            if (model.ResolvedId is { } resolved)
            {
                marks.Add($"resolves to {resolved}");
            }

            // The vendor's own multiplier, shown so an operator can weigh a choice. Wrighty never
            // ranks or sorts by it.
            if (model.RelativeCost is { } cost)
            {
                marks.Add($"cost {cost}");
            }

            marks.Add(DescribeEffort(model));
            if (model.DefaultEffort is { } fallback)
            {
                marks.Add($"vendor default effort {fallback}");
            }

            var current = string.Equals(model.Id, catalog.CurrentModelId, StringComparison.OrdinalIgnoreCase)
                ? " (used when no model is pinned)"
                : string.Empty;
            await output.WriteLineAsync($"  {model.Id}{current}: {string.Join(", ", marks)}");
        }
    }

    /// <summary>
    /// Says what is known *and* what is not. "effort unknown" is deliberately not shortened to
    /// "no effort": the operator can still configure one, and the vendor will decide.
    /// </summary>
    private static string DescribeEffort(AgentModel model) => model.Effort switch
    {
        EffortSupport.Yes when model.Efforts.Count > 0 => $"effort {string.Join("/", model.Efforts)}",
        EffortSupport.Yes => "effort accepted",
        EffortSupport.No => "accepts no effort",
        _ => "effort unknown here"
    };

    /// <summary>
    /// The reason alone, as a clause that reads correctly in a sentence. The listing appends its own
    /// "models unknown"; folding that in here made the mid-sentence form say
    /// "codex could not be asked; models unknown, so this mapping was saved".
    /// </summary>
    private static string DescribeFailure(ModelDiscoveryFailure failure) => failure switch
    {
        ModelDiscoveryFailure.NotInstalled => "is not installed",
        ModelDiscoveryFailure.NotAuthenticated => "needs sign-in before it will answer",
        ModelDiscoveryFailure.TimedOut => "did not answer in time",
        ModelDiscoveryFailure.Unrecognized => "answered in a form this Wrighty does not understand",
        _ => "could not be asked"
    };

    private Command BuildConfigProfileShowCommand()
    {
        var name = new Argument<string>(ProfileArgumentName) { Description = "Execution profile name." };
        var json = new Option<bool>("--json") { Description = "Emit a versioned JSON response." };
        var command = new Command("show", "Show your mapping for one profile");
        command.Arguments.Add(name);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(
                parseResult.GetValue(json),
                () => ShowProfileAsync(
                    parseResult.GetValue(name)!, parseResult.GetValue(json), cancellationToken)));
        return command;
    }

    private async Task ShowProfileAsync(
        string profile, bool json, CancellationToken cancellationToken)
    {
        var settings = await RequireUserSettings().LoadAsync(cancellationToken);
        var agents = FindAgents(settings, profile);

        if (json)
        {
            await writer.WriteExecutionProfilesAsync(new { profile, agents });
            return;
        }

        if (agents is { Count: > 0 })
        {
            await output.WriteLineAsync(profile);
            await WriteAgentsAsync(agents);
            return;
        }

        // Not an error: the repository may well recognize this name. What is missing is the local
        // mapping, and saying which of the two to fix is the useful part.
        if (!BuiltInExecutionProfiles.IsBuiltIn(profile))
        {
            await output.WriteLineAsync($"No mapping for '{profile}' in your user settings.");
            return;
        }

        await output.WriteLineAsync($"{profile} (built-in, not overridden here)");
        await WriteBuiltInAgentsAsync(settings, profile);
    }

    private Command BuildConfigProfileSetCommand()
    {
        var name = new Argument<string>(ProfileArgumentName) { Description = "Execution profile name." };
        var agent = new Option<string>("--agent")
        {
            Description = $"Agent this mapping applies to: {agents.DescribeWorkerIds()}.",
            Required = true
        };
        var model = new Option<string?>("--model")
        {
            Description =
                "Vendor model selector: a rolling alias such as 'sonnet' or 'auto', or an exact " +
                "model name. Omit to use the vendor CLI's own default."
        };
        var unsetModel = new Option<bool>("--unset-model")
        {
            Description =
                "Remove only the model from this mapping, keeping the effort, so the vendor CLI's " +
                "own default model applies."
        };
        var effort = new Option<string?>("--effort")
        {
            Description = $"Reasoning effort: {string.Join(", ", ExecutionEfforts.All)}."
        };
        var command = new Command("set", "Set your mapping for one profile and agent");
        command.Arguments.Add(name);
        foreach (Option option in (Option[])[agent, model, unsetModel, effort])
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(false, () => SetProfileAsync(
                parseResult.GetValue(name)!.Trim(),
                parseResult.GetValue(agent)!,
                parseResult.GetValue(model),
                parseResult.GetValue(unsetModel),
                parseResult.GetValue(effort),
                cancellationToken)));
        return command;
    }

    private async Task SetProfileAsync(
        string profile,
        string agent,
        string? model,
        bool clearModel,
        string? effort,
        CancellationToken cancellationToken)
    {
        var agentName = NormalizeProfileAgent(agent);
        var capability = RequireExecutionCapability(agentName);
        RejectInvalidSetArguments(profile, model, clearModel);
        var parsedEffort = ParseEffort(effort, agentName, capability);

        var store = RequireUserSettings();
        var settings = await store.LoadAsync(cancellationToken);
        // Merged onto what is already there: model and effort are set by separate invocations, and
        // an operator adjusting one has not withdrawn the other.
        var existing = settings.FindMapping(profile, agentName);
        var updated = new ExecutionProfileMapping
        {
            Model = clearModel ? null : model ?? existing?.Model,
            Effort = parsedEffort ?? existing?.Effort
        };

        if (updated.IsEmpty)
        {
            throw new TrackerException(ArgumentInvalid,
                "A mapping needs a model or an effort. Use 'wrighty config profile unset' " +
                "to remove it entirely.", 2);
        }

        var caution = await CheckAgainstTheVendorAsync(agentName, updated, cancellationToken);

        await store.SaveAsync(
            settings with { WorkerProfiles = Upsert(settings, profile, agentName, updated) },
            cancellationToken);
        await output.WriteLineAsync($"{profile} / {agentName}: {DescribeMapping(updated)}");
        if (caution is not null)
        {
            await output.WriteLineAsync(caution);
        }
    }

    /// <summary>
    /// Asks the vendor whether this exact model accepts this exact effort, and refuses only a
    /// combination it *says* is impossible.
    ///
    /// Three outcomes, and the middle one is the point. A known-invalid pair is refused, because
    /// the alternative is a launch that fails having spent a request. An unknown pair is saved with
    /// a note, because discovery is an enrichment and an operator who knows their account better
    /// than Wrighty does must not be blocked by a probe that timed out. A valid pair says nothing.
    ///
    /// Only asked when both a model and an effort are in play: with either missing there is no
    /// per-model question to answer, and a config command should not spawn a vendor CLI for nothing.
    /// </summary>
    private async Task<string?> CheckAgainstTheVendorAsync(
        string agent, ExecutionProfileMapping mapping, CancellationToken cancellationToken)
    {
        if (Discoveries is not { } discoveries ||
            mapping.Model is not { } model ||
            mapping.Effort is not { } effort)
        {
            return null;
        }

        var catalog = await discoveries.DiscoverAsync(agent, cancellationToken);
        if (!catalog.Succeeded)
        {
            return $"Note: {agent} {DescribeFailure(catalog.Failure)}, so this mapping was saved " +
                   "without checking the model against it.";
        }

        var token = effort.ToToken();
        if (catalog.Find(model) is not { } known)
        {
            // Saved anyway: an operator may be entitled to a model this account cannot currently
            // enumerate, and the vendor is the authority on that, not a list read seconds ago.
            return $"Note: {agent} did not list a model '{model}' on this computer. Saved anyway; " +
                   "run 'wrighty config profile models' to see what it does list.";
        }

        if (known.Rejects(token))
        {
            throw new TrackerException(ArgumentInvalid,
                $"Model '{model}' does not accept effort '{token}'. " +
                (known.Efforts.Count > 0
                    ? $"It accepts: {string.Join(", ", known.Efforts)}."
                    : "It accepts no reasoning effort."), 2);
        }

        return known.Effort == EffortSupport.Unknown
            ? $"Note: {agent} does not report which efforts '{model}' accepts, so '{token}' was " +
              "saved unchecked. A model that cannot use it will be relaunched without it."
            : null;
    }

    private static void RejectInvalidSetArguments(string profile, string? model, bool clearModel)
    {
        if (!ExecutionProfileResolver.IsValidName(profile))
        {
            throw new TrackerException(ArgumentInvalid,
                $"'{profile}' is not a valid execution profile name. Use lowercase words " +
                "separated by dashes, and not a ranking word such as 'best' or 'cheapest'.", 2);
        }

        if (clearModel && model is not null)
        {
            throw new TrackerException(ArgumentInvalid,
                "--model and --unset-model contradict each other.", 2);
        }

        if (model is not null && string.IsNullOrWhiteSpace(model))
        {
            throw new TrackerException(ArgumentInvalid,
                "--model cannot be empty. Use --unset-model to fall back to the vendor default.", 2);
        }
    }

    /// <summary>
    /// Parses an effort and refuses one this agent could never accept. Refused here rather than at
    /// launch because codex validates nothing locally: a bad value there starts a session and fails
    /// at the API, having already spent a request.
    /// </summary>
    private static ExecutionEffort? ParseEffort(
        string? effort, string agent, AgentExecutionCapability capability)
    {
        if (effort is null)
        {
            return null;
        }

        if (!ExecutionEfforts.TryParse(effort, out var level))
        {
            throw new TrackerException(ArgumentInvalid,
                $"'{effort}' is not a known effort level. Expected one of: " +
                $"{string.Join(", ", ExecutionEfforts.All)}.", 2);
        }

        if (!capability.Supports(level))
        {
            throw new TrackerException(ArgumentInvalid,
                $"Agent '{agent}' does not accept effort '{level.ToToken()}'. It " +
                $"supports: {string.Join(", ", capability.SupportedEfforts
                    .OrderBy(value => value).Select(value => value.ToToken()))}.", 2);
        }

        return level;
    }

    private Command BuildConfigProfileUnsetCommand()
    {
        var name = new Argument<string>(ProfileArgumentName) { Description = "Execution profile name." };
        var agent = new Option<string>("--agent")
        {
            Description = "Agent whose mapping to remove.",
            Required = true
        };
        var command = new Command("unset", "Remove your mapping for one profile and agent");
        command.Arguments.Add(name);
        command.Options.Add(agent);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(false, async () =>
            {
                var profile = parseResult.GetValue(name)!.Trim();
                var agentName = NormalizeProfileAgent(parseResult.GetValue(agent)!);
                var store = RequireUserSettings();
                var settings = await store.LoadAsync(cancellationToken);
                if (settings.FindMapping(profile, agentName) is null)
                {
                    await output.WriteLineAsync($"No mapping for {profile} / {agentName}.");
                    return;
                }

                await store.SaveAsync(
                    settings with { WorkerProfiles = Remove(settings, profile, agentName) },
                    cancellationToken);
                await output.WriteLineAsync($"Removed {profile} / {agentName}.");
            }));
        return command;
    }

    /// <summary>
    /// <c>wrighty config repository profiles …</c> — the shared vocabulary in <c>.wrighty.json</c>,
    /// which lives there because everyone working this repository must agree on the names.
    ///
    /// Split into verbs rather than one list-taking command. Adding a profile should not require
    /// retyping the others, and dropping one is not a local edit: the next <c>wrighty init</c>
    /// offers to delete the matching Project option and clear it from every item holding it.
    /// </summary>
    private Command BuildConfigRepositoryProfilesCommand()
    {
        var group = new Command("profiles", "Manage the repository's execution-profile vocabulary");
        group.Subcommands.Add(BuildProfilesEditCommand(
            "set",
            "Replace the whole vocabulary with the names given",
            ExecutionProfilesEdit.Replace));
        group.Subcommands.Add(BuildProfilesEditCommand(
            "add", "Add profile names, keeping the existing ones", ExecutionProfilesEdit.Add));
        group.Subcommands.Add(BuildProfilesEditCommand(
            "remove", "Remove profile names, keeping the rest", ExecutionProfilesEdit.Remove));
        group.Subcommands.Add(BuildProfilesDefaultCommand());
        return group;
    }

    private Command BuildProfilesEditCommand(
        string verb, string description, ExecutionProfilesEdit edit)
    {
        var names = new Argument<string[]>("profiles")
        {
            Description = edit switch
            {
                ExecutionProfilesEdit.Add => "Profile names to add.",
                ExecutionProfilesEdit.Remove => "Profile names to remove.",
                _ => "The complete list of profile names this repository recognizes. Any existing " +
                     "name not listed here is removed."
            },
            Arity = ArgumentArity.ZeroOrMore
        };
        var command = new Command(verb, description);
        command.Arguments.Add(names);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) => ExecuteConfigurationMutationAsync(
            parseResult,
            common,
            new ExecutionProfilesMutation(
                parseResult.GetValue(names) ?? [],
                SetDefault: false,
                DefaultProfile: null,
                edit),
            cancellationToken));
        return command;
    }

    private Command BuildProfilesDefaultCommand()
    {
        var name = new Argument<string?>(ProfileArgumentName)
        {
            Description = "Profile applied when neither the worker nor the item names one.",
            Arity = ArgumentArity.ZeroOrOne
        };
        var clear = new Option<bool>("--clear")
        {
            Description = "Remove the repository default, so a run without a profile uses vendor defaults."
        };
        var command = new Command("default", "Set or clear the repository's default profile");
        command.Arguments.Add(name);
        command.Options.Add(clear);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var selected = parseResult.GetValue(name);
            var clearing = parseResult.GetValue(clear);
            if (clearing && selected is not null)
            {
                throw new TrackerException(ArgumentInvalid,
                    "Name a profile or pass --clear, not both.", 2);
            }

            if (!clearing && selected is null)
            {
                throw new TrackerException(ArgumentInvalid,
                    "Name a profile to make the default, or pass --clear.", 2);
            }

            return ExecuteConfigurationMutationAsync(
                parseResult,
                common,
                // The vocabulary is untouched: an empty Add leaves the existing list alone.
                new ExecutionProfilesMutation(
                    [], SetDefault: true, clearing ? null : selected, ExecutionProfilesEdit.Add),
                cancellationToken);
        });
        return command;
    }

    private AgentExecutionCapability RequireExecutionCapability(string agent) =>
        AgentExecutionCapabilities.ForAgent(agent, agents)
        ?? throw new TrackerException(ArgumentInvalid,
            $"Unknown agent '{agent}'. Expected {agents.DescribeWorkerIds()}.", 2);

    private IReadOnlyList<string> ProfileAgents => agents.WorkerIds;

    private const string ArgumentInvalid = "ARGUMENT_INVALID";

    private const string ProfileArgumentName = "profile";

    private string NormalizeProfileAgent(string agent)
    {
        var normalized = agent.Trim().ToLowerInvariant();
        return agents.IsWorkerAgent(normalized)
            ? normalized
            : throw new TrackerException(ArgumentInvalid,
                $"Unknown agent '{agent}'. Expected {string.Join(", ", agents.WorkerIds)}.", 2);
    }

    private static string DescribeMapping(ExecutionProfileMapping mapping) =>
        string.Join(", ", new[]
        {
            mapping.Model is { } model ? $"model {model}" : "model: vendor default",
            mapping.Effort is { } effort ? $"effort {effort.ToToken()}" : "effort: vendor default"
        });

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
        Upsert(
            UserSettings settings,
            string profile,
            string agent,
            ExecutionProfileMapping mapping)
    {
        var profiles = Clone(settings);
        var key = profiles.Keys.FirstOrDefault(existing =>
            string.Equals(existing, profile, StringComparison.OrdinalIgnoreCase)) ?? profile;
        var agents = profiles.TryGetValue(key, out var existingAgents)
            ? new Dictionary<string, ExecutionProfileMapping>(existingAgents, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ExecutionProfileMapping>(StringComparer.OrdinalIgnoreCase);
        agents[agent] = mapping;
        profiles[key] = agents;
        return profiles.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyDictionary<string, ExecutionProfileMapping>)entry.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
        Remove(UserSettings settings, string profile, string agent)
    {
        var profiles = Clone(settings);
        var key = profiles.Keys.FirstOrDefault(existing =>
            string.Equals(existing, profile, StringComparison.OrdinalIgnoreCase));
        if (key is not null && profiles.TryGetValue(key, out var agents))
        {
            var remaining = new Dictionary<string, ExecutionProfileMapping>(
                agents, StringComparer.OrdinalIgnoreCase);
            remaining.Remove(agent);
            // An empty profile entry would show as a configured profile with no mapping, which
            // fails at resolution anyway; dropping it keeps `list` honest.
            if (remaining.Count == 0)
            {
                profiles.Remove(key);
            }
            else
            {
                profiles[key] = remaining;
            }
        }

        return profiles.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyDictionary<string, ExecutionProfileMapping>)entry.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Dictionary<string, ExecutionProfileMapping>> Clone(
        UserSettings settings) =>
        settings.WorkerProfiles.ToDictionary(
            entry => entry.Key,
            entry => new Dictionary<string, ExecutionProfileMapping>(
                entry.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
}
