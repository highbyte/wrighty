using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.GitHub;

/// <summary>The GitHub login this installation posts as, or null when it cannot be established.</summary>
public interface IGitHubViewerIdentity
{
    Task<string?> GetLoginAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the authenticated login once and remembers it.
///
/// This exists to answer one question — is this comment one of ours? — which is a question about
/// authorship rather than authority. It is deliberately not part of the approver policy: whose
/// judgement counts needs roles, teams, and explicit users, while whose comment this is needs only
/// the account the token belongs to.
///
/// **Null is the failure answer, and callers must treat it as "exclude nothing".** An identity that
/// resolved wrongly in the permissive direction would silently drop comments from what a maintainer
/// reviews, which is the failure the exclusion boundary exists to prevent. Failing closed costs an
/// unnecessary re-approval; failing open hides requirements.
///
/// The result is cached for the process because it cannot change under a running worker: the token
/// is fixed for the lifetime of the process, and a lookup on every comment of every conversation
/// read would be a request per item in a polling loop.
/// </summary>
public sealed class GitHubViewerIdentity(GhApi api) : IGitHubViewerIdentity
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string?> byHost = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string?> GetLoginAsync(string host, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (byHost.TryGetValue(host, out var cached))
                return cached;

            string? login = null;
            try
            {
                using var document = await api.GetAsync(host, "user", cancellationToken);
                if (document.RootElement.TryGetProperty("login", out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String)
                    login = value.GetString();
            }
            catch (TrackerException)
            {
                // Unauthenticated, rate limited, offline: all the same answer here. Nothing is
                // excluded, every marker comment stays ordinary discussion, and the launch gate
                // decides it as it would any other comment.
                login = null;
            }

            byHost[host] = string.IsNullOrWhiteSpace(login) ? null : login;
            return byHost[host];
        }
        finally
        {
            gate.Release();
        }
    }
}
