#!/usr/bin/env bash
#
# test-launch-preflight.sh — isolated Local Markdown smoke test for the worker launch preflight.
#
# The launch preflight is the single internal boundary every vendor spawn passes through
# (docs/reference/worker.md, "Launch preflight"). Its whole purpose is that a refused launch never
# reaches an agent, so — unlike the walkthrough scripts — this one needs no live vendor and no
# second terminal. It drives the locally built CLI against a temporary store with a fake `claude`
# on PATH, and asserts what an operator would actually see.
#
# What it covers:
#   1. An admitted launch still reaches the vendor.
#   2. A post-claim refusal releases the claim, restores the source status, never starts the agent,
#      and never creates a workspace.
#
# What it deliberately does NOT cover, and why:
#   * The pre-spawn stage. It is wired and enforced, but no BUILT-IN check registers there yet and
#     the CLI passes no additional checks (src/Highbyte.Wrighty.Cli/Program.cs), so from the command
#     line that stage always admits. Plan 030 phase 4 registers the approved-context check into it;
#     until then it is covered by LaunchPreflightWorkerTests, which asserts that a registered
#     pre-spawn check blocks the spawn and releases cleanly. Adding a fault-injection hook to
#     production code purely to make this script reach it would be the wrong trade.
#   * A policy edit landing inside the claim-to-revalidation window. That is a genuine race with no
#     CLI seam to interpose on; the unit tests trigger it with a backend wrapper instead.
#
# Use --narrate to see each step explained and every worker event printed before it is checked.

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

BUILD_CONFIGURATION="Debug"
SKIP_BUILD=false
KEEP_STORE=false
NARRATE=false

usage() {
    printf '%s\n' \
        "Usage: scripts/test-launch-preflight.sh [options]" \
        "" \
        "Run an isolated Local Markdown smoke test for the worker launch preflight through the" \
        "locally built Wrighty CLI. Creates a temporary git repository, store, and fake vendor;" \
        "removes them on exit. No real agent runs and nothing is billed." \
        "" \
        "Options:" \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-store            Preserve the temporary fixture for inspection." \
        "  --narrate               Explain each step and print the worker events it checks." \
        "  -h, --help              Show this help."
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

require_command() {
    local name=$1
    command -v "$name" >/dev/null 2>&1 || die "required command '$name' was not found"
    return
}

step() { printf '\n==> %s\n' "$*"; }
pass() { printf 'ok: %s\n' "$*"; }

# Narration is the point of --narrate: say what is about to happen and why it matters, so the
# assertions read as an explanation of the feature rather than a wall of greps.
explain() {
    [[ "$NARRATE" == true ]] || return 0
    printf '    %s\n' "$*"
}

show_events() {
    local events_file=$1
    [[ "$NARRATE" == true ]] || return 0
    printf '    --- worker events ---\n'
    jq -r '"    " + .type + " | " + (.itemId // "-") + " | " + ((.message // "") | .[0:150])' \
        <"$events_file" 2>/dev/null || sed 's/^/    /' "$events_file"
    printf '    ---------------------\n'
    return
}

while (($# > 0)); do
    case "$1" in
        --configuration)
            (($# >= 2)) || die "--configuration requires a value"
            BUILD_CONFIGURATION=$2
            shift 2
            ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --keep-store) KEEP_STORE=true; shift ;;
        --narrate) NARRATE=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

require_command dotnet
require_command jq
require_command git

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
if [[ "$SKIP_BUILD" == false ]]; then
    step "Building the local Wrighty CLI"
    dotnet build "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION" --nologo
fi
[[ -f "$CLI_DLL" ]] || die "local CLI output '$CLI_DLL' was not found"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-launch-preflight.XXXXXX")
REPO="$RUN_ROOT/repo"
CACHE="$RUN_ROOT/cache"
FAKE_BIN="$RUN_ROOT/fake-bin"
EVENTS="$RUN_ROOT/events"
SPAWN_MARKER="$RUN_ROOT/vendor-spawned"
mkdir -p "$REPO" "$CACHE" "$FAKE_BIN" "$EVENTS"

cleanup() {
    local original_status=$?
    trap - EXIT
    if [[ "$KEEP_STORE" == true ]]; then
        printf '\nKept temporary fixture: %s\n' "$RUN_ROOT"
        exit "$original_status"
    fi
    case "$RUN_ROOT" in
        "${TMPDIR:-/tmp}"/wrighty-launch-preflight.*) rm -rf "$RUN_ROOT" ;;
        *)
            printf 'warning: refusing to remove unexpected temporary path %s\n' "$RUN_ROOT" >&2
            exit 1
            ;;
    esac
    exit "$original_status"
}
trap cleanup EXIT

