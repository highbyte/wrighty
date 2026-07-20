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

        await tracker.InitializeAsync(destination, checkOnly: true, cancellationToken);
        var sourceConfig = destination with { Backend = "local-markdown" };
        var scope = options.IncludeArchived ? ArchiveScope.All : ArchiveScope.Active;
        var source = (await tracker.ListOperationalAsync(
                sourceConfig,
                new ListWorkItemsRequest(null, null, scope),
                cancellationToken))
            .OrderBy(value => LocalNumber(value.Item.Id))
            .ToArray();

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

        var planned = new List<StoreImportEntry>();
        var validationFailures = new List<string>();
        foreach (var value in source)
        {
            var item = value.Item;
            var status = MapValue(
                item.Status ?? destination.DefaultPickFrom,
                options.StatusMappings);
            var priority = item.Priority is null
                ? null
                : MapValue(item.Priority, options.PriorityMappings);
            try
            {
                await tracker.ValidateImportFieldsAsync(
                    destination,
                    status,
                    priority,
                    cancellationToken);
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
            planned.Add(new StoreImportEntry(
                item.Id.Value,
                fingerprint,
                Guid.NewGuid().ToString("D"),
                item.Title,
                body,
                status,
                priority,
                item.Archived,
                references));
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
        var warnings = planned.SelectMany(value => value.ReferenceWarnings).Distinct().ToArray();
        if (warnings.Length > 0 && !options.AllowUnmappedReferences)
        {
            throw new TrackerException(
                "IMPORT_REFERENCES_UNMAPPED",
                $"Local #N references are ambiguous in GitHub: {string.Join(", ", warnings)}. Use --allow-unmapped-references to preserve them and record warnings.",
                3,
                new Dictionary<string, object?> { ["references"] = warnings });
        }

        var sourceStore = SourceStorePath(destination);
        var manifestPath = options.ManifestPath is null
            ? Path.Combine(
                destination.SourcePath is null
                    ? Environment.CurrentDirectory
                    : Path.GetDirectoryName(Path.GetFullPath(destination.SourcePath))!,
                ".wrighty-imports",
                $"local-markdown-to-{Sanitize(destination.Repository)}-project-{destination.ProjectNumber}.json")
            : Path.GetFullPath(options.ManifestPath);
        var configurationFingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            destination.Repository,
            destination.ProjectNumber,
            options.IncludeArchived,
            options.CopyAsReleased,
            options.AllowUnmappedReferences,
            options.StatusMappings,
            options.PriorityMappings
        }));

        if (options.DryRun)
        {
            return new WholeStoreImportSummary(
                true,
                manifestPath,
                planned.Count,
                planned.Sum(item => item.Archived ? 4 : 3),
                planned.Select(item =>
                    $"{item.SourceId} -> {item.Status} / {item.Priority ?? "-"}{(item.Archived ? " / archived" : string.Empty)} / {item.Title}").ToArray(),
                0,
                0,
                0,
                0,
                warnings,
                BackendSwitchGuidance(destination));
        }

        var manifest = File.Exists(manifestPath)
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
        await SaveManifestAsync(manifestPath, manifest, cancellationToken);

        var created = 0;
        var resumed = 0;
        var skipped = 0;
        var failed = 0;
        for (var index = 0; index < manifest.Items.Count; index++)
        {
            var entry = manifest.Items[index];
            if (entry.TargetId is not null &&
                entry.FailedStage is null &&
                entry.Disposition is "created" or "resumed")
            {
                skipped++;
                continue;
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
                var applied = new List<string> { "create" };
                if (entry.Archived)
                {
                    await tracker.ArchiveImportedAsync(
                        destination,
                        result.Id,
                        cancellationToken);
                    applied.Add("archive");
                }
                var disposition = result.Disposition == CreateDisposition.Created
                    ? "created"
                    : "resumed";
                if (disposition == "created") created++; else resumed++;
                manifest.Items[index] = entry with
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                var trackerException = exception as TrackerException;
                manifest.Items[index] = entry with
                {
                    TargetId = result?.Id.Value ??
                               trackerException?.Details.GetValueOrDefault("id")?.ToString(),
                    Url = result?.Url ??
                          trackerException?.Details.GetValueOrDefault("url")?.ToString(),
                    Disposition = "failed",
                    AppliedStages = result is null ? [] : ["create"],
                    PendingStages = result is null
                        ? entry.Archived ? ["create", "archive"] : ["create"]
                        : ["archive"],
                    FailedStage = result is null
                        ? trackerException?.Details.GetValueOrDefault("failedStage")?.ToString() ??
                          "create"
                        : "archive",
                    Failure = exception.Message,
                    CompletedAt = DateTimeOffset.UtcNow
                };
                await SaveManifestAsync(manifestPath, manifest, cancellationToken);
                if (options.StopOnError) break;
                continue;
            }
            await SaveManifestAsync(manifestPath, manifest, cancellationToken);
        }

        return new WholeStoreImportSummary(
            false,
            manifestPath,
            manifest.Items.Count,
            manifest.Items.Sum(item => item.Archived ? 4 : 3),
            manifest.Items.Select(item =>
                $"{item.SourceId} -> {item.Status} / {item.Priority ?? "-"}{(item.Archived ? " / archived" : string.Empty)} / {item.Title}").ToArray(),
            created,
            resumed,
            skipped,
            failed,
            warnings,
            BackendSwitchGuidance(destination));
    }

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
