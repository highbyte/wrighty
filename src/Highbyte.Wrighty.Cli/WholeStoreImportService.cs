using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Importing;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Cli;

internal sealed record WholeStoreImportOptions(
    bool IncludeArchived,
    bool DryRun,
    bool CopyAsReleased,
    bool AllowUnmappedReferences,
    bool StopOnError,
    IReadOnlyDictionary<string, string> StatusMappings,
    IReadOnlyDictionary<string, string> PriorityMappings,
    string? ManifestPath);

internal sealed record WholeStoreImportSummary(
    bool DryRun,
    string ManifestPath,
    int Planned,
    int EstimatedRemoteOperations,
    IReadOnlyList<string> PlannedItems,
    int Created,
    int Resumed,
    int Skipped,
    int Failed,
    IReadOnlyList<string> ReferenceWarnings,
    string BackendSwitchGuidance);

internal sealed record StoreImportManifest(
    int Version,
    string BatchId,
    string SourceStore,
    string DestinationRepository,
    int DestinationProjectNumber,
    string ConfigurationFingerprint,
    DateTimeOffset PlannedAt,
    List<StoreImportEntry> Items);

internal sealed record StoreImportEntry(
    string SourceId,
    string SourceFingerprint,
    string CreationAttemptId,
    string Title,
    string Body,
    string Status,
    string? Priority,
    bool Archived,
    IReadOnlyList<string> ReferenceWarnings,
    string? TargetId = null,
    string? Url = null,
    string? Disposition = null,
    IReadOnlyList<string>? AppliedStages = null,
    IReadOnlyList<string>? PendingStages = null,
    string? FailedStage = null,
    string? Failure = null,
    DateTimeOffset? CompletedAt = null);

internal enum StoreImportOutcome
{
    Created,
    Resumed,
    Skipped,
    Failed
}

