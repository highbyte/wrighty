using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Settings;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class ExecutionProfileResolverTests
{
    private static readonly AgentExecutionCapability ClaudeCapability =
        new ClaudeAgentAdapter().DescribeExecutionCapability();

    private static WorkerConfig Worker(
        string? defaultProfile = "balanced",
        params string[] profiles) =>
        new()
        {
            ExecutionProfiles = profiles.Length == 0 ? ["economy", "balanced", "deep"] : profiles,
            DefaultExecutionProfile = defaultProfile
        };

    private static UserSettings Settings(
        string profile = "balanced",
        string agent = "claude",
        string? model = "sonnet",
        ExecutionEffort? effort = ExecutionEffort.Medium) =>
        new()
        {
            WorkerProfiles = new Dictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
            {
                [profile] = new Dictionary<string, ExecutionProfileMapping>
                {
                    [agent] = new() { Model = model, Effort = effort }
                }
            }
        };

    private static ExecutionProfileResolution Resolve(
        string? commandLine = null,
        string? item = null,
        WorkerConfig? worker = null,
        UserSettings? settings = null,
        AgentExecutionCapability? capability = null) =>
        ExecutionProfileResolver.Resolve(
            "claude",
            commandLine,
            item,
            worker ?? Worker(),
            settings ?? Settings(),
            capability ?? ClaudeCapability);

    [Fact]
    public void No_profile_anywhere_preserves_the_vendor_default()
    {
        var resolution = Resolve(worker: new WorkerConfig(), settings: new UserSettings());

        Assert.True(resolution.Succeeded);
        Assert.True(resolution.Selection!.IsVendorDefault);
        Assert.Null(resolution.Selection.Profile);
        Assert.Equal(ExecutionProfileSource.None, resolution.Selection.Source);
    }

    [Fact]
    public void Command_line_beats_item_which_beats_repository_default()
    {
        Assert.Equal(
            ("deep", ExecutionProfileSource.CommandLine),
            ExecutionProfileResolver.SelectProfile("deep", "economy", "balanced"));
        Assert.Equal(
            ("economy", ExecutionProfileSource.WorkItem),
            ExecutionProfileResolver.SelectProfile(null, "economy", "balanced"));
        Assert.Equal(
            ("balanced", ExecutionProfileSource.RepositoryDefault),
            ExecutionProfileResolver.SelectProfile(null, null, "balanced"));
        Assert.Equal(
            ((string?)null, ExecutionProfileSource.None),
            ExecutionProfileResolver.SelectProfile(null, null, null));
    }

    [Fact]
    public void A_resolved_profile_carries_the_machine_local_mapping()
    {
        var resolution = Resolve(commandLine: "deep", settings: Settings("deep", model: "opus",
            effort: ExecutionEffort.XHigh));

        Assert.True(resolution.Succeeded);
        Assert.Equal("deep", resolution.Selection!.Profile);
        Assert.Equal("opus", resolution.Selection.Model);
        Assert.Equal(ExecutionEffort.XHigh, resolution.Selection.Effort);
        Assert.False(resolution.Selection.IsVendorDefault);
    }

    [Fact]
    public void An_omitted_model_defers_to_the_vendor_cli_default_without_failing()
    {
        var resolution = Resolve(settings: Settings(model: null, effort: ExecutionEffort.Medium));

        Assert.True(resolution.Succeeded);
        Assert.Null(resolution.Selection!.Model);
        Assert.Equal(ExecutionEffort.Medium, resolution.Selection.Effort);
        // Effort alone still means the launch is not a plain vendor default.
        Assert.False(resolution.Selection.IsVendorDefault);
    }

    [Fact]
    public void An_unmapped_tier_uses_its_own_built_in_never_another_tiers_mapping()
    {
        // This machine has mapped only 'balanced', to sonnet/medium. Asking for 'deep' must give
        // deep's built-in effort — never balanced's mapping, which is the substitution the design
        // forbids.
        var resolution = Resolve(commandLine: "deep", settings: Settings("balanced"));

        Assert.True(resolution.Succeeded);
        Assert.Equal(ExecutionEffort.High, resolution.Selection!.Effort);
        Assert.Null(resolution.Selection.Model);
        Assert.NotEqual("sonnet", resolution.Selection.Model);
        Assert.Equal(ExecutionMappingSource.BuiltIn, resolution.Selection.MappingSource);
    }

    [Fact]
    public void A_profile_outside_the_repository_vocabulary_fails()
    {
        var resolution = Resolve(commandLine: "experimental", settings: Settings("experimental"));

        Assert.False(resolution.Succeeded);
        Assert.Contains("not one of this repository's configured profiles", resolution.FailureReason);
    }

    [Theory]
    [InlineData("best")]
    [InlineData("cheapest")]
    [InlineData("latest")]
    [InlineData("Deep")]            // uppercase
    [InlineData("deep_work")]       // underscore
    [InlineData("-deep")]
    public void Ranking_words_and_malformed_names_are_invalid(string name) =>
        Assert.False(ExecutionProfileResolver.IsValidName(name));

    [Fact]
    public void A_model_id_is_rejected_by_the_vocabulary_rather_than_by_syntax()
    {
        // A model ID like this is structurally indistinguishable from a legitimate profile name
        // such as 'high-volume-triage', so no name pattern can separate them without banning real
        // names too. What actually stops it is that it is not in the repository's vocabulary.
        Assert.True(ExecutionProfileResolver.IsValidName("claude-opus-4-1"));

        var resolution = Resolve(commandLine: "claude-opus-4-1");

        Assert.False(resolution.Succeeded);
        Assert.Contains("not one of this repository's configured profiles", resolution.FailureReason);
    }

    [Theory]
    [InlineData("economy")]
    [InlineData("balanced")]
    [InlineData("docs-only")]
    [InlineData("high-volume-triage")]
    public void Lowercase_dashed_names_are_valid(string name) =>
        Assert.True(ExecutionProfileResolver.IsValidName(name));

    [Fact]
    public void An_effort_the_vendor_rejects_fails_instead_of_rounding_to_a_neighbour()
    {
        // claude accepts low..max but not 'minimal'. Rounding it up to 'low' would silently spend
        // more than the operator asked for.
        var resolution = Resolve(settings: Settings(effort: ExecutionEffort.Minimal));

        Assert.False(resolution.Succeeded);
        Assert.Contains("does not accept effort 'minimal'", resolution.FailureReason);
        Assert.Contains("low, medium, high, xhigh, max", resolution.FailureReason);
    }

    [Fact]
    public void The_same_effort_is_accepted_for_a_vendor_that_supports_it()
    {
        // `ultra` is the live divergence: the GPT-5.6 family offers it and claude has no such level.
        var resolution = ExecutionProfileResolver.Resolve(
            "codex",
            "deep",
            null,
            Worker(),
            Settings("deep", "codex", "gpt-5.6-sol", ExecutionEffort.Ultra),
            new CodexAgentAdapter().DescribeExecutionCapability());

        Assert.True(resolution.Succeeded);
        Assert.Equal(ExecutionEffort.Ultra, resolution.Selection!.Effort);

        var onClaude = Resolve(
            commandLine: "deep",
            settings: Settings("deep", "claude", "opus", ExecutionEffort.Ultra));

        Assert.False(onClaude.Succeeded);
        Assert.Contains("does not accept effort 'ultra'", onClaude.FailureReason);
    }

    [Fact]
    public void An_empty_model_string_is_rejected_rather_than_reaching_a_command_line()
    {
        var resolution = Resolve(settings: Settings(model: "   "));

        Assert.False(resolution.Succeeded);
        Assert.Contains("empty model", resolution.FailureReason);
    }

    [Fact]
    public void An_empty_user_mapping_falls_through_to_the_built_in()
    {
        // A mapping stripped of both its model and effort is not an instruction to run bare; it is
        // an absent override, so the shipped tier applies.
        var resolution = Resolve(settings: Settings(model: null, effort: null));

        Assert.True(resolution.Succeeded);
        Assert.Equal(ExecutionEffort.Medium, resolution.Selection!.Effort);
        Assert.Equal(ExecutionMappingSource.BuiltIn, resolution.Selection.MappingSource);
    }

    [Fact]
    public void A_repository_default_resolves_when_the_item_says_nothing()
    {
        var resolution = Resolve();

        Assert.True(resolution.Succeeded);
        Assert.Equal("balanced", resolution.Selection!.Profile);
        Assert.Equal(ExecutionProfileSource.RepositoryDefault, resolution.Selection.Source);
    }

    [Fact]
    public void The_shipped_tiers_resolve_on_a_machine_with_no_settings_at_all()
    {
        // The zero-config promise, and the fix for a repository default breaking every unmapped
        // machine: an empty repository config and empty settings must still resolve.
        foreach (var (profile, expected) in new[]
                 {
                     (BuiltInExecutionProfiles.Economy, ExecutionEffort.Low),
                     (BuiltInExecutionProfiles.Balanced, ExecutionEffort.Medium),
                     (BuiltInExecutionProfiles.Deep, ExecutionEffort.High)
                 })
        {
            var resolution = ExecutionProfileResolver.Resolve(
                "claude", profile, null, new WorkerConfig(), new UserSettings(), ClaudeCapability);

            Assert.True(resolution.Succeeded, resolution.FailureReason);
            Assert.Equal(expected, resolution.Selection!.Effort);
            // Effort only: the vendor's own model still applies.
            Assert.Null(resolution.Selection.Model);
            Assert.Equal(ExecutionMappingSource.BuiltIn, resolution.Selection.MappingSource);
        }
    }

    [Fact]
    public void An_unmapped_repository_default_no_longer_breaks_a_plain_run()
    {
        var resolution = ExecutionProfileResolver.Resolve(
            "claude",
            commandLineProfile: null,
            itemProfile: null,
            new WorkerConfig { DefaultExecutionProfile = BuiltInExecutionProfiles.Balanced },
            new UserSettings(),
            ClaudeCapability);

        Assert.True(resolution.Succeeded);
        Assert.Equal(ExecutionEffort.Medium, resolution.Selection!.Effort);
    }

    [Fact]
    public void A_user_mapping_overrides_the_shipped_tier()
    {
        var resolution = Resolve(
            commandLine: "deep",
            settings: Settings("deep", model: "opus", effort: ExecutionEffort.XHigh));

        Assert.Equal("opus", resolution.Selection!.Model);
        Assert.Equal(ExecutionEffort.XHigh, resolution.Selection.Effort);
        Assert.Equal(ExecutionMappingSource.UserSettings, resolution.Selection.MappingSource);
    }

    [Fact]
    public void A_repository_name_wrighty_does_not_ship_still_needs_a_local_mapping()
    {
        var resolution = ExecutionProfileResolver.Resolve(
            "claude", "docs-only", null,
            new WorkerConfig { ExecutionProfiles = ["docs-only"] },
            new UserSettings(), ClaudeCapability);

        Assert.False(resolution.Succeeded);
        Assert.Contains("not one of Wrighty's built-in profiles", resolution.FailureReason);
    }

    [Fact]
    public void Resolution_never_downgrades_to_economy_on_its_own()
    {
        // The guard for the plan's hard rule: economy is reachable only by explicit resolution.
        // Nothing about a failure or a missing mapping may produce it.
        var settings = new UserSettings
        {
            WorkerProfiles = new Dictionary<string, IReadOnlyDictionary<string, ExecutionProfileMapping>>
            {
                ["economy"] = new Dictionary<string, ExecutionProfileMapping>
                {
                    ["claude"] = new() { Model = "haiku", Effort = ExecutionEffort.Low }
                }
            }
        };

        // Asking for 'deep' with only an 'economy' mapping present must resolve deep, never reach
        // for the cheaper tier the operator happens to have configured.
        var resolution = Resolve(commandLine: "deep", settings: settings);

        Assert.True(resolution.Succeeded);
        Assert.Equal(ExecutionEffort.High, resolution.Selection!.Effort);
        Assert.NotEqual(ExecutionEffort.Low, resolution.Selection.Effort);
        Assert.Null(resolution.Selection.Model);   // never 'haiku' from the economy entry
    }
}
