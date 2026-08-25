using System.Reflection;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

/// <summary>
/// Every POST handler is treated as a work-item mutation unless it is named as shared, and a
/// work-item mutation is refused on a backend that owns its own items. So forgetting to classify a
/// new handler does not fail here or in review — it fails for an operator on GitHub, with a message
/// about work items for something that has nothing to do with them.
///
/// That is exactly what happened twice: user settings, and provider capacity probing.
/// This test enumerates the handlers that actually exist, so the next one cannot be forgotten
/// silently.
/// </summary>
public sealed class SharedMutationHandlerTests
{
    /// <summary>
    /// Handlers that genuinely edit a work item, and so are correctly refused where the backend
    /// owns them. Listing them explicitly is what makes the assertion below meaningful: a new
    /// handler belongs in this list or in the shared one, and the test says which is missing.
    /// </summary>
    private static readonly HashSet<string> WorkItemHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Create", "Claim", "Save", "Release", "Archive", "Delete", "ClaimAndArchive", "ArchiveItem",
        "Unarchive", "Takeover", "OverrideRelease", "QueueItem", "MoveItem", "DequeueItem",
        // Queue and panel actions belong to the Local Markdown item surface. Operations' direct
        // OpenSession actions are shared instead: they change Wrighty's claim/session metadata,
        // not backend-owned item content.
        "ResumeSession", "HoldSession", "QueueForWorker",
        "LaunchAgentCli", "LaunchAgentDesktop"
    };

    public static TheoryData<string> PostHandlers()
    {
        var data = new TheoryData<string>();
        foreach (var name in typeof(WrightyWebServer).Assembly
                     .GetTypes()
                     .Where(type => type.Name == "IndexModel")
                     .SelectMany(type => type.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                     .Select(method => method.Name)
                     .Where(name => name.StartsWith("OnPost", StringComparison.Ordinal))
                     .Select(name => name["OnPost".Length..])
                     .Select(name => name.EndsWith("Async", StringComparison.Ordinal)
                         ? name[..^"Async".Length]
                         : name)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PostHandlers))]
    public void Every_post_handler_is_classified_as_shared_or_as_a_work_item_edit(string handler)
    {
        var shared = WrightyWebServer.IsSharedMutation(handler);
        var workItem = WorkItemHandlers.Contains(handler);

        Assert.True(
            shared ^ workItem,
            $"Handler '{handler}' is classified as neither shared nor a work-item edit, or as both. " +
            "An unclassified handler is refused on GitHub with a work-item error message. Add it to " +
            "WrightyWebServer.IsSharedMutation if it does not edit a work item, or to this test's " +
            "WorkItemHandlers if it does.");
    }

    [Theory]
    // The two that were wrong, pinned by name so a refactor cannot quietly drop them again.
    [InlineData("UserConfiguration")]
    [InlineData("ProbeAllProviders")]
    [InlineData("ProbeProvider")]
    [InlineData("Configuration")]
    [InlineData("InstallSkill")]
    [InlineData("UpdateSkill")]
    [InlineData("UninstallSkill")]
    [InlineData("MaintainAllSkills")]
    [InlineData("OpenSessionCli")]
    [InlineData("OpenSessionDesktop")]
    public void Machine_local_and_provider_posts_survive_a_backend_that_owns_its_items(string handler) =>
        Assert.True(WrightyWebServer.IsSharedMutation(handler));
}
