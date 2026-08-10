using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// Pins the vendor capability surface this feature is built on, as observed on 2026-08-08 against
/// Claude Code 2.1.222, codex-cli 0.145.0, and GitHub Copilot CLI 1.0.78. These are assertions
/// about vendors, not about Wrighty's logic: when one of them fails after a vendor upgrade, the
/// correct response is to re-observe the CLI and update the adapter, not to relax the test.
/// </summary>
public sealed class AgentExecutionCapabilityTests
{
    private static AgentExecutionCapability Capability(IAgentAdapter adapter) =>
        adapter.DescribeExecutionCapability();

    [Fact]
    public void Every_supported_vendor_accepts_an_explicit_model()
    {
        Assert.True(Capability(new ClaudeAgentAdapter()).SupportsModel);
        Assert.True(Capability(new CodexAgentAdapter()).SupportsModel);
        Assert.True(Capability(new CopilotAgentAdapter()).SupportsModel);
    }

    [Fact]
    public void Claude_rejects_the_two_lowest_effort_levels_the_other_vendors_accept()
    {
        var claude = Capability(new ClaudeAgentAdapter());

        // `claude --effort` documents exactly: low, medium, high, xhigh, max.
        Assert.False(claude.Supports(ExecutionEffort.None));
        Assert.False(claude.Supports(ExecutionEffort.Minimal));
        Assert.True(claude.Supports(ExecutionEffort.Low));
        Assert.True(claude.Supports(ExecutionEffort.Max));
        Assert.Equal(5, claude.SupportedEfforts.Count);
    }

    [Fact]
    public void Codex_offers_ultra_but_not_the_two_lowest_levels()
    {
        // From a `model/list` capability query on 2026-08-08: every model offered low..max and the
        // GPT-5.6 family added `ultra`. None advertised `none` or `minimal`, despite an API
        // rejection message having once listed them — the capability query is the better source.
        var codex = Capability(new CodexAgentAdapter());

        Assert.True(codex.Supports(ExecutionEffort.Ultra));
        Assert.False(codex.Supports(ExecutionEffort.None));
        Assert.False(codex.Supports(ExecutionEffort.Minimal));
    }

    [Fact]
    public void Copilot_offers_the_two_lowest_levels_but_not_ultra()
    {
        // Copilot's own `--effort` help enumerates none..max, with no `ultra`.
        var copilot = Capability(new CopilotAgentAdapter());

        Assert.True(copilot.Supports(ExecutionEffort.None));
        Assert.True(copilot.Supports(ExecutionEffort.Minimal));
        Assert.False(copilot.Supports(ExecutionEffort.Ultra));
    }

    [Fact]
    public void Every_vendor_accepts_the_three_levels_the_shipped_profiles_use()
    {
        // The built-in economy/balanced/deep tiers map to these, so all three must work everywhere
        // — including on codex's older models, which stop at xhigh.
        foreach (var adapter in new IAgentAdapter[]
                 { new ClaudeAgentAdapter(), new CodexAgentAdapter(), new CopilotAgentAdapter() })
        {
            var capability = Capability(adapter);
            foreach (var effort in new[]
                     { ExecutionEffort.Low, ExecutionEffort.Medium, ExecutionEffort.High })
            {
                Assert.True(
                    capability.Supports(effort),
                    $"{capability.Agent} must accept '{effort.ToToken()}'");
            }
        }
    }

    [Fact]
    public void Effort_levels_have_stable_lowercase_wire_tokens()
    {
        // The token reaches a vendor command line, so a casing slip is a launch failure.
        Assert.Equal("xhigh", ExecutionEffort.XHigh.ToToken());
        Assert.Equal("minimal", ExecutionEffort.Minimal.ToToken());
        Assert.Equal(
            ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"],
            ExecutionEfforts.All);
    }

    [Theory]
    [InlineData("high", ExecutionEffort.High)]
    [InlineData("HIGH", ExecutionEffort.High)]
    [InlineData("xhigh", ExecutionEffort.XHigh)]
    public void Stored_effort_values_parse_case_insensitively(string stored, ExecutionEffort expected)
    {
        Assert.True(ExecutionEfforts.TryParse(stored, out var effort));
        Assert.Equal(expected, effort);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("hi")]          // no prefix matching onto "high"
    [InlineData("x-high")]      // no punctuation-insensitive matching
    [InlineData("bogus")]
    [InlineData("very high")]
    public void Unrecognized_effort_values_are_rejected_rather_than_approximated(string? stored)
    {
        Assert.False(ExecutionEfforts.TryParse(stored, out _));
    }

    [Fact]
    public async Task The_version_probe_reads_a_real_vendor_cli_and_caches_it()
    {
        // Uses `echo` rather than a vendor, so the test does not depend on which agents happen to
        // be installed on the machine running it.
        var probe = new AgentVersionProbe(new PathExecutableResolver());
        var version = await probe.TryGetVersionAsync("echo", CancellationToken.None);

        // `echo --version` prints "--version" on macOS/BSD and a GNU banner on Linux; either way a
        // zero exit means the first non-empty line is what gets recorded.
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(version, await probe.TryGetVersionAsync("echo", CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_agent_yields_no_version_rather_than_throwing()
    {
        var probe = new AgentVersionProbe(new PathExecutableResolver());
        Assert.Null(await probe.TryGetVersionAsync(
            "wrighty-no-such-agent-abc123", CancellationToken.None));
    }
}
