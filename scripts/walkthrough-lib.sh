#!/usr/bin/env bash
#
# walkthrough-lib.sh — shared library for the worker-completion-lifecycle walkthroughs.
#
# The scenario logic (worker run, branch/worktree recording, cleanup-guard matrix, integration
# guidance, guided completion) is driven entirely through the Wrighty CLI, which is backend-neutral
# — so every scenario here is identical for the Local Markdown and GitHub backends. Only the pieces
# that genuinely differ per backend live in the thin driver scripts that source this file:
# fixture provisioning, `write_config`, and teardown.
#
# This file only defines functions and initializes presentation state; it is safe to source. Each
# driver sets the globals the functions rely on before calling them:
#   ASSUME_AGENT, FIXTURE_REPO, CLI_DLL, WORKTREE_ROOT, ACTIVATE_SCRIPT
#   ITEM_INSPECT, ITEM_AGENT, ITEM_NAMING, ITEM_NOWORKTREE, ITEM_MERGE, ITEM_PUSH (via wt_seed_items)
# and provides a `write_config <commit-policy> <integration> <branchFormat>` function.

# ----- presentation state (set once when sourced) ---------------------------

if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
    C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'; C_OK=$'\033[32m'; C_WARN=$'\033[33m'
    C_ERR=$'\033[31m'; C_CYAN=$'\033[36m'; C_RESET=$'\033[0m'
else
    C_BOLD=""; C_DIM=""; C_OK=""; C_WARN=""; C_ERR=""; C_CYAN=""; C_RESET=""
fi

PASS_COUNT=0
FAIL_COUNT=0

# ----- output helpers -------------------------------------------------------

die() { printf '%serror:%s %s\n' "$C_ERR" "$C_RESET" "$*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || die "required command '$1' was not found"; }
step() { printf '\n%s==> %s%s\n' "$C_BOLD" "$*" "$C_RESET"; }
explain() { printf '    %s%s%s\n' "$C_DIM" "$*" "$C_RESET"; }
pass() { printf '%sok:%s %s\n' "$C_OK" "$C_RESET" "$*"; PASS_COUNT=$((PASS_COUNT + 1)); }
fail() { printf '%sFAIL:%s %s\n' "$C_ERR" "$C_RESET" "$*"; FAIL_COUNT=$((FAIL_COUNT + 1)); }
note() { printf '%snote:%s %s\n' "$C_WARN" "$C_RESET" "$*"; }

# An instruction block for the SECOND terminal. Header/footer rules delimit it, but the content
# lines are printed flush-left with no prefix so commands can be copied and pasted directly.
manual() {
    printf '\n%s── do this in your SECOND terminal ─────────────────────────%s\n' "$C_CYAN" "$C_RESET"
    local line
    for line in "$@"; do printf '%s\n' "$line"; done
    printf '%s────────────────────────────────────────────────────────────%s\n' "$C_CYAN" "$C_RESET"
}

# After the operator presses Enter, the scenario immediately runs its verification, which queries
# the backend (network round-trips on the GitHub backend). Acknowledge the keypress right away so a
# multi-second GitHub check does not look like a hang.
pause() {
    printf '\n%s[press Enter when done]%s ' "$C_BOLD" "$C_RESET"
    read -r _
    printf '%s… verifying results (querying the backend; on GitHub this can take a few seconds)%s\n' \
        "$C_DIM" "$C_RESET"
}

confirm() {
    local answer
    printf '\n%s%s%s [y/N] ' "$C_BOLD" "$1" "$C_RESET"
    read -r answer
    [[ "$answer" == [yY] || "$answer" == [yY][eE][sS] ]]
}

# ----- agent + build --------------------------------------------------------

# wt_resolve_agent [preset] -> sets ASSUME_AGENT (prompts when the preset is empty) and validates.
wt_resolve_agent() {
    ASSUME_AGENT=${1:-}
    if [[ -z "$ASSUME_AGENT" ]]; then
        printf '\n%sWhich agent will you drive?%s [claude/codex/copilot] (default claude): ' \
            "$C_BOLD" "$C_RESET"
        read -r ASSUME_AGENT
        [[ -z "$ASSUME_AGENT" ]] && ASSUME_AGENT="claude"
    fi
    case "$ASSUME_AGENT" in
        claude | codex | copilot) ;;
        *) die "unsupported agent '$ASSUME_AGENT' (use claude, codex, or copilot)" ;;
    esac
}

# Codex invokes the skill with $wrighty; claude and copilot use /wrighty.
skill_prefix() {
    [[ "$ASSUME_AGENT" == "codex" ]] && printf '$wrighty' || printf '/wrighty'
}

# wt_build_cli <cli-project> <cli-dll> <skip-build:true|false> <configuration>
wt_build_cli() {
    local cli_project=$1 cli_dll=$2 skip_build=$3 configuration=$4
    if [[ "$skip_build" == false ]]; then
        step "Building the local Wrighty CLI"
        dotnet build "$cli_project" --configuration "$configuration" --nologo || die "build failed"
    fi
    [[ -f "$cli_dll" ]] || die "local CLI output '$cli_dll' was not found (run without --skip-build)"
}

# ----- CLI + fixture helpers (backend-neutral, driven through the dev CLI) ---

# Run the dev CLI against the fixture repo. Both backends discover their config from FIXTURE_REPO.
wr() { (cd "$FIXTURE_REPO" && dotnet "$CLI_DLL" "$@"); }

# wt_install_and_commit_skill — install the real Wrighty skill for ASSUME_AGENT with the CLI's own
# installer (claude -> .claude/skills, codex/copilot -> .agents/skills), then commit it so it is
# present at HEAD when the worker checks out a fresh worktree. Force-add the skill: a global ignore
# of .claude/ or .agents/ (common in dotfiles) would otherwise silently exclude it and every
# worktree scenario would fail the worker's skill-availability preflight. Sets SKILL_DIR.
wt_install_and_commit_skill() {
    SKILL_DIR=".claude/skills/wrighty"
    [[ "$ASSUME_AGENT" == "codex" || "$ASSUME_AGENT" == "copilot" ]] && SKILL_DIR=".agents/skills/wrighty"
    if wr skill install --agent "$ASSUME_AGENT" --scope project --force >/dev/null 2>&1; then
        (cd "$FIXTURE_REPO" && git add -f "$SKILL_DIR" >/dev/null 2>&1 &&
            git commit -q -m "Install Wrighty skill for $ASSUME_AGENT" >/dev/null 2>&1) || true
    fi
    if git -C "$FIXTURE_REPO" cat-file -e "HEAD:$SKILL_DIR/SKILL.md" 2>/dev/null; then
        explain "Installed and committed the Wrighty skill for $ASSUME_AGENT ($SKILL_DIR)"
    else
        die "Could not commit the Wrighty skill '$SKILL_DIR/SKILL.md' to the fixture. Worktree scenarios need it at HEAD (a global ignore of $SKILL_DIR would cause this)."
    fi
}

# create_item <title> <body>  -> echoes the new id on stdout, or prints the CLI error to stderr and
# returns non-zero. --auto + --agent make the item eligible for a fresh worker run (the worker
# requires wrighty-auto=true and a resolvable vendor). This returns rather than calling `die`
# because it is invoked inside `$( … )`: a `die`/`exit` there would only kill the command-
# substitution subshell, letting the parent continue with an empty id. The caller (wt_seed_items)
# aborts in the main shell instead.
create_item() {
    local out rc
    out=$(wr create --title "$1" --body "$2" --auto --agent "$ASSUME_AGENT" --json 2>&1)
    rc=$?
    if ((rc != 0)); then
        printf 'create failed for %s:\n%s\n' "$1" "$out" >&2
        return 1
    fi
    printf '%s' "$out" | jq -er '.result.id' 2>/dev/null || {
        printf 'create for %s returned no id:\n%s\n' "$1" "$out" >&2
        return 1
    }
}

# wt_seed_items — create the six work items the scenarios use and export their ids. Aborts the whole
# run (in the main shell) if any create fails, so a partial seed never continues with empty ids.
wt_seed_items() {
    local seed_error="could not seed the work items (see the CLI error above); aborting before creating orphans"
    ITEM_INSPECT=$(create_item "Add a greeting file" \
        "Create a file HELLO.md in the repo root containing a one-line greeting. Keep it tiny.") || die "$seed_error"
    ITEM_AGENT=$(create_item "Add a farewell file" \
        "Create a file BYE.md in the repo root containing a one-line farewell. Keep it tiny.") || die "$seed_error"
    ITEM_NAMING=$(create_item "Tweak the readme" \
        "Append a single line to README.md. Keep it tiny.") || die "$seed_error"
    ITEM_NOWORKTREE=$(create_item "Never run in worktree" \
        "This item exists only to demonstrate a guard; do not process it.") || die "$seed_error"
    ITEM_MERGE=$(create_item "Add a merge file" \
        "Create a file MERGE.md in the repo root containing a one-line note. Keep it tiny.") || die "$seed_error"
    ITEM_PUSH=$(create_item "Add a push file" \
        "Create a file PUSH.md in the repo root containing a one-line note. Keep it tiny.") || die "$seed_error"
    # E1 gets its own item so the guided-completion scenario is order-independent: scenario D
    # consumes A1's worktree (its D4 cleanup removes the worktree before failing on the unmerged
    # branch), so E1 must not reuse A1's item.
    ITEM_GUIDED=$(create_item "Guide a completion file" \
        "Create a file GUIDE.md in the repo root containing a one-line note. Keep it tiny.") || die "$seed_error"
    pass "created items: $ITEM_INSPECT, $ITEM_AGENT, $ITEM_NAMING, $ITEM_NOWORKTREE, $ITEM_MERGE, $ITEM_PUSH, $ITEM_GUIDED"
}

# wt_bootstrap — tell the operator how to prepare the SECOND terminal.
wt_bootstrap() {
    step "Set up your SECOND terminal now"
    explain "${WT_BOOTSTRAP_NOTE:-Everything you type in that terminal targets the disposable fixture only.}"
    manual \
        "cd '$FIXTURE_REPO'" \
        "source '$ACTIVATE_SCRIPT'" \
        "wrighty list" \
        "" \
        "The source line makes 'wrighty' the dev build; wrighty list should show the seeded items." \
        "Your agent CLI ('$ASSUME_AGENT') must be installed and authenticated."
    pause
}

# ----- verification helpers -------------------------------------------------

item_status() { wr get "$1" --json 2>/dev/null | jq -r '.result.status // empty'; }

# Branch and workspace path are surfaced by 'wrighty workspaces', not 'get'. They only return a
# value while the worktree still exists on disk under worktreeRoot (a removed worktree drops out).
ws_field() {
    # ws_field <item-id> <field>
    wr workspaces --json 2>/dev/null |
        jq -r --arg id "$1" --arg f "$2" \
            '.result.workspaces[]? | select(.itemId == $id) | .[$f] // empty' |
        head -n1
}
item_branch() { ws_field "$1" branch; }
item_workspace() { ws_field "$1" path; }

# Capture the error code from a failing 'wr workspaces cleanup' (best effort: JSON first, then text).
cleanup_error_code() {
    local out
    out=$(wr workspaces cleanup "$1" --json 2>&1)
    local code
    code=$(printf '%s' "$out" | jq -r '.error.code // empty' 2>/dev/null)
    if [[ -z "$code" ]]; then
        code=$(printf '%s' "$out" | grep -oE 'WORKSPACE_[A-Z_]+|CLAIM_HELD' | head -n1)
    fi
    printf '%s' "$code"
}

# Worker activity and the captured last-run outcome are surfaced by 'wrighty get' (plan 023 a/f).
item_activity() { wr get "$1" --json 2>/dev/null | jq -r '.result.worker.activity // empty'; }
item_last_run_outcome() { wr get "$1" --json 2>/dev/null | jq -r '.result.session.lastRun.outcome // empty'; }

# Verify the plan-023 discovery surfaces after a run reached a terminal state: the captured last-run
# outcome and worker activity (a/f), the at-a-glance worktree flag (d), and the 'wrighty status'
# grouping (c). Uses note (not fail) for the state-dependent checks so ordinary agent variance in a
# walkthrough does not read as a hard failure.
wt_verify_run_surfaces() {
    # wt_verify_run_surfaces <item> <expected-activity> <expected-outcome>
    local item=$1 expect_activity=$2 expect_outcome=$3
    local activity outcome
    activity=$(item_activity "$item")
    outcome=$(item_last_run_outcome "$item")
    [[ "$activity" == "$expect_activity" ]] &&
        pass "worker activity for $item is '$activity' (plan 023 f)" ||
        note "worker activity for $item is '${activity:-<none>}' (expected '$expect_activity'); confirm the run reached that state"
    [[ "$outcome" == "$expect_outcome" ]] &&
        pass "captured last-run outcome for $item is '$outcome' (plan 023 a)" ||
        note "last-run outcome for $item is '${outcome:-<none>}' (expected '$expect_outcome')"
    if wr status --json 2>/dev/null | jq -e --arg id "$item" --arg g "$expect_activity" '
        ($g == "completed" and any(.result.completed[]?; .id == $id))
        or ($g == "needs-attention" and any(.result.needsAttention[]?; .id == $id))
        or ($g == "paused-session" and any(.result.paused[]?; .id == $id))' >/dev/null 2>&1; then
        pass "wrighty status grouped $item under its expected section (plan 023 c)"
    else
        note "wrighty status did not group $item as '$expect_activity' — inspect 'wrighty status'"
    fi
    if wr list --json 2>/dev/null |
        jq -e --arg id "$item" 'any(.result[]?; .id == $id and .hasRecordedWorktree == true)' >/dev/null 2>&1; then
        pass "list flags $item with a recorded worktree (plan 023 d)"
    else
        note "list did not flag $item with a recorded worktree — inspect 'wrighty list'"
    fi
}

# Parse a GitHub item id ("github:owner/repo#N") into "owner/repo N"; prints nothing for local ids.
gh_issue_ref() {
    case "$1" in
        github:*)
            local rest=${1#github:}
            printf '%s %s' "${rest%%#*}" "${rest##*#}"
            ;;
        *)
            # Local (or any non-GitHub) id: no owner/repo#N to emit.
            ;;
    esac
}

