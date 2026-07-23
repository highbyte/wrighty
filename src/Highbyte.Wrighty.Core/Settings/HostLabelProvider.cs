namespace Highbyte.Wrighty.Settings;

/// <summary>
/// Resolves the host label shown to remote parties (currently the GitHub handover comment). Privacy
/// is the default: unless the operator sets a symbolic label with <c>wrighty config set-host</c>, no
/// real machine name is published — the placeholder <see cref="AnonymousLabel"/> is shown instead.
/// </summary>
public interface IHostLabelProvider
{
    Task<string> GetHostLabelAsync(CancellationToken cancellationToken);
}

public sealed class HostLabelProvider(UserSettingsStore store) : IHostLabelProvider
{
    /// <summary>Placeholder published when no symbolic host label is configured. Chosen so the real
    /// machine name never leaves the machine by default.</summary>
    public const string AnonymousLabel = "anonymous";

    private string? cached;

    public async Task<string> GetHostLabelAsync(CancellationToken cancellationToken)
    {
        if (cached is not null)
        {
            return cached;
        }

        var settings = await store.LoadAsync(cancellationToken);
        return cached = string.IsNullOrWhiteSpace(settings.HostLabel)
            ? AnonymousLabel
            : settings.HostLabel.Trim();
    }
}

/// <summary>
/// Default provider used when no user settings store is wired in (e.g. tests): always the
/// privacy-preserving <see cref="HostLabelProvider.AnonymousLabel"/> placeholder, matching the
/// unconfigured production default.
/// </summary>
public sealed class AnonymousHostLabelProvider : IHostLabelProvider
{
    public Task<string> GetHostLabelAsync(CancellationToken cancellationToken) =>
        Task.FromResult(HostLabelProvider.AnonymousLabel);
}
