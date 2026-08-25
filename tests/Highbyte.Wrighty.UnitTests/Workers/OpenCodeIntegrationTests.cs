using System.Text;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>Fixtures captured from OpenCode 1.18.23 on 2026-08-25.</summary>
public sealed class OpenCodeIntegrationTests
{
    private static readonly Workspace Workspace = new("/tmp/repository");
    private static readonly SessionHandle Handle = new("ses_existing");

    [Fact]
    public void Start_uses_json_stdin_auto_mode_and_inline_workspace_permissions()
    {
        var adapter = new OpenCodeAgentAdapter();
        var item = new WorkItemDetail(
            new WorkItemId("local:42"), "Title", "Body", null, "Todo", null);

        var invocation = adapter.BuildStart(
            item, Handle, Workspace, AgentPermissionProfile.Workspace);

        Assert.Equal("opencode", invocation.Executable);
        Assert.Equal("/tmp/repository", invocation.WorkingDirectory);
        Assert.True(invocation.CloseStandardInput);
        Assert.Contains("local:42", invocation.StandardInput);
        Assert.Contains("--format", invocation.Arguments);
        Assert.Contains("json", invocation.Arguments);
        Assert.Contains("--auto", invocation.Arguments);
        Assert.Contains("--pure", invocation.Arguments);
        Assert.DoesNotContain(invocation.Arguments, argument => argument.Contains("local:42"));
        var config = invocation.Environment["OPENCODE_CONFIG_CONTENT"];
        Assert.Contains("\"external_directory\":\"deny\"", config);
        Assert.Contains("\"question\":\"deny\"", config);
        Assert.Equal(
            AgentPermissionEnforcement.Partial,
            adapter.DescribePermissions(AgentPermissionProfile.Workspace).Enforcement);
    }

    [Fact]
    public void Read_only_denies_everything_except_local_read_and_search_tools()
    {
        var invocation = new OpenCodeAgentAdapter().BuildStartWithPrompt(
            Handle, Workspace, AgentPermissionProfile.ReadOnly, "inspect");

        var config = invocation.Environment["OPENCODE_CONFIG_CONTENT"];
        Assert.Contains("\"*\":\"deny\"", config);
        Assert.Contains("\"read\":\"allow\"", config);
        Assert.Contains("\"grep\":\"allow\"", config);
        Assert.DoesNotContain("\"bash\":\"allow\"", config);
        var permissions = new OpenCodeAgentAdapter().DescribePermissions(
            AgentPermissionProfile.ReadOnly);
        Assert.Equal(AgentPermissionEnforcement.Enforced, permissions.Enforcement);
        Assert.True(permissions.ConfinesFileWrites);
        Assert.False(permissions.AllowsNetwork);
    }

    [Fact]
    public void Full_permissions_are_explicitly_unrestricted()
    {
        var adapter = new OpenCodeAgentAdapter();
        var invocation = adapter.BuildStartWithPrompt(
            Handle, Workspace, AgentPermissionProfile.Full, "work");

        Assert.Contains(
            "\"*\":\"allow\"",
            invocation.Environment["OPENCODE_CONFIG_CONTENT"]);
        Assert.Equal(
            AgentPermissionEnforcement.Unrestricted,
            adapter.DescribePermissions(AgentPermissionProfile.Full).Enforcement);
    }

    [Fact]
    public void Resume_carries_the_recorded_session_as_a_separate_argument()
    {
        var invocation = ((IAgentResumeAdapter)new OpenCodeAgentAdapter()).BuildResumeWithPrompt(
            Handle, Workspace, AgentPermissionProfile.Workspace, "continue");

        AssertAdjacent(invocation.Arguments, "--session", "ses_existing");
        Assert.Equal("continue", invocation.StandardInput);
    }

