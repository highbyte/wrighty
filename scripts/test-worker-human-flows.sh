#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

BUILD_CONFIGURATION="Debug"
KEEP_STORE=false
SKIP_BUILD=false
SUITE="all"

usage() {
    printf '%s\n' \
        "Usage: scripts/test-worker-human-flows.sh [options]" \
        "" \
        "Run isolated Local Markdown worker/human integration scenarios with a fake" \
        "Claude executable. No vendor service or GitHub resource is contacted." \
        "" \
        "Suites:" \
        "  all          Rejection, handoff, shared/worktree concurrency, dashboard, and probes." \
        "  rejection    Shared-current-workspace rejection only." \
        "  happy        Needs-attention, requeue, handoff, shared concurrency, and worktree flows." \
        "  probes       Non-gating observations for behavior whose policy is unresolved." \
        "" \
        "Options:" \
        "  --suite NAME            Select all, rejection, happy, or probes." \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-store            Preserve the temporary fixture and transcripts." \
        "  -h, --help              Show this help."
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' was not found"
}

step() {
    printf '\n==> %s\n' "$*"
}

explain() {
    printf '    %s\n' "$*"
}

pass() {
    printf 'ok: %s\n' "$*"
}

probe() {
    printf 'probe: %s\n' "$*"
}

while (($# > 0)); do
    case "$1" in
        --suite)
            (($# >= 2)) || die "--suite requires a value"
            SUITE=$2
            shift 2
            ;;
        --configuration)
            (($# >= 2)) || die "--configuration requires a value"
            BUILD_CONFIGURATION=$2
            shift 2
            ;;
        --skip-build)
            SKIP_BUILD=true
            shift
            ;;
        --keep-store)
            KEEP_STORE=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown option '$1'"
            ;;
    esac
done

[[ "$SUITE" == "all" || "$SUITE" == "rejection" ||
   "$SUITE" == "happy" || "$SUITE" == "probes" ]] ||
    die "--suite must be all, rejection, happy, or probes"

require_command curl
require_command dotnet
require_command git
require_command jq

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
if [[ "$SKIP_BUILD" == false ]]; then
    step "Building the local Wrighty CLI"
    dotnet build "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION" --nologo
fi
[[ -f "$CLI_DLL" ]] || die "local CLI output '$CLI_DLL' was not found"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-worker-human-flows.XXXXXX")
REPOSITORY="$RUN_ROOT/repo"
CACHE_A="$RUN_ROOT/cache-a"
CACHE_B="$RUN_ROOT/cache-b"
FAKE_BIN="$RUN_ROOT/fake-bin"
CONTROL="$RUN_ROOT/fake-agent-control"
TRANSCRIPTS="$RUN_ROOT/transcripts"
mkdir -p "$REPOSITORY" "$CACHE_A" "$CACHE_B" "$FAKE_BIN" "$CONTROL" "$TRANSCRIPTS"

BACKGROUND_PIDS=()
LAST_OUTPUT=""
LAST_STATUS=0
CREATED_ID=""
ATTENTION_READY=false

cleanup() {
    local original_status=$?
    trap - EXIT

    touch "$CONTROL/release" 2>/dev/null || true
    for pid in "${BACKGROUND_PIDS[@]:-}"; do
        [[ -n "$pid" ]] || continue
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    done

    if [[ "$KEEP_STORE" == true ]]; then
        printf '\nKept worker/human fixture: %s\n' "$RUN_ROOT"
        exit "$original_status"
    fi

    case "$RUN_ROOT" in
        "${TMPDIR:-/tmp}"/wrighty-worker-human-flows.*)
            rm -rf "$RUN_ROOT"
            ;;
        *)
            printf 'warning: refusing to remove unexpected temporary path %s\n' "$RUN_ROOT" >&2
            exit 1
            ;;
    esac
    exit "$original_status"
}
trap cleanup EXIT

cat >"$FAKE_BIN/claude" <<'FAKE_CLAUDE'
#!/usr/bin/env bash
set -euo pipefail

session_id=""
prompt=""
while (($# > 0)); do
    case "$1" in
        -p)
            prompt=${2:-}
            shift 2
            ;;
        --session-id|--resume)
            session_id=${2:-}
            shift 2
            ;;
        *)
            shift
            ;;
    esac
done

[[ -n "$session_id" ]] || session_id="fake-claude-session"
control=${FAKE_AGENT_CONTROL_DIR:?FAKE_AGENT_CONTROL_DIR is required}
cli_dll=${WRIGHTY_TEST_CLI_DLL:?WRIGHTY_TEST_CLI_DLL is required}
: "${WRIGHTY_CONFIG_PATH:?WRIGHTY_CONFIG_PATH is required}"
mkdir -p "$control"
[[ -f "$PWD/.claude/skills/wrighty/SKILL.md" ]] || {
    printf 'Wrighty Claude skill is missing in %s\n' "$PWD" >&2
    exit 91
}
item_id=$(printf '%s\n' "$prompt" |
    sed -n 's/.*\(local:[0-9][0-9]*\).*/\1/p' |
    head -1)
