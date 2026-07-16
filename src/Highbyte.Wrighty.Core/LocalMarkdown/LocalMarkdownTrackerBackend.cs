using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;

namespace Highbyte.Wrighty.LocalMarkdown;

public sealed partial class LocalMarkdownTrackerBackend(
    IWorkerIdentityProvider identityProvider,
    IClock clock) : ITrackerBackend, ITrackerDashboardBackend, ILocalMarkdownImportBackend
{
    private const string GitIgnoreComment = "# Wrighty runtime state";
    private static readonly string[] GitIgnoreRules = ["/.lock", ".*.tmp"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly LocalMarkdownWorkItemAddressResolver resolver = new();

    public string Name => "local-markdown";

    public IWorkItemAddressResolver AddressResolver => resolver;

    public async Task<BackendInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var paths = Paths(config);
        var actions = GetRequiredInitializationActions(paths);

        if (checkOnly)
        {
            return await ValidateInitializationAsync(
                config, paths, actions, cancellationToken);
        }

        await InitializeStoreAsync(config, paths, actions, cancellationToken);
        return new BackendInitializationResult(actions.Count > 0, actions);
    }

    private static List<string> GetRequiredInitializationActions(LocalStorePaths paths)
    {
        var actions = new List<string>();
        AddMissingDirectoryAction(paths.Root, "create local Wrighty directory", actions);
        AddMissingDirectoryAction(paths.Items, "create items directory", actions);
        AddMissingDirectoryAction(paths.Archive, "create archive directory", actions);
        if (!File.Exists(Path.Combine(paths.Root, ".lock")))
        {
            actions.Add("create store lock");
        }

        return actions;
    }

    private static void AddMissingDirectoryAction(
        string path,
        string action,
        ICollection<string> actions)
    {
        if (!Directory.Exists(path))
        {
            actions.Add(action);
        }
    }

    private async Task<BackendInitializationResult> ValidateInitializationAsync(
        TrackerConfig config,
        LocalStorePaths paths,
        List<string> actions,
        CancellationToken cancellationToken)
    {
        ThrowIfInitializationRequired(paths.Root, actions);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var documents = await LoadAllUnlockedAsync(config, cancellationToken);
        AddRenameActions(config, documents, actions);
        ThrowIfInitializationRequired(paths.Root, actions);
        return new BackendInitializationResult(false, ["Local Markdown store is valid."]);
    }

    private static void ThrowIfInitializationRequired(string root, IReadOnlyCollection<string> actions)
    {
        if (actions.Count == 0)
        {
            return;
        }

        throw new TrackerException(
            "STORE_INITIALIZATION_REQUIRED",
            $"Local Wrighty initialization is required: {string.Join("; ", actions)}. Run 'wrighty init'.",
            5,
            new Dictionary<string, object?> { ["path"] = root, ["actions"] = actions });
    }

    private void AddRenameActions(
        TrackerConfig config,
        IEnumerable<LocalMarkdownDocument> documents,
        ICollection<string> actions)
    {
        foreach (var document in documents)
        {
            var canonical = CanonicalPath(config, document);
            if (!string.Equals(document.Path, canonical, StringComparison.Ordinal))
            {
                actions.Add($"rename {Path.GetFileName(document.Path)} to {Path.GetFileName(canonical)}");
            }
        }
    }

    private async Task InitializeStoreAsync(
        TrackerConfig config,
        LocalStorePaths paths,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Items);
        Directory.CreateDirectory(paths.Archive);
        await using (var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken))
        {
            await RenameDocumentsAsync(config, actions, cancellationToken);
            await AddGitIgnoreActionAsync(paths.Root, actions, cancellationToken);
        }
    }

    private async Task RenameDocumentsAsync(
        TrackerConfig config,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        var documents = await LoadAllUnlockedAsync(config, cancellationToken);
        foreach (var document in documents)
        {
            var original = document.Path;
            var canonical = CanonicalPath(config, document);
            if (string.Equals(original, canonical, StringComparison.Ordinal))
            {
                continue;
            }

            document.Path = canonical;
            await WriteUnlockedAsync(document, original, cancellationToken);
            actions.Add($"renamed {Path.GetFileName(original)} to {Path.GetFileName(canonical)}");
        }
    }

    private static async Task AddGitIgnoreActionAsync(
        string root,
        ICollection<string> actions,
        CancellationToken cancellationToken)
    {
        if (!IsInsideGitWorktree(root))
        {
            return;
        }

        var action = await EnsureGitIgnoreAsync(root, cancellationToken);
        if (action is not null)
        {
            actions.Add(action);
        }
    }

    private static bool IsInsideGitWorktree(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path));
             directory is not null;
             directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string?> EnsureGitIgnoreAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ".gitignore");
        if (!File.Exists(path))
        {
            var content = string.Join(Environment.NewLine, [GitIgnoreComment, .. GitIgnoreRules]) +
                          Environment.NewLine;
            await File.WriteAllTextAsync(path, content, cancellationToken);
            return "created local Wrighty .gitignore";
        }

        var existing = await File.ReadAllTextAsync(path, cancellationToken);
        var lines = existing
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToHashSet(StringComparer.Ordinal);
        var missing = GitIgnoreRules.Where(rule => !lines.Contains(rule)).ToArray();
        if (missing.Length == 0)
        {
            return null;
        }

        var separator = existing.Length == 0 || existing.EndsWith('\n')
            ? string.Empty
            : Environment.NewLine;
        var addition = string.Join(Environment.NewLine, [GitIgnoreComment, .. missing]) +
                       Environment.NewLine;
        await File.AppendAllTextAsync(path, separator + addition, cancellationToken);
        return "updated local Wrighty .gitignore";
    }

    public async Task<IReadOnlyList<WorkItemSummary>> ListAsync(
        TrackerConfig config,
        ListWorkItemsRequest request,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var documents = await LoadAllUnlockedAsync(config, cancellationToken);
        IEnumerable<LocalMarkdownDocument> query = documents;
        query = request.ArchiveScope switch
        {
            ArchiveScope.Active => query.Where(item => !item.Archived),
            ArchiveScope.Archived => query.Where(item => item.Archived),
            _ => query
        };
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            query = query.Where(item =>
                string.Equals(item.Status, request.Status, StringComparison.OrdinalIgnoreCase));
        }

        if (request.Fields is { Count: > 0 })
        {
            foreach (var field in request.Fields)
            {
                LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
                query = query.Where(item => string.Equals(
                    item.CustomFieldScalar(field.Key), field.Value, StringComparison.Ordinal));
            }
        }

        query = query
            .OrderBy(item => PriorityRank(config, item.Priority))
            .ThenBy(item => item.Id);
        if (request.Limit is not null)
        {
            if (request.Limit <= 0)
            {
                throw new TrackerException("ARGUMENT_INVALID", "limit must be positive.", 2);
            }

            query = query.Take(request.Limit.Value);
        }

        return query.Select(Summary).ToArray();
    }

    public async Task<WorkItemDetail?> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await FindUnlockedAsync(config, LocalMarkdownWorkItemAddressResolver.Decode(id), cancellationToken);
        return document is null ? null : Detail(document);
    }

    public async Task<LocalMarkdownImportResult> ImportAsync(
        TrackerConfig config,
        LocalMarkdownImportRequest request,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        if (request.Paths.Count == 0)
        {
            throw new TrackerException("ARGUMENT_INVALID", "At least one import path is required.", 2);
        }

        foreach (var mapping in request.FieldMappings)
        {
            if (mapping.Key is not ("status" or "priority") || string.IsNullOrWhiteSpace(mapping.Value))
            {
                throw new TrackerException(
                    "ARGUMENT_INVALID",
                    $"Invalid --map '{mapping.Key}={mapping.Value}'; only status=<source-key> and priority=<source-key> are supported.",
                    2);
            }
        }

        var paths = Paths(config);
        var sources = ResolveImportPaths(request.Paths, request.Recursive)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (sources.Length == 0)
        {
            throw new TrackerException("ARGUMENT_INVALID", "No Markdown files were found to import.", 2);
        }

        var root = Path.GetFullPath(paths.Root) + Path.DirectorySeparatorChar;
        if (sources.Any(source => source.StartsWith(root, StringComparison.Ordinal)))
        {
            throw new TrackerException("ARGUMENT_INVALID", "Import sources must be outside the Local Markdown store.", 2);
        }

        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var existing = await LoadAllUnlockedAsync(config, cancellationToken);
        var nextId = existing.Count == 0 ? 1 : checked(existing.Max(item => item.Id) + 1);
        var planned = new List<(LocalMarkdownImportItem Item, LocalMarkdownDocument Document)>();
        foreach (var sourcePath in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = StrictUtf8.GetString(await File.ReadAllBytesAsync(sourcePath, cancellationToken));
            var source = LocalMarkdownDocumentCodec.ParseImportSource(sourcePath, content);
            var title = SourceScalar(source.Metadata, "title") ?? FirstHeading(source.Body) ??
                        Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(title))
            {
                throw ImportInvalid(sourcePath, "Could not resolve a title from frontmatter, H1, or filename.");
            }

            var statusKey = request.FieldMappings.GetValueOrDefault("status") ?? "status";
            var priorityKey = request.FieldMappings.GetValueOrDefault("priority") ?? "priority";
            var sourceStatus = request.ForceStatus ?? SourceScalar(source.Metadata, statusKey) ?? config.DefaultPickFrom;
            string status;
            string? priority;
            try
            {
                status = CanonicalStatus(config, sourceStatus);
            }
            catch (TrackerException exception) when (exception.Code == "ARGUMENT_INVALID")
            {
                throw ImportInvalid(
                    sourcePath,
                    $"Frontmatter field '{(request.ForceStatus is null ? statusKey : "--force-status")}' has unsupported value '{sourceStatus}'.");
            }

            var sourcePriority = SourceScalar(source.Metadata, priorityKey);
            try
            {
                priority = CanonicalPriority(config, sourcePriority);
            }
            catch (TrackerException exception) when (exception.Code == "ARGUMENT_INVALID")
            {
                throw ImportInvalid(
                    sourcePath,
                    $"Frontmatter field '{priorityKey}' has unsupported value '{sourcePriority}'.");
            }
            var createdAtText = SourceScalar(source.Metadata, "createdAt") ?? SourceScalar(source.Metadata, "date");
            var createdAt = DateTimeOffset.TryParse(
                createdAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsedDate)
                ? parsedDate.ToUniversalTime()
                : clock.UtcNow;
            var destination = Path.Combine(
                request.Archive ? paths.Archive : paths.Items,
                PortableFilenameSlugger.FileName(nextId, title));
            var attemptId = Guid.NewGuid().ToString("D");
            var createRequest = new CreateWorkItemRequest(title, source.Body, status, priority);
            var document = LocalMarkdownDocumentCodec.Create(
                nextId,
                destination,
                request.Archive,
                title,
                source.Body,
                status,
                priority,
                new LocalCreationMetadata(
                    1,
                    attemptId,
                    CreationAttempt.ComputeIntentHash(createRequest, request.Archive)),
                createdAt);
            document.UpdatedAt = clock.UtcNow;

            foreach (var pair in source.Metadata.Children)
            {
                var name = (pair.Key as YamlDotNet.RepresentationModel.YamlScalarNode)?.Value
                           ?? throw ImportInvalid(sourcePath, "Frontmatter keys must be scalar.");
                if (LocalMarkdownReservedFields.IsReserved(name))
                {
                    if (!LocalMarkdownReservedFields.ManagedKeys.Contains(name, StringComparer.Ordinal))
                    {
                        throw ImportInvalid(sourcePath, $"Frontmatter field '{name}' is reserved for Wrighty.");
                    }

                    continue;
                }

                document.SetCustomFieldNode(name, pair.Value);
            }

            planned.Add((
                new LocalMarkdownImportItem(sourcePath, nextId, destination, title, status, priority),
                document));
            nextId = checked(nextId + 1);
        }

        if (request.DryRun)
        {
            return new LocalMarkdownImportResult(true, request.Move, planned.Select(value => value.Item).ToArray());
        }

        var staging = Path.Combine(paths.Root, $".import-{Guid.NewGuid():N}.tmp");
        var committed = new List<string>();
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var value in planned)
            {
                var staged = Path.Combine(staging, Path.GetFileName(value.Item.DestinationPath));
                await File.WriteAllTextAsync(
                    staged,
                    LocalMarkdownDocumentCodec.Serialize(value.Document),
                    StrictUtf8,
                    cancellationToken);
                _ = LocalMarkdownDocumentCodec.Parse(
                    value.Item.Id,
                    staged,
                    request.Archive,
                    await File.ReadAllTextAsync(staged, cancellationToken),
                    string.Empty);
            }

            foreach (var value in planned)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(value.Item.DestinationPath)!);
                var staged = Path.Combine(staging, Path.GetFileName(value.Item.DestinationPath));
                File.Move(staged, value.Item.DestinationPath, overwrite: false);
                committed.Add(value.Item.DestinationPath);
            }

            if (request.Move)
            {
                foreach (var value in planned)
                {
                    File.Delete(value.Item.SourcePath);
                }
            }
        }
        catch
        {
            foreach (var destination in committed)
            {
                if (File.Exists(destination)) File.Delete(destination);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        return new LocalMarkdownImportResult(false, request.Move, planned.Select(value => value.Item).ToArray());
    }

    private static IReadOnlyList<string> ResolveImportPaths(
        IReadOnlyList<string> inputs,
        bool recursive)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (File.Exists(input))
            {
                if (!string.Equals(Path.GetExtension(input), ".md", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TrackerException("ARGUMENT_INVALID", $"Import file '{input}' is not Markdown.", 2);
                }

                files.Add(input);
            }
            else if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(
                        input,
                        "*",
                        recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(path => string.Equals(
                        Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                throw new TrackerException("ARGUMENT_INVALID", $"Import path '{input}' does not exist.", 2);
            }
        }

        return files;
    }

    private static string? SourceScalar(
        YamlDotNet.RepresentationModel.YamlMappingNode metadata,
        string key)
    {
        foreach (var pair in metadata.Children)
        {
            if (pair.Key is not YamlDotNet.RepresentationModel.YamlScalarNode { Value: { } name } ||
                !string.Equals(name, key, StringComparison.Ordinal)) continue;
            return pair.Value is YamlDotNet.RepresentationModel.YamlScalarNode scalar
                ? scalar.Value
                : throw new TrackerException(
                    "RESERVED_FIELD_COLLISION",
                    $"Imported frontmatter field '{key}' collides with a Wrighty-managed field and must be scalar.",
                    2,
                    new Dictionary<string, object?> { ["field"] = key });
        }

        return null;
    }

    private static string? FirstHeading(string body)
    {
        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) && line[2..].Trim() is { Length: > 0 } title)
            {
                return title;
            }
        }

        return null;
    }

    private static TrackerException ImportInvalid(string path, string message) => new(
        "WORK_ITEM_DOCUMENT_INVALID",
        $"Cannot import '{path}': {message}",
        5,
        new Dictionary<string, object?> { ["path"] = path });

    public async Task<DashboardSnapshot> GetDashboardAsync(
        TrackerConfig config,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var documents = await LoadAllUnlockedAsync(config, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var now = clock.UtcNow;
        var items = documents
            .Where(document => archiveScope switch
            {
                ArchiveScope.Active => !document.Archived,
                ArchiveScope.Archived => document.Archived,
                _ => true
            })
            .OrderBy(document => StatusRank(config, document.Status))
            .ThenBy(document => PriorityRank(config, document.Priority))
            .ThenBy(document => document.Id)
            .Select(document => new DashboardWorkItem(
                Summary(document),
                ClaimSummary(document, worker, now)))
            .ToArray();
        var revisionInput = string.Join('\n', [
            archiveScope.ToString(),
            .. config.LocalMarkdown!.Statuses,
            "--priorities--",
            .. config.LocalMarkdown.Priorities,
            "--documents--",
            .. documents
                .OrderBy(document => document.Id)
                .Select(document => $"{document.Id}:{document.Archived}:{document.Revision}"),
            "--visible-claims--",
            .. items.Select(item => $"{item.Item.Id.Value}:{item.Claim.State}:{item.Claim.ExpiresAt:O}")
        ]);
        return new DashboardSnapshot(
            config.LocalMarkdown.Statuses,
            config.LocalMarkdown.Priorities,
            items,
            Revision(StrictUtf8.GetBytes(revisionInput)));
    }

    public async Task<EditableWorkItem> GetEditableAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        return new EditableWorkItem(
            Detail(document),
            document.Revision,
            ClaimSummary(document, worker, clock.UtcNow));
    }

    public async Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        ValidateCreate(operation.Request);
        EnsureStore(config);
        var status = CanonicalStatus(config, operation.Request.Status ?? config.DefaultPickFrom);
        var priority = CanonicalPriority(config, operation.Request.Priority);
        var attemptId = CreationAttempt.NormalizeOrCreate(
            string.IsNullOrWhiteSpace(operation.CreationAttemptId) ? null : operation.CreationAttemptId);
        var resolvedRequest = operation.Request with { Status = status, Priority = priority };
        var requestHash = CreationAttempt.ComputeIntentHash(resolvedRequest, operation.ArchiveAfterCreate);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var existing = await LoadAllUnlockedAsync(config, cancellationToken);
        var matches = existing
            .Where(item => string.Equals(item.Creation?.AttemptId, attemptId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new TrackerException(
                "CREATION_ATTEMPT_DUPLICATE",
                $"Creation attempt '{attemptId}' is recorded by multiple local work items.",
                9,
                new Dictionary<string, object?>
                {
                    ["creationAttemptId"] = attemptId,
                    ["ids"] = matches.Select(item => $"local:{item.Id}").ToArray()
                });
        }

        if (matches.Length == 1)
        {
            var match = matches[0];
            if (!string.Equals(match.Creation!.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new TrackerException(
                    "CREATION_ATTEMPT_CONFLICT",
                    $"Creation attempt '{attemptId}' was already used for a different request.",
                    9,
                    new Dictionary<string, object?>
                    {
                        ["creationAttemptId"] = attemptId,
                        ["id"] = $"local:{match.Id}"
                    });
            }

            var matchedDetail = Detail(match);
            return new CreateWorkItemResult(
                matchedDetail.Id,
                null,
                matchedDetail,
                attemptId,
                CreateDisposition.Resumed,
                []);
        }

        var idNumber = existing.Count == 0 ? 1 : checked(existing.Max(item => item.Id) + 1);
        var archived = operation.ArchiveAfterCreate;
        var directory = archived ? paths.Archive : paths.Items;
        var path = Path.Combine(directory, PortableFilenameSlugger.FileName(idNumber, operation.Request.Title));
        var document = LocalMarkdownDocumentCodec.Create(
            idNumber,
            path,
            archived,
            operation.Request.Title,
            operation.Request.Body,
            status,
            priority,
            new LocalCreationMetadata(1, attemptId, requestHash),
            clock.UtcNow);
        foreach (var field in operation.Request.Fields ?? new Dictionary<string, string?>())
        {
            LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
            document.SetCustomField(field.Key, field.Value);
        }
        await WriteUnlockedAsync(document, originalPath: null, cancellationToken);
        var detail = Detail(document);
        return new CreateWorkItemResult(
            detail.Id,
            null,
            detail,
            attemptId,
            CreateDisposition.Created,
            []);
    }

    public async Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        UpdateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        WorkItemPatchValidator.Validate(operation.Patch);
        EnsureStore(config);
        var paths = Paths(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(paths.Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            throw Archived(id);
        }

        await EnsureOwnedUnlockedAsync(document, cancellationToken);
        if (operation.ExpectedRevision is not null &&
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(document.Revision),
                Encoding.ASCII.GetBytes(operation.ExpectedRevision)))
        {
            throw new TrackerException(
                "UPDATE_CONFLICT",
                $"Work item '{id}' changed after it was loaded.",
                9,
                new Dictionary<string, object?>
                {
                    ["id"] = id.Value,
                    ["currentRevision"] = document.Revision
                });
        }

        var patch = operation.Patch;
        var changed = new List<string>();
        if (patch.Title.IsSpecified && !string.Equals(document.Title, patch.Title.Value, StringComparison.Ordinal))
        {
            document.Title = patch.Title.Value!;
            changed.Add("title");
        }

        if (patch.Body.IsSpecified && !string.Equals(document.Body, patch.Body.Value, StringComparison.Ordinal))
        {
            document.Body = patch.Body.Value!;
            changed.Add("body");
        }

        if (patch.Priority.IsSpecified)
        {
            var priority = CanonicalPriority(config, patch.Priority.Value);
            if (!string.Equals(document.Priority, priority, StringComparison.OrdinalIgnoreCase))
            {
                document.Priority = priority;
                changed.Add("priority");
            }
        }

        if (patch.Status.IsSpecified)
        {
            var status = CanonicalStatus(config, patch.Status.Value!);
            if (!string.Equals(document.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                document.Status = status;
                changed.Add("status");
            }
        }

        if (patch.Fields.IsSpecified)
        {
            foreach (var field in patch.Fields.Value ?? new Dictionary<string, string?>())
            {
                LocalMarkdownReservedFields.ValidateCustomFieldName(field.Key);
                var before = document.CustomFieldScalar(field.Key);
                if (!string.Equals(before, field.Value, StringComparison.Ordinal) ||
                    (field.Value is null && document.CustomFields.ContainsKey(field.Key)))
                {
                    document.SetCustomField(field.Key, field.Value);
                    changed.Add($"field:{field.Key}");
                }
            }
        }

        if (operation.ArchiveAfterUpdate)
        {
            document.Archived = true;
            document.Claim = null;
            changed.Add("archived");
        }

        if (changed.Count == 0)
        {
            return new UpdateWorkItemResult(Detail(document), false, []);
        }

        var originalPath = document.Path;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new UpdateWorkItemResult(Detail(document), true, changed);
    }

    public async Task<ClaimResult> TryClaimAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentExecutionContext agentContext,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            throw Archived(id);
        }

        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        var now = clock.UtcNow;
        var current = document.Claim;
        if (current is not null && current.ExpiresAt > now)
        {
            return string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal)
                ? ClaimResult(current, ClaimOutcome.AlreadyOwned)
                : ClaimResult(current, ClaimOutcome.HeldByOther);
        }

        var claim = new LocalClaimMetadata(
            worker,
            agentContext.AgentType,
            agentContext.SessionId,
            Guid.NewGuid().ToString("N"),
            now,
            now.AddMinutes(config.LeaseMinutes),
            ClaimantKinds.ToStorageValue(agentContext.EffectiveClaimantKind));
        document.ClaimEpoch++;
        document.Claim = claim;
        document.UpdatedAt = now;
        await WriteUnlockedAsync(document, document.Path, cancellationToken);
        return ClaimResult(claim, ClaimOutcome.Acquired);
    }

    public async Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var current = document.Claim;
        if (current is null || current.ExpiresAt <= clock.UtcNow)
        {
            return new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed);
        }

        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        return new ClaimOwnershipResult(
            string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal)
                ? ClaimOwnershipState.OwnedByCurrent
                : ClaimOwnershipState.HeldByOther,
            current.WorkerIdentity,
            current.ExpiresAt);
    }

    public async Task ReleaseAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        var current = document.Claim;
        if (current is null || current.ExpiresAt <= clock.UtcNow)
        {
            throw new TrackerException("CLAIM_NOT_FOUND", $"Work item '{id}' does not have an active claim.", 5);
        }

        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (!string.Equals(current.WorkerIdentity, worker, StringComparison.Ordinal))
        {
            throw new TrackerException(
                "CLAIM_NOT_OWNER",
                $"Work item '{id}' is claimed by worker {current.WorkerIdentity}.",
                7,
                new Dictionary<string, object?>
                {
                    ["workerIdentity"] = current.WorkerIdentity,
                    ["expiresAt"] = current.ExpiresAt
                });
        }

        document.Claim = null;
        document.UpdatedAt = clock.UtcNow;
        await WriteUnlockedAsync(document, document.Path, cancellationToken);
    }

    public async Task<ArchiveWorkItemResult> ArchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (document.Archived)
        {
            return new ArchiveWorkItemResult(Detail(document), false, true);
        }

        await EnsureOwnedUnlockedAsync(document, cancellationToken);
        var originalPath = document.Path;
        document.Archived = true;
        document.Claim = null;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new ArchiveWorkItemResult(Detail(document), true, true);
    }

    public async Task<ArchiveWorkItemResult> UnarchiveAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        EnsureStore(config);
        await using var storeLock = await LocalStoreLock.AcquireAsync(Paths(config).Root, cancellationToken);
        var document = await RequiredUnlockedAsync(config, id, cancellationToken);
        if (!document.Archived)
        {
            return new ArchiveWorkItemResult(Detail(document), false, false);
        }

        if (document.Claim is { } claim && claim.ExpiresAt > clock.UtcNow)
        {
            throw new TrackerException(
                "CLAIM_HELD",
                $"Archived work item '{id}' has an active claim by worker {claim.WorkerIdentity}.",
                6,
                new Dictionary<string, object?>
                {
                    ["workerIdentity"] = claim.WorkerIdentity,
                    ["expiresAt"] = claim.ExpiresAt
                });
        }

        var originalPath = document.Path;
        document.Claim = null;
        document.Archived = false;
        document.UpdatedAt = clock.UtcNow;
        document.Path = CanonicalPath(config, document);
        await WriteUnlockedAsync(document, originalPath, cancellationToken);
        return new ArchiveWorkItemResult(Detail(document), true, false);
    }

    private async Task EnsureOwnedUnlockedAsync(
        LocalMarkdownDocument document,
        CancellationToken cancellationToken)
    {
        var claim = document.Claim;
        var worker = await identityProvider.GetIdentityAsync(cancellationToken);
        if (claim is null || claim.ExpiresAt <= clock.UtcNow ||
            !string.Equals(claim.WorkerIdentity, worker, StringComparison.Ordinal))
        {
            throw new TrackerException(
                "CLAIM_REQUIRED",
                $"Work item 'local:{document.Id}' must be claimed by the current worker before it can be updated.",
                6);
        }
    }

    private async Task<LocalMarkdownDocument> RequiredUnlockedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken) =>
        await FindUnlockedAsync(config, LocalMarkdownWorkItemAddressResolver.Decode(id), cancellationToken)
        ?? throw new TrackerException(
            "WORK_ITEM_NOT_FOUND",
            $"Work item '{id}' was not found in the configured tracker.",
            5,
            new Dictionary<string, object?> { ["id"] = id.Value });

    private async Task<LocalMarkdownDocument?> FindUnlockedAsync(
        TrackerConfig config,
        int id,
        CancellationToken cancellationToken) =>
        (await LoadAllUnlockedAsync(config, cancellationToken)).SingleOrDefault(item => item.Id == id);

    private async Task<IReadOnlyList<LocalMarkdownDocument>> LoadAllUnlockedAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var paths = Paths(config);
        var documents = new List<LocalMarkdownDocument>();
        await LoadDirectoryAsync(paths.Items, archived: false);
        await LoadDirectoryAsync(paths.Archive, archived: true);
        var duplicate = documents.GroupBy(item => item.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new TrackerException(
                "STORE_INVALID",
                $"The local tracker contains duplicate work-item ID {duplicate.Key}.",
                3,
                new Dictionary<string, object?>
                {
                    ["id"] = duplicate.Key,
                    ["paths"] = duplicate.Select(item => item.Path).ToArray()
                });
        }

        return documents;

        async Task LoadDirectoryAsync(string directory, bool archived)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.md").Order())
            {
                var match = ItemFileName().Match(Path.GetFileName(path));
                if (!match.Success ||
                    !int.TryParse(match.Groups["number"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    throw new TrackerException(
                        "WORK_ITEM_DOCUMENT_INVALID",
                        $"Markdown file '{path}' does not use the required numeric-title filename format. Use 'wrighty import {path}' to add ordinary Markdown files.",
                        5,
                        new Dictionary<string, object?> { ["path"] = path, ["reason"] = "filename" });
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var content = StrictUtf8.GetString(bytes);
                var document = LocalMarkdownDocumentCodec.Parse(
                    id,
                    path,
                    archived,
                    content,
                    Revision(bytes));
                _ = CanonicalStatus(config, document.Status);
                _ = CanonicalPriority(config, document.Priority);
                documents.Add(document);
            }
        }
    }

    private static async Task WriteUnlockedAsync(
        LocalMarkdownDocument document,
        string? originalPath,
        CancellationToken cancellationToken)
    {
        var destination = document.Path;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (originalPath is not null &&
            !string.Equals(originalPath, destination, StringComparison.Ordinal) &&
            File.Exists(destination))
        {
            throw new TrackerException(
                "STORE_INVALID",
                $"Cannot move work item because '{destination}' already exists.",
                3);
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var content = LocalMarkdownDocumentCodec.Serialize(document);
            var bytes = StrictUtf8.GetBytes(content);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, destination, overwrite: originalPath == destination);
            if (originalPath is not null &&
                !string.Equals(originalPath, destination, StringComparison.Ordinal))
            {
                File.Delete(originalPath);
            }

            document.Revision = Revision(bytes);
            document.RawFrontmatter = LocalMarkdownDocumentCodec.SerializeFrontmatter(document.Metadata);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static WorkItemSummary Summary(LocalMarkdownDocument document) => new(
        LocalMarkdownWorkItemAddressResolver.FromNumber(document.Id),
        document.Title,
        null,
        document.Status,
        document.Priority,
        document.Archived);

    private static WorkItemDetail Detail(LocalMarkdownDocument document) => new(
        LocalMarkdownWorkItemAddressResolver.FromNumber(document.Id),
        document.Title,
        document.Body,
        null,
        document.Status,
        document.Priority,
        document.Archived,
        document.CustomFields,
        document.RawFrontmatter);

    private static ClaimResult ClaimResult(LocalClaimMetadata claim, ClaimOutcome outcome) => new(
        outcome,
        claim.WorkerIdentity,
        claim.ExpiresAt,
        claim.ClaimAttemptId,
        claim.AgentType,
        claim.SessionId,
        claim.ClaimantKind);

    private static string CanonicalPath(TrackerConfig config, LocalMarkdownDocument document)
    {
        var paths = Paths(config);
        return Path.Combine(
            document.Archived ? paths.Archive : paths.Items,
            PortableFilenameSlugger.FileName(document.Id, document.Title));
    }

    private static string CanonicalStatus(TrackerConfig config, string status) =>
        config.LocalMarkdown!.Statuses.SingleOrDefault(item =>
            string.Equals(item, status, StringComparison.OrdinalIgnoreCase))
        ?? throw new TrackerException(
            "ARGUMENT_INVALID",
            $"Unknown local tracker status '{status}'.",
            2);

    private static string? CanonicalPriority(TrackerConfig config, string? priority) =>
        priority is null
            ? null
            : config.LocalMarkdown!.Priorities.SingleOrDefault(item =>
                string.Equals(item, priority, StringComparison.OrdinalIgnoreCase))
              ?? throw new TrackerException(
                  "ARGUMENT_INVALID",
                  $"Unknown local tracker priority '{priority}'.",
                  2);

    private static int PriorityRank(TrackerConfig config, string? priority)
    {
        if (priority is null)
        {
            return int.MaxValue;
        }

        for (var index = 0; index < config.LocalMarkdown!.Priorities.Count; index++)
        {
            if (string.Equals(config.LocalMarkdown.Priorities[index], priority,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static int StatusRank(TrackerConfig config, string status)
    {
        for (var index = 0; index < config.LocalMarkdown!.Statuses.Count; index++)
        {
            if (string.Equals(config.LocalMarkdown.Statuses[index], status,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static WorkItemClaimSummary ClaimSummary(
        LocalMarkdownDocument document,
        string worker,
        DateTimeOffset now)
    {
        var claim = document.Claim;
        if (claim is null || claim.ExpiresAt <= now)
        {
            return new WorkItemClaimSummary(ClaimOwnershipState.Unclaimed);
        }

        return new WorkItemClaimSummary(
            string.Equals(claim.WorkerIdentity, worker, StringComparison.Ordinal)
                ? ClaimOwnershipState.OwnedByCurrent
                : ClaimOwnershipState.HeldByOther,
            claim.WorkerIdentity,
            claim.ExpiresAt,
            claim.AgentType,
            claim.SessionId,
            claim.ClaimantKind);
    }

    private static string Revision(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void ValidateCreate(CreateWorkItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 256 ||
            request.Title.Contains('\r') || request.Title.Contains('\n'))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "title must be a non-empty single line of at most 256 characters.",
                2);
        }
    }

    private static LocalStorePaths Paths(TrackerConfig config)
    {
        var local = config.LocalMarkdown
            ?? throw new TrackerException("CONFIG_INVALID", "localMarkdown configuration is missing.", 3);
        var configDirectory = config.SourcePath is null
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(Path.GetFullPath(config.SourcePath))!;
        var root = Path.GetFullPath(local.Path, configDirectory);
        return new LocalStorePaths(
            root,
            Path.Combine(root, "items"),
            Path.Combine(root, "archive"));
    }

    private static void EnsureStore(TrackerConfig config)
    {
        var paths = Paths(config);
        if (!Directory.Exists(paths.Items) || !Directory.Exists(paths.Archive))
        {
            throw new TrackerException(
                "STORE_NOT_FOUND",
                $"The local Wrighty store '{paths.Root}' is not initialized. Run 'wrighty init'.",
                3,
                new Dictionary<string, object?> { ["path"] = paths.Root });
        }
    }

    private static TrackerException Archived(WorkItemId id) => new(
        "WORK_ITEM_ARCHIVED",
        $"Work item '{id}' is archived. Unarchive it before modifying or claiming it.",
        7,
        new Dictionary<string, object?> { ["id"] = id.Value });

    private sealed record LocalStorePaths(string Root, string Items, string Archive);

    [GeneratedRegex(@"^(?<number>[0-9]+)-[a-z0-9]+(?:-[a-z0-9]+)*\.md$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemFileName();
}