internal sealed partial class WholeStoreImportService(TrackerService tracker)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<WholeStoreImportSummary> RunAsync(
        TrackerConfig destination,
        WholeStoreImportOptions options,
        CancellationToken cancellationToken)
    {
        ValidateDestination(destination);
        await tracker.InitializeAsync(destination, checkOnly: true, cancellationToken);
        var source = await LoadSourceAsync(destination, options, cancellationToken);
        EnforceClaimPolicy(source, options);
        var planned = await PlanEntriesAsync(
            destination, options, source, cancellationToken);
        var warnings = planned.SelectMany(value => value.ReferenceWarnings).Distinct().ToArray();
        EnforceReferencePolicy(warnings, options);
        var sourceStore = SourceStorePath(destination);
        var manifestPath = ResolveManifestPath(destination, options);
        var configurationFingerprint = ConfigurationFingerprint(destination, options);

        if (options.DryRun)
        {
            return BuildSummary(
                true, manifestPath, planned, 0, 0, 0, 0, warnings, destination);
        }

        var manifest = await LoadOrCreateManifestAsync(
            manifestPath,
            sourceStore,
            destination,
            configurationFingerprint,
            planned,
            cancellationToken);
        await SaveManifestAsync(manifestPath, manifest, cancellationToken);
        return await ExecuteManifestAsync(
            destination, options, manifestPath, manifest, warnings, cancellationToken);
    }

    private static void ValidateDestination(TrackerConfig destination)
    {
        if (destination.GitHub is null || destination.LocalMarkdown is null)
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "Whole-store import requires both github and localMarkdown configuration sections.",
                2);
        }
        if (!string.Equals(destination.Backend, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "NOT_SUPPORTED",
                "--from-store local-markdown requires the selected destination backend to be github.",
                3);
        }
    }

    private async Task<WorkItemOperationalState[]> LoadSourceAsync(
        TrackerConfig destination,
        WholeStoreImportOptions options,
        CancellationToken cancellationToken)
    {
        var sourceConfig = destination with { Backend = "local-markdown" };
        var scope = options.IncludeArchived ? ArchiveScope.All : ArchiveScope.Active;
        return (await tracker.ListOperationalAsync(
                sourceConfig,
                new ListWorkItemsRequest(null, null, scope),
                cancellationToken))
            .OrderBy(value => LocalNumber(value.Item.Id))
            .ToArray();
    }

    private static void EnforceClaimPolicy(
        IReadOnlyList<WorkItemOperationalState> source,
        WholeStoreImportOptions options)
    {
        var claimed = source
            .Where(value => value.Claim.State != ClaimOwnershipState.Unclaimed)
            .Select(value => value.Item.Id.Value)
            .ToArray();
        if (claimed.Length > 0 && !options.CopyAsReleased)
        {
            throw new TrackerException(
                "IMPORT_ACTIVE_CLAIMS",
                $"Whole-store import refuses active claims: {string.Join(", ", claimed)}. Use --copy-as-released to copy content without claim/session state.",
                3,
                new Dictionary<string, object?> { ["ids"] = claimed });
        }
    }

    private async Task<List<StoreImportEntry>> PlanEntriesAsync(
        TrackerConfig destination,
        WholeStoreImportOptions options,
        IEnumerable<WorkItemOperationalState> source,
        CancellationToken cancellationToken)
    {
        var planned = new List<StoreImportEntry>();
        var validationFailures = new List<string>();
        foreach (var value in source)
        {
            planned.Add(await PlanEntryAsync(
                destination, options, value.Item, validationFailures, cancellationToken));
        }
        if (validationFailures.Count > 0)
        {
            throw new TrackerException(
                "IMPORT_PREFLIGHT_FAILED",
                "Whole-store target mapping validation failed:\n" +
                string.Join("\n", validationFailures),
                3,
                new Dictionary<string, object?> { ["failures"] = validationFailures });
        }
        return planned;
    }

    private async Task<StoreImportEntry> PlanEntryAsync(
        TrackerConfig destination,
        WholeStoreImportOptions options,
        WorkItemDetail item,
        ICollection<string> validationFailures,
        CancellationToken cancellationToken)
    {
        var status = MapValue(
            item.Status ?? destination.DefaultPickFrom,
            options.StatusMappings);
        var priority = item.Priority is null
            ? null
            : MapValue(item.Priority, options.PriorityMappings);
        try
        {
            await tracker.ValidateImportFieldsAsync(
                destination, status, priority, cancellationToken);
        }
        catch (TrackerException exception)
        {
            validationFailures.Add($"{item.Id.Value}: {exception.Message}");
        }

        var references = LocalReference()
            .Matches(item.Body)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var body = item.EffectiveFields.Count == 0
            ? item.Body
            : MarkdownImportPlanner.AppendCustomFieldBlock(
                item.Body,
                JsonSerializer.Serialize(item.EffectiveFields, JsonOptions));
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            item.Id.Value,
            item.Title,
            Body = body,
            Status = status,
            Priority = priority,
            item.Archived,
            options.CopyAsReleased,
            options.AllowUnmappedReferences
        }));
        return new StoreImportEntry(
            item.Id.Value,
            fingerprint,
            Guid.NewGuid().ToString("D"),
            item.Title,
            body,
            status,
            priority,
            item.Archived,
            references);
    }

    private static void EnforceReferencePolicy(
        IReadOnlyList<string> warnings,
        WholeStoreImportOptions options)
    {
        if (warnings.Count > 0 && !options.AllowUnmappedReferences)
        {
            throw new TrackerException(
                "IMPORT_REFERENCES_UNMAPPED",
                $"Local #N references are ambiguous in GitHub: {string.Join(", ", warnings)}. Use --allow-unmapped-references to preserve them and record warnings.",
                3,
                new Dictionary<string, object?> { ["references"] = warnings });
        }
    }

    private static string ResolveManifestPath(
        TrackerConfig destination,
        WholeStoreImportOptions options) =>
        options.ManifestPath is null
            ? Path.Combine(
                destination.SourcePath is null
                    ? Environment.CurrentDirectory
                    : Path.GetDirectoryName(Path.GetFullPath(destination.SourcePath))!,
                ".wrighty-imports",
                $"local-markdown-to-{Sanitize(destination.Repository)}-project-{destination.ProjectNumber}.json")
            : Path.GetFullPath(options.ManifestPath);

    private static string ConfigurationFingerprint(
        TrackerConfig destination,
        WholeStoreImportOptions options) =>
        Fingerprint(JsonSerializer.Serialize(new
        {
            destination.Repository,
            destination.ProjectNumber,
            options.IncludeArchived,
            options.CopyAsReleased,
            options.AllowUnmappedReferences,
            options.StatusMappings,
            options.PriorityMappings
        }));

    private static async Task<StoreImportManifest> LoadOrCreateManifestAsync(
        string manifestPath,
        string sourceStore,
        TrackerConfig destination,
        string configurationFingerprint,
        List<StoreImportEntry> planned,
        CancellationToken cancellationToken) =>
        File.Exists(manifestPath)
            ? await LoadAndValidateManifestAsync(
                manifestPath,
                sourceStore,
                destination,
                configurationFingerprint,
                planned,
                cancellationToken)
            : new StoreImportManifest(
                1,
                Guid.NewGuid().ToString("D"),
                sourceStore,
                destination.Repository,
                destination.ProjectNumber,
                configurationFingerprint,
                DateTimeOffset.UtcNow,
                planned);

    private async Task<WholeStoreImportSummary> ExecuteManifestAsync(
        TrackerConfig destination,
        WholeStoreImportOptions options,
        string manifestPath,
        StoreImportManifest manifest,
        IReadOnlyList<string> warnings,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var resumed = 0;
        var skipped = 0;
        var failed = 0;
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            var outcome = await ExecuteManifestItemAsync(
                destination,
                manifestPath,
                manifest,
                index,
                cancellationToken);
            switch (outcome)
            {
                case StoreImportOutcome.Created:
                    created++;
                    break;
                case StoreImportOutcome.Resumed:
                    resumed++;
                    break;
                case StoreImportOutcome.Skipped:
                    skipped++;
                    break;
                case StoreImportOutcome.Failed:
                    failed++;
                    break;
            }
            if (outcome == StoreImportOutcome.Failed && options.StopOnError) break;
        }

        return BuildSummary(
            false, manifestPath, manifest.Items, created, resumed, skipped, failed,
            warnings, destination);
    }

    private async Task<StoreImportOutcome> ExecuteManifestItemAsync(
        TrackerConfig destination,
        string manifestPath,
        StoreImportManifest manifest,
        int index,
        CancellationToken cancellationToken)
    {
        var entry = manifest.Items[index];
        if (IsComplete(entry))
        {
            return StoreImportOutcome.Skipped;
        }

        CreateWorkItemResult? result = null;
        try
        {
            result = await tracker.CreateAsync(
                destination,
                new CreateWorkItemRequest(
                    entry.Title,
                    entry.Body,
                    entry.Status,
                    entry.Priority),
                entry.CreationAttemptId,
                cancellationToken);
            var applied = await ApplyArchiveStateAsync(
                destination, entry, result, cancellationToken);
            manifest.Items[index] = CompletedEntry(entry, result, applied);
            await SaveManifestAsync(manifestPath, manifest, cancellationToken);
            return result.Disposition == CreateDisposition.Created
                ? StoreImportOutcome.Created
                : StoreImportOutcome.Resumed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            manifest.Items[index] = FailedEntry(entry, result, exception);
            await SaveManifestAsync(manifestPath, manifest, cancellationToken);
            return StoreImportOutcome.Failed;
        }
    }

    private static bool IsComplete(StoreImportEntry entry) =>
        entry.TargetId is not null &&
        entry.FailedStage is null &&
        entry.Disposition is "created" or "resumed";

    private async Task<IReadOnlyList<string>> ApplyArchiveStateAsync(
        TrackerConfig destination,
        StoreImportEntry entry,
        CreateWorkItemResult result,
        CancellationToken cancellationToken)
    {
        var applied = new List<string> { "create" };
        if (entry.Archived)
        {
            await tracker.ArchiveImportedAsync(
                destination,
                result.Id,
                cancellationToken);
            applied.Add("archive");
        }
        return applied;
    }

    private static StoreImportEntry CompletedEntry(
        StoreImportEntry entry,
        CreateWorkItemResult result,
        IReadOnlyList<string> applied)
    {
        var disposition = result.Disposition == CreateDisposition.Created
            ? "created"
            : "resumed";
        return entry with
        {
            TargetId = result.Id.Value,
            Url = result.Url,
            Disposition = disposition,
            AppliedStages = applied,
            PendingStages = [],
            FailedStage = null,
            Failure = null,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static StoreImportEntry FailedEntry(
        StoreImportEntry entry,
        CreateWorkItemResult? result,
        Exception exception)
    {
        if (result is not null)
        {
            return entry with
            {
                TargetId = result.Id.Value,
                Url = result.Url,
                Disposition = "failed",
                AppliedStages = ["create"],
                PendingStages = ["archive"],
                FailedStage = "archive",
                Failure = exception.Message,
                CompletedAt = DateTimeOffset.UtcNow
            };
        }

        var trackerException = exception as TrackerException;
        var pending = entry.Archived
            ? new[] { "create", "archive" }
            : ["create"];
        return entry with
        {
            TargetId = trackerException?.Details.GetValueOrDefault("id")?.ToString(),
            Url = trackerException?.Details.GetValueOrDefault("url")?.ToString(),
            Disposition = "failed",
            AppliedStages = [],
            PendingStages = pending,
            FailedStage = trackerException?.Details
                .GetValueOrDefault("failedStage")?.ToString() ?? "create",
            Failure = exception.Message,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static WholeStoreImportSummary BuildSummary(
        bool dryRun,
        string manifestPath,
        IReadOnlyList<StoreImportEntry> items,
        int created,
        int resumed,
        int skipped,
        int failed,
        IReadOnlyList<string> warnings,
        TrackerConfig destination) =>
        new(
            dryRun,
            manifestPath,
            items.Count,
            items.Sum(item => item.Archived ? 4 : 3),
            items.Select(item =>
                $"{item.SourceId} -> {item.Status} / {item.Priority ?? "-"}{(item.Archived ? " / archived" : string.Empty)} / {item.Title}").ToArray(),
            created,
            resumed,
            skipped,
            failed,
            warnings,
            BackendSwitchGuidance(destination));

    private static async Task<StoreImportManifest> LoadAndValidateManifestAsync(
        string path,
        string sourceStore,
        TrackerConfig destination,
        string configurationFingerprint,
        IReadOnlyList<StoreImportEntry> planned,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<StoreImportManifest>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new TrackerException(
                "IMPORT_MANIFEST_INVALID",
                $"Import manifest '{path}' is empty or invalid.",
                3);
        if (manifest.Version != 1 ||
            !string.Equals(manifest.SourceStore, sourceStore, StringComparison.Ordinal) ||
            !string.Equals(
                manifest.DestinationRepository,
                destination.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            manifest.DestinationProjectNumber != destination.ProjectNumber ||
            !string.Equals(
                manifest.ConfigurationFingerprint,
                configurationFingerprint,
                StringComparison.Ordinal))
        {
            throw new TrackerException(
                "IMPORT_INTENT_CONFLICT",
                $"Import manifest '{path}' describes a different source, destination, or option set.",
                9);
        }
        var existing = manifest.Items.ToDictionary(value => value.SourceId, StringComparer.Ordinal);
        foreach (var item in planned)
        {
            if (!existing.TryGetValue(item.SourceId, out var prior) ||
                !string.Equals(
                    prior.SourceFingerprint,
                    item.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                throw new TrackerException(
                    "IMPORT_INTENT_CONFLICT",
                    $"Source intent for '{item.SourceId}' changed since manifest planning.",
                    9);
            }
        }
        return manifest;
    }

    private static async Task SaveManifestAsync(
        string path,
        StoreImportManifest manifest,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporary,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                manifest,
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static string MapValue(
        string source,
        IReadOnlyDictionary<string, string> mappings) =>
        mappings.TryGetValue(source, out var target) ? target : source;

    private static int LocalNumber(WorkItemId id) =>
        int.Parse(id.Value["local:".Length..], System.Globalization.CultureInfo.InvariantCulture);

    private static string SourceStorePath(TrackerConfig config)
    {
        var configured = config.LocalMarkdown!.Path;
        var root = config.SourcePath is null
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(Path.GetFullPath(config.SourcePath))!;
        return Path.GetFullPath(configured, root);
    }

    private static string BackendSwitchGuidance(TrackerConfig config) =>
        $"After verifying the manifest, explicitly set \"backend\": \"github\" in {config.SourcePath ?? ".wrighty.json"}. No synchronization or automatic undo exists.";

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sanitize(string value) =>
        value.Replace("/", "-", StringComparison.Ordinal)
            .Replace("\\", "-", StringComparison.Ordinal);

    [GeneratedRegex(@"(?<![A-Za-z0-9_])#[0-9]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex LocalReference();
}
