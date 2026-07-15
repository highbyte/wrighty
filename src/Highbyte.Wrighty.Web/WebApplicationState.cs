using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Web;

public sealed class WebApplicationState(TrackerConfig config, string token)
{
    public TrackerConfig Config { get; } = config;
    public string Token { get; } = token;
    public int Port { get; set; }
    public string Origin => $"http://127.0.0.1:{Port}";
}
