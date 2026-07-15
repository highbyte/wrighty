namespace Highbyte.Wrighty.Identity;

public interface IWorkerIdentityProvider
{
    Task<string> GetIdentityAsync(CancellationToken cancellationToken);
}
