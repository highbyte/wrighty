using System.CommandLine;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Settings;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Cli;

/// <summary>
/// The <c>wrighty config profile</c> command group: this machine's mapping from a shared profile
/// name to concrete vendor model and effort.
///
/// These commands are deliberately user-scoped. The profile *vocabulary* is repository policy and
/// belongs in <c>.wrighty.json</c>; what <c>deep</c> means in vendor terms depends on what this
/// operator has installed and is entitled to, and publishing that to the repository would make one
/// machine's entitlement everyone else's problem.
/// </summary>
public sealed partial class CliApplication
{
    private Command BuildConfigProfileCommand()
    {
        var command = new Command(
            "profile", "Manage this machine's model and effort mapping for each execution profile");
        command.Subcommands.Add(BuildConfigProfileListCommand());
        command.Subcommands.Add(BuildConfigProfileShowCommand());
        command.Subcommands.Add(BuildConfigProfileSetCommand());
        command.Subcommands.Add(BuildConfigProfileUnsetCommand());
        return command;
    }

    private Command BuildConfigProfileListCommand()
    {
        var json = new Option<bool>("--json") { Description = "Emit a versioned JSON response." };
        var command = new Command("list", "List this machine's execution-profile mappings");
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(parseResult.GetValue(json), async () =>
            {
                var settings = await RequireUserSettings().LoadAsync(cancellationToken);
                if (parseResult.GetValue(json))
                {
                    await writer.WriteExecutionProfilesAsync(new { profiles = settings.WorkerProfiles });
                    return;
                }

                // Built-ins are listed first and always, so "what does balanced mean" is answerable
                // before the operator has configured anything.
                foreach (var profile in BuiltInExecutionProfiles.Names)
                {
                    await output.WriteLineAsync($"{profile} (built-in)");
                    foreach (var agent in ProfileAgents)
                    {
                        var user = settings.FindMapping(profile, agent);
                        var mapping = user is { IsEmpty: false }
                            ? user
                            : BuiltInExecutionProfiles.Find(
                                profile, RequireExecutionCapability(agent));
                        var origin = user is { IsEmpty: false } ? " [overridden here]" : string.Empty;
                        await output.WriteLineAsync(mapping is null
                            ? $"  {agent}: unavailable"
                            : $"  {agent}: {DescribeMapping(mapping)}{origin}");
                    }
                }

                var extra = settings.WorkerProfiles.Keys
                    .Where(name => !BuiltInExecutionProfiles.IsBuiltIn(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
                foreach (var profile in extra)
                {
                    await output.WriteLineAsync(profile);
                    var agents = settings.WorkerProfiles.First(entry =>
                        string.Equals(entry.Key, profile, StringComparison.OrdinalIgnoreCase)).Value;
                    foreach (var (agent, mapping) in agents.OrderBy(entry => entry.Key,
                                 StringComparer.OrdinalIgnoreCase))
                    {
                        await output.WriteLineAsync($"  {agent}: {DescribeMapping(mapping)}");
                    }
                }

                await output.WriteLineAsync(
                    "\nBuilt-in tiers set reasoning effort only; each vendor's own default model " +
                    "still applies. Name a model to pin one.");
            }));
        return command;
    }

    private Command BuildConfigProfileShowCommand()
    {
        var name = new Argument<string>("profile") { Description = "Execution profile name." };
        var json = new Option<bool>("--json") { Description = "Emit a versioned JSON response." };
        var command = new Command("show", "Show one profile's mapping on this machine");
        command.Arguments.Add(name);
        command.Options.Add(json);
        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(parseResult.GetValue(json), async () =>
            {
                var profile = parseResult.GetValue(name)!;
                var settings = await RequireUserSettings().LoadAsync(cancellationToken);
                var agents = settings.WorkerProfiles.FirstOrDefault(entry =>
                    string.Equals(entry.Key, profile, StringComparison.OrdinalIgnoreCase)).Value;

                if (parseResult.GetValue(json))
                {
                    await writer.WriteExecutionProfilesAsync(new { profile, agents });
                    return;
                }

                if (agents is null || agents.Count == 0)
                {
                    if (!BuiltInExecutionProfiles.IsBuiltIn(profile))
                    {
                        await output.WriteLineAsync($"No mapping for '{profile}' on this machine.");
                        return;
                    }

                    await output.WriteLineAsync($"{profile} (built-in, not overridden here)");
                    foreach (var agent in ProfileAgents)
                    {
                        var builtIn = BuiltInExecutionProfiles.Find(
                            profile, RequireExecutionCapability(agent));
                        await output.WriteLineAsync(builtIn is null
                            ? $"  {agent}: unavailable"
                            : $"  {agent}: {DescribeMapping(builtIn)}");
                    }

                    return;
                }

                await output.WriteLineAsync(profile);
                foreach (var (agent, mapping) in agents.OrderBy(entry => entry.Key,
                             StringComparer.OrdinalIgnoreCase))
                {
                    await output.WriteLineAsync($"  {agent}: {DescribeMapping(mapping)}");
                }
            }));
        return command;
    }

