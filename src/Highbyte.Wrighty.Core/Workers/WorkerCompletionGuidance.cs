namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Builds the operator guidance for landing a finished worktree item. Shared by the worker finish
/// path (<see cref="WorkerService"/>) and the web dashboard so the completion commands — in
/// particular the "remove the worktree before deleting the branch" ordering that git enforces —
/// have a single source of truth and cannot drift between the two surfaces.
/// </summary>
public static class WorkerCompletionGuidance
{
    /// <summary>
    /// The integrate step for a completed worktree branch, selected by
    /// <c>worker.completion.integration</c>. Returns <c>null</c> when no integration mode is
    /// configured (unset or <c>none</c>). <paramref name="commit"/>, when provided, is prepended so
    /// uncommitted work is committed before integrating. <paramref name="workspaceRemoved"/> is true
    /// when the worktree has already been cleaned up, in which case the removal command is omitted.
    /// </summary>
    public static WorkerOperatorAction? IntegrationAction(
        string? integration,
        string path,
        string branch,
        string? commit,
        bool workspaceRemoved) =>
        integration?.ToLowerInvariant() switch
        {
            "merge-local" => new WorkerOperatorAction(
                "Merge into the main checkout",
                [
                    .. commit is null ? Array.Empty<string>() : [commit],
                    $"git merge --ff-only {branch}",
                    // Remove the worktree before deleting the branch: git refuses to delete a
                    // branch that is still checked out in a worktree.
                    .. workspaceRemoved
                        ? Array.Empty<string>()
                        : [$"git worktree remove {path}"],
                    $"git branch -d {branch}"
                ],
                "Run the merge from the main checkout, then archive the item from the web " +
                "dashboard or with wrighty archive."),
            "push-pr" => new WorkerOperatorAction(
                "Push the branch and open a pull request",
                [
                    .. commit is null ? Array.Empty<string>() : [commit],
                    $"git push -u origin {branch}"
                ],
                "Create the pull request with your provider, then archive the item after " +
                "the merge."),
            _ => null
        };

    /// <summary>
    /// State-aware completion guidance for the web dashboard, computed from the item's live
    /// workspace state. The tree is reviewed and, when dirty, committed before integrating; an
    /// already-merged branch skips straight to cleanup; and when no integration mode is configured
    /// the guidance names the <c>worker.completion.integration</c> setting instead of guessing.
    /// </summary>
    public static IReadOnlyList<WorkerOperatorAction> ForCompletedWorktree(
        string workspacePath,
        string branch,
        string? integration,
        bool dirty,
        bool merged)
    {
        var path = InteractiveAgentCommand.Quote(workspacePath);
        var quotedBranch = InteractiveAgentCommand.Quote(branch);
        var actions = new List<WorkerOperatorAction>
        {
            dirty
                ? new WorkerOperatorAction(
                    "Review the uncommitted changes",
                    [$"cd {path} && git status && git diff"],
                    "The worktree has uncommitted changes retained for review.")
                : new WorkerOperatorAction(
                    "Review the committed work",
                    [$"cd {path} && git status && git log --oneline -10"],
                    "The worktree is clean; the work is committed on the branch.")
        };

        // Cleanup-only is offered solely when the branch is merged AND the tree is clean — i.e. all
        // work is committed and integrated. A dirty tree always means there is uncommitted work to
        // land, even when the ancestor check reports "merged": a freshly created worker branch with
        // no commits of its own is vacuously an ancestor of HEAD, so under the inspect policy (work
        // left uncommitted) merged is true while nothing has actually been integrated.
        if (merged && !dirty)
        {
            actions.Add(new WorkerOperatorAction(
                "Clean up the merged worktree",
                [
                    // git refuses to delete a branch checked out in a worktree, so remove the
                    // worktree first.
                    $"git worktree remove {path}",
                    $"git branch -d {quotedBranch}"
                ],
                "The branch is already merged into HEAD. Remove the worktree, delete the branch, " +
                "then archive the item with the Archive button below."));
            return actions;
        }

        var commit = dirty ? $"cd {path} && git add -A && git commit && cd -" : null;
        if (IntegrationAction(integration, path, quotedBranch, commit, workspaceRemoved: false)
            is { } integrate)
        {
            actions.Add(integrate);
        }
        else
        {
            actions.Add(new WorkerOperatorAction(
                "Choose how to integrate",
                [],
                "No integration mode is configured (worker.completion.integration is unset or " +
                "\"none\"). Set it to \"merge-local\" to fast-forward the branch into the main " +
                "checkout, or \"push-pr\" to push the branch and open a pull request, and this " +
                "panel will show the exact commands. Otherwise integrate manually, then archive " +
                "the item with the Archive button below."));
        }

        return actions;
    }
}