# Verify the single overwrite-style handover comment (plan 023 b) on a GitHub issue. A no-op for the
# Local Markdown backend, whose equivalent surface is the web dashboard / 'wrighty get' last-run
# block, so the same call is safe to make from the backend-neutral scenarios.
wt_verify_handover_comment() {
    # wt_verify_handover_comment <item> <present|resolved>
    local ref repo num body
    ref=$(gh_issue_ref "$1")
    [[ -n "$ref" ]] || return 0
    command -v gh >/dev/null 2>&1 || { note "gh not available; cannot verify the handover comment for $1"; return 0; }
    read -r repo num <<<"$ref"
    body=$(gh api "repos/$repo/issues/$num/comments" --jq '.[].body' 2>/dev/null | tr '\n' ' ')
    if ! printf '%s' "$body" | grep -q "wrighty-handover:v1"; then
        note "no handover comment (<!-- wrighty-handover:v1 -->) on $repo#$num yet — confirm worker.handoverComment is not 'off'"
        return 0
    fi
    if [[ "$2" == "resolved" ]]; then
        printf '%s' "$body" | grep -qi "resolved" &&
            pass "handover comment on $repo#$num was trimmed to its resolved form (plan 023 b)" ||
            note "handover comment on $repo#$num present but not yet in resolved form"
    else
        pass "handover comment posted on $repo#$num (plan 023 b)"
    fi
}

