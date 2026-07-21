#!/usr/bin/env bash

# Interactive walkthrough for the worker workspace-lifecycle and completion features
# (branch/worktree recording, commit policy, integration guidance, `wrighty workspaces`,
# and the guided-completion skill flow).
#
# This script does NOT spawn a vendor agent. It provisions a disposable Local Markdown
# repository, then guides you step by step. The live-agent work (running `wrighty worker`
# and driving the guided-completion session) is done by YOU in a SECOND terminal; this
# script sets up each scenario, pauses, and then verifies the observable result.
#
# Nothing outside a temporary directory is touched. No network or GitHub resource is used.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

BUILD_CONFIGURATION="Debug"
SKIP_BUILD=false
KEEP_FIXTURE=false
ASSUME_AGENT=""

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-worker-completion.sh [options]" \
        "" \
        "Guided, semi-automated manual test of the worker completion lifecycle." \
        "Provisions a disposable Local Markdown repo and walks you through scenarios;" \
        "you run the actual 'wrighty worker' commands in a second terminal." \
        "" \
        "Options:" \
        "  --agent NAME            Vendor you will drive: claude, codex, or copilot." \
        "                          Prompted interactively when omitted." \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-fixture          Do not delete the temporary repo on exit." \
        "  -h, --help              Show this help."
}

# ----- output helpers -------------------------------------------------------