[[ -n "$item_id" ]] || {
    printf 'Could not find a local item ID in the worker prompt\n' >&2
    exit 92
}
dotnet "$cli_dll" get "$item_id" --json >"$control/get.$$.json"
jq -e --arg id "$item_id" '.result.id == $id' "$control/get.$$.json" >/dev/null
printf '%s\n' "$PWD" >"$control/started.$$"

if [[ "${FAKE_AGENT_MODE:-attention}" == "hold" ]]; then
    while [[ ! -e "$control/release" ]]; do
        sleep 0.05
    done
fi

# 'finish' mode drives the item to its completion status via the pre-claimed worker token, so the
# worker observes a genuinely finished item (item -> Done, claim released) rather than the default
# needs-attention exit. This is what a real agent does when the tracked work is complete.
if [[ "${FAKE_AGENT_MODE:-attention}" == "finish" ]]; then
    dotnet "$cli_dll" finish "$item_id" \
        --claimant-id "${WRIGHTY_CLAIMANT_ID:?finish mode needs WRIGHTY_CLAIMANT_ID}" \
        --claim-token "${WRIGHTY_CLAIM_TOKEN:?finish mode needs WRIGHTY_CLAIM_TOKEN}" \
        --json >"$control/finish.$$.json" 2>&1 || {
        printf 'fake agent could not finish %s\n' "$item_id" >&2
        cat "$control/finish.$$.json" >&2
        exit 93
    }
    printf '{"type":"result","subtype":"success","is_error":false,"session_id":"%s","result":"Fake Claude completed the item."}\n' \
        "$session_id"
    exit 0
fi

printf '{"type":"result","subtype":"success","is_error":false,"session_id":"%s","result":"Fake Claude needs operator attention."}\n' \
    "$session_id"
FAKE_CLAUDE
chmod +x "$FAKE_BIN/claude"

wrighty() {
    local cache=$1
    shift
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$cache" \
        dotnet "$CLI_DLL" "$@"
}

capture_json() {
    local cache=$1
    shift
    set +e
    LAST_OUTPUT=$(wrighty "$cache" "$@" --json 2>"$TRANSCRIPTS/last.stderr")
    LAST_STATUS=$?
    set -e
}

expect_success() {
    local cache=$1
    shift
    capture_json "$cache" "$@"
    ((LAST_STATUS == 0)) || {
        sed -n '1,120p' "$TRANSCRIPTS/last.stderr" >&2
        printf '%s\n' "$LAST_OUTPUT" >&2
        die "expected success from 'wrighty $*', got exit $LAST_STATUS"
    }
    printf '%s\n' "$LAST_OUTPUT" | jq -e '.result != null' >/dev/null ||
        die "'wrighty $*' did not return a versioned result"
}

create_item() {
    local title=$1
    local body=$2
    expect_success "$CACHE_A" create \
        --title "$title" \
        --body "$body" \
        --status Todo \
        --priority P1 \
        --auto \
        --agent claude
    CREATED_ID=$(printf '%s\n' "$LAST_OUTPUT" | jq -er '.result.id')
}

reset_control() {
    find "$CONTROL" -type f -delete
}

wait_for_agent_count() {
    local expected=$1
    local attempts=0
    while ((attempts < 200)); do
        local count
        count=$(find "$CONTROL" -type f -name 'started.*' | wc -l | tr -d ' ')
        if ((count >= expected)); then
            return
        fi
        attempts=$((attempts + 1))
        sleep 0.05
    done
    die "timed out waiting for $expected fake agent process(es)"
}

wait_for_log_text() {
    local path=$1
    local text=$2
    local attempts=0
    while ((attempts < 200)); do
        if [[ -f "$path" ]] && grep -Fq "$text" "$path"; then
            return
        fi
        attempts=$((attempts + 1))
        sleep 0.05
    done
    die "timed out waiting for '$text' in $path"
}

wait_for_worker() {
    local pid=$1
    local expected_status=$2
    local description=$3
    set +e
    wait "$pid"
    local status=$?
    set -e
    ((status == expected_status)) ||
        die "$description: expected exit $expected_status, got $status"
}

launch_worker() {
    local cache=$1
    local mode=$2
    local workspace_mode=$3
    local output=$4
    local error=$5
    local worker_command=(
        dotnet "$CLI_DLL" worker
        --once
        --yes
        --json
    )
    if [[ "$workspace_mode" != "configured" ]]; then
        worker_command+=(--workspace-mode "$workspace_mode")
    fi
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$cache" \
        FAKE_AGENT_CONTROL_DIR="$CONTROL" \
        FAKE_AGENT_MODE="$mode" \
        WRIGHTY_TEST_CLI_DLL="$CLI_DLL" \
        "${worker_command[@]}" \
        >"$output" 2>"$error" &
    LAST_WORKER_PID=$!
    BACKGROUND_PIDS+=("$LAST_WORKER_PID")
}

assert_jsonl_event() {
    local path=$1
    local event_type=$2
    jq -s -e --arg type "$event_type" 'any(.type == $type)' "$path" >/dev/null ||
        die "transcript $path did not contain worker event '$event_type'"
}