should_run() {
    # should_run <title>
    printf '\n%s──────────────────────────────────────────────────────────%s\n' "$C_BOLD" "$C_RESET"
    confirm "Run scenario: $1 ?"
}

# ===========================================================================
# Scenario A1 — inspect policy (default): retained dirty worktree
# ===========================================================================
scenario_inspect() {
    should_run "A1 — inspect commit policy (worktree retained for review)" || return 0
    write_config "inspect" "merge-local" "wrighty-worker/{id}-{unique}"
    step "A1: commit=inspect, integration=merge-local"
    explain "The agent is told NOT to commit; on finish the worktree is retained as your review queue."
    manual \
        "wrighty worker --item $ITEM_INSPECT --agent $ASSUME_AGENT --workspace-mode worktree --once --yes" \
        "" \
        "Watch the finish output for:  branch: wrighty-worker/...   and an operator-actions block." \
        "Let the agent finish, then come back here."
    pause

    local branch ws
    branch=$(item_branch "$ITEM_INSPECT")
    ws=$(item_workspace "$ITEM_INSPECT")
    [[ -n "$branch" ]] && pass "branch recorded on the item: $branch" || fail "no branch recorded on $ITEM_INSPECT"
    if [[ -n "$ws" && -d "$ws" ]]; then
        pass "worktree retained on disk: $ws"
    else
        fail "expected a retained worktree directory (workspacePath='$ws')"
    fi
    if wr workspaces --json 2>/dev/null | jq -e --arg id "$ITEM_INSPECT" \
        '.result.workspaces[]? | select(.itemId == $id)' >/dev/null 2>&1; then
        pass "'wrighty workspaces' lists the retained worktree for $ITEM_INSPECT"
    else
        note "'wrighty workspaces' did not list $ITEM_INSPECT — inspect the output manually:"
        wr workspaces || true
    fi

    # Plan 023: a finished-and-landed item with a retained worktree reports worker activity
    # 'completed' (not 'paused-session'), carries the captured succeeded outcome, is flagged in
    # list/status, and — on GitHub — has the retained-worktree handover comment posted.
    wt_verify_run_surfaces "$ITEM_INSPECT" "completed" "succeeded"
    wt_verify_handover_comment "$ITEM_INSPECT" present
}