    private Command BuildConfigProfileSetCommand()
    {
        var name = new Argument<string>("profile") { Description = "Execution profile name." };
        var agent = new Option<string>("--agent")
        {
            Description = "Agent this mapping applies to: claude, codex, or copilot.",
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
        var command = new Command("set", "Set this machine's mapping for one profile and agent");
        command.Arguments.Add(name);
        foreach (var option in new Option[] { agent, model, unsetModel, effort })
        {
            command.Options.Add(option);
        }

        command.SetAction((parseResult, cancellationToken) =>
            ExecuteConfigurationCommandAsync(false, async () =>
            {
                var profile = parseResult.GetValue(name)!.Trim();
                var agentName = NormalizeProfileAgent(parseResult.GetValue(agent)!);
                var modelValue = parseResult.GetValue(model);
                var clearModel = parseResult.GetValue(unsetModel);
                var effortValue = parseResult.GetValue(effort);

                if (!ExecutionProfileResolver.IsValidName(profile))
                {
                    throw new TrackerException("ARGUMENT_INVALID",
                        $"'{profile}' is not a valid execution profile name. Use lowercase words " +
                        "separated by dashes, and not a ranking word such as 'best' or 'cheapest'.", 2);
                }

                if (clearModel && modelValue is not null)
                {
                    throw new TrackerException("ARGUMENT_INVALID",
                        "--model and --unset-model contradict each other.", 2);
                }

                if (modelValue is not null && string.IsNullOrWhiteSpace(modelValue))
                {
                    throw new TrackerException("ARGUMENT_INVALID",
                        "--model cannot be empty. Use --unset-model to fall back to the vendor default.", 2);
                }

                var capability = RequireExecutionCapability(agentName);
                ExecutionEffort? parsedEffort = null;
                if (effortValue is not null)
                {
                    if (!ExecutionEfforts.TryParse(effortValue, out var level))
                    {
                        throw new TrackerException("ARGUMENT_INVALID",
                            $"'{effortValue}' is not a known effort level. Expected one of: " +
                            $"{string.Join(", ", ExecutionEfforts.All)}.", 2);
                    }

                    if (!capability.Supports(level))
                    {
                        // Refused here rather than at launch: for codex this value is not validated
                        // until the API rejects it, having already spent a request.
                        throw new TrackerException("ARGUMENT_INVALID",
                            $"Agent '{agentName}' does not accept effort '{level.ToToken()}'. It " +
                            $"supports: {string.Join(", ", capability.SupportedEfforts
                                .OrderBy(value => value).Select(value => value.ToToken()))}.", 2);
                    }

                    parsedEffort = level;
                }

                var store = RequireUserSettings();
                var settings = await store.LoadAsync(cancellationToken);
                var existing = settings.FindMapping(profile, agentName);
                var updated = new ExecutionProfileMapping
                {
                    Model = clearModel ? null : modelValue ?? existing?.Model,
                    Effort = parsedEffort ?? existing?.Effort
                };

                if (updated.IsEmpty)
                {
                    throw new TrackerException("ARGUMENT_INVALID",
                        "A mapping needs a model or an effort. Use 'wrighty config profile unset' " +
                        "to remove it entirely.", 2);
                }

                await store.SaveAsync(
                    settings with { WorkerProfiles = Upsert(settings, profile, agentName, updated) },
                    cancellationToken);
                await output.WriteLineAsync(
                    $"{profile} / {agentName}: {DescribeMapping(updated)}");
            }));
        return command;
    }

    private Command BuildConfigProfileUnsetCommand()
    {
        var name = new Argument<string>("profile") { Description = "Execution profile name." };
        var agent = new Option<string>("--agent")
        {
            Description = "Agent whose mapping to remove.",
            Required = true
        };
        var command = new Command("unset", "Remove this machine's mapping for one profile and agent");
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
    /// <c>wrighty config repository profiles set …</c> — the shared vocabulary, which lives in
    /// <c>.wrighty.json</c> because every machine working this repository must agree on the names.
    /// </summary>
    private Command BuildConfigRepositoryProfilesCommand()
    {
        var group = new Command("profiles", "Manage the repository's execution-profile vocabulary");
        var names = new Argument<string[]>("profiles")
        {
            Description = "Profile names this repository recognizes.",
            Arity = ArgumentArity.ZeroOrMore
        };
        var defaultProfile = new Option<string?>("--default")
        {
            Description = "Profile applied when neither the worker nor the item names one."
        };
        var clearDefault = new Option<bool>("--clear-default")
        {
            Description = "Remove the repository default, so a run without a profile uses vendor defaults."
        };
        var command = new Command("set", "Set the profile vocabulary and default");
        command.Arguments.Add(names);
        command.Options.Add(defaultProfile);
        command.Options.Add(clearDefault);
        var common = AddMutationOptions(command);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var selected = parseResult.GetValue(defaultProfile);
            var clearing = parseResult.GetValue(clearDefault);
            if (clearing && selected is not null)
            {
                throw new TrackerException("ARGUMENT_INVALID",
                    "--default and --clear-default cannot be combined.", 2);
            }

            return ExecuteConfigurationMutationAsync(
                parseResult,
                common,
                new ExecutionProfilesMutation(
                    parseResult.GetValue(names) ?? [],
                    clearing || selected is not null,
                    clearing ? null : selected),
                cancellationToken);
        });
        group.Subcommands.Add(command);
        return group;
    }

    private static AgentExecutionCapability RequireExecutionCapability(string agent) =>
        AgentExecutionCapabilities.ForAgent(agent)
        ?? throw new TrackerException("ARGUMENT_INVALID",
            $"Unknown agent '{agent}'. Expected claude, codex, or copilot.", 2);

    private static readonly string[] ProfileAgents = ["claude", "codex", "copilot"];

    private static string NormalizeProfileAgent(string agent)
    {
        var normalized = agent.Trim().ToLowerInvariant();
        return normalized is "claude" or "codex" or "copilot"
            ? normalized
            : throw new TrackerException("ARGUMENT_INVALID",
                $"Unknown agent '{agent}'. Expected claude, codex, or copilot.", 2);
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
