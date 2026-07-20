using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Cli;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.UnitTests.Cli;

public sealed class WholeStoreImportServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-whole-store-{Guid.NewGuid():N}");
    private readonly LocalMarkdownTrackerBackend local =
        new(new TestIdentity(), new TestClock());
    private readonly ImportTargetBackend github = new();

    [Theory]
    [InlineData(false, "ARGUMENT_INVALID")]
    [InlineData(true, "NOT_SUPPORTED")]
    public async Task Requires_complete_configuration_and_GitHub_destination(
        bool useLocalBackend,
        string expectedCode)
    {
        var config = useLocalBackend
            ? Config() with { Backend = "local-markdown" }
            : new TrackerConfig
            {
                Backend = "github",
                GitHub = new GitHubBackendConfig
                {
                    Repository = "owner/repo",
                    ProjectNumber = 1
                }
            };

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => Service().RunAsync(config, Options(dryRun: true), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(0, github.InitializeCalls);
    }

    [Fact]
    public async Task Dry_run_preflights_fields_maps_values_and_writes_nothing()
    {
        var config = Config();
        await InitializeAndCreateAsync(
            config,
            "First",
            "Body",
            "Todo",
            "P1",
            new Dictionary<string, string?> { ["epic"] = "PLAT-3" });

        var summary = await Service().RunAsync(
            config,
            Options(
                dryRun: true,
                statusMappings: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Todo"] = "Backlog"
                }),
            CancellationToken.None);

        Assert.True(summary.DryRun);
        Assert.Equal(1, summary.Planned);
        Assert.Contains("local:1 -> Backlog / P1", summary.PlannedItems.Single());
        Assert.Equal(1, github.InitializeCalls);
        Assert.True(github.LastCheckOnly);
        Assert.Equal([("Backlog", "P1")], github.ValidatedFields);
        Assert.Equal(0, github.CreateCalls);
        Assert.False(File.Exists(summary.ManifestPath));
    }

    [Fact]
    public async Task Rejects_all_invalid_mappings_before_any_create()
    {
        var config = Config();
        await InitializeAndCreateAsync(config, "One", "Body", "Bad", null);
        await InitializeAndCreateAsync(config, "Two", "Body", "Worse", null);
        github.InvalidStatuses.UnionWith(["Bad", "Worse"]);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => Service().RunAsync(
                config, Options(dryRun: false), CancellationToken.None));

        Assert.Equal("IMPORT_PREFLIGHT_FAILED", exception.Code);
        Assert.Contains("local:1", exception.Message);
        Assert.Contains("local:2", exception.Message);
        Assert.Equal(0, github.CreateCalls);
    }

    [Fact]
    public async Task References_and_active_claims_are_blocked_before_writes()
    {
        var config = Config();
        var created = await InitializeAndCreateAsync(
            config, "Referenced", "See #1", "Todo", null);

        var referenceFailure = await Assert.ThrowsAsync<TrackerException>(
            () => Service().RunAsync(
                config, Options(dryRun: false), CancellationToken.None));
        Assert.Equal("IMPORT_REFERENCES_UNMAPPED", referenceFailure.Code);

        var claim = await local.TryClaimAsync(
            config with { Backend = "local-markdown" },
            created.Id,
            AgentExecutionContext.Human,
            CancellationToken.None);
        Assert.Equal(ClaimOutcome.Acquired, claim.Outcome);

        var claimFailure = await Assert.ThrowsAsync<TrackerException>(
            () => Service().RunAsync(
                config,
                Options(dryRun: false, allowReferences: true),
                CancellationToken.None));
        Assert.Equal("IMPORT_ACTIVE_CLAIMS", claimFailure.Code);
        Assert.Equal(0, github.CreateCalls);
    }

    [Fact]
    public async Task Execution_persists_manifest_archives_and_resumes_without_duplicates()
    {
        var config = Config();
        await InitializeAndCreateAsync(config, "Active", "Body", "Todo", null);
        var archived = await InitializeAndCreateAsync(
            config, "Archived", "Old", "Done", "P2");
        var claim = await local.TryClaimAsync(
            config with { Backend = "local-markdown" },
            archived.Id,
            AgentExecutionContext.Human,
            CancellationToken.None);
        await local.ArchiveAsync(
            config with { Backend = "local-markdown" },
            archived.Id,
            new ClaimHandle(AgentExecutionContext.Human, claim.ClaimToken),
            CancellationToken.None);
        var manifest = Path.Combine(directory, "manifest.json");
        var options = Options(
            dryRun: false,
            includeArchived: true,
            manifestPath: manifest);

        var first = await Service().RunAsync(config, options, CancellationToken.None);
        var second = await Service().RunAsync(config, options, CancellationToken.None);

        Assert.Equal(2, first.Created);
        Assert.Equal(1, github.ArchiveCalls);
        Assert.True(File.Exists(manifest));
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, github.CreateCalls);
        var manifestText = await File.ReadAllTextAsync(manifest);
        Assert.Contains("github:owner/repo#1", manifestText);
        Assert.Contains("github:owner/repo#2", manifestText);
    }

    [Fact]
    public async Task Item_failure_is_flushed_and_later_items_continue_unless_stopped()
    {
        var config = Config();
        await InitializeAndCreateAsync(config, "Fails", "Body", "Todo", null);
        await InitializeAndCreateAsync(config, "Succeeds", "Body", "Todo", null);
        github.FailingTitles.Add("Fails");
        var manifest = Path.Combine(directory, "continue.json");

        var summary = await Service().RunAsync(
            config,
            Options(dryRun: false, manifestPath: manifest),
            CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Created);
        Assert.Equal(2, github.CreateCalls);
        Assert.Contains("\"failedStage\": \"create\"", await File.ReadAllTextAsync(manifest));

        github.CreateCalls = 0;
        var stopManifest = Path.Combine(directory, "stop.json");
        var stopped = await Service().RunAsync(
            config,
            Options(dryRun: false, stopOnError: true, manifestPath: stopManifest),
            CancellationToken.None);

        Assert.Equal(1, stopped.Failed);
        Assert.Equal(0, stopped.Created);
        Assert.Equal(1, github.CreateCalls);
    }

    [Fact]
    public async Task Existing_manifest_rejects_changed_import_intent()
    {
        var config = Config();
        await InitializeAndCreateAsync(config, "One", "Body", "Todo", null);
        var manifest = Path.Combine(directory, "intent.json");
        await Service().RunAsync(
            config,
            Options(dryRun: false, manifestPath: manifest),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => Service().RunAsync(
                config,
                Options(
                    dryRun: false,
                    copyAsReleased: true,
                    manifestPath: manifest),
                CancellationToken.None));

        Assert.Equal("IMPORT_INTENT_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task Tracker_service_reports_unsupported_import_and_adoption_capabilities()
    {
        var tracker = new TrackerService(new TrackerBackendRegistry([local]));
        var config = Config() with { Backend = "local-markdown" };

        var adoption = await Assert.ThrowsAsync<TrackerException>(
            () => tracker.AdoptAsync(
                config,
                "feature.md",
                new AdoptWorkItemOptions(null, null, false, null),
                CancellationToken.None));
        var validation = await Assert.ThrowsAsync<TrackerException>(
            () => tracker.ValidateImportFieldsAsync(
                config, "Todo", null, CancellationToken.None));
        var archive = await Assert.ThrowsAsync<TrackerException>(
            () => tracker.ArchiveImportedAsync(
                config, new WorkItemId("local:1"), CancellationToken.None));

        Assert.Equal("NOT_SUPPORTED", adoption.Code);
        Assert.Contains("import --in-place", adoption.Message);
        Assert.Equal("NOT_SUPPORTED", validation.Code);
        Assert.Equal("NOT_SUPPORTED", archive.Code);
    }

    private WholeStoreImportService Service() =>
        new(new TrackerService(new TrackerBackendRegistry([local, github])));

    private TrackerConfig Config() => new()
    {
        Backend = "github",
        SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
        GitHub = new GitHubBackendConfig
        {
            Repository = "owner/repo",
            ProjectNumber = 1
        },
        LocalMarkdown = new LocalMarkdownBackendConfig
        {
            Statuses = ["Todo", "In Progress", "Done", "Bad", "Worse"]
        }
    };

    private async Task<CreateWorkItemResult> InitializeAndCreateAsync(
        TrackerConfig config,
        string title,
        string body,
        string status,
        string? priority,
        IReadOnlyDictionary<string, string?>? fields = null)
    {
        var source = config with { Backend = "local-markdown" };
        await local.InitializeAsync(source, false, CancellationToken.None);
        return await local.CreateAsync(
            source,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    title, body, status, priority, fields),
                false,
                Guid.NewGuid().ToString("N")),
            CancellationToken.None);
    }

    private static WholeStoreImportOptions Options(
        bool dryRun,
        bool includeArchived = false,
        bool copyAsReleased = false,
        bool allowReferences = false,
        bool stopOnError = false,
        IReadOnlyDictionary<string, string>? statusMappings = null,
        string? manifestPath = null) =>
        new(
            includeArchived,
            dryRun,
            copyAsReleased,
            allowReferences,
            stopOnError,
            statusMappings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            manifestPath);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class TestIdentity : IWorkerIdentityProvider
    {
        public Task<string> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult("test-worker");
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-07-20T12:00:00Z");
    }

    private sealed class ImportTargetBackend :
        ITrackerBackend,
        IWorkItemImportTargetBackend
    {
        public string Name => "github";
        public IWorkItemAddressResolver AddressResolver { get; } =
            new GitHubWorkItemAddressResolver();
        public int InitializeCalls { get; private set; }
        public bool LastCheckOnly { get; private set; }
        public int CreateCalls { get; set; }
        public int ArchiveCalls { get; private set; }
        public List<(string Status, string? Priority)> ValidatedFields { get; } = [];
        public HashSet<string> InvalidStatuses { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailingTitles { get; } =
            new(StringComparer.Ordinal);

        public Task<BackendInitializationResult> InitializeAsync(
            TrackerConfig config,
            bool checkOnly,
            CancellationToken cancellationToken)
        {
            InitializeCalls++;
            LastCheckOnly = checkOnly;
            return Task.FromResult(new BackendInitializationResult(false, ["valid"]));
        }

        public Task ValidateImportFieldsAsync(
            TrackerConfig config,
            string status,
            string? priority,
            CancellationToken cancellationToken)
        {
            ValidatedFields.Add((status, priority));
            if (InvalidStatuses.Contains(status))
            {
                throw new TrackerException(
                    "STATUS_NOT_FOUND", $"Status '{status}' is invalid.", 3);
            }
            return Task.CompletedTask;
        }

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            if (FailingTitles.Contains(operation.Request.Title))
            {
                throw new TrackerException(
                    "CREATION_OUTCOME_UNKNOWN", "ambiguous", 10,
                    new Dictionary<string, object?>
                    {
                        ["failedStage"] = "create"
                    });
            }
            var id = new WorkItemId($"github:owner/repo#{CreateCalls}");
            return Task.FromResult(new CreateWorkItemResult(
                id,
                $"https://github.com/owner/repo/issues/{CreateCalls}",
                null,
                operation.CreationAttemptId));
        }

        public Task ArchiveImportedAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            ArchiveCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkItemSummary>> ListAsync(
            TrackerConfig config,
            ListWorkItemsRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkItemSummary>>([]);

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkItemDetail?>(null);

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            UpdateWorkItemOperation operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentExecutionContext,
            CancellationToken cancellationToken,
            string? expectedClaimToken) => throw new NotSupportedException();

        public Task<ClaimResult> TakeoverAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext claimantContext,
            string? currentClaimToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed));

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            bool overrideClaimant,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ArchiveWorkItemResult> UnarchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