# ===========================================================================
# Scenario D — the cleanup guard matrix (mostly automated on real state)
# ===========================================================================
scenario_guards() {
    should_run "D — 'wrighty workspaces cleanup' guard matrix" || return 0
    step "D: cleanup guards"

    # D6 — no recorded workspace.
    explain "D6: cleanup an item that never ran in worktree mode -> WORKSPACE_NOT_FOUND"
    local code
    code=$(cleanup_error_code "$ITEM_NOWORKTREE")
    [[ "$code" == "WORKSPACE_NOT_FOUND" ]] && pass "D6 got WORKSPACE_NOT_FOUND" || fail "D6 expected WORKSPACE_NOT_FOUND, got '${code:-<none>}'"

    # D5 — active claim blocks cleanup (checked before anything else).
    explain "D5: an item with an active claim -> CLAIM_HELD"
    wr claim "$ITEM_NOWORKTREE" >/dev/null 2>&1
    code=$(cleanup_error_code "$ITEM_NOWORKTREE")
    [[ "$code" == "CLAIM_HELD" ]] && pass "D5 got CLAIM_HELD" || fail "D5 expected CLAIM_HELD, got '${code:-<none>}'"
    wr release "$ITEM_NOWORKTREE" >/dev/null 2>&1 || true

    # D3 / D4 need the retained worktree from A1.
    local ws branch
    ws=$(item_workspace "$ITEM_INSPECT")
    branch=$(item_branch "$ITEM_INSPECT")
    if [[ -z "$ws" || ! -d "$ws" ]]; then
        note "D3/D4 need the retained worktree from scenario A1 — skipping (run A1 first)."
        return 0
    fi

    # Release any lingering claim so we get past the CLAIM_HELD gate.
    wr release "$ITEM_INSPECT" >/dev/null 2>&1 || true
    if [[ "$(cleanup_error_code "$ITEM_INSPECT")" == "CLAIM_HELD" ]]; then
        note "$ITEM_INSPECT still shows an active claim; cannot reach the git guards. Skipping D3/D4."
        return 0
    fi

    # D3 — dirty worktree refused by git.
    explain "D3: make the worktree dirty -> WORKSPACE_NOT_CLEAN"
    printf 'uncommitted change\n' >>"$ws/README.md"
    code=$(cleanup_error_code "$ITEM_INSPECT")
    [[ "$code" == "WORKSPACE_NOT_CLEAN" ]] && pass "D3 got WORKSPACE_NOT_CLEAN" || fail "D3 expected WORKSPACE_NOT_CLEAN, got '${code:-<none>}'"

    # D4 — clean tree but an unmerged commit on the branch.
    explain "D4: commit inside the worktree (unmerged) -> WORKSPACE_BRANCH_UNMERGED"
    (cd "$ws" && git add -A && git commit -q -m "Walkthrough unmerged commit") || note "could not stage the D4 commit"
    code=$(cleanup_error_code "$ITEM_INSPECT")
    [[ "$code" == "WORKSPACE_BRANCH_UNMERGED" ]] && pass "D4 got WORKSPACE_BRANCH_UNMERGED" || fail "D4 expected WORKSPACE_BRANCH_UNMERGED, got '${code:-<none>}'"
    explain "(git removed the clean worktree during D4; the branch '$branch' remains until merged/deleted.)"

    note "Guards verified. This left branch '$branch' behind by design; the guided-completion or merge-local flow would finish it."
}

