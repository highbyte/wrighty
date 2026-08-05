using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.UnitTests.Configuration;

/// <summary>Compatibility for configurations written before reports joined the handover.</summary>
public class SessionReportModeTests
{
    [Theory]
    [InlineData("off")]
    [InlineData("completed")]
    [InlineData("ALL")]
    public async Task Legacy_values_remain_accepted(string configured)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wrighty-report-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, TrackerConfigLoader.FileName),
                $$"""
                {
                  "backend": "local-markdown",
                  "defaultPickFrom": "Todo",
                  "localMarkdown": { "path": ".wrighty" },
                  "worker": { "sessionReportMode": "{{configured}}" }
                }
                """);

            var config = await new TrackerConfigLoader().LoadAsync(
                directory, CancellationToken.None);

            Assert.Equal(configured, config.Worker?.SessionReportMode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task AMisspelledModeIsRejectedRatherThanSilentlyMeaningOff()
    {
        // A typo is still rejected while the legacy property remains accepted; silently accepting
        // arbitrary values would hide configuration mistakes during the compatibility period.
        var directory = Path.Combine(Path.GetTempPath(), $"wrighty-report-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, TrackerConfigLoader.FileName),
                """
                {
                  "backend": "local-markdown",
                  "defaultPickFrom": "Todo",
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
