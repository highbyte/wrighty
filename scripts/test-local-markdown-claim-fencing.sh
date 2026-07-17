#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

BUILD_CONFIGURATION="Debug"
KEEP_STORE=false
SKIP_BUILD=false

usage() {
    printf '%s\n' \
        "Usage: scripts/test-local-markdown-claim-fencing.sh [options]" \
        "" \
        "Run an isolated Local Markdown claim-fencing smoke test through the locally" \
        "built Wrighty CLI. The script creates a temporary configuration and store," \
        "uses separate cache directories to simulate two installations, and removes" \
        "the temporary state on exit." \
        "" \
        "Options:" \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-store            Preserve the temporary configuration, store, and" \
        "                          command output for inspection." \
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

pass() {
    printf 'ok: %s\n' "$*"
}

while (($# > 0)); do
    case "$1" in
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

require_command dotnet
require_command jq

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
if [[ "$SKIP_BUILD" == false ]]; then
    step "Building the local Wrighty CLI"
    dotnet build "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION" --nologo
fi
[[ -f "$CLI_DLL" ]] || die "local CLI output '$CLI_DLL' was not found"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-local-claim-fencing.XXXXXX")
CACHE_A="$RUN_ROOT/cache-installation-a"
CACHE_B="$RUN_ROOT/cache-installation-b"
mkdir -p "$CACHE_A" "$CACHE_B"

LAST_OUTPUT=""
LAST_STATUS=0
LAST_STDERR="$RUN_ROOT/last.stderr"

cleanup() {
    local original_status=$?
    trap - EXIT

    if [[ "$KEEP_STORE" == true ]]; then
        printf '\nKept temporary Local Markdown fixture: %s\n' "$RUN_ROOT"
        exit "$original_status"
    fi

    case "$RUN_ROOT" in
        "${TMPDIR:-/tmp}"/wrighty-local-claim-fencing.*)
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

cd "$RUN_ROOT"

wrighty_with_cache() {
    local cache=$1
    shift
    WRIGHTY_CACHE_DIR="$cache" dotnet "$CLI_DLL" "$@"
}

capture() {
    set +e
    LAST_OUTPUT=$("$@" 2>"$LAST_STDERR")
    LAST_STATUS=$?
    set -e
    if ((LAST_STATUS != 0)) &&
        ! printf '%s\n' "$LAST_OUTPUT" | jq -e . >/dev/null 2>&1 &&
        jq -e . "$LAST_STDERR" >/dev/null 2>&1; then
        LAST_OUTPUT=$(<"$LAST_STDERR")
        : >"$LAST_STDERR"
    fi
}

safe_last_output() {
    if printf '%s\n' "$LAST_OUTPUT" | jq -e . >/dev/null 2>&1; then
        printf '%s\n' "$LAST_OUTPUT" |
            jq 'if (.result | type) == "object" then .result |= del(.claimToken) else . end'
    else
        printf '%s\n' "$LAST_OUTPUT"
    fi
}

fail_last() {
    printf 'error: %s\n' "$*" >&2
    if [[ -s "$LAST_STDERR" ]]; then
        sed -n '1,80p' "$LAST_STDERR" >&2
    fi
    safe_last_output >&2
    exit 1
}

expect_success() {
    capture "$@"
    ((LAST_STATUS == 0)) || fail_last "expected success, got exit $LAST_STATUS"
    printf '%s\n' "$LAST_OUTPUT" | jq -e '.schemaVersion == 1 and .result != null' >/dev/null ||
        fail_last "success output was not a versioned result"
}

expect_error() {
    local expected_code=$1
    local expected_status=$2
    shift 2
    capture "$@"
    ((LAST_STATUS == expected_status)) ||
        fail_last "expected $expected_code exit $expected_status, got exit $LAST_STATUS"
    local actual_code
    actual_code=$(printf '%s\n' "$LAST_OUTPUT" | jq -r '.error.code // empty')
    [[ "$actual_code" == "$expected_code" ]] ||
        fail_last "expected error $expected_code, got '${actual_code:-non-JSON output}'"
}

json_result() {
    printf '%s\n' "$LAST_OUTPUT" | jq -er "$1"
}

assert_equal() {
    local expected=$1
    local actual=$2
    local description=$3
    [[ "$actual" == "$expected" ]] ||
        die "$description: expected '$expected', got '$actual'"
}

assert_not_equal() {
    local unexpected=$1
    local actual=$2
    local description=$3
    [[ "$actual" != "$unexpected" ]] ||
        die "$description: value unexpectedly remained '$actual'"
}

assert_item_state() {
    local expected_status=$1
    local expected_priority=$2
    expect_success wrighty_with_cache "$CACHE_A" get "$ITEM_ID" --json
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e \
            --arg title "$ITEM_TITLE" \
            --arg body "$ITEM_BODY" \
            --arg status "$expected_status" \
            --arg priority "$expected_priority" \
            '.result.title == $title and
             .result.body == $body and
             .result.status == $status and
             .result.priority == $priority and
             .result.archived == false' >/dev/null ||
        fail_last "work-item fields were not preserved"
}

assert_claim_frontmatter() {
    local claimant_id=$1
    local claim_token=$2
    local claimant_kind=$3
    local agent_type=${4:-}
    local session_id=${5:-}

    grep -Fxq "  version: 2" "$ITEM_FILE" ||
        die "frontmatter does not contain claim protocol version 2"
    grep -Eq '^  workerIdentity: [0-9a-f]{12}$' "$ITEM_FILE" ||
        die "frontmatter does not contain a worker identity"
    grep -Fxq "  claimantId: $claimant_id" "$ITEM_FILE" ||
        die "frontmatter claimant ID did not match '$claimant_id'"
    grep -Fxq "  claimToken: $claim_token" "$ITEM_FILE" ||
        die "frontmatter claim token did not match the successful caller"
    grep -Fxq "  claimantKind: $claimant_kind" "$ITEM_FILE" ||
        die "frontmatter claimant kind did not match '$claimant_kind'"
    if [[ -n "$agent_type" ]]; then
        grep -Fxq "  agentType: $agent_type" "$ITEM_FILE" ||
            die "frontmatter agent type did not match '$agent_type'"
    else
        ! grep -Eq '^  agentType:' "$ITEM_FILE" ||
            die "frontmatter retained an agent type for a non-agent claimant"
    fi
    if [[ -n "$session_id" ]]; then
        grep -Fxq "  sessionId: $session_id" "$ITEM_FILE" ||
            die "frontmatter session ID did not match '$session_id'"
    else
        ! grep -Eq '^  sessionId:' "$ITEM_FILE" ||
            die "frontmatter retained a session ID unexpectedly"
    fi
}

assert_unclaimed_frontmatter() {
    ! grep -Eq '^claim:' "$ITEM_FILE" ||
        die "released item still contains a claim mapping"
    grep -Eq '^claimEpoch: [1-9][0-9]*$' "$ITEM_FILE" ||
        die "released item did not retain its claim epoch"
}

assert_store_clean() {
    local unexpected
    unexpected=$(find "$RUN_ROOT/.wrighty" -type f \
        ! -name ".lock" \
        ! -path "$ITEM_FILE" \
        -print)
    [[ -z "$unexpected" ]] ||
        die "unexpected or temporary store files remained: $unexpected"
    [[ "$(find "$RUN_ROOT/.wrighty/items" -type f -name '*.md' | wc -l | tr -d ' ')" == "1" ]] ||
        die "the active store did not contain exactly one Markdown item"
    [[ -z "$(find "$RUN_ROOT/.wrighty/archive" -type f -print)" ]] ||
        die "a stale archive operation created an archived file"
}

RUN_SUFFIX="$(date -u +%H%M%S)-$$"
AGENT_A="agent-a-$RUN_SUFFIX"
SESSION_A="session-a-$RUN_SUFFIX"
HUMAN_B="human-b-$RUN_SUFFIX"
AUTOMATION_C="automation-c-$RUN_SUFFIX"
OTHER_INSTALLATION="agent-d-$RUN_SUFFIX"
HUMAN_E="human-e-$RUN_SUFFIX"
HUMAN_F="human-f-$RUN_SUFFIX"
ITEM_TITLE="Local claim-fencing smoke item"
ITEM_BODY="Original body sentinel for Local Markdown fencing."

step "Initializing an isolated Local Markdown store"
expect_success wrighty_with_cache "$CACHE_A" init \
    --backend local-markdown \
    --local-path .wrighty \
    --status Todo \
    --status "In Progress" \
    --status Done \
    --priority P0 \
    --priority P1 \
    --priority P2 \
    --json
assert_equal "local-markdown" "$(json_result '.result.backend')" "initialized backend"
pass "temporary store initialized at $RUN_ROOT/.wrighty"

step "Creating one disposable work item"
expect_success wrighty_with_cache "$CACHE_A" create \
    --title "$ITEM_TITLE" \
    --body "$ITEM_BODY" \
    --status Todo \
    --priority P1 \
    --creation-attempt-id 019f6cba-8cf3-7130-9eba-29dd4054e254 \
    --json
ITEM_ID=$(json_result '.result.id')
assert_equal "local:1" "$ITEM_ID" "created work-item ID"
ITEM_FILE=$(find "$RUN_ROOT/.wrighty/items" -type f -name '*.md')
[[ -n "$ITEM_FILE" && "$(printf '%s\n' "$ITEM_FILE" | wc -l | tr -d ' ')" == "1" ]] ||
    die "expected exactly one active Markdown item"
assert_item_state Todo P1
pass "created $ITEM_ID at $ITEM_FILE"

step "Exact reconnect and same-installation claimant separation"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --agent-type codex \
    --session-id "$SESSION_A" \
    --json
TOKEN_A=$(json_result '.result.claimToken')
assert_equal "Acquired" "$(json_result '.result.outcome')" "initial claim outcome"
assert_claim_frontmatter "$AGENT_A" "$TOKEN_A" agent codex "$SESSION_A"

expect_error "CLAIM_TOKEN_REQUIRED" 6 \
    wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --agent-type codex \
    --session-id "$SESSION_A" \
    --json

expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --claim-token "$TOKEN_A" \
    --agent-type codex \
    --session-id "$SESSION_A" \
    --json
assert_equal "AlreadyOwned" "$(json_result '.result.outcome')" "reconnect outcome"
assert_equal "$TOKEN_A" "$(json_result '.result.claimToken')" "reconnect token"

expect_error "CLAIM_HELD_BY_LOCAL_CLAIMANT" 6 \
    wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --json
pass "exact reconnect was idempotent and claimant identity remained distinct"

step "Explicit takeover and old-generation fencing"
expect_success wrighty_with_cache "$CACHE_A" takeover "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --yes \
    --json
TOKEN_B=$(json_result '.result.claimToken')
assert_equal "TakenOver" "$(json_result '.result.outcome')" "takeover outcome"
assert_not_equal "$TOKEN_A" "$TOKEN_B" "takeover token rotation"
assert_claim_frontmatter "$HUMAN_B" "$TOKEN_B" human
! grep -Fq "$TOKEN_A" "$ITEM_FILE" ||
    die "frontmatter retained the superseded claim token"
assert_item_state Todo P1

OLD_HANDLE=(
    --claimant-kind agent
    --claimant-id "$AGENT_A"
    --claim-token "$TOKEN_A"
    --agent-type codex
    --session-id "$SESSION_A"
    --json
)
expect_error "CLAIM_STALE" 6 \
    wrighty_with_cache "$CACHE_A" edit "$ITEM_ID" --priority P2 "${OLD_HANDLE[@]}"
expect_error "CLAIM_STALE" 6 \
    wrighty_with_cache "$CACHE_A" move "$ITEM_ID" "In Progress" "${OLD_HANDLE[@]}"
expect_error "CLAIM_STALE" 6 \
    wrighty_with_cache "$CACHE_A" finish "$ITEM_ID" "${OLD_HANDLE[@]}"
expect_error "CLAIM_STALE" 6 \
    wrighty_with_cache "$CACHE_A" archive "$ITEM_ID" "${OLD_HANDLE[@]}"
expect_error "CLAIM_STALE" 6 \
    wrighty_with_cache "$CACHE_A" release "$ITEM_ID" "${OLD_HANDLE[@]}"
assert_item_state Todo P1
assert_claim_frontmatter "$HUMAN_B" "$TOKEN_B" human
assert_store_clean
pass "old edit, move, finish, archive, and release were fenced without partial files"

step "Current-generation mutation, restoration, and release"
expect_success wrighty_with_cache "$CACHE_A" edit "$ITEM_ID" \
    --status "In Progress" \
    --priority P2 \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --claim-token "$TOKEN_B" \
    --json
assert_item_state "In Progress" P2

expect_success wrighty_with_cache "$CACHE_A" edit "$ITEM_ID" \
    --status Todo \
    --priority P1 \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --claim-token "$TOKEN_B" \
    --json
expect_success wrighty_with_cache "$CACHE_A" release "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --claim-token "$TOKEN_B" \
    --json
assert_item_state Todo P1
assert_unclaimed_frontmatter
pass "current claimant mutated, restored, and released the item"

step "Confirmed same-installation override release"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind automation \
    --claimant-id "$AUTOMATION_C" \
    --json
assert_claim_frontmatter "$AUTOMATION_C" "$(json_result '.result.claimToken')" automation

expect_error "CLAIM_CONFIRMATION_REQUIRED" 2 \
    wrighty_with_cache "$CACHE_A" release "$ITEM_ID" \
    --override \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --json
expect_success wrighty_with_cache "$CACHE_A" release "$ITEM_ID" \
    --override \
    --yes \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --json
assert_item_state Todo P1
assert_unclaimed_frontmatter
pass "override release required confirmation and changed no item fields"

step "Cross-installation denial"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --agent-type codex \
    --session-id "$SESSION_A" \
    --json
CROSS_TOKEN=$(json_result '.result.claimToken')

expect_error "CLAIM_HELD" 6 \
    wrighty_with_cache "$CACHE_B" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$OTHER_INSTALLATION" \
    --agent-type codex \
    --session-id "$OTHER_INSTALLATION" \
    --json
expect_error "CLAIM_NOT_OWNER" 6 \
    wrighty_with_cache "$CACHE_B" takeover "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$OTHER_INSTALLATION" \
    --agent-type codex \
    --session-id "$OTHER_INSTALLATION" \
    --yes \
    --json
expect_error "CLAIM_NOT_OWNER" 6 \
    wrighty_with_cache "$CACHE_B" release "$ITEM_ID" \
    --override \
    --yes \
    --claimant-kind agent \
    --claimant-id "$OTHER_INSTALLATION" \
    --agent-type codex \
    --session-id "$OTHER_INSTALLATION" \
    --json
expect_success wrighty_with_cache "$CACHE_A" release "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --claim-token "$CROSS_TOKEN" \
    --agent-type codex \
    --session-id "$SESSION_A" \
    --json
assert_item_state Todo P1
assert_unclaimed_frontmatter
pass "a second simulated installation could neither take over nor override-release"

step "Concurrent takeover processes and store integrity"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --agent-type codex \
    --session-id "$SESSION_A" \
    --json

OUT_E="$RUN_ROOT/takeover-e.json"
OUT_F="$RUN_ROOT/takeover-f.json"
ERR_E="$RUN_ROOT/takeover-e.stderr"
ERR_F="$RUN_ROOT/takeover-f.stderr"
set +e
WRIGHTY_CACHE_DIR="$CACHE_A" dotnet "$CLI_DLL" takeover "$ITEM_ID" \
    --claimant-kind human --claimant-id "$HUMAN_E" --yes --json \
    >"$OUT_E" 2>"$ERR_E" &
PID_E=$!
WRIGHTY_CACHE_DIR="$CACHE_A" dotnet "$CLI_DLL" takeover "$ITEM_ID" \
    --claimant-kind human --claimant-id "$HUMAN_F" --yes --json \
    >"$OUT_F" 2>"$ERR_F" &
PID_F=$!
wait "$PID_E"
STATUS_E=$?
wait "$PID_F"
STATUS_F=$?
set -e

for output_pair in "$OUT_E:$ERR_E" "$OUT_F:$ERR_F"; do
    output_file=${output_pair%%:*}
    error_file=${output_pair#*:}
    if [[ ! -s "$output_file" && -s "$error_file" ]] && jq -e . "$error_file" >/dev/null 2>&1; then
        cp "$error_file" "$output_file"
    fi
done

SUCCESS_COUNT=0
for command_status in "$STATUS_E" "$STATUS_F"; do
    if ((command_status == 0)); then
        SUCCESS_COUNT=$((SUCCESS_COUNT + 1))
    elif ((command_status != 6)); then
        die "concurrent takeover returned unexpected exit $command_status"
    fi
done
((SUCCESS_COUNT >= 1)) || die "both concurrent takeover commands failed"

for output_file in "$OUT_E" "$OUT_F"; do
    if jq -e '.error != null' "$output_file" >/dev/null 2>&1; then
        [[ "$(jq -r '.error.code' "$output_file")" == "CLAIM_STALE" ]] ||
            die "losing concurrent takeover did not return CLAIM_STALE"
    fi
done

WINNING_CLAIMANT=$(awk '$1 == "claimantId:" { print $2; exit }' "$ITEM_FILE")
WINNING_TOKEN=""
for output_file in "$OUT_E" "$OUT_F"; do
    if [[ "$(jq -r '.result.claimantId // empty' "$output_file")" == "$WINNING_CLAIMANT" ]]; then
        WINNING_TOKEN=$(jq -er '.result.claimToken' "$output_file")
    fi
done
[[ -n "$WINNING_TOKEN" ]] ||
    die "resolved concurrent claimant did not match a successful takeover"
assert_claim_frontmatter "$WINNING_CLAIMANT" "$WINNING_TOKEN" human
assert_item_state Todo P1

expect_success wrighty_with_cache "$CACHE_A" release "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$WINNING_CLAIMANT" \
    --claim-token "$WINNING_TOKEN" \
    --json
assert_unclaimed_frontmatter
assert_item_state Todo P1
assert_store_clean
if ((SUCCESS_COUNT == 1)); then
    pass "overlapping takeovers produced one winner and one stale loser"
else
    pass "the store lock serialized both takeovers and the final resolved handle was verified"
fi

printf '\nLocal Markdown claim-fencing smoke test passed.\n'
printf 'Item:  %s\n' "$ITEM_ID"
printf 'Store: %s%s\n' "$RUN_ROOT/.wrighty" \
    "$([[ "$KEEP_STORE" == true ]] && printf ' (kept)' || true)"