# ===========================================================================
# Scenario A2 — agent policy: clean worktree removed on finish
# ===========================================================================
scenario_agent_policy() {
    should_run "A2 — agent commit policy (clean worktree removed)" || return 0
    write_config "agent" "none" "wrighty-worker/{id}-{unique}"
    step "A2: commit=agent, integration=none"
    explain "The agent is told to commit in logical commits; a CLEAN worktree is removed on finish."
    explain "Heads-up: 'agent' mode only works if your agent is allowed to commit unattended."
    explain "A global 'do not commit unless I ask' rule or a restrictive permission mode will veto"
    explain "the commit; the item then safely lands in needs-attention with the worktree retained"
    explain "(expected, not a bug). Permit commits for this run to see the clean-removal path."
    manual \
        "wrighty worker --item $ITEM_AGENT --agent $ASSUME_AGENT --workspace-mode worktree --once --yes" \
        "" \
        "Expect a 'workspace-removed' line if the agent left the tree clean (committed everything)."
    pause

    local ws
    ws=$(item_workspace "$ITEM_AGENT")
    if [[ -n "$ws" && -d "$ws" ]]; then
        note "worktree still present at $ws — the agent likely left uncommitted changes, so git kept it (this is the safety guard working, not a bug)."
    else
        pass "clean worktree was removed on finish"
        # After removal the worktree drops out of 'workspaces'; the committed work should
        # survive on the worker branch.
        if git -C "$FIXTURE_REPO" for-each-ref --format='%(refname:short)' refs/heads/ |
            grep -q 'wrighty-worker'; then
            pass "worker branch preserved after worktree removal"
        else
            note "no wrighty-worker branch found — if the agent made no commits there is nothing to preserve"
        fi
    fi
}

