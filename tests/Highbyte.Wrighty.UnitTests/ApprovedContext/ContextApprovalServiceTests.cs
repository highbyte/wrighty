using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.UnitTests.Workers;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

public class ContextApprovalServiceTests
{
    private static readonly WorkItemId Id = new("github:owner/repository#42");
    private static readonly TrackerConfig GitHubConfig = new()
    {
        Backend = "github",
        Repository = "owner/repository",
        ProjectNumber = 1
    };

    [Fact]
    public async Task Inspect_uses_the_diagnostics_purpose_and_effective_limits()
    {
        var result = ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.BaseNeedsReview,
            "The body changed.");
        var provider = new RecordingProvider(result);
        var service = Service(new RecordingApprovalBackend(), _ => provider);

        var actual = await service.InspectAsync(GitHubConfig, Id, default);

        Assert.Same(result, actual);
        Assert.Equal(Id, provider.Id);
        Assert.Equal(ContextReadPurpose.Diagnostics, provider.Purpose);
        Assert.Equal(
            GitHubConfig.EffectiveWorker.EffectiveContext.ToLimits(),
            provider.Limits);
    }

    [Fact]
    public async Task Inspect_refuses_a_backend_without_a_context_provider()
    {
        var service = Service(new RecordingApprovalBackend(), _ => null);

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => service.InspectAsync(GitHubConfig, Id, default));

        Assert.Equal(ExecutionContextResult.Codes.Unsupported, exception.Code);
    }

    [Fact]
    public async Task Approve_cycles_the_field_before_reading_current_diagnostics()
    {
        var events = new List<string>();
        var result = ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.CommentPending,
            "A comment needs a decision.");
        var backend = new RecordingApprovalBackend(events);
        var provider = new RecordingProvider(result, events);
        var service = Service(backend, _ => provider);

        var actual = await service.ApproveAsync(GitHubConfig, Id, default);

        Assert.Same(result, actual);
        Assert.Equal(["cycle", "inspect"], events);
        Assert.Equal(Id, backend.Cycled);
    }

    [Fact]
    public async Task Invalidate_rejects_non_GitHub_backends_before_inspection()
    {
        var provider = new RecordingProvider(ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.BaseNeedsReview,
            "The body changed."));
        var service = Service(new RecordingApprovalBackend(), _ => provider);
        var config = GitHubConfig with { Backend = "local-markdown" };

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => service.InvalidateAsync(config, Id, default));

        Assert.Equal("CONTEXT_APPROVAL_UNSUPPORTED", exception.Code);
        Assert.Null(provider.Id);
    }

    [Fact]
    public async Task Invalidate_resets_a_stale_base()
    {
        var backend = new RecordingApprovalBackend();
        var provider = new RecordingProvider(ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.BaseNeedsReview,
            "The body changed."));
        var service = Service(backend, _ => provider);

        var disposition = await service.InvalidateAsync(GitHubConfig, Id, default);

        Assert.Equal(ContextApprovalInvalidationDisposition.ResetToNeedsReview, disposition);
        Assert.Equal(Id, backend.Invalidated);
    }

    [Fact]
    public async Task Invalidate_preserves_a_newer_approval()
    {
        var approval = new ContextApproval(
            ContextApprovalSource.ProjectField,
            DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            DateTimeOffset.Parse("2026-08-04T12:00:00Z"));
        var backend = new RecordingApprovalBackend();
        var provider = new RecordingProvider(ExecutionContextResult.Refused(
            ExecutionContextResult.Codes.CommentPending,
            "A comment needs a decision.",
            diagnostics: new ExecutionContextDiagnostics(approval, PendingCount: 1)));
        var service = Service(backend, _ => provider);

        var disposition = await service.InvalidateAsync(GitHubConfig, Id, default);

        Assert.Equal(ContextApprovalInvalidationDisposition.PreservedNewerApproval, disposition);
        Assert.Null(backend.Invalidated);
    }

    private static ContextApprovalService Service(
        RecordingApprovalBackend backend,
        Func<TrackerConfig, IExecutionContextProvider?> providers) =>
        new(
            new TrackerService(new TrackerBackendRegistry([
                backend,
                new LocalMarkdownTrackerBackend(
                    new FixedIdentity("context-approval-tests"),
                    new SystemClock())
            ])),
            providers);

    private sealed class RecordingProvider(
        ExecutionContextResult result,
        List<string>? events = null) : IExecutionContextProvider
    {
        public WorkItemId? Id { get; private set; }
        public ContextReadPurpose? Purpose { get; private set; }
        public ContextLimits? Limits { get; private set; }

        public Task<ExecutionContextResult> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            ContextReadPurpose purpose,
            ContextLimits limits,
            CancellationToken cancellationToken)
        {
            events?.Add("inspect");
            Id = id;
            Purpose = purpose;
            Limits = limits;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingApprovalBackend(List<string>? events = null) :
        DelegatingTrackerBackend(
            new LocalMarkdownTrackerBackend(
                new FixedIdentity("context-approval-backend"),
                new SystemClock())),
        ITrackerBackend
    {
        public override string Name => "github";

        public WorkItemId? Cycled { get; private set; }
        public WorkItemId? Invalidated { get; private set; }

        public Task CycleContextApprovalAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            events?.Add("cycle");
            Cycled = id;
            return Task.CompletedTask;
        }

        public Task InvalidateContextApprovalAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            events?.Add("invalidate");
            Invalidated = id;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedIdentity(string identity) : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult(identity);
    }
}
