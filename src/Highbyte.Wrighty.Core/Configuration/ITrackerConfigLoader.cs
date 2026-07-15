namespace Highbyte.Wrighty.Configuration;

public interface ITrackerConfigLoader
{
    Task<TrackerConfig> LoadAsync(string startDirectory, CancellationToken cancellationToken);
}