# The fake vendor exists to prove one thing per scenario: whether a process was spawned at all. It
# touches a marker and returns the JSON shape the Claude adapter parses.
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
        WRIGHTY_CACHE_DIR="$CACHE" \
        WRIGHTY_TEST_SPAWN_MARKER="$SPAWN_MARKER" \
        dotnet "$CLI_DLL" "$@"
}

# Runs the worker, capturing its JSON event stream to a file. A skipped launch is a success exit,
# so the status is asserted per scenario rather than here. --yes is required because live worker
# execution refuses to start unattended in JSON mode without it.
run_worker() {
    local events_file=$1
    shift
    set +e
    wrighty worker --json --yes "$@" >"$events_file" 2>"$events_file.stderr"
    WORKER_STATUS=$?
    set -e
    # The worker warns on stderr on every run, and exit 10 (needs-attention) is an ordinary
    # outcome — neither is a failure signal. Surface stderr only for a status no scenario expects,
    # so a real error is not buried under the event stream.
    if ((WORKER_STATUS != 0 && WORKER_STATUS != 10)) && [[ -s "$events_file.stderr" ]]; then
        printf 'worker exited %s; stderr:\n' "$WORKER_STATUS" >&2
        sed -n '1,20p' "$events_file.stderr" >&2
    fi
}

event_exists() {
    local type=$1 events_file=$2
    jq -e --arg type "$type" 'select(.type == $type)' <"$events_file" >/dev/null 2>&1
    return
}

event_message() {
    local type=$1 events_file=$2
    jq -r --arg type "$type" 'select(.type == $type) | .message // ""' <"$events_file" | head -1
    return
}

assert_message_contains() {
    local haystack=$1 needle=$2 description=$3
    [[ "$haystack" == *"$needle"* ]] ||
        die "$description: expected the message to mention '$needle', got: $haystack"
    return
}

item_field() {
    local id=$1 filter=$2
    wrighty get "$id" --json | jq -r "$filter"
}

assert_unclaimed() {
    local id=$1
    local state
    state=$(wrighty get "$id" --json | jq -r '.result.claim.state // "Unclaimed"')
    [[ "$state" == "Unclaimed" || "$state" == "null" ]] ||
        die "item $id should have been released, but its claim state is '$state'"
}

create_item() {
    local title=$1
    wrighty create --title "$title" --body "Launch preflight fixture." \
        --status Todo --priority P1 --auto --agent claude --json |
        jq -r '.result.id'
}

# ---------------------------------------------------------------------------------------------
# Fixture
# ---------------------------------------------------------------------------------------------

step "Preparing an isolated repository, store, and fake vendor"
cd "$REPO"
git init --quiet .
git config user.email "preflight@example.invalid"
git config user.name "Launch Preflight Test"
# Worktree mode refuses to launch unless the agent's skill is reachable, so commit a stub one.
mkdir -p .claude/skills/wrighty
printf '%s\n' "---" "name: wrighty" \
    "description: Test-only Wrighty worker skill." "---" \
    "Test-only skill." >.claude/skills/wrighty/SKILL.md
git add -f .claude/skills/wrighty/SKILL.md
git commit --quiet -m "Add a test-only Wrighty skill"

wrighty init --backend local-markdown --local-path .wrighty \
    --status Todo --status "In Progress" --status Done \
    --priority P0 --priority P1 --priority P2 \
    --yes --json >/dev/null
explain "The store, the fake vendor, and the Wrighty cache all live under $RUN_ROOT."
explain "No real agent binary is involved, so nothing here is billed."
pass "isolated fixture ready at $REPO"

# ---------------------------------------------------------------------------------------------
# Scenario 1 — an admitted launch still reaches the vendor
# ---------------------------------------------------------------------------------------------

step "Scenario 1: an admitted launch reaches the vendor"
explain "The preflight must not block ordinary work. This is the control case: nothing has changed"
explain "between selection and launch, so both stages admit and the vendor process starts."
ITEM_OK=$(create_item "Preflight admits this item")
rm -f "$SPAWN_MARKER"
run_worker "$EVENTS/admitted.jsonl" --once --item "$ITEM_OK" --fresh --agent claude \
    --workspace-mode current
show_events "$EVENTS/admitted.jsonl"

# 10 is the needs-attention exit code: the fake vendor exits successfully without calling finish,
# which is exactly what the worker should report. Anything else means the launch itself misbehaved.
((WORKER_STATUS == 10)) ||
    die "an admitted launch should exit 10 (needs-attention), got $WORKER_STATUS"