    [Fact]
    public async Task Json_events_supply_generated_session_text_and_terminal_success()
    {
        var fixture = string.Join('\n',
            """{"type":"step_start","sessionID":"ses_new","part":{"type":"step-start"}}""",
            """{"type":"text","sessionID":"ses_new","part":{"type":"text","text":"OK"}}""",
            """{"type":"step_finish","sessionID":"ses_new","part":{"type":"step-finish","reason":"stop"}}""") + "\n";

        var result = await new OpenCodeAgentAdapter().InterpretAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(fixture)),
            0,
            CancellationToken.None);

        Assert.Equal(AgentOutcome.Succeeded, result.Outcome);
        Assert.Equal("ses_new", result.SessionId);
        Assert.Equal("OK", result.FinalMessage);
    }

    [Fact]
    public async Task Missing_terminal_step_fails_even_when_the_process_exit_is_zero()
    {
        const string fixture =
            """{"type":"text","sessionID":"ses_new","part":{"type":"text","text":"partial"}}""";

        var result = await new OpenCodeAgentAdapter().InterpretAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(fixture)),
            0,
            CancellationToken.None);

        Assert.Equal(AgentOutcome.Failed, result.Outcome);
        Assert.Equal("partial", result.FinalMessage);
    }

    [Fact]
    public async Task Output_without_a_session_id_is_rejected()
    {
        const string fixture =
            """{"type":"step_finish","part":{"type":"step-finish","reason":"stop"}}""";

        var result = await new OpenCodeAgentAdapter().InterpretAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(fixture)),
            0,
            CancellationToken.None);

        Assert.Equal(AgentOutcome.Rejected, result.Outcome);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public void Session_id_extraction_ignores_non_json_diagnostics()
    {
        var adapter = new OpenCodeAgentAdapter();

        Assert.Equal(
            "ses_123",
            adapter.TryExtractSessionId("""{"sessionID":"ses_123"}"""));
        Assert.Null(adapter.TryExtractSessionId("OpenCode diagnostic"));
    }

    [Fact]
    public async Task Model_discovery_reads_provider_qualified_models_and_known_variants()
    {
        var command = new StubCommand(new BoundedAgentCommandResult(
            BoundedAgentCommandStatus.Completed,
            0,
            """
            anthropic/claude-sonnet-4-5
            {
              "id": "claude-sonnet-4-5",
              "providerID": "anthropic",
              "name": "Claude Sonnet 4.5",
              "capabilities": { "reasoning": true },
              "variants": { "high": {}, "max": {}, "turbo": {} }
            }
            opencode/big-pickle
            {
              "id": "big-pickle",
              "providerID": "opencode",
              "name": "Big Pickle",
              "capabilities": { "reasoning": false },
              "variants": {}
            }
            """));

        var catalog = await new OpenCodeModelDiscovery(
            command,
            () => DateTimeOffset.UnixEpoch).DiscoverAsync(CancellationToken.None);

        Assert.True(catalog.Succeeded);
        Assert.Equal("opencode", catalog.Agent);
        var reasoning = catalog.Find("anthropic/claude-sonnet-4-5")!;
        Assert.Equal("Claude Sonnet 4.5", reasoning.DisplayName);
        Assert.Equal(EffortSupport.Yes, reasoning.Effort);
        Assert.Equal(["high", "max"], reasoning.Efforts);
        Assert.Equal(EffortSupport.No, catalog.Find("opencode/big-pickle")!.Effort);
        Assert.Equal(["models", "--verbose"], command.Arguments);
    }

    [Fact]
    public async Task Model_discovery_maps_bounded_command_failures_without_throwing()
    {
        var discovery = new OpenCodeModelDiscovery(new StubCommand(
            new BoundedAgentCommandResult(BoundedAgentCommandStatus.TimedOut)));

        var catalog = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ModelDiscoveryFailure.TimedOut, catalog.Failure);
        Assert.Empty(catalog.Models);
    }

    [Fact]
    public async Task Model_discovery_rejects_a_changed_object_shape_as_unrecognized()
    {
        var discovery = new OpenCodeModelDiscovery(new StubCommand(
            new BoundedAgentCommandResult(
                BoundedAgentCommandStatus.Completed,
                0,
                """opencode/model\n{"model":"model","provider":"opencode"}""")));

        var catalog = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ModelDiscoveryFailure.Unrecognized, catalog.Failure);
    }

    [Fact]
    public async Task Model_discovery_recognizes_authentication_failure_on_standard_output()
    {
        var discovery = new OpenCodeModelDiscovery(new StubCommand(
            new BoundedAgentCommandResult(
                BoundedAgentCommandStatus.Completed,
                1,
                "Please log in to the provider.")));

        var catalog = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(ModelDiscoveryFailure.NotAuthenticated, catalog.Failure);
    }

    [Fact]
    public async Task Export_keeps_only_user_and_assistant_text_parts()
    {
        var command = new StubCommand(new BoundedAgentCommandResult(
            BoundedAgentCommandStatus.Completed,
            0,
            """
            Exporting session: ses_123
            {
              "info": { "id": "ses_123" },
              "messages": [
                {
                  "info": { "role": "user" },
                  "parts": [
                    { "type": "text", "text": "Please inspect this." },
                    { "type": "file", "url": "file:///secret" }
                  ]
                },
                {
                  "info": { "role": "assistant" },
                  "parts": [
                    { "type": "reasoning", "text": "private chain" },
                    { "type": "text", "text": "Done." }
                  ]
                }
              ]
            }
            """));

        var result = await new OpenCodeSessionExporter(command)
            .ExportAsync("ses_123", CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("opencode-cli-export", result.Source);
        Assert.Equal(
            [
                new ExportedSessionMessage("user", "Please inspect this."),
                new ExportedSessionMessage("assistant", "Done.")
            ],
            result.Messages);
        Assert.Equal(["export", "ses_123"], command.Arguments);
    }

    [Theory]
    [InlineData("../session")]
    [InlineData("ses 123")]
    [InlineData("")]
    public async Task Export_rejects_unsafe_session_ids_before_starting_a_process(string sessionId)
    {
        var command = new StubCommand(new BoundedAgentCommandResult(
            BoundedAgentCommandStatus.Completed));

        var result = await new OpenCodeSessionExporter(command)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Null(command.Arguments);
    }

    [Fact]
    public async Task Export_maps_bounded_command_failure_to_a_useful_reason()
    {
        var cases = new[]
        {
            (BoundedAgentCommandStatus.NotInstalled, "not installed"),
            (BoundedAgentCommandStatus.TimedOut, "time limit"),
            (BoundedAgentCommandStatus.OutputTooLarge, "export limit"),
            (BoundedAgentCommandStatus.Unavailable, "unavailable")
        };
        foreach (var (status, expected) in cases)
        {
            var result = await new OpenCodeSessionExporter(new StubCommand(
                new BoundedAgentCommandResult(status)))
                .ExportAsync("ses_123", CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.Contains(expected, result.Unavailable!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(1, "{}")]
    [InlineData(0, "not json")]
    public async Task Export_degrades_when_the_cli_fails_or_changes_shape(
        int exitCode,
        string output)
    {
        var result = await new OpenCodeSessionExporter(new StubCommand(
            new BoundedAgentCommandResult(
                BoundedAgentCommandStatus.Completed,
                exitCode,
                output)))
            .ExportAsync("ses_123", CancellationToken.None);

        Assert.False(result.IsAvailable);
    }

    [Fact]
    public void Registry_declares_intentionally_partial_desktop_support()
    {
        var integration = BuiltInAgentRegistry.Create(new MissingExecutableResolver())
            .GetRequired("opencode");

        Assert.Null(integration.DesktopAdapter);
        Assert.Null(integration.Descriptor.LocalLaunch);
        Assert.False(integration.Descriptor.Capabilities.HasFlag(AgentCapabilities.DesktopLaunch));
        Assert.True(integration.Descriptor.Capabilities.HasFlag(AgentCapabilities.GitHubProjection));
        Assert.Equal("OpenCode", integration.Descriptor.Projection!.OptionName);
        Assert.Equal("PURPLE", integration.Descriptor.Projection.Color);
    }

    private static void AssertAdjacent(
        IReadOnlyList<string> arguments,
        string flag,
        string value)
    {
        Assert.Contains(
            Enumerable.Range(0, arguments.Count - 1),
            index => arguments[index] == flag && arguments[index + 1] == value);
    }

    private sealed class StubCommand(BoundedAgentCommandResult result) : IBoundedAgentCommand
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<BoundedAgentCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            int maximumOutputBytes,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Assert.Equal("opencode", executable);
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }

    private sealed class MissingExecutableResolver : Highbyte.Wrighty.Processes.IExecutableResolver
    {
        public string Resolve(string executableName) => throw new FileNotFoundException();
    }
}
