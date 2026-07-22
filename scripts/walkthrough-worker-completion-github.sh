#!/usr/bin/env bash
#
# Interactive walkthrough for the worker workspace-lifecycle and completion features against the
# GITHUB backend. It runs the exact same backend-neutral scenarios as the Local Markdown
# walkthrough (scripts/walkthrough-worker-completion.sh) — shared via scripts/walkthrough-lib.sh —
# but provisions its fixture on a dedicated, private, disposable GitHub repository derived as
# <owner>/<repo>-test (see scripts/ensure-github-test-repo.sh and plan 024).
#
# UNLIKE the local walkthrough, this one is LIVE: it creates real issues, project items, labels,
# and branches on the <owner>/<repo>-test repository, and you drive a real vendor agent in a second
# terminal. It never touches the product repository. Set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to
# acknowledge that.
#
# The clone and worktrees live under a temporary directory and are removed on exit; the issues
# created this run are deleted on exit (unless --keep-fixture). The test repository, its Project,
# and its labels are reused across runs and are NOT deleted here.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# shellcheck source=scripts/walkthrough-lib.sh
source "$SCRIPT_DIR/walkthrough-lib.sh"
# shellcheck source=scripts/ensure-github-test-repo.sh
source "$SCRIPT_DIR/ensure-github-test-repo.sh"

BUILD_CONFIGURATION="Debug"
SKIP_BUILD=false
KEEP_FIXTURE=false
ASSUME_AGENT=""
SOURCE_REPO=""
PROJECT_TITLE="Wrighty completion walkthrough"

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-worker-completion-github.sh [options]" \
        "" \
        "Guided, semi-automated manual test of the worker completion lifecycle against the" \
        "GitHub backend, on a dedicated private <owner>/<repo>-test repository. LIVE: creates" \
        "real issues/branches and drives a real agent. Set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1." \
        "" \
        "Options:" \
        "  --agent NAME            Vendor you will drive: claude, codex, or copilot." \
        "  --source-repo OWNER/REPO  Source to derive the -test repo from (default: current gh repo)." \
        "  --project-title TITLE   Project title to create/reuse (default: '$PROJECT_TITLE')." \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-fixture          Keep the temporary clone and the created issues on exit." \
        "  -h, --help              Show this help."
}

