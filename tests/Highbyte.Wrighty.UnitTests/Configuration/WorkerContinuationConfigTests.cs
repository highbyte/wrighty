using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Configuration;

/// <summary>
/// The settings that decide when a trusted author's reply continues a waiting session.
///
/// The trigger mode is the one worth guarding: its permissive value is the default, so an
/// unrecognised setting that quietly fell back would widen what continues a session for exactly the
/// operator who was trying to narrow it.
/// </summary>
public sealed class WorkerContinuationConfigTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-continuation-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task<TrackerConfig> LoadAsync(string continuationJson)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            $$"""
            {
              "backend": "github",
              "github": { "repository": "owner/repo", "projectNumber": 1 },
              "worker": { "continuation": {{continuationJson}} }
            }
            """);
        return await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
    }

    [Fact]
    public void TheDefaultsContinueOnAnyTrustedReply()
    {
        var continuation = new WorkerConfig().EffectiveContinuation;

        Assert.Equal(
            WorkerContinuationConfig.TriggerModes.AnyTrustedComment, continuation.Trigger);
        Assert.False(continuation.RequiresCommand);
        Assert.Equal(10, continuation.MaxAutomaticContinuations);
        Assert.Equal(TimeSpan.FromSeconds(30), continuation.Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(10), continuation.Debounce);
        Assert.Equal("rocket", continuation.ResumeReaction);
        Assert.Equal("hooray", continuation.CompletionReaction);
    }

    [Fact]
    public async Task CommandOnlyBindsAndRequiresTheCommand()
    {
        var config = await LoadAsync("""{ "trigger": "command-only" }""");

        Assert.True(config.EffectiveWorker.EffectiveContinuation.RequiresCommand);
        Assert.Equal("/wrighty continue", config.EffectiveWorker.EffectiveContinuation.Command);
    }

    [Fact]
    public async Task AnUnrecognisedTriggerFailsInsteadOfFallingBack()
    {
        // A typo such as "command_only" must not resolve to the permissive default. Silently
        // widening the trigger is the failure an operator would never think to look for.
        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => LoadAsync("""{ "trigger": "command_only" }"""));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Contains("command-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_or_conflicting_control_reactions_fail_configuration()
    {
        var unsupported = await Assert.ThrowsAsync<TrackerException>(
            () => LoadAsync("""{ "resumeReaction": "ship-it" }"""));
        Assert.Equal("CONFIG_INVALID", unsupported.Code);

        var conflicting = await Assert.ThrowsAsync<TrackerException>(
            () => LoadAsync(
                """{ "resumeReaction": "rocket", "completionReaction": "rocket" }"""));
        Assert.Equal("CONFIG_INVALID", conflicting.Code);
        Assert.Contains("must be different", conflicting.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AbsentContinuationMeansDefaultsRatherThanDisabled()
    {
        // There is no enable switch by design: naming a trusted author is the opt-in, so an absent
        // section must not read as "off".
        var config = await LoadAsync("""{ }""");

        Assert.Equal(
            WorkerContinuationConfig.TriggerModes.AnyTrustedComment,
            config.EffectiveWorker.EffectiveContinuation.Trigger);
    }

    [Fact]
    public async Task NegativeIntervalsClampRatherThanInvertingTheControl()
    {
        var config = await LoadAsync(
            """{ "cooldownSeconds": -5, "debounceSeconds": -5 }""");
        var continuation = config.EffectiveWorker.EffectiveContinuation;

        Assert.Equal(TimeSpan.Zero, continuation.Cooldown);
        Assert.Equal(TimeSpan.Zero, continuation.Debounce);
    }
}