event_exists "started" "$EVENTS/admitted.jsonl" ||
    die "an admitted launch did not emit a 'started' event"
[[ -f "$SPAWN_MARKER" ]] ||
    die "an admitted launch did not spawn the vendor process"
! event_exists "skipped-policy" "$EVENTS/admitted.jsonl" ||
    die "an admitted launch unexpectedly emitted 'skipped-policy'"
explain "Both stages admitted, so the worker built the invocation and ran the vendor."
pass "the launch was admitted, the vendor ran, and no refusal was emitted"

# ---------------------------------------------------------------------------------------------
# Scenario 2 — a post-claim refusal never reaches the vendor
# ---------------------------------------------------------------------------------------------

step "Scenario 2: a post-claim refusal releases the claim before the vendor or workspace exists"
explain "Trigger: --filter status=Todo. The pre-claim scan sees the item in Todo and admits it, the"
explain "worker claims it and moves it to In Progress, and the post-claim stage then re-reads the"
explain "item and finds the operator filter no longer matches. That is a deterministic way to reach"
explain "the same code path a real mid-flight policy change takes — and it is worth knowing that a"
explain "status filter matching the source status will churn every item this way."
ITEM_REFUSED=$(create_item "Preflight refuses this item")
rm -f "$SPAWN_MARKER"
WORKTREE_ROOT="$RUN_ROOT/repo.worktrees"
run_worker "$EVENTS/refused.jsonl" --once --item "$ITEM_REFUSED" --fresh --agent claude \
    --workspace-mode worktree --filter status=Todo
show_events "$EVENTS/refused.jsonl"

((WORKER_STATUS == 0)) ||
    die "a refused launch should exit 0 (skipped is not a failure), got $WORKER_STATUS"

event_exists "skipped-policy" "$EVENTS/refused.jsonl" ||
    die "a post-claim refusal did not emit 'skipped-policy'"
REFUSAL_MESSAGE=$(event_message "skipped-policy" "$EVENTS/refused.jsonl")

# The message is the operator's only view of why the item was skipped, so it must name the stage,
# the check, and the stable code rather than a generic "policy changed".
assert_message_contains "$REFUSAL_MESSAGE" "post-claim" "refusal message names the stage"
assert_message_contains "$REFUSAL_MESSAGE" "worker-policy" "refusal message names the check"
assert_message_contains "$REFUSAL_MESSAGE" "LAUNCH_POLICY_CHANGED" "refusal message names the code"
explain "Refusal message: $REFUSAL_MESSAGE"

! event_exists "started" "$EVENTS/refused.jsonl" ||
    die "a refused launch emitted 'started'"
[[ ! -f "$SPAWN_MARKER" ]] ||
    die "a refused launch spawned the vendor process"
explain "The vendor marker is absent, so no agent process was ever created."

assert_equal_status() {
    local actual
    actual=$(item_field "$ITEM_REFUSED" '.result.status')
    [[ "$actual" == "Todo" ]] ||
        die "a refused fresh launch should restore the source status, got '$actual'"
}
assert_equal_status
assert_unclaimed "$ITEM_REFUSED"
explain "The item is back in Todo and unclaimed, so resolving the refusal is all an operator does."

# The refusal happens before PrepareAsync, so worktree mode must not have created anything at all.
if [[ -d "$WORKTREE_ROOT" ]]; then
    LEFTOVER=$(find "$WORKTREE_ROOT" -mindepth 1 -maxdepth 1 -type d -print)
    [[ -z "$LEFTOVER" ]] ||
        die "a refused launch left a worktree behind: $LEFTOVER"
fi
[[ "$(git -C "$REPO" worktree list | wc -l | tr -d ' ')" == "1" ]] ||
    die "a refused launch registered an extra git worktree"
explain "Worktree mode was requested, yet no worktree exists: the refusal landed before the"
explain "workspace was ever created."
pass "the claim was released, the status restored, and no vendor or workspace was created"

# ---------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------

printf '\nLaunch preflight smoke test passed.\n'
printf 'Admitted item: %s\n' "$ITEM_OK"
printf 'Refused item:  %s\n' "$ITEM_REFUSED"
printf 'Fixture:       %s%s\n' "$RUN_ROOT" \
    "$([[ "$KEEP_STORE" == true ]] && printf ' (kept)' || true)"
printf '\nNot covered here: the pre-spawn stage always admits from the CLI because no built-in\n'
printf 'check registers there yet. See LaunchPreflightWorkerTests for its coverage, and plan 030\n'
printf 'phase 4 for the approved-context check that will make it reachable live.\n'
