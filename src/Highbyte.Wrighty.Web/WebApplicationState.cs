using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Highbyte.Wrighty.Web;

public sealed class WebApplicationState(TrackerConfig config, string token)
{
    private readonly ConcurrentDictionary<string, ClaimHandle> handles = new(StringComparer.Ordinal);
    public TrackerConfig Config { get; } = config;
    public string Token { get; } = token;
    public string ClaimantId { get; } = $"web:{Guid.NewGuid():N}";
    public AgentExecutionContext ClaimantContext => new(null, null, AgentContextSource.ExplicitOption,
        ClaimantKind: ClaimantKind.Human, ClaimantId: ClaimantId);
    public void Retain(string itemId, ClaimResult result) =>
        handles[itemId] = new ClaimHandle(ClaimantContext, result.ClaimToken);
    public bool TryHandle(string itemId, out ClaimHandle handle) => handles.TryGetValue(itemId, out handle!);
    public void Forget(string itemId) => handles.TryRemove(itemId, out _);
    public string? Generation(string itemId) => TryHandle(itemId, out var handle) && handle.ClaimToken is { } tokenValue
        ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue))) : null;
    public int Port { get; set; }
    public string Origin => $"http://127.0.0.1:{Port}";
}