initialize_fixture() {
    step "Initializing an isolated repository and Local Markdown worker backlog"
    explain "The fixture uses two Wrighty cache directories and a fake Claude executable."
    explain "No real agent, network service, or user repository is touched."

    cd "$REPOSITORY"
    git init -q -b main
    git config user.name "Wrighty integration fixture"
    git config user.email "wrighty-fixture@example.invalid"
    mkdir -p .claude/skills/wrighty
    printf '%s\n' \
        "---" \
        "name: wrighty" \
        "description: Test-only Wrighty worker skill." \
        "disable-model-invocation: true" \
        "---" \
        "" \
        "Use Wrighty for tracked work." \
        >.claude/skills/wrighty/SKILL.md
    git add -f .claude/skills/wrighty/SKILL.md
    git commit -q -m "Initialize worker integration fixture"

    expect_success "$CACHE_A" init \
        --backend local-markdown \
        --local-path .wrighty \
        --status Todo \
        --status "In Progress" \
        --status Done \
        --priority P0 \
        --priority P1 \
        --priority P2 \
        --yes

    create_item "Needs clarification" "..."
    ITEM_ATTENTION=$CREATED_ID
    create_item "Second current-workspace candidate" "Run only after the workspace is available."
    ITEM_SECOND=$CREATED_ID
    pass "created worker-eligible items $ITEM_ATTENTION and $ITEM_SECOND with a committed Claude skill"
}

test_worker_agent_default_notice() {
    step "Explaining worker agent fallback configuration"
    explain "The repository has no worker.defaultAgent, so a generic worker should say that only"
    explain "items with wrighty-agent can run. An explicit --agent should suppress that notice."

    local no_default_out="$TRANSCRIPTS/no-default-agent.jsonl"
    wrighty "$CACHE_A" worker --dry-run --once --json >"$no_default_out"
    jq -s -e '
        any(
            .type == "info" and
            (.message | contains("No default worker agent is configured")) and
            (.message | contains("only items with wrighty-agent can run"))
        )
    ' "$no_default_out" >/dev/null ||
        die "worker did not explain the missing command/config agent default"

    local explicit_out="$TRANSCRIPTS/explicit-agent.jsonl"
    wrighty "$CACHE_A" worker --dry-run --once --json --agent claude >"$explicit_out"
    jq -s -e '
        all(.type != "info" or (.message | contains("No default worker agent is configured") | not))
    ' "$explicit_out" >/dev/null ||
        die "worker printed the missing-default notice despite --agent claude"

    pass "worker explains missing fallback configuration once and honors an explicit agent"
}

start_attention_worker() {
    local test_contender=$1
    reset_control
    local first_out="$TRANSCRIPTS/current-first.jsonl"
    local first_err="$TRANSCRIPTS/current-first.stderr"

    step "Starting one fake agent in the current workspace"
    explain "The fake vendor blocks after spawn, then exits successfully without calling finish."
    explain "Wrighty should retain a finite resumable claim and report needs-attention."
    launch_worker "$CACHE_A" hold current "$first_out" "$first_err"
    local first_pid=$LAST_WORKER_PID
    wait_for_agent_count 1
    assert_jsonl_event "$first_out" "started"

    if [[ "$test_contender" == true ]]; then
        step "Rejecting a second worker that targets the same current workspace"
        explain "Worker A is already running an agent in the canonical repository directory."
        explain "Worker B must fail with WORKSPACE_BUSY before claiming $ITEM_SECOND or spawning Claude."
        local second_out="$TRANSCRIPTS/current-second.jsonl"
        local second_err="$TRANSCRIPTS/current-second.stderr"
        set +e
        env \
            PATH="$FAKE_BIN:$PATH" \
            WRIGHTY_CACHE_DIR="$CACHE_B" \
            FAKE_AGENT_CONTROL_DIR="$CONTROL" \
            FAKE_AGENT_MODE=hold \
            dotnet "$CLI_DLL" worker --once --yes --json --workspace-mode current \
            >"$second_out" 2>"$second_err"
        local second_status=$?
        set -e
        ((second_status == 7)) ||
            die "second current-workspace worker should exit 7, got $second_status"
        awk 'found || /^\{/ { found=1; print }' "$second_err" >"$TRANSCRIPTS/current-second-error.json"
        jq -e '.error.code == "WORKSPACE_BUSY"' \
            "$TRANSCRIPTS/current-second-error.json" >/dev/null ||
            die "second worker did not return WORKSPACE_BUSY"
        [[ "$(find "$CONTROL" -type f -name 'started.*' | wc -l | tr -d ' ')" == "1" ]] ||
            die "the rejected worker spawned a second vendor process"
        local second_file
        second_file=$(find "$REPOSITORY/.wrighty/items" -type f -name '002-*.md')
        [[ -n "$second_file" ]] || die "could not find the second work-item document"
        grep -Fxq "status: Todo" "$second_file" ||
            die "$ITEM_SECOND did not remain in Todo"
        local runtime_state="$REPOSITORY/.wrighty/.runtime-state.json"
        if [[ -f "$runtime_state" ]]; then
            jq -e '(.claims["2"] // null) == null' "$runtime_state" >/dev/null ||
                die "$ITEM_SECOND was claimed before the workspace rejection"
        fi
        pass "second worker was rejected before claim and spawn; $ITEM_SECOND remained unclaimed"
    fi

    touch "$CONTROL/release"
    wait_for_worker "$first_pid" 10 "needs-attention worker"
    assert_jsonl_event "$first_out" "needs-attention"
    jq -e 'select(.type == "needs-attention") |
        (.operatorActions | length == 4) and
        (.operatorActions[0].commands[0] == "wrighty web") and
        (.operatorActions[0].description | contains("Save and queue for worker")) and
        (.operatorActions[0].description | contains("Finish when complete")) and
        (.operatorActions[1].commands[0] == ("wrighty edit " + .itemId + " --takeover --yes --body-file requirements.md --requeue")) and
        (.operatorActions[1].description | contains("prioritizes it before fresh Todo work")) and
        (.operatorActions[2].commands[0] == ("wrighty worker --item " + .itemId + " --yes")) and
        (.operatorActions[2].description | contains("active or after it expires")) and
        (.operatorActions[3].commands[0] == ("wrighty edit " + .itemId + " --takeover")) and
        (.operatorActions[3].commands[1] | contains("--takeover --yes --title \"Clear title\" --body-file requirements.md")) and
        (.operatorActions[3].description | contains("edit --takeover works before or after that time")) and
        (.operatorActions[3].description | contains("after expiry, it acquires"))' \
        "$first_out" >/dev/null ||
        die "needs-attention did not provide intent-based worker, web, and human-control actions"

    # The captured run outcome (plan 023 part a) must be durable on the session record and surfaced
    # by `wrighty get`. A current-mode session records no branch, so it is not a retained worktree.
    expect_success "$CACHE_A" get "$ITEM_ATTENTION"
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e '
            (.result.worker.activity == "needs-attention") and
            (.result.session.lastRun.outcome == "succeeded") and
            (.result.session.lastRun.finalMessage | contains("needs operator attention")) and
            (.result.session.lastRun.endedAt != null) and
            (.result.hasRecordedWorktree == false)' >/dev/null ||
        die "needs-attention item did not expose a durable last-run outcome in wrighty get"

    # `wrighty status` (plan 023 part c) must group the blocked item under needs-attention with the
    # same captured outcome, so an operator can discover it without the web dashboard.
    expect_success "$CACHE_A" status
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e --arg id "$ITEM_ATTENTION" '
            [.result.needsAttention[] | select(.id == $id)] as $found
            | ($found | length == 1)
            and ($found[0].lastRun.outcome == "succeeded")' >/dev/null ||
        die "wrighty status did not group the blocked item under needs-attention with its outcome"

    ATTENTION_READY=true
    pass "$ITEM_ATTENTION retained a resumable claim, a durable last-run outcome, and status grouping"
}

