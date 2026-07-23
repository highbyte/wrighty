#!/usr/bin/env bash
#
# Interactive walkthrough for the worker workspace-lifecycle and completion features
# (branch/worktree recording, commit policy, integration guidance, `wrighty workspaces`,
# the guided-completion skill flow, and the plan-023 run-outcome surfaces — the captured last-run
# outcome, the `completed` worker activity, the `wrighty status` grouping, and the list worktree
# flag) against the LOCAL MARKDOWN backend.
#
# This script does NOT spawn a vendor agent. It provisions a disposable Local Markdown
# repository, then guides you step by step. The live-agent work (running `wrighty worker`
# and driving the guided-completion session) is done by YOU in a SECOND terminal; this
# script sets up each scenario, pauses, and then verifies the observable result.
#
# Nothing outside a temporary directory is touched. No network or GitHub resource is used.
# The backend-neutral scenario logic lives in scripts/walkthrough-lib.sh; the GitHub-backend
# counterpart is scripts/walkthrough-worker-completion-github.sh.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# shellcheck source=scripts/walkthrough-lib.sh
source "$SCRIPT_DIR/walkthrough-lib.sh"

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

while (($# > 0)); do
    case "$1" in
        --agent) (($# >= 2)) || die "--agent requires a value"; ASSUME_AGENT=$2; shift 2 ;;
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

require_command dotnet
require_command git
require_command jq

wt_resolve_agent "$ASSUME_AGENT"

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
ACTIVATE_SCRIPT="$REPO_ROOT/scripts/activate-development-cli.sh"

wt_build_cli "$CLI_PROJECT" "$CLI_DLL" "$SKIP_BUILD" "$BUILD_CONFIGURATION"

# ----- fixture provisioning (local-markdown) --------------------------------

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

write_config() {
    # write_config <commit-policy> <integration> <branchFormat>
    local commit=$1 integration=$2 branch_format=$3
    cat >"$FIXTURE_REPO/.wrighty.json" <<JSON
{
  "backend": "local-markdown",
  "localMarkdown": {
    "path": ".wrighty",
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

wt_install_and_commit_skill

# Create the store and a bootstrap config first (bootstrap-only flags need no pre-existing
# config), then overwrite the config with our worker/completion template.
wr init --backend local-markdown --local-path .wrighty \
    --status Todo --status "In Progress" --status Done \
    --priority P0 --priority P1 --priority P2 --yes >/dev/null 2>&1 ||
    die "wrighty init failed"
write_config "inspect" "merge-local" "wrighty-worker/{id}-{title}"

# A local bare remote lets scenario B2 (push-pr) actually push. Harmless if B is skipped.
git init -q --bare "$RUN_ROOT/origin.git"
(cd "$FIXTURE_REPO" && git remote add origin "$RUN_ROOT/origin.git" && git push -q -u origin main) ||
    note "Could not set up the local origin remote; scenario B2 (push-pr) will be limited"

wt_seed_items
wt_bootstrap
wt_run_scenarios
wt_print_summary

if ((FAIL_COUNT > 0)); then exit 1; fi
