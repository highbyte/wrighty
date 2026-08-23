using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Web;

/// <summary>
/// What a configuration save tells the operator it did.
///
/// A save can do two independent things: change values, and clear values an earlier Wrighty
/// version wrote into the file. Reporting only the first left a save that did nothing but migrate
/// looking like a save that did nothing at all — which is what an operator sees after acting on
/// the legacy-properties notice, since that save changes no value by design.
/// </summary>
public static class ConfigurationSaveNotice
{
    public static string Describe(RepositoryConfigurationMutationResult result)
    {
        string notice;
        if (result.Changes.Count == 0)
            notice = "Configuration already matched the submitted values.";
        else if (result.RestartRequired)
            notice = "Configuration saved. Restart this web process and any affected workers to apply it.";
        else
            notice = "Configuration saved. The change applies without restarting Wrighty.";
        if (result.MigratedLegacyProperties is not { Count: > 0 } removed)
            return notice;
        var values = removed.Count == 1 ? "value" : "values";
        return $"{notice} Removed {removed.Count} {values} written by an earlier Wrighty version.";
    }
}