ensure_attention_item() {
    if [[ "$ATTENTION_READY" == false ]]; then
        start_attention_worker false
    fi
}

test_dashboard_view() {
    ensure_attention_item
    step "Inspecting, taking over, editing, and handing back through the web dashboard"
    explain "A newly started web session may inspect the recorded address but must not adopt the"
    explain "worker token. After explicit takeover, Save and hand back must rotate to an agent"
    explain "claim and render copyable interactive and headless continuation commands."

    local web_log="$TRANSCRIPTS/web.log"
    env WRIGHTY_CACHE_DIR="$CACHE_A" dotnet "$CLI_DLL" web --no-open >"$web_log" 2>&1 &
    local web_pid=$!
    BACKGROUND_PIDS+=("$web_pid")
    wait_for_log_text "$web_log" "Press Ctrl+C to stop."
    local origin token
    origin=$(sed -n 's/^Wrighty web server listening on //p' "$web_log" | head -1)
    token=$(sed -n 's/^Open .*#token=//p' "$web_log" | head -1)
    [[ -n "$origin" && -n "$token" ]] || die "could not parse the web server address"
    local item_html="$TRANSCRIPTS/item-detail.html"
    local cookies="$TRANSCRIPTS/web.cookies"
    curl -fsS \
        -c "$cookies" \
        -H "X-Wrighty-Token: $token" \
        "$origin/?handler=Item&id=local%3A1" \
        >"$item_html"
    grep -Fq "Take over for editing" "$item_html" ||
        die "dashboard did not show the human takeover action"
    grep -Fq "data-copy-target=\"claimant-id-value\"" "$item_html" ||
        die "dashboard did not expose the claimant copy control"
    ! grep -Fq "Continue agent session" "$item_html" ||
        die "new web session silently adopted a claim token it was not given"

    local csrf
    csrf=$(sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' \
        "$item_html" | head -1)
    [[ -n "$csrf" ]] || die "could not read the takeover antiforgery token"
    local edit_html="$TRANSCRIPTS/web-takeover-edit.html"
    curl -fsS \
        -b "$cookies" \
        -c "$cookies" \
        -H "Origin: $origin" \
        -H "X-Wrighty-Token: $token" \
        --data-urlencode "__RequestVerificationToken=$csrf" \
        --data-urlencode "id=$ITEM_ATTENTION" \
        "$origin/?handler=Takeover" \
        >"$edit_html"
    grep -Fq "Takeover complete" "$edit_html" ||
        die "web takeover did not open the protected editor"
    grep -Fq "Save and hand back to Claude" "$edit_html" ||
        die "web editor did not offer agent handback"

    local revision generation save_csrf
    revision=$(sed -n 's/.*name="expectedRevision"[^>]*value="\([^"]*\)".*/\1/p' \
        "$edit_html" | head -1)
    generation=$(sed -n 's/.*name="expectedClaimGeneration"[^>]*value="\([^"]*\)".*/\1/p' \
        "$edit_html" | head -1)
    save_csrf=$(sed -n 's/.*name="__RequestVerificationToken"[^>]*value="\([^"]*\)".*/\1/p' \
        "$edit_html" | head -1)
    [[ -n "$revision" && -n "$generation" && -n "$save_csrf" ]] ||
        die "web editor did not expose its concurrency and antiforgery values"
    local handback_html="$TRANSCRIPTS/web-handback.html"
    curl -fsS \
        -b "$cookies" \
        -c "$cookies" \
        -H "Origin: $origin" \
        -H "X-Wrighty-Token: $token" \
        --data-urlencode "__RequestVerificationToken=$save_csrf" \
        --data-urlencode "id=$ITEM_ATTENTION" \
        --data-urlencode "expectedRevision=$revision" \
        --data-urlencode "expectedClaimGeneration=$generation" \
        --data-urlencode "title=Needs clarification" \
        --data-urlencode "body=Clarified by the web integration scenario." \
        --data-urlencode "status=In Progress" \
        --data-urlencode "priority=P1" \
        --data-urlencode "automationEligible=true" \
        --data-urlencode "preferredAgent=claude" \
        --data-urlencode "action=save-handback" \
        "$origin/?handler=Save" \
        >"$handback_html"
    grep -Fq "Saved and handed back to Claude" "$handback_html" ||
        die "web save did not rotate the human claim back to Claude"
    grep -Fq "Continue agent session" "$handback_html" ||
        die "web handback did not show the continuation disclosure"
    grep -Fq "data-copy-target=\"interactive-resume-command\"" "$handback_html" ||
        die "web handback did not expose the interactive command copy control"
    grep -Fq "data-copy-target=\"headless-resume-command\"" "$handback_html" ||
        die "web handback did not expose the headless command copy control"
    kill "$web_pid"
    wait "$web_pid" 2>/dev/null || true
    pass "web takeover and handback rendered both copyable continuation paths"
}

test_cli_handoff() {
    ensure_attention_item
    step "Taking over, clarifying, and handing the recorded session back to a headless worker"
    explain "One edit --takeover command must rotate the claim and use its returned handle internally."
    explain "worker --item must then infer the active session and rotate back to an agent claimant."

    local takeover_out="$TRANSCRIPTS/atomic-takeover-edit.txt"
    local takeover_err="$TRANSCRIPTS/atomic-takeover-edit.stderr"
    set +e
    env WRIGHTY_CACHE_DIR="$CACHE_A" \
        dotnet "$CLI_DLL" edit "$ITEM_ATTENTION" \
        --takeover \
        --yes \
        --body "Clarified: implement the deterministic fixture behavior." \
        --claimant-kind human \
        --claimant-id human-script \
        >"$takeover_out" 2>"$takeover_err"
    local takeover_status=$?
    set -e
    ((takeover_status == 0)) ||
        die "edit --takeover should succeed, got $takeover_status"
    grep -Fq "The human editing claim remains active" "$takeover_out" ||
        die "edit --takeover did not report retained human ownership"
    grep -Fq "wrighty worker --item '$ITEM_ATTENTION' --yes" "$takeover_out" ||
        die "atomic takeover edit did not print the inferred headless continuation"
    ! grep -Fq "Claim token:" "$takeover_out" ||
        die "edit --takeover unnecessarily exposed a token for manual propagation"

    local resume_out="$TRANSCRIPTS/resume.jsonl"
    local resume_err="$TRANSCRIPTS/resume.stderr"
    reset_control
    set +e
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$CACHE_A" \
        FAKE_AGENT_CONTROL_DIR="$CONTROL" \
        FAKE_AGENT_MODE=attention \
        WRIGHTY_TEST_CLI_DLL="$CLI_DLL" \
        dotnet "$CLI_DLL" worker \
        --item "$ITEM_ATTENTION" \
        --yes \
        --json \
        >"$resume_out" 2>"$resume_err"
    local resume_status=$?
    set -e
    ((resume_status == 10)) ||
        die "headless resume should report needs-attention exit 10, got $resume_status"
    assert_jsonl_event "$resume_out" "resumed"
    assert_jsonl_event "$resume_out" "needs-attention"
    pass "atomic human clarification handed the same fake Claude session back without shell tokens"
}

test_expired_session_recovery() {
    step "Editing after expiry without losing the recorded agent session"
    explain "edit --takeover should acquire a human claim without a takeover prompt and preserve"
    explain "the durable Claude session for the following inferred worker continuation."
    create_item "Recover expired session" "Continue this work with the existing session context."
    local item=$CREATED_ID
    local initial_out="$TRANSCRIPTS/expired-initial.jsonl"
    local initial_err="$TRANSCRIPTS/expired-initial.stderr"
    reset_control
    set +e
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$CACHE_A" \
        FAKE_AGENT_CONTROL_DIR="$CONTROL" \
        FAKE_AGENT_MODE=attention \
        WRIGHTY_TEST_CLI_DLL="$CLI_DLL" \
        dotnet "$CLI_DLL" worker --item "$item" --fresh --yes --json \
        >"$initial_out" 2>"$initial_err"
    local initial_status=$?
    set -e
    ((initial_status == 10)) ||
        die "initial exact-item run should report needs-attention exit 10, got $initial_status"
    local old_session
    old_session=$(jq -rs '[.[] | select(.type == "needs-attention")][0].sessionId' "$initial_out")
    [[ -n "$old_session" && "$old_session" != "null" ]] ||
        die "initial exact-item run did not retain a session ID"

    local number runtime_state replacement
    number=${item#local:}
    runtime_state="$REPOSITORY/.wrighty/.runtime-state.json"
    [[ -f "$runtime_state" ]] || die "could not find the runtime-state sidecar to expire"
    jq -e --arg key "$number" '.claims[$key] != null' "$runtime_state" >/dev/null ||
        die "expired-session fixture item has no runtime-state claim"
    replacement="$runtime_state.expired"
    jq --arg key "$number" \
        '.claims[$key].expiresAt = "2000-01-01T00:00:00+00:00"' \
        "$runtime_state" >"$replacement"
    mv "$replacement" "$runtime_state"

    local edit_out="$TRANSCRIPTS/expired-edit-takeover.txt"
    local edit_err="$TRANSCRIPTS/expired-edit-takeover.stderr"
    set +e
    env WRIGHTY_CACHE_DIR="$CACHE_A" \
        dotnet "$CLI_DLL" edit "$item" --takeover \
        --title "Clarified after expiry" \
        >"$edit_out" 2>"$edit_err"
    local edit_status=$?
    set -e
    ((edit_status == 0)) ||
        die "edit --takeover should acquire an expired item without --yes, got $edit_status"
    grep -Fq "The human editing claim remains active" "$edit_out" ||
        die "expired edit --takeover did not retain a human editing claim"

    local recovered_out="$TRANSCRIPTS/expired-recovered.jsonl"
    local recovered_err="$TRANSCRIPTS/expired-recovered.stderr"
    reset_control
    set +e
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$CACHE_A" \
        FAKE_AGENT_CONTROL_DIR="$CONTROL" \
        FAKE_AGENT_MODE=attention \
        WRIGHTY_TEST_CLI_DLL="$CLI_DLL" \
        dotnet "$CLI_DLL" worker --item "$item" --yes --json \
        >"$recovered_out" 2>"$recovered_err"
    local recovered_status=$?
    set -e
    ((recovered_status == 10)) ||
        die "expired-session recovery should report needs-attention exit 10, got $recovered_status"
    jq -e --arg session "$old_session" '
        select(.type == "resumed") |
        (.sessionId == $session)' "$recovered_out" >/dev/null ||
        die "post-edit worker did not resume the original vendor session"
    pass "$item was clarified after expiry and resumed Claude session $old_session"
}

test_continuous_requeue() {
    step "Clarifying a paused session and requeueing it for a continuous worker"
    explain "wrighty-auto remains the durable automation permission."
    explain "edit --takeover --requeue must preserve the vendor session, end human ownership,"
    explain "and mark the In Progress item queued. A normal worker loop must then resume that"
    explain "session before starting a fresh Todo item."

    create_item "Continuous requeue" "..."
    local item=$CREATED_ID
    local initial_out="$TRANSCRIPTS/requeue-initial.jsonl"
    local initial_err="$TRANSCRIPTS/requeue-initial.stderr"
    reset_control
    set +e
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$CACHE_A" \
        FAKE_AGENT_CONTROL_DIR="$CONTROL" \
        FAKE_AGENT_MODE=attention \
        WRIGHTY_TEST_CLI_DLL="$CLI_DLL" \
        dotnet "$CLI_DLL" worker --item "$item" --fresh --yes --json \
        >"$initial_out" 2>"$initial_err"
    local initial_status=$?
    set -e
    ((initial_status == 10)) ||
        die "initial requeue fixture run should report needs-attention exit 10, got $initial_status"
    local session
    session=$(jq -rs '[.[] | select(.type == "needs-attention")][0].sessionId' "$initial_out")
    [[ -n "$session" && "$session" != "null" ]] ||
        die "requeue fixture did not retain a session ID"

    local edit_out="$TRANSCRIPTS/requeue-edit.txt"
    local edit_err="$TRANSCRIPTS/requeue-edit.stderr"
    set +e
    env WRIGHTY_CACHE_DIR="$CACHE_A" \
        dotnet "$CLI_DLL" edit "$item" \
        --takeover \
        --yes \
        --body "Clarified requirements for the continuous worker." \
        --requeue \
        >"$edit_out" 2>"$edit_err"
    local edit_status=$?
    set -e
    ((edit_status == 0)) ||
        die "edit --takeover --requeue should succeed, got $edit_status"
    grep -Fq "queued #${item#local:} to resume its recorded agent session" "$edit_out" ||
        die "requeue edit did not report the queued session"

    expect_success "$CACHE_A" get "$item"
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e --arg session "$session" '
            (.result.worker.state == "queued") and
            (.result.worker.activity == "queued") and
            (.result.claim.state == "Unclaimed") and
            (.result.session.sessionId == $session)' >/dev/null ||
        die "queued item did not expose matching CLI worker, claim, and session state"

    local resumed_out="$TRANSCRIPTS/requeue-continuous.jsonl"
    local resumed_err="$TRANSCRIPTS/requeue-continuous.stderr"
    reset_control
    set +e
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$CACHE_A" \
        FAKE_AGENT_CONTROL_DIR="$CONTROL" \
        FAKE_AGENT_MODE=attention \
        WRIGHTY_TEST_CLI_DLL="$CLI_DLL" \
        dotnet "$CLI_DLL" worker --once --yes --json \
        >"$resumed_out" 2>"$resumed_err"
    local resumed_status=$?
    set -e
    ((resumed_status == 10)) ||
        die "continuous queued resume should report needs-attention exit 10, got $resumed_status"
    jq -e --arg item "$item" --arg session "$session" '
        select(.type == "resumed") |
        (.itemId == $item) and (.sessionId == $session)' "$resumed_out" >/dev/null ||
        die "continuous worker did not prioritize and resume the queued session"
    pass "$item was requeued explicitly and resumed by the normal continuous-worker path"
}

test_worktree_concurrency() {
    step "Allowing two workers when each agent receives an isolated worktree"
    explain "This is the positive control for WORKSPACE_BUSY."
    explain "Both workers must spawn, and their recorded workspace paths must be different."

    create_item "Third isolated candidate" "Process in a dedicated worktree."
    reset_control
    local out_a="$TRANSCRIPTS/worktree-a.jsonl"
    local err_a="$TRANSCRIPTS/worktree-a.stderr"
    local out_b="$TRANSCRIPTS/worktree-b.jsonl"
    local err_b="$TRANSCRIPTS/worktree-b.stderr"
    launch_worker "$CACHE_A" hold worktree "$out_a" "$err_a"
    local pid_a=$LAST_WORKER_PID
    wait_for_agent_count 1
    launch_worker "$CACHE_B" hold worktree "$out_b" "$err_b"
    local pid_b=$LAST_WORKER_PID
    wait_for_agent_count 2
    assert_jsonl_event "$out_a" "started"
    assert_jsonl_event "$out_b" "started"
    local workspace_a workspace_b
    workspace_a=$(jq -rs '[.[] | select(.type == "started")][0].workspacePath' "$out_a")
    workspace_b=$(jq -rs '[.[] | select(.type == "started")][0].workspacePath' "$out_b")
    [[ -n "$workspace_a" && -n "$workspace_b" && "$workspace_a" != "$workspace_b" ]] ||
        die "worktree workers did not receive distinct workspace paths"
    touch "$CONTROL/release"
    wait_for_worker "$pid_a" 10 "first worktree worker"
    wait_for_worker "$pid_b" 10 "second worktree worker"

    # Worktree-mode sessions record a branch, so the cheap at-a-glance "worktree recorded" flag
    # (plan 023 part d) must be true in JSON and render the [worktree] marker in the compact list.
    expect_success "$CACHE_A" list
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e 'any(.result[]; .hasRecordedWorktree == true)' >/dev/null ||
        die "list --json did not flag any item as having a recorded worktree"
    wrighty "$CACHE_A" list --compact >"$TRANSCRIPTS/worktree-list.txt" 2>/dev/null
    grep -Fq "[worktree]" "$TRANSCRIPTS/worktree-list.txt" ||
        die "list --compact did not render the [worktree] marker for a retained worktree"

    pass "two worktree workers ran concurrently in distinct directories with worktree flags"
}

test_configured_shared_concurrency() {
    step "Using the configured shared workspace mode when no CLI override is supplied"
    explain "Both fake agents intentionally run in the same repository directory."
    explain "The explicit worker.workspaceMode=shared default must bypass WORKSPACE_BUSY"
    explain "and warn that Wrighty cannot"
    explain "detect or resolve concurrent file, staging, or commit conflicts."

    create_item "First explicitly shared candidate" "Process in the shared repository."
    create_item "Second explicitly shared candidate" "Process concurrently in the shared repository."
    local configured="$TRANSCRIPTS/configured-shared.json"
    jq '.worker = ((.worker // {}) + { "workspaceMode": "shared" })' \
        "$REPOSITORY/.wrighty.json" >"$configured"
    mv "$configured" "$REPOSITORY/.wrighty.json"
    reset_control
    local out_a="$TRANSCRIPTS/shared-a.jsonl"
    local err_a="$TRANSCRIPTS/shared-a.stderr"
    local out_b="$TRANSCRIPTS/shared-b.jsonl"
    local err_b="$TRANSCRIPTS/shared-b.stderr"
    launch_worker "$CACHE_A" hold configured "$out_a" "$err_a"
    local pid_a=$LAST_WORKER_PID
    wait_for_agent_count 1
    launch_worker "$CACHE_B" hold configured "$out_b" "$err_b"
    local pid_b=$LAST_WORKER_PID
    wait_for_agent_count 2
    assert_jsonl_event "$out_a" "started"
    assert_jsonl_event "$out_b" "started"
    local workspace_a workspace_b expected_repository
    workspace_a=$(jq -rs '[.[] | select(.type == "started")][0].workspacePath' "$out_a")
    workspace_b=$(jq -rs '[.[] | select(.type == "started")][0].workspacePath' "$out_b")
    workspace_a=$(cd "$workspace_a" && pwd -P)
    workspace_b=$(cd "$workspace_b" && pwd -P)
    expected_repository=$(cd "$REPOSITORY" && pwd -P)
    [[ "$workspace_a" == "$workspace_b" && "$workspace_a" == "$expected_repository" ]] ||
        die "shared workers did not both use the fixture repository"
    grep -Fq "shared workspace mode allows multiple agents" "$err_a" ||
        die "first shared worker did not print the collision warning"
    grep -Fq "shared workspace mode allows multiple agents" "$err_b" ||
        die "second shared worker did not print the collision warning"
    touch "$CONTROL/release"
    wait_for_worker "$pid_a" 10 "first shared worker"
    wait_for_worker "$pid_b" 10 "second shared worker"
    pass "two workers used the configured shared default in the same directory with warnings"
}

run_policy_probes() {
    ensure_attention_item
    step "PROBE: direct interactive resume does not participate in worker workspace locking"
    explain "Policy is not finalized for a human launching the vendor command directly."
    explain "This probe records whether resume-command delegates straight to Claude."
    expect_success "$CACHE_A" resume-command "$ITEM_ATTENTION"
    local command
    command=$(printf '%s\n' "$LAST_OUTPUT" | jq -er '.result.command')
    [[ "$command" == *"claude --resume"* ]] ||
        die "interactive resume probe did not produce a Claude command"
    [[ "$command" != *"wrighty worker --item"* ]] ||
        die "interactive resume unexpectedly went through worker supervision"
    probe "interactive continuation currently bypasses Wrighty workspace locking: $command"

    step "PROBE: --on-fenced detach cannot retain an in-process workspace lease"
    explain "The detached child may outlive the supervising worker after cancellation."
    explain "This is a documented design question, not a gating assertion in this harness."
    probe "decide whether detach should be forbidden, warned, or supervised until child exit"
}

test_completed_activity() {
    step "Finishing an item and confirming completed (not paused) activity"
    explain "The fake agent calls 'wrighty finish' with its pre-claimed worker token, so the item"
    explain "reaches Done and the claim is released. Wrighty must then report worker activity"
    explain "'completed' with a captured succeeded outcome — distinct from a paused resumable session."
    create_item "Completion candidate" "Finish this item end to end."
    local item=$CREATED_ID
    reset_control
    local out="$TRANSCRIPTS/completed.jsonl"
    local err="$TRANSCRIPTS/completed.stderr"
    set +e
    env \
        PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CACHE_DIR="$CACHE_A" \
        FAKE_AGENT_CONTROL_DIR="$CONTROL" \
        FAKE_AGENT_MODE=finish \
        WRIGHTY_TEST_CLI_DLL="$CLI_DLL" \
        dotnet "$CLI_DLL" worker --item "$item" --fresh --yes --json \
        >"$out" 2>"$err"
    local status=$?
    set -e
    ((status == 0)) ||
        die "finish-mode worker should complete with exit 0, got $status"
    assert_jsonl_event "$out" "finished"

    # Plan 023 a/f: a finished, landed, unclaimed item reports 'completed', not 'paused-session'.
    expect_success "$CACHE_A" get "$item"
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e '
            (.result.status == "Done") and
            (.result.claim.state == "Unclaimed") and
            (.result.worker.activity == "completed") and
            (.result.session.lastRun.outcome == "succeeded")' >/dev/null ||
        die "finished item did not report completed activity with a succeeded last run"

    # Plan 023 c: wrighty status groups it under completed, not needs-attention or paused.
    expect_success "$CACHE_A" status
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e --arg id "$item" '
            (any(.result.completed[]; .id == $id))
            and ([.result.needsAttention[], .result.paused[]] | all(.id != $id))' >/dev/null ||
        die "wrighty status did not group the finished item under completed"
    pass "$item finished, released, and reported completed activity distinct from paused"
}

initialize_fixture
test_worker_agent_default_notice

case "$SUITE" in
    all)
        start_attention_worker true
        test_dashboard_view
        test_cli_handoff
        test_expired_session_recovery
        test_continuous_requeue
        test_completed_activity
        test_worktree_concurrency
        test_configured_shared_concurrency
        run_policy_probes
        ;;
    rejection)
        start_attention_worker true
        ;;
    happy)
        start_attention_worker false
        test_dashboard_view
        test_cli_handoff
        test_expired_session_recovery
        test_continuous_requeue
        test_completed_activity
        test_worktree_concurrency
        test_configured_shared_concurrency
        ;;
    probes)
        start_attention_worker false
        run_policy_probes
        ;;
    *)
        die "unknown suite '$SUITE' (expected all, rejection, happy, or probes)"
        ;;
esac

printf '\nWorker/human integration suite passed.\n'
printf 'Suite:     %s\n' "$SUITE"
printf 'Fixture:   %s%s\n' "$RUN_ROOT" \
    "$([[ "$KEEP_STORE" == true ]] && printf ' (kept)' || true)"
