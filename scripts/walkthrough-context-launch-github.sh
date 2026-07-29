#!/usr/bin/env bash
#
# walkthrough-context-launch-github.sh — approval decides whether an agent starts at all.
#
# The companion walkthrough (walkthrough-context-approval-github.sh) shows how Wrighty decides what
# content is approved. This one shows the consequence: the worker will not start an agent on an
# item whose context is not approved, and will start one the moment it is.
#
# That is the whole point of the feature, and it is two worker runs apart.
#
# No real agent runs and nothing is billed. A fake vendor on PATH stands in for one, and records
# whether it was executed — which is the actual assertion: not "the worker reported a refusal" but
# "no agent process came into existence".
#
# LIVE: creates a Project, fields, and issues on the dedicated private <owner>/<repo>-test
# repository, and clones it to a temporary directory for the worker's workspace. It never touches
# the product repository. Set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge that.
#
# Issues created this run are deleted on exit unless the run fails or --keep-fixture is given.
#
# It is not cheap against the GraphQL point budget: `wrighty init` reads and provisions the whole
# Project schema, and each worker run reads the conversation twice. A few runs in close succession
# can exhaust the hourly allowance, at which point provisioning fails outright rather than quietly
# reading empty — check `gh api rate_limit --jq .resources.graphql` before
# assuming a failure means something else.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# shellcheck source=scripts/walkthrough-lib.sh
source "$SCRIPT_DIR/walkthrough-lib.sh"
# shellcheck source=scripts/ensure-github-test-repo.sh
source "$SCRIPT_DIR/ensure-github-test-repo.sh"
# shellcheck source=scripts/github-project-lib.sh
source "$SCRIPT_DIR/github-project-lib.sh"

BUILD_CONFIGURATION="Debug"
SKIP_BUILD=false
KEEP_FIXTURE=false
AUTO=false
SOURCE_REPO=""
PROJECT_TITLE="Wrighty context launch walkthrough"
CONTEXT_FIELD="Wrighty policy - context approval"
EXECUTION_FIELD="Wrighty policy - execution"

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-context-launch-github.sh [options]" \
        "" \
        "Shows that an unapproved context stops a worker launch and an approved one permits it," \
        "against the dedicated private <owner>/<repo>-test repository. A fake vendor stands in for" \
        "a real agent, so nothing is billed. Requires WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1." \
        "" \
        "Options:" \
        "  --configuration NAME      Build configuration; defaults to Debug." \
        "  --skip-build              Use the existing local build output." \
        "  --source-repo OWNER/REPO  Source to derive the -test repo from." \
        "  --keep-fixture            Keep the issue this run created." \
        "  --auto                    Set the approval field itself instead of asking you to." \
        "  -h, --help                Show this help."
    return
}

