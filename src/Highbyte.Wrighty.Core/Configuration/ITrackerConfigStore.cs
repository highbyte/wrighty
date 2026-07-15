namespace Highbyte.Wrighty.Configuration;

public interface ITrackerConfigStore : ITrackerConfigLoader
{
    string ResolvePath(string startDirectory, string? explicitPath);

    Task<TrackerConfig?> TryLoadPathAsync(string path, CancellationToken cancellationToken);

    Task SaveAsync(string path, TrackerConfig config, CancellationToken cancellationToken);
}