# ===========================================================================
# Scenario C — naming template + config validation
# ===========================================================================
scenario_naming() {
    should_run "C — branch naming template and config validation" || return 0

    # C4 — config validation is fully automated (no agent needed).
    step "C4: invalid placeholder is rejected at config load"
    write_config "inspect" "none" "wrighty-worker/{bogus}"
    local out
    out=$(wr list --json 2>&1)
    if printf '%s' "$out" | grep -q "CONFIG_INVALID"; then
        pass "C4 rejected the unknown placeholder with CONFIG_INVALID"
    else
        fail "C4 expected CONFIG_INVALID; got: $(printf '%s' "$out" | head -n1)"
    fi

    # C1 — custom branchFormat with {number} and {title}.
    write_config "inspect" "none" "feature/{number}-{title}"
    step "C1: branchFormat=feature/{number}-{title}"
    local expected_number expected
    expected_number=$(printf '%s' "$ITEM_NAMING" | grep -oE '[0-9]+$')
    expected="feature/${expected_number}-tweak-the-readme"
    explain "Expected branch: $expected"
    manual \
        "wrighty worker --item $ITEM_NAMING --agent $ASSUME_AGENT --workspace-mode worktree --once --yes"
    pause
    local branch
    branch=$(item_branch "$ITEM_NAMING")
    if [[ "$branch" == "$expected" ]]; then
        pass "C1 branch matched the template: $branch"
    else
        note "C1 branch was '$branch' (expected '$expected'). Title slugging may differ; verify it is sanitized and lowercased."
    fi
}

