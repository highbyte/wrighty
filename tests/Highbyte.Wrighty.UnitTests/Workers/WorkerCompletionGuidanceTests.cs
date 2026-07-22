using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class WorkerCompletionGuidanceTests
{
    private const string Path = "/repo/.worktrees/item-1";
    private const string Branch = "wrighty-worker/local-1-add-feature";

    [Fact]
    public void Dirty_merge_local_reviews_commits_and_merges_in_the_correct_order()
    {
        var actions = WorkerCompletionGuidance.ForCompletedWorktree(
            Path, Branch, "merge-local", dirty: true, merged: false);

        var review = Assert.Single(actions, a => a.Scenario.Contains("Review the uncommitted"));
        Assert.Contains(review.Commands, c => c.Contains("git status && git diff"));

        var merge = Assert.Single(actions, a => a.Scenario.Contains("Merge into the main checkout"));
        Assert.Contains(merge.Commands, c => c.Contains("git add -A"));
        Assert.Contains(merge.Commands, c => c.Contains($"git merge --ff-only '{Branch}'"));
        var commands = merge.Commands.ToList();
        var removeIndex = commands.FindIndex(c => c.Contains("git worktree remove"));
        var deleteIndex = commands.FindIndex(c => c.Contains("git branch -d"));
        Assert.True(removeIndex >= 0 && deleteIndex > removeIndex,
            "git worktree remove must precede git branch -d");
    }

    [Fact]
    public void Clean_merge_local_reviews_the_log_and_omits_the_commit_step()
    {
        var actions = WorkerCompletionGuidance.ForCompletedWorktree(
            Path, Branch, "merge-local", dirty: false, merged: false);

        var review = Assert.Single(actions, a => a.Scenario.Contains("Review the committed work"));
        Assert.Contains(review.Commands, c => c.Contains("git log --oneline"));

        var merge = Assert.Single(actions, a => a.Scenario.Contains("Merge into the main checkout"));
        Assert.DoesNotContain(merge.Commands, c => c.Contains("git add -A"));
    }

    [Fact]
    public void Dirty_but_merged_still_commits_and_integrates_rather_than_cleaning_up()
    {
        // A worker branch left uncommitted under the inspect policy has no commits of its own, so
        // the ancestor check reports "merged" even though nothing is integrated. A dirty tree must
        // drive the commit+integrate flow, never the cleanup-only shortcut (which fails on a dirty
        // worktree: git refuses to remove it).
        var actions = WorkerCompletionGuidance.ForCompletedWorktree(
            Path, Branch, "merge-local", dirty: true, merged: true);

        Assert.DoesNotContain(actions, a => a.Scenario.Contains("Clean up the merged worktree"));
        Assert.Single(actions, a => a.Scenario.Contains("Review the uncommitted"));
        var merge = Assert.Single(actions, a => a.Scenario.Contains("Merge into the main checkout"));
        Assert.Contains(merge.Commands, c => c.Contains("git add -A"));
        var commands = merge.Commands.ToList();
        var removeIndex = commands.FindIndex(c => c.Contains("git worktree remove"));
        var deleteIndex = commands.FindIndex(c => c.Contains("git branch -d"));
        Assert.True(removeIndex >= 0 && deleteIndex > removeIndex,
            "git worktree remove must precede git branch -d");
    }

    [Fact]
    public void Merged_branch_only_offers_cleanup_in_the_correct_order()
    {
        var actions = WorkerCompletionGuidance.ForCompletedWorktree(
            Path, Branch, "merge-local", dirty: false, merged: true);

        Assert.DoesNotContain(actions, a => a.Scenario.Contains("Merge into the main checkout"));
        var cleanup = Assert.Single(actions, a => a.Scenario.Contains("Clean up the merged worktree"));
        var commands = cleanup.Commands.ToList();
        var removeIndex = commands.FindIndex(c => c.Contains("git worktree remove"));
        var deleteIndex = commands.FindIndex(c => c.Contains("git branch -d"));
        Assert.True(removeIndex >= 0 && deleteIndex > removeIndex,
            "git worktree remove must precede git branch -d");
    }

    [Fact]
    public void Push_pr_pushes_the_branch_without_local_cleanup()
    {
        var actions = WorkerCompletionGuidance.ForCompletedWorktree(
            Path, Branch, "push-pr", dirty: true, merged: false);

        var push = Assert.Single(actions, a => a.Scenario.Contains("pull request"));
        Assert.Contains(push.Commands, c => c.Contains($"git push -u origin '{Branch}'"));
        Assert.DoesNotContain(push.Commands, c => c.Contains("git worktree remove"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    public void Unset_or_none_integration_references_the_setting(string? integration)
    {
        var actions = WorkerCompletionGuidance.ForCompletedWorktree(
            Path, Branch, integration, dirty: true, merged: false);

        var choose = Assert.Single(actions, a => a.Scenario.Contains("Choose how to integrate"));
        Assert.Empty(choose.Commands);
        Assert.Contains("worker.completion.integration", choose.Description);
    }
}
