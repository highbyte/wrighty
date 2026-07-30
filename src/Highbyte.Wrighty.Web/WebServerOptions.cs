namespace Highbyte.Wrighty.Web;

public sealed record WebServerOptions(
    int Port = 0,
    bool OpenBrowser = true,
    string? BindAddress = null,
    IReadOnlyList<string>? AllowedHosts = null,
    string AuthMode = "token",
    bool PersistToken = false,
    string? TokenFile = null,
    bool RotateToken = false,
    string? PublicUrl = null);