# ===========================================================================
# Scenario E1 — the guided-completion skill flow (headline)
# ===========================================================================
scenario_guided() {
    should_run "E1 — guided-completion skill flow (review -> commit -> integrate -> cleanup -> archive)" || return 0
    write_config "inspect" "merge-local" "wrighty-worker/{id}-{unique}"
    step "E1: run a fresh inspect worker, then drive the guided completion"
    explain "E1 uses its own item ($ITEM_GUIDED) so it does not depend on A1 or D — scenario D consumes"
    explain "A1's worktree. First run a worker (inspect) to leave a retained worktree with review-ready"
    explain "changes, then resume that session and let the skill drive completion with approval at each step."
    manual \
        "wrighty worker --item $ITEM_GUIDED --agent $ASSUME_AGENT --workspace-mode worktree --once --yes" \
        "" \
        "Let the agent create GUIDE.md and finish (leaving it uncommitted under inspect), then come back here."
    pause

    if [[ -z "$(item_workspace "$ITEM_GUIDED")" ]]; then
        note "E1 needs a retained worktree for $ITEM_GUIDED; skipping (did the worker run and finish?)."
        return 0
    fi

    step "Now drive the guided completion inside the recorded session"
    explain "Open the recorded vendor session and let the skill walk you through completion with approval at each step."
    manual \
        "wrighty resume-command $ITEM_GUIDED" \
        "" \
        "That prints a command; paste and run it to open the $ASSUME_AGENT session in the worktree." \
        "Then, inside that session, enter:" \
        "" \
        "$(skill_prefix) Complete item $ITEM_GUIDED: summarize the diff, propose a commit message, and after my approval commit, integrate, clean up the workspace, and archive the item." \
        "" \
        "Approve each step. When the session finishes, come back here."
    pause

    local status ws
    status=$(item_status "$ITEM_GUIDED")
    ws=$(item_workspace "$ITEM_GUIDED")
    if wr get "$ITEM_GUIDED" --json 2>/dev/null | jq -e '.result.archived == true' >/dev/null 2>&1; then
        pass "E1 item is archived"
        # Plan 023 b: archiving trims the handover comment to its short resolved form (GitHub only).
        wt_verify_handover_comment "$ITEM_GUIDED" resolved
    elif [[ "$status" == "Done" ]]; then
        pass "E1 item reached Done (archive optional per your archive.onStatuses)"
    else
        note "E1 item status is '$status' — confirm the guided flow completed the archive step."
    fi
    if [[ -n "$ws" && -d "$ws" ]]; then
        note "worktree still present at $ws — confirm the cleanup step ran (or run 'wrighty workspaces cleanup $ITEM_GUIDED')."
    else
        pass "E1 worktree was cleaned up"
    fi
}