while (($# > 0)); do
    case "$1" in
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --source-repo) (($# >= 2)) || die "--source-repo requires OWNER/REPO"; SOURCE_REPO=$2; shift 2 ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        --auto) AUTO=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

require_command dotnet
require_command gh
require_command jq
require_command git
gh auth status >/dev/null 2>&1 || die "gh is not authenticated; run 'gh auth login'"

[[ "${WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE:-}" == "1" ]] ||
    die "set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge this creates real GitHub resources on <owner>/<repo>-test"

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
if [[ "$SKIP_BUILD" == false ]]; then
    step "Building the local Wrighty CLI"
    dotnet build "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION" --nologo || die "build failed"
fi
[[ -f "$CLI_DLL" ]] || die "local CLI output '$CLI_DLL' was not found"

TEST_REPO=$(ensure_github_test_repo "${SOURCE_REPO:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}") ||
    die "could not resolve the private test repository"
OWNER=${TEST_REPO%%/*}

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-context-launch.XXXXXX")
ISSUE_LEDGER="$RUN_ROOT/created-issues"
CLONE="$RUN_ROOT/repo"
FAKE_BIN="$RUN_ROOT/fake-bin"
CACHE="$RUN_ROOT/cache"
SPAWN_MARKER="$RUN_ROOT/vendor-spawned"
CONFIG_PATH="$RUN_ROOT/.wrighty.json"
: >"$ISSUE_LEDGER"
mkdir -p "$FAKE_BIN" "$CACHE"
RUN_COMPLETED=false

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$KEEP_FIXTURE" == true || "$RUN_COMPLETED" == false ]]; then
        if [[ -s "$ISSUE_LEDGER" ]]; then
            note "keeping $(wc -l <"$ISSUE_LEDGER" | tr -d ' ') issue(s) on $TEST_REPO for inspection"
            sed 's|^|  https://github.com/'"$TEST_REPO"'/issues/|' "$ISSUE_LEDGER"
        fi
        note "keeping the fixture: $RUN_ROOT"
    else
        gpl_delete_ledger_issues
        rm -rf "$RUN_ROOT"
    fi
    exit "$status"
}
trap cleanup EXIT

# The fake vendor proves one thing per run: whether a process was created at all. That is a
# stronger assertion than reading the worker's own report, which could claim a refusal while still
# having spawned something.
cat >"$FAKE_BIN/claude" <<'FAKE_CLAUDE'
#!/usr/bin/env bash
set -euo pipefail
session_id=""
while (($# > 0)); do
    case "$1" in
        --session-id|--resume) session_id=${2:-}; shift 2 ;;
        *) shift ;;
    esac
done
[[ -n "$session_id" ]] || session_id="fake-claude-session"
printf '%s\n' "$PWD" >"${WRIGHTY_TEST_SPAWN_MARKER:?}"
printf '{"type":"result","subtype":"success","is_error":false,"session_id":"%s","result":"Fake Claude needs operator attention."}\n' \
    "$session_id"
FAKE_CLAUDE
chmod +x "$FAKE_BIN/claude"

wrighty() {
    env PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CONFIG_PATH="$CONFIG_PATH" \
        WRIGHTY_CACHE_DIR="$CACHE" \
        WRIGHTY_TEST_SPAWN_MARKER="$SPAWN_MARKER" \
        dotnet "$CLI_DLL" "$@"
    return
}

# Runs one worker attempt against the item and reports what happened, without asserting: the
# scenarios decide what the outcome should be.
run_worker() {
    # No intent means the worker resolves one: fresh when the item is unclaimed, otherwise a resume
    # or recovery of the recorded session.
    local events="$RUN_ROOT/worker.jsonl"
    rm -f "$SPAWN_MARKER"
    (cd "$CLONE" && wrighty worker --json --yes --once --item "$ISSUE_ID" "$@" \
        --agent claude --workspace-mode current >"$events" 2>"$events.stderr")
    jq -r '"    " + .type + " | " + ((.message // "") | .[0:160])' <"$events" 2>/dev/null |
        grep -vE "^\s+(running|renewed|waiting) " || sed 's/^/    /' "$events"
    return
}

vendor_started() { [[ -f "$SPAWN_MARKER" ]]; }

set_context_approval() {
    gpl_set_single_select "$ISSUE_NUMBER" "$CONTEXT_FIELD" "$1"
    return
}

# ---------------------------------------------------------------------------------------------
# Walkthrough
# ---------------------------------------------------------------------------------------------

printf '\n%sApproval decides whether an agent starts%s\n' "$C_BOLD" "$C_RESET"
explain "The companion walkthrough shows how content becomes approved. This one shows why that"
explain "matters: an unapproved item does not get an agent, and an approved one does."
explain "Repository: $TEST_REPO"
explain "A fake vendor stands in for a real agent — nothing here is billed."
if [[ "$AUTO" == false ]]; then
    begin_walkthrough
else
    explain "auto mode: setting the approval field itself, with no pauses"
fi

step "Provisioning"
# Before anything is created, so an exhausted budget stops the run rather than littering it.
gpl_require_budget
gpl_ensure_project
pass "Project #$PROJECT_NUMBER"

git clone --quiet "https://github.com/$TEST_REPO.git" "$CLONE" 2>/dev/null ||
    die "could not clone $TEST_REPO"
cat >"$CONFIG_PATH" <<CONFIG
{
  "backend": "github",
  "github": {
    "repository": "$TEST_REPO",
    "projectOwner": "$OWNER",
    "projectNumber": $PROJECT_NUMBER,
    "gitHubHost": "github.com"
  }
}
CONFIG

# wrighty init owns the Wrighty field schema. Provisioning it here by hand would be a second
# implementation that drifts from the real one silently.
(cd "$CLONE" && wrighty init --yes --json >/dev/null) ||
    die "could not initialise the Project schema; run wrighty init against $TEST_REPO by hand"
gpl_ensure_single_select "$CONTEXT_FIELD" "Needs review,Approved"
pass "Project schema initialised, with the context-approval field"

ISSUE_NUMBER=$(gpl_create_issue \
    "Walkthrough: approval gates the launch" \
    "Add a short note to the repository README describing the retry behaviour.")
ISSUE_ID="github:$TEST_REPO#$ISSUE_NUMBER"
ISSUE_URL="https://github.com/$TEST_REPO/issues/$ISSUE_NUMBER"

# Worker execution authorises SCHEDULING; context approval approves CONTENT. Both are required,
# and this walkthrough is about the second — so the first is set here, once, and left alone.
gpl_set_single_select "$ISSUE_NUMBER" "$EXECUTION_FIELD" "Automatic allowed"
sleep 2
pass "created $ISSUE_URL, authorised for unattended execution"

explain "You can inspect it yourself at any pause, from another terminal:"
printf '\n  WRIGHTY_CONFIG_PATH=%s \\\n    dotnet %s context %s\n\n' \
    "$CONFIG_PATH" "$CLI_DLL" "$ISSUE_ID"

step "1. Unapproved content: the worker refuses to start an agent"
explain "The item is authorised to run unattended — Worker execution is 'Automatic allowed' — but"
explain "its content has not been approved. Scheduling authority is not content approval."
run_worker --fresh
if vendor_started; then
    fail "an agent process was started for an item whose context was not approved"
else
    pass "no agent process came into existence"
fi

step "2. Approve the content"
if [[ "$AUTO" == true ]]; then
    explain "auto: setting '$CONTEXT_FIELD' to 'Approved'"
    set_context_approval "Approved"
    sleep 3
else
    manual \
        "Open the Project: https://github.com/users/$OWNER/projects/$PROJECT_NUMBER" \
        "" \
        "Set '$CONTEXT_FIELD' to 'Approved' for issue #$ISSUE_NUMBER." \
        "Leave '$EXECUTION_FIELD' as it is — it is already 'Automatic allowed'."
    pause
fi

step "3. Approved content: the agent starts"
explain "Nothing else changed. The same command, the same item, the same execution policy."
run_worker --fresh
if vendor_started; then
    pass "the agent process ran"
else
    fail "the agent did not start even though the context was approved"
fi

step "4. The launch recorded what it supplied"
explain "The agent asked for attention rather than finishing, so the session is resumable. For that"
explain "to be possible later, this run had to record what it was given — as hashes, never content."
CONTEXT_RECORD=$(jq -r --arg id "$ISSUE_ID" \
    '.entries[$id].context // empty | "digest=\(.manifest.digest) approval=\(.approvalSource) entries=\(.manifest.included | length)"' \
    "$CACHE/work-item-runtime-v1.json" 2>/dev/null)
if [[ -n "$CONTEXT_RECORD" ]]; then
    pass "recorded with the session: $CONTEXT_RECORD"
else
    fail "the launch started an agent but recorded no context, so this session could never resume"
fi

step "5. The paused run added a comment of its own, and it needs approving too"
explain "Wrighty posted a handover comment when the agent paused. Until the authorization work"
explain "lands, Wrighty's own comments are not recognised as protocol, so that comment reads as"
explain "undecided discussion and blocks the resume — try it and you will see CONTEXT_COMMENT_PENDING."
explain "Re-approving moves the batch cutoff past it, which is the operator's move either way after"
explain "reviewing what the agent said."
if [[ "$AUTO" == true ]]; then
    explain "auto: setting the field to 'Needs review' and back to 'Approved'"
    set_context_approval "Needs review"
    sleep 2
    set_context_approval "Approved"
    sleep 3
else
    manual \
        "Read the handover comment on $ISSUE_URL." \
        "" \
        "Then set '$CONTEXT_FIELD' to 'Needs review' and back to 'Approved'." \
        "Both steps are needed: selecting the value the field already holds renews nothing," \
        "so the approval instant — and with it the cutoff — would not move."
    pause
fi
pass "the approval cutoff now covers everything on the issue"

step "6. Resuming uses the recorded context as its baseline"
explain "A resume never re-runs the post-claim check — the item is already claimed — so it compares"
explain "the current approved context against the record from step 4, and admits only an unchanged"
explain "or purely additive one."
run_worker
if vendor_started; then
    pass "the session resumed against an unchanged context"
else
    # The error object is preceded by human-readable warnings, so the JSON starts partway in.
    fail "the resume did not start the agent: $(sed -n '/^{/,$p' "$RUN_ROOT/worker.jsonl.stderr" |
        jq -r '.error.code // "no error reported"' 2>/dev/null || echo unknown)"
fi

step "What this proves"
explain "Approval is not advisory. The worker read the approved context, confirmed it after"
explain "claiming and again immediately before spawning, and only then started the vendor."
note "Not shown: a context that changes between those last two reads, or one edited between runs"
note "so the resume is refused. Staging either reliably is beyond a script — the unit tests in"
note "ExecutionContextLaunchCheckTests cover both directly."

printf '\n%s%d passed, %d failed%s\n' "$C_BOLD" "$PASS_COUNT" "$FAIL_COUNT" "$C_RESET"
((FAIL_COUNT == 0)) && RUN_COMPLETED=true
((FAIL_COUNT == 0)) || exit 1
exit 0
