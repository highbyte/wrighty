namespace Highbyte.Wrighty.Web;

public interface IWrightyWebServer
{
    Task RunAsync(
        WebServerOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken);
}
