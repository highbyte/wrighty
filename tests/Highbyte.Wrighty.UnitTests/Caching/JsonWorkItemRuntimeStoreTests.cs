using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Caching;

public sealed class JsonWorkItemRuntimeStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-session-cache-{Guid.NewGuid():N}");

    private JsonWorkItemRuntimeStore Cache() => new(new CachePaths(directory));

    private static StoredWorkItemRuntime Record(string sessionId) => new(
        new SessionAddress("claude", sessionId, "/tmp/workspace"),
        null,
        null,
        new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 19, 11, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Missing_cache_returns_null()
    {
        Assert.Null(await Cache().GetAsync("github:owner/repo#1", CancellationToken.None));
    }

    [Fact]
    public async Task Put_persists_and_a_new_instance_reads_the_record()
    {
        await Cache().PutAsync("github:owner/repo#1", Record("session-one"), CancellationToken.None);

        var reread = await Cache().GetAsync("github:owner/repo#1", CancellationToken.None);

        Assert.NotNull(reread);
        Assert.Equal("claude", reread!.Session?.Agent);
        Assert.Equal("session-one", reread.Session?.Id);
        Assert.Equal("/tmp/workspace", reread.Session?.WorkspacePath);
        Assert.NotNull(reread.LastClaimExpiresAt);
        Assert.Null(await Cache().GetAsync("github:owner/repo#2", CancellationToken.None));
        Assert.True(File.Exists(new CachePaths(directory).WorkItemRuntimePath));
    }

    [Fact]
    public async Task Put_overwrites_the_existing_record_per_key()
    {
        var cache = Cache();
        await cache.PutAsync("github:owner/repo#1", Record("session-old"), CancellationToken.None);
        await cache.PutAsync("github:owner/repo#1", Record("session-new"), CancellationToken.None);
        await cache.PutAsync("github:owner/repo#2", Record("session-other"), CancellationToken.None);

        Assert.Equal("session-new",
            (await cache.GetAsync("github:owner/repo#1", CancellationToken.None))!.Session?.Id);
        Assert.Equal("session-other",
            (await cache.GetAsync("github:owner/repo#2", CancellationToken.None))!.Session?.Id);
    }

    [Fact]
    public async Task Session_context_round_trips_so_a_later_launch_can_classify_what_changed()
    {
        // The manifest is the whole basis for deciding whether a session may be resumed. If it does
        // not survive the file, every resume degrades to "contents unknown" and blocks — safely,
        // but silently, and for no real reason.
        var captured = new DateTimeOffset(2026, 7, 19, 9, 30, 0, TimeSpan.Zero);
        var context = new SessionContextMetadata(
            new ContextManifest(
                1, "sha256:abc", "sha256:title", "sha256:body",
                [new ContextManifestEntry("c1", "sha256:c1body", captured, Minimized: true)],
                captured),
            BaseApprovedAt: captured,
            BatchCommentCutoff: captured,
            ApprovalSource: ContextApprovalSource.ProjectField,
            Decisions: [new DiscussionDecision(
                "c1", DiscussionDecisionKind.Include, DiscussionDecisionSource.Reaction,
                "maintainer", captured, "reaction-1")],
            ReportRunIds: ["run-1"],
            CapturedAt: captured);

        await Cache().PutAsync(
            "github:owner/repo#1",
            Record("session-one") with { Context = context },
            CancellationToken.None);
        var reread = (await Cache().GetAsync("github:owner/repo#1", CancellationToken.None))!.Context;

        // Asserted field by field rather than as a whole record: the collection members compare by
        // reference, so a record-level Assert.Equal could never pass across a file and would say
        // nothing about the values. These are the fields a resume comparison actually reads, and an
        // enum that round-tripped to its default would read as "nothing was approved" rather than
        // failing outright.
        Assert.NotNull(reread!.Manifest);
        Assert.Equal(context.Manifest, reread.Manifest with { Included = context.Manifest!.Included });
        Assert.Equal(ContextApprovalSource.ProjectField, reread.ApprovalSource);
        Assert.Equal(captured, reread.BaseApprovedAt);
        Assert.Equal(captured, reread.BatchCommentCutoff);
        Assert.Equal(captured, reread.CapturedAt);
        Assert.Equal("sha256:abc", reread.SuppliedDigest);
        Assert.Equal(context.Manifest.Included[0], Assert.Single(reread.Manifest!.Included));
        Assert.Equal(context.Decisions![0], Assert.Single(reread.Decisions!));
        Assert.Equal(["run-1"], reread.ReportRunIds);
    }

    [Fact]
    public async Task A_record_written_before_approved_context_support_still_reads()
    {
        // Older cache files have no context member at all. Such an entry must read back as "no
        // manifest" rather than failing the whole file, because the file holds every item's state.
        var path = new CachePaths(directory).WorkItemRuntimePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "version": 1,
              "entries": {
                "github:owner/repo#1": {
                  "session": { "agent": "claude", "id": "session-one", "workspacePath": "/tmp/ws" },
                  "updatedAt": "2026-07-19T10:00:00+00:00",
                  "lastClaimExpiresAt": "2026-07-19T11:00:00+00:00"
                }
              }
            }
            """, CancellationToken.None);

        var reread = await Cache().GetAsync("github:owner/repo#1", CancellationToken.None);

        Assert.Equal("session-one", reread!.Session?.Id);
        Assert.Null(reread.Context);
    }

    [Fact]
    public async Task A_record_holding_removed_continuation_state_still_reads()
    {
        // The trusted-continuation fields were written by an earlier build and are gone. A file
        // holding them must read back intact rather than failing: the file carries every item's
        // state, so one stale member would take the whole installation's runtime record with it.
        var path = new CachePaths(directory).WorkItemRuntimePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "version": 1,
              "entries": {
                "github:owner/repo#1": {
                  "session": { "agent": "claude", "id": "session-one", "workspacePath": "/tmp/ws" },
                  "updatedAt": "2026-07-19T10:00:00+00:00",
                  "context": {
                    "manifest": {
                      "formatVersion": 2,
                      "digest": "sha256:abc",
                      "titleHash": "sha256:title",
                      "bodyHash": "sha256:body",
                      "included": [],
                      "capturedAt": "2026-07-19T09:30:00+00:00"
                    },
                    "approvalSource": "project-field",
                    "consumedContinuationKeys": ["comment:c9@r1"],
                    "automaticContinuations": 2,
                    "lastAutomaticQueueAt": "2026-07-19T09:45:00+00:00"
                  }
                }
              }
            }
            """, CancellationToken.None);

        var reread = await Cache().GetAsync("github:owner/repo#1", CancellationToken.None);

        Assert.Equal("session-one", reread!.Session?.Id);
        Assert.Equal("sha256:abc", reread.Context?.SuppliedDigest);
        Assert.Equal(ContextApprovalSource.ProjectField, reread.Context!.ApprovalSource);
    }

    [Fact]
    public async Task Structured_failure_round_trips_with_stable_wire_names()
    {
        var failure = new AgentFailure(
            AgentFailureKind.UsageExhausted,
            "usage_limit_reached",
            new DateTimeOffset(2026, 7, 24, 4, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(30),
            true,
            AgentFailureConfidence.Authoritative,
            "Usage limit reached.");
        var record = Record("session-one") with
        {
            LastRun = new LastRunRecord(
                RunOutcome.Failed,
                new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero),
                Failure: failure)
        };

        await Cache().PutAsync("github:owner/repo#1", record, CancellationToken.None);

        var reread = await Cache().GetAsync("github:owner/repo#1", CancellationToken.None);
        var json = await File.ReadAllTextAsync(new CachePaths(directory).WorkItemRuntimePath);
        Assert.Equal(failure, reread?.LastRun?.Failure);
        Assert.Contains("\"kind\": \"usage-exhausted\"", json);
        Assert.Contains("\"confidence\": \"authoritative\"", json);
    }

    [Fact]
    public async Task Corrupt_runtime_file_fails_closed()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(paths.WorkItemRuntimePath, "{ not json");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => Cache().GetAsync("github:owner/repo#1", CancellationToken.None));
        Assert.Equal("WORK_ITEM_RUNTIME_CORRUPT", exception.Code);
    }

    [Fact]
    public async Task Unsupported_schema_version_fails_closed()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            paths.WorkItemRuntimePath,
            """{ "version": 99, "entries": { "github:owner/repo#1": {} } }""");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => Cache().GetAsync("github:owner/repo#1", CancellationToken.None));
        Assert.Equal("WORK_ITEM_RUNTIME_CORRUPT", exception.Code);
    }

    [Fact]
    public async Task Pre_overhaul_cache_is_rejected_without_migration()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            Path.Combine(paths.Root, "sessions-v1.json"),
            """{ "version": 1, "entries": {} }""");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => Cache().GetAsync("github:owner/repo#1", CancellationToken.None));

        Assert.Equal("STORE_SCHEMA_UNSUPPORTED", exception.Code);
        Assert.Contains("sessions-v1.json", exception.Message);
        Assert.Contains("Remove or rename", exception.Message);
        Assert.False(File.Exists(paths.WorkItemRuntimePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