while (($# > 0)); do
    case "$1" in
        --agent) (($# >= 2)) || die "--agent requires a value"; ASSUME_AGENT=$2; shift 2 ;;
        --source-repo) (($# >= 2)) || die "--source-repo requires OWNER/REPO"; SOURCE_REPO=$2; shift 2 ;;
        --project-title) (($# >= 2)) || die "--project-title requires a title"; PROJECT_TITLE=$2; shift 2 ;;
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

[[ "${WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE:-}" == "1" ]] ||
    die "set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge this creates real GitHub issues/branches on <owner>/<repo>-test and drives a real agent"

require_command dotnet
require_command git
require_command jq
require_command gh
gh auth status >/dev/null 2>&1 || die "gh is not authenticated; run 'gh auth login'"

wt_resolve_agent "$ASSUME_AGENT"

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
ACTIVATE_SCRIPT="$REPO_ROOT/scripts/activate-development-cli.sh"

wt_build_cli "$CLI_PROJECT" "$CLI_DLL" "$SKIP_BUILD" "$BUILD_CONFIGURATION"

# ----- resolve + provision the private -test repository ---------------------

[[ -n "$SOURCE_REPO" ]] || SOURCE_REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
    die "could not determine the current gh repository; pass --source-repo OWNER/REPO"
step "Ensuring the private integration-test repository exists"
TEST_REPO=$(ensure_github_test_repo "$SOURCE_REPO") || die "could not ensure the <owner>/<repo>-test repository"
explain "Test repository: $TEST_REPO"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-completion-walkthrough-gh.XXXXXX")
FIXTURE_REPO="$RUN_ROOT/repo"
WORKTREE_ROOT="$RUN_ROOT/worktrees"
CREATED_ITEMS=()

# GitHub-flavored second-terminal / push notes for the shared scenarios.
WT_BOOTSTRAP_NOTE="Everything you type targets the disposable $TEST_REPO repository (issues, project items, branches)."
WT_PUSH_REMOTE_NOTE="origin is $TEST_REPO, so the push creates a real branch there; you can open a real pull request on GitHub."

cleanup() {
    local status=$?
    if [[ "$KEEP_FIXTURE" == true ]]; then
        printf '\nfixture clone kept at %s\n' "$RUN_ROOT"
        printf 'created issues left in place on %s: %s\n' "$TEST_REPO" "${CREATED_ITEMS[*]:-none}"
        return $status
    fi
    # Scoped teardown: delete only the issues this run created; keep the repo, Project, and labels.
    local item number
    for item in "${CREATED_ITEMS[@]:-}"; do
        [[ -n "$item" ]] || continue
        number=${item##*\#}
        [[ "$number" =~ ^[0-9]+$ ]] || continue
        gh issue delete "$number" --repo "$TEST_REPO" --yes >/dev/null 2>&1 || true
    done
    [[ "$RUN_ROOT" == *wrighty-completion-walkthrough-gh.* ]] && rm -rf "$RUN_ROOT"
    return $status
}
trap cleanup EXIT

step "Cloning $TEST_REPO"
gh repo clone "$TEST_REPO" "$FIXTURE_REPO" -- -q || die "failed to clone $TEST_REPO"
(
    cd "$FIXTURE_REPO"
    git config user.name "Wrighty walkthrough"
    git config user.email "walkthrough@example.invalid"
    # A freshly created repository has no commits; give it a main branch so worktrees can branch off.
    if ! git rev-parse --verify HEAD >/dev/null 2>&1; then
        git switch -c main 2>/dev/null || git checkout -b main
        printf '# Walkthrough fixture\n\nSafe throwaway integration-test repository.\n' >README.md
        git add README.md
        git commit -q -m "Initialize walkthrough fixture"
        git push -q -u origin main
    fi
) || die "failed to prepare the fixture clone"

# Provision the GitHub tracker: worker labels, a linked Project, and the base config. Issue forms
# are skipped so nothing is committed to the repository tree as a side effect of testing.
step "Initializing the GitHub tracker on $TEST_REPO"
wr init --backend github --repository "$TEST_REPO" \
    --project-title "$PROJECT_TITLE" --skip-issue-forms --yes >/dev/null 2>&1 ||
    die "wrighty init (github) failed for $TEST_REPO"

wt_install_and_commit_skill
git -C "$FIXTURE_REPO" push -q origin HEAD 2>/dev/null || note "could not push the skill commit to origin (worktree preflight only needs it at local HEAD)"

# GitHub write_config: patch only the worker block, preserving the github block that init wrote
# (repository/projectOwner/projectNumber and any other fields), so we never guess its shape.
write_config() {
    # write_config <commit-policy> <integration> <branchFormat>
    local commit=$1 integration=$2 branch_format=$3
    local cfg="$FIXTURE_REPO/.wrighty.json" tmp
    tmp=$(mktemp) || die "mktemp failed"
    jq --arg wr "$WORKTREE_ROOT" --arg bf "$branch_format" --arg c "$commit" --arg i "$integration" \
        '.worker = {worktreeRoot: $wr, branchFormat: $bf, completion: {commit: $c, integration: $i}}
         | .archive = (.archive // {onStatuses: []})' \
        "$cfg" >"$tmp" 2>/dev/null && mv "$tmp" "$cfg" ||
        { rm -f "$tmp"; die "failed to patch the worker config into $cfg"; }
}
write_config "inspect" "merge-local" "wrighty-worker/{id}-{title}"

# Delete any pre-existing issues whose title matches a walkthrough item, so re-running on an existing
# -test repository is idempotent and never accumulates duplicates — including orphans left by an
# interrupted or partially-failed earlier seed. Scoped to the exact walkthrough titles; any other
# issues on the repository are untouched.
github_reset_walkthrough_items() {
    local titles=(
        "Add a greeting file" "Add a farewell file" "Tweak the readme"
        "Never run in worktree" "Add a merge file" "Add a push file" "Guide a completion file"
    )
    local title numbers number removed=0
    for title in "${titles[@]}"; do
        numbers=$(gh issue list --repo "$TEST_REPO" --state all --search "\"$title\" in:title" \
            --json number,title --jq ".[] | select(.title == \"$title\") | .number" 2>/dev/null) || continue
        for number in $numbers; do
            [[ "$number" =~ ^[0-9]+$ ]] || continue
            gh issue delete "$number" --repo "$TEST_REPO" --yes >/dev/null 2>&1 && removed=$((removed + 1)) || true
        done
    done
    ((removed > 0)) && explain "removed $removed pre-existing walkthrough issue(s) from $TEST_REPO"
    return 0
}

step "Provisioning work items as GitHub issues on $TEST_REPO"
github_reset_walkthrough_items
wt_seed_items
CREATED_ITEMS=("$ITEM_INSPECT" "$ITEM_AGENT" "$ITEM_NAMING" "$ITEM_NOWORKTREE" "$ITEM_MERGE" "$ITEM_PUSH" "$ITEM_GUIDED")

wt_bootstrap
wt_run_scenarios
wt_print_summary

if ((FAIL_COUNT > 0)); then exit 1; fi
