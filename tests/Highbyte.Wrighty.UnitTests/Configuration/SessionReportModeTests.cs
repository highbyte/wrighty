using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.UnitTests.Configuration;

/// <summary>
/// The gate on publishing. Reports go to a surface other people read, so the question this settles
/// is whether Wrighty writes there at all.
/// </summary>
public class SessionReportModeTests
{
    [Fact]
    public void PublishingIsOffWhenNothingIsConfigured()
    {
        // An upgrade must not start commenting on someone's issues because they took a new version.
        Assert.Equal(SessionReportMode.Off, new WorkerConfig().EffectiveSessionReportMode);
        Assert.Equal(
            SessionReportMode.Off, new TrackerConfig().EffectiveWorker.EffectiveSessionReportMode);
    }

    [Theory]
    [InlineData("off", SessionReportMode.Off)]
    [InlineData("completed", SessionReportMode.Completed)]
    [InlineData("ALL", SessionReportMode.All)]
    public void ConfiguredModesBind(string configured, SessionReportMode expected) =>
        Assert.Equal(
            expected,
            new WorkerConfig { SessionReportMode = configured }.EffectiveSessionReportMode);

    [Fact]
    public async Task AMisspelledModeIsRejectedRatherThanSilentlyMeaningOff()
    {
        // Publishing is what the operator asked for. Quietly not doing it looks exactly like it
        // working, and they would find out only by going to look for reports that never appeared.
        var directory = Path.Combine(Path.GetTempPath(), $"wrighty-report-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, TrackerConfigLoader.FileName),
                """
                {
                  "backend": "local-markdown",
                  "localMarkdown": { "path": ".wrighty" },
                  "worker": { "sessionReportMode": "everything" }
                }
                """);

            var failure = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
                () => new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None));

            Assert.Contains("sessionReportMode", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
