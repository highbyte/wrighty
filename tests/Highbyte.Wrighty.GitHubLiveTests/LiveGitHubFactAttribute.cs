namespace Highbyte.Wrighty.GitHubLiveTests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LiveGitHubFactAttribute : FactAttribute
{
    public const string EnableVariable = "WRIGHTY_RUN_GITHUB_LIVE";

    public LiveGitHubFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {EnableVariable}=1 to run opt-in read-only GitHub live tests.";
        }
    }
}