# ===========================================================================
# Scenario B — integration guidance (merge-local executed, push-pr to a remote)
# ===========================================================================
scenario_integration() {
    should_run "B — integration guidance (merge-local executed, push-pr to the configured remote)" || return 0

    # B1 — merge-local, actually integrated into the main checkout.
    write_config "inspect" "merge-local" "wrighty-worker/{id}-{title}"
    step "B1: integration=merge-local"
    explain "The agent creates MERGE.md and leaves it uncommitted (inspect). The finish output prints"
    explain "a 'Merge into the main checkout' action; you will run those commands to land it on main."
    manual \
        "wrighty worker --item $ITEM_MERGE --agent $ASSUME_AGENT --workspace-mode worktree --once --yes"
    pause
    local ws branch
    ws=$(item_workspace "$ITEM_MERGE")
    branch=$(item_branch "$ITEM_MERGE")
    if [[ -z "$ws" || ! -d "$ws" || -z "$branch" ]]; then
        note "B1 needs a retained worktree and branch for $ITEM_MERGE; skipping (did the worker run?)."
    else
        explain "Now commit the work and merge it (the finish output prints these):"
        manual \
            "cd '$ws' && git add -A && git commit -m 'Complete $ITEM_MERGE'" \
            "cd '$FIXTURE_REPO' && git merge --ff-only $branch && git worktree remove '$ws' && git branch -d $branch"
        pause
        [[ -f "$FIXTURE_REPO/MERGE.md" ]] &&
            pass "B1 work landed on main (MERGE.md present in the main checkout)" ||
            fail "B1 MERGE.md is not on main — the merge did not complete"
        git -C "$FIXTURE_REPO" show-ref --verify --quiet "refs/heads/$branch" &&
            fail "B1 branch $branch still exists" ||
            pass "B1 worker branch was deleted"
        [[ -d "$ws" ]] && note "B1 worktree still present at $ws (run 'git worktree remove')" ||
            pass "B1 worktree was removed"
    fi

    # B2 — push-pr, pushed to the configured origin.
    write_config "inspect" "push-pr" "wrighty-worker/{id}-{title}"
    step "B2: integration=push-pr"
    explain "The finish output prints a 'Push the branch and open a pull request' action."
    explain "${WT_PUSH_REMOTE_NOTE:-The fixture has a local bare remote as origin, so the push actually runs (PR creation is manual/N-A).}"
    manual \
        "wrighty worker --item $ITEM_PUSH --agent $ASSUME_AGENT --workspace-mode worktree --once --yes"
    pause
    local ws2 branch2
    ws2=$(item_workspace "$ITEM_PUSH")
    branch2=$(item_branch "$ITEM_PUSH")
    if [[ -z "$ws2" || ! -d "$ws2" || -z "$branch2" ]]; then
        note "B2 needs a retained worktree and branch for $ITEM_PUSH; skipping (did the worker run?)."
    else
        explain "Now commit the work and push it (the finish output prints these):"
        manual \
            "cd '$ws2' && git add -A && git commit -m 'Complete $ITEM_PUSH'" \
            "cd '$ws2' && git push -u origin $branch2"
        pause
        if git -C "$FIXTURE_REPO" ls-remote --heads origin "$branch2" 2>/dev/null | grep -q "$branch2"; then
            pass "B2 branch $branch2 was pushed to origin"
        else
            fail "B2 branch $branch2 was not found on origin — the push did not complete"
        fi
    fi
}

# ----- run all scenarios + summary ------------------------------------------

wt_run_scenarios() {
    step "Walkthrough scenarios"
    explain "Answer y to run each scenario, or N to skip. A1 first is recommended (others build on it)."

    scenario_inspect
    scenario_guards
    scenario_agent_policy
    scenario_integration
    scenario_naming
    scenario_guided
}

wt_print_summary() {
    step "Summary"
    printf '  checks passed: %s%d%s\n' "$C_OK" "$PASS_COUNT" "$C_RESET"
    printf '  checks failed: %s%d%s\n' "$C_ERR" "$FAIL_COUNT" "$C_RESET"
    if [[ "${KEEP_FIXTURE:-false}" == true ]]; then
        printf '  fixture:       %s\n' "${RUN_ROOT:-$FIXTURE_REPO}"
        [[ -n "${WORKTREE_ROOT:-}" ]] && printf '  worktrees:     %s\n' "$WORKTREE_ROOT"
    fi
    explain "Notes above (yellow) are observations that need your eye, not automatic failures."
}
