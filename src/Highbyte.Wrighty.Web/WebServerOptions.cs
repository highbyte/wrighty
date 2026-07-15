namespace Highbyte.Wrighty.Web;

public sealed record WebServerOptions(
    int Port = 0,
    bool OpenBrowser = true);