if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
    C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'; C_OK=$'\033[32m'; C_WARN=$'\033[33m'
    C_ERR=$'\033[31m'; C_CYAN=$'\033[36m'; C_RESET=$'\033[0m'
else
    C_BOLD=""; C_DIM=""; C_OK=""; C_WARN=""; C_ERR=""; C_CYAN=""; C_RESET=""
fi

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

pause() { printf '\n%s[press Enter when done]%s ' "$C_BOLD" "$C_RESET"; read -r _; }

confirm() {
    local answer
    printf '\n%s%s%s [y/N] ' "$C_BOLD" "$1" "$C_RESET"
    read -r answer
    [[ "$answer" == [yY] || "$answer" == [yY][eE][sS] ]]
}

PASS_COUNT=0
FAIL_COUNT=0

# ----- argument parsing -----------------------------------------------------

while (($# > 0)); do
    case "$1" in
        --agent) (($# >= 2)) || die "--agent requires a value"; ASSUME_AGENT=$2; shift 2 ;;
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

require_command dotnet
require_command git
require_command jq

# Choose the vendor agent to drive: the --agent flag wins, otherwise prompt.
if [[ -z "$ASSUME_AGENT" ]]; then
    printf '\n%sWhich agent will you drive?%s [claude/codex/copilot] (default claude): ' \
        "$C_BOLD" "$C_RESET"
    read -r ASSUME_AGENT
    [[ -z "$ASSUME_AGENT" ]] && ASSUME_AGENT="claude"
fi
case "$ASSUME_AGENT" in
    claude|codex|copilot) ;;
    *) die "unsupported agent '$ASSUME_AGENT' (use claude, codex, or copilot)" ;;
esac

# Codex invokes the skill with $wrighty; claude and copilot use /wrighty.
skill_prefix() {
    [[ "$ASSUME_AGENT" == "codex" ]] && printf '$wrighty' || printf '/wrighty'
}

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
ACTIVATE_SCRIPT="$REPO_ROOT/scripts/activate-development-cli.sh"

if [[ "$SKIP_BUILD" == false ]]; then
    step "Building the local Wrighty CLI"
    dotnet build "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION" --nologo || die "build failed"
fi
[[ -f "$CLI_DLL" ]] || die "local CLI output '$CLI_DLL' was not found (run without --skip-build)"

# ----- fixture provisioning -------------------------------------------------

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-completion-walkthrough.XXXXXX")
FIXTURE_REPO="$RUN_ROOT/repo"
WORKTREE_ROOT="$RUN_ROOT/worktrees"
mkdir -p "$FIXTURE_REPO"

cleanup() {
    local status=$?
    if [[ "$KEEP_FIXTURE" == true ]]; then
        printf '\nfixture kept at %s\n' "$RUN_ROOT"
    elif [[ "$RUN_ROOT" == *wrighty-completion-walkthrough.* ]]; then
        rm -rf "$RUN_ROOT"
    fi
    return $status
}
trap cleanup EXIT

# Run the dev CLI against the fixture repo.
wr() { (cd "$FIXTURE_REPO" && dotnet "$CLI_DLL" "$@"); }

write_config() {
    # write_config <commit-policy> <integration> <branchFormat>
    local commit=$1 integration=$2 branch_format=$3
    cat >"$FIXTURE_REPO/.wrighty.json" <<JSON
{
  "backend": "local-markdown",
  "localMarkdown": {
    "root": ".wrighty",
    "statuses": ["Todo", "In Progress", "Done"],
    "priorities": ["P0", "P1", "P2"]
  },
  "archive": { "onStatuses": [] },
  "worker": {
    "worktreeRoot": "$WORKTREE_ROOT",
    "branchFormat": "$branch_format",
    "completion": { "commit": "$commit", "integration": "$integration" }
  }
}
JSON
}

step "Provisioning a disposable Local Markdown repository"
explain "Location: $FIXTURE_REPO"
explain "Worktrees will be created under: $WORKTREE_ROOT"

(
    cd "$FIXTURE_REPO"
    git init -q -b main
    git config user.name "Wrighty walkthrough"
    git config user.email "walkthrough@example.invalid"
    printf '# Walkthrough fixture\n\nSafe throwaway repository for manual worker testing.\n' >README.md
    git add README.md
    git commit -q -m "Initialize walkthrough fixture"
) || die "failed to initialize fixture git repository"

# Install the real Wrighty skill for the chosen vendor using the CLI's own installer
# (claude -> .claude/skills, codex/copilot -> .agents/skills). Commit it so the skill is
# present when the worker checks out a fresh worktree.
if wr skill install --agent "$ASSUME_AGENT" --scope project --force >/dev/null 2>&1; then
    (cd "$FIXTURE_REPO" && git add -A >/dev/null 2>&1 && \
        git commit -q -m "Install Wrighty skill for $ASSUME_AGENT" >/dev/null 2>&1) || true
    explain "Installed the Wrighty skill for $ASSUME_AGENT"
else
    note "Could not install the Wrighty skill for $ASSUME_AGENT; the guided-completion scenario needs it present"
fi

# Create the store and a bootstrap config first (bootstrap-only flags need no pre-existing
# config), then overwrite the config with our worker/completion template.
wr init --backend local-markdown --local-path .wrighty \
    --status Todo --status "In Progress" --status Done \
    --priority P0 --priority P1 --priority P2 --yes >/dev/null 2>&1 \
    || die "wrighty init failed"
write_config "inspect" "merge-local" "wrighty-worker/{id}-{unique}"

create_item() {
    # create_item <title> <body>  -> echoes the new id
    # --auto + --agent make the item eligible for a fresh worker run
    # (worker requires wrighty-auto=true and a resolvable vendor).
    local out
    out=$(wr create --title "$1" --body "$2" --auto --agent "$ASSUME_AGENT" --json 2>/dev/null) \
        || die "create failed for '$1'"
    printf '%s' "$out" | jq -er '.result.id'
}

ITEM_INSPECT=$(create_item "Add a greeting file" \
    "Create a file HELLO.md in the repo root containing a one-line greeting. Keep it tiny.")
ITEM_AGENT=$(create_item "Add a farewell file" \
    "Create a file BYE.md in the repo root containing a one-line farewell. Keep it tiny.")
ITEM_NAMING=$(create_item "Tweak the readme" \
    "Append a single line to README.md. Keep it tiny.")
ITEM_NOWORKTREE=$(create_item "Never run in worktree" \
    "This item exists only to demonstrate a guard; do not process it.")

pass "created items: $ITEM_INSPECT, $ITEM_AGENT, $ITEM_NAMING, $ITEM_NOWORKTREE"

# ----- second-terminal bootstrap -------------------------------------------

step "Set up your SECOND terminal now"
explain "Everything you type in that terminal targets the disposable fixture only."
manual \
    "cd '$FIXTURE_REPO'" \
    "source '$ACTIVATE_SCRIPT'" \
    "wrighty list" \
    "" \
    "The source line makes 'wrighty' the dev build; wrighty list should show the seeded items." \
    "Your agent CLI ('$ASSUME_AGENT') must be installed and authenticated."
pause

# ----- verification helpers -------------------------------------------------

item_status() { wr get "$1" --json 2>/dev/null | jq -r '.result.status // empty'; }
# Branch and workspace path are surfaced by 'wrighty workspaces', not 'get'. They only
# return a value while the worktree still exists on disk under worktreeRoot (a removed
# worktree drops out of the listing).
ws_field() {
    # ws_field <item-id> <field>
    wr workspaces --json 2>/dev/null \
        | jq -r --arg id "$1" --arg f "$2" \
            '.result.workspaces[]? | select(.itemId == $id) | .[$f] // empty' \
        | head -n1
}
item_branch() { ws_field "$1" branch; }
item_workspace() { ws_field "$1" path; }

# Capture the error code from a failing wr command (best effort: JSON first, then text).
cleanup_error_code() {
    # cleanup_error_code <item-id>
    local out
    out=$(wr workspaces cleanup "$1" --json 2>&1)
    local code
    code=$(printf '%s' "$out" | jq -r '.error.code // empty' 2>/dev/null)
    if [[ -z "$code" ]]; then
        code=$(printf '%s' "$out" | grep -oE 'WORKSPACE_[A-Z_]+|CLAIM_HELD' | head -n1)
    fi
    printf '%s' "$code"
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
        if git -C "$FIXTURE_REPO" for-each-ref --format='%(refname:short)' refs/heads/ \
            | grep -q 'wrighty-worker'; then
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
    if [[ -z "$(item_workspace "$ITEM_INSPECT")" ]]; then
        note "E1 works best right after A1 (needs a retained worktree with review-ready changes). Run A1 first."
    fi
    step "E1: drive the guided completion inside the recorded session"
    explain "Open the recorded vendor session and let the skill walk you through completion with approval at each step."
    manual \
        "wrighty resume-command $ITEM_INSPECT" \
        "" \
        "That prints a command; paste and run it to open the $ASSUME_AGENT session in the worktree." \
        "Then, inside that session, enter:" \
        "" \
        "$(skill_prefix) Complete item $ITEM_INSPECT: summarize the diff, propose a commit message, and after my approval commit, integrate, clean up the workspace, and archive the item." \
        "" \
        "Approve each step. When the session finishes, come back here."
    pause

    local status ws
    status=$(item_status "$ITEM_INSPECT")
    ws=$(item_workspace "$ITEM_INSPECT")
    if wr get "$ITEM_INSPECT" --json 2>/dev/null | jq -e '.result.archived == true' >/dev/null 2>&1; then
        pass "E1 item is archived"
    elif [[ "$status" == "Done" ]]; then
        pass "E1 item reached Done (archive optional per your archive.onStatuses)"
    else
        note "E1 item status is '$status' — confirm the guided flow completed the archive step."
    fi
    if [[ -n "$ws" && -d "$ws" ]]; then
        note "worktree still present at $ws — confirm the cleanup step ran (or run 'wrighty workspaces cleanup $ITEM_INSPECT')."
    else
        pass "E1 worktree was cleaned up"
    fi
}

# ----- run the scenarios ----------------------------------------------------

step "Walkthrough scenarios"
explain "Answer y to run each scenario, or N to skip. A1 first is recommended (others build on it)."

scenario_inspect
scenario_guards
scenario_agent_policy
scenario_naming
scenario_guided

# ----- summary --------------------------------------------------------------

step "Summary"
printf '  checks passed: %s%d%s\n' "$C_OK" "$PASS_COUNT" "$C_RESET"
printf '  checks failed: %s%d%s\n' "$C_ERR" "$FAIL_COUNT" "$C_RESET"
if [[ "$KEEP_FIXTURE" == true ]]; then
    printf '  fixture:       %s\n' "$RUN_ROOT"
    printf '  worktrees:     %s\n' "$WORKTREE_ROOT"
fi
explain "Notes above (yellow) are observations that need your eye, not automatic failures."

if ((FAIL_COUNT > 0)); then exit 1; fi
