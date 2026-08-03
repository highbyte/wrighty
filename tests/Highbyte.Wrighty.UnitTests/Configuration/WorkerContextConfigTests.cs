using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.UnitTests.Configuration;

/// <summary>
/// The context bounds a launch applies. Their refusal messages name these settings by path and tell
/// an operator to raise them, so a key that does not bind would send someone editing configuration
/// that changes nothing.
/// </summary>
public sealed class WorkerContextConfigTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-context-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public async Task TheDocumentedKeysBindFromConfiguration()
    {
        // Loaded through the real loader rather than a hand-built serializer, so this covers the
        // naming the file actually uses.
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            """
            {
              "backend": "local-markdown",
              "defaultPickFrom": "Todo",
              "localMarkdown": { "path": ".wrighty" },
              "worker": {
                "context": {
                  "maxDiscussionComments": 7,
                  "maxEntryCharacters": 900,
                  "maxTotalCharacters": 5000
                }
              }
            }
            """);

        var config = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);

        var limits = config.EffectiveWorker.EffectiveContext.ToLimits();

        Assert.Equal(7, limits.MaxDiscussionEntries);
        Assert.Equal(900, limits.MaxEntryCharacters);
        Assert.Equal(5000, limits.MaxTotalCharacters);
    }

    [Fact]
    public void AbsentConfigurationKeepsTheBuiltInDefaults()
    {
        var limits = new TrackerConfig().EffectiveWorker.EffectiveContext.ToLimits();

        Assert.Equal(ContextLimits.Default, limits);
    }

    [Fact]
    public void AConfiguredLimitActuallyRefusesAContextTheDefaultWouldAdmit()
    {
        // The binding is only half of it: a bound value that never reaches the check would leave the
        // setting inert while looking correct in configuration output.
        var config = new TrackerConfig
        {
            Worker = new WorkerConfig { Context = new WorkerContextConfig { MaxTotalCharacters = 20 } }
        };
        var limits = config.EffectiveWorker.EffectiveContext.ToLimits();

        var result = ContextLimitResult.Check(
            "A title well past twenty characters", "and a body too", [], [], limits);

        Assert.False(result.Within);
        Assert.Equal(ContextLimitResult.TooLargeCode, result.Code);
        Assert.Contains("maxTotalCharacters", result.Message!, StringComparison.Ordinal);
    }
}
