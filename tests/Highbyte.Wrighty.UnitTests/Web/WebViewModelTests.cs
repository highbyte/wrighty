using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class WebViewModelTests
{
    [Fact]
    public void Configuration_draft_retains_every_repository_settings_field()
    {
        var draft = new ConfigurationFormDraft(
            Operation: "all",
            DefaultPickFrom: "Worker queue",
            DefaultPickTo: "In Progress",
            DefaultFinishTo: "Done",
            DefaultAgent: "codex",
            WorkspaceMode: "worktree",
            CompletionCommit: "inspect",
            CompletionIntegration: "push-pr",
            ArchiveStatuses: "Done",
            ProtectNonHumanClaims: true,
            ApproveCanonicalization: true,
            ExecutionProfiles: "balanced",
            DefaultExecutionProfile: "balanced",
            Agent: "codex",
            PretendNotInstalled: true,
            FailureKind: "usage-exhausted",
            RetryAfterSeconds: 30,
            UsageFailureAction: "retry",
            UsageFailureInitialRetryMinutes: "1",
            UsageFailureBackoffMultiplier: "2",
            UsageFailureMaxRetryHours: "12",
            UsageFailureMaxAttempts: "4",
            UsageFailureResetGraceMinutes: "5",
            UsageFailureAllowCrossAgentHandoff: true,
            UsageFailureFallbacks: new Dictionary<string, string?>
            {
                ["claude"] = "codex",
                ["codex"] = "claude",
                ["copilot"] = "codex"
            },
            LeaseMinutes: "60",
            UseWorkerQueue: false,
            RequirementsAssessmentMode: "enforced",
            AgentPermissions: "workspace",
            AgentPermissionOverrides: new Dictionary<string, string?>
            {
                ["claude"] = "full",
                ["codex"] = "workspace",
                ["copilot"] = "read-only"
            },
            WorktreeRoot: "{repoParent}/{repo}.worktrees",
            BranchFormat: "wrighty-worker/{id}",
            WorktreeNameFormat: "{id}-{title}",
            CompletionPolicy: "user-confirmed",
            HandoverComment: "minimal",
            ShareLocalPaths: true,
            TrustedCommentAuthors: "octocat",
            ContextApprovers: "maintainer",
            ClaimHistoryLimit: "20",
            MaxDiscussionComments: "100",
            MaxEntryCharacters: "1000",
            MaxTotalCharacters: "10000",
            ContinuationTrigger: "command-only",
            ContinuationCommand: "/continue",
            ResumeReaction: "eyes",
            CompletionReaction: "+1",
            MaxAutomaticContinuations: "3",
            CooldownSeconds: "5",
            DebounceSeconds: "2",
            LocalMarkdownStatuses: "Todo, Done",
            LocalMarkdownPriorities: "P1, P2",
            DefaultCreateStatus: "Todo",
            CapacityProbeResult: "rate-limited",
            CapacityProbeRetryAfterSeconds: 45);

        Assert.Equal("all", draft.Operation);
        Assert.Equal("Worker queue", draft.DefaultPickFrom);
        Assert.Equal("Todo", draft.DefaultCreateStatus);
        Assert.Equal("codex", draft.Agent);
        Assert.True(draft.PretendNotInstalled);
        Assert.True(draft.UsageFailureAllowCrossAgentHandoff);
        Assert.False(draft.UseWorkerQueue);
        Assert.True(draft.ShareLocalPaths);
        Assert.Equal("P1, P2", draft.LocalMarkdownPriorities);
        Assert.Equal("rate-limited", draft.CapacityProbeResult);
        Assert.Equal(45, draft.CapacityProbeRetryAfterSeconds);
        Assert.Equal(
            new string?[]
            {
                "codex",
                "enforced",
                "workspace",
                "full",
                "workspace",
                "read-only",
                "{repoParent}/{repo}.worktrees",
                "wrighty-worker/{id}",
                "{id}-{title}",
                "user-confirmed",
                "minimal",
                "octocat",
                "maintainer",
                "20",
                "100",
                "1000",
                "10000",
                "command-only",
                "/continue",
                "eyes",
                "+1",
                "3",
                "5",
                "2",
                "Todo, Done"
            },
            new string?[]
            {
                draft.UsageFailureFallbacks?["copilot"],
                draft.RequirementsAssessmentMode,
                draft.AgentPermissions,
                draft.AgentPermissionOverrides?["claude"],
                draft.AgentPermissionOverrides?["codex"],
                draft.AgentPermissionOverrides?["copilot"],
                draft.WorktreeRoot,
                draft.BranchFormat,
                draft.WorktreeNameFormat,
                draft.CompletionPolicy,
                draft.HandoverComment,
                draft.TrustedCommentAuthors,
                draft.ContextApprovers,
                draft.ClaimHistoryLimit,
                draft.MaxDiscussionComments,
                draft.MaxEntryCharacters,
                draft.MaxTotalCharacters,
                draft.ContinuationTrigger,
                draft.ContinuationCommand,
                draft.ResumeReaction,
                draft.CompletionReaction,
                draft.MaxAutomaticContinuations,
                draft.CooldownSeconds,
                draft.DebounceSeconds,
                draft.LocalMarkdownStatuses
            });
    }
}
