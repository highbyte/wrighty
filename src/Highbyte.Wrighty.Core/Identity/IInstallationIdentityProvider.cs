namespace Highbyte.Wrighty.Identity;

public interface IInstallationIdentityProvider
{
    Task<string> GetInstallationIdAsync(CancellationToken cancellationToken);
}
