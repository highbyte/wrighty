#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

EXPECTED_REPOSITORY="highbyte/wrighty"
EXPECTED_PROJECT_TITLE="Wrighty claim fencing"
CONFIG_PATH="$REPO_ROOT/.wrighty.integration-fixture.json"
BUILD_CONFIGURATION="Debug"
KEEP_ISSUE=false
SKIP_BUILD=false

usage() {
    printf '%s\n' \
        "Usage: scripts/test-github-claim-fencing.sh [options]" \
        "" \
        "Run opt-in, mutating GitHub claim-fencing integration tests through the locally" \
        "built Wrighty CLI. The script refuses any repository or Project other than:" \
        "  repository: highbyte/wrighty" \
        "  Project:    Wrighty claim fencing" \
        "" \
        "Options:" \
        "  --config PATH           Wrighty GitHub configuration; defaults to" \
        "                          .wrighty.integration-fixture.json." \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-issue            Leave the uniquely created issue and Project item" \
        "                          for inspection instead of deleting them." \
        "  -h, --help              Show this help." \
        "" \
        "Set WRIGHTY_RUN_GITHUB_CLAIM_FENCING_LIVE=1 to acknowledge that this script" \
        "creates and mutates a real issue. Unless --keep-issue is used, cleanup permanently" \
        "deletes only the uniquely titled issue created by this run."
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
        --config)
            (($# >= 2)) || die "--config requires a path"
            CONFIG_PATH=$2
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
        --keep-issue)
            KEEP_ISSUE=true
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

[[ "${WRIGHTY_RUN_GITHUB_CLAIM_FENCING_LIVE:-}" == "1" ]] ||
    die "set WRIGHTY_RUN_GITHUB_CLAIM_FENCING_LIVE=1 to run mutating live tests"

require_command dotnet
require_command gh
require_command jq

[[ -f "$CONFIG_PATH" ]] || die "configuration '$CONFIG_PATH' was not found"
CONFIG_DIRECTORY=$(dirname "$CONFIG_PATH")
CONFIG_PATH="$(cd "$CONFIG_DIRECTORY" && pwd)/$(basename "$CONFIG_PATH")"

REPOSITORY=$(jq -er '.github.repository' "$CONFIG_PATH")
PROJECT_NUMBER=$(jq -er '.github.projectNumber' "$CONFIG_PATH")
PROJECT_OWNER=$(jq -r '.github.projectOwner // empty' "$CONFIG_PATH")
if [[ -z "$PROJECT_OWNER" ]]; then
    PROJECT_OWNER=${REPOSITORY%%/*}
fi

[[ "$REPOSITORY" == "$EXPECTED_REPOSITORY" ]] ||
    die "refusing repository '$REPOSITORY'; expected '$EXPECTED_REPOSITORY'"
[[ "$PROJECT_OWNER" == "highbyte" ]] ||
    die "refusing Project owner '$PROJECT_OWNER'; expected 'highbyte'"
[[ "$PROJECT_NUMBER" =~ ^[1-9][0-9]*$ ]] ||
    die "github.projectNumber must be a positive integer"

gh auth status >/dev/null
gh repo view "$REPOSITORY" --json nameWithOwner --jq .nameWithOwner |
    grep -Fxq "$EXPECTED_REPOSITORY" ||
    die "the authenticated GitHub account cannot resolve '$EXPECTED_REPOSITORY'"

PROJECT_JSON=$(gh project view "$PROJECT_NUMBER" --owner "$PROJECT_OWNER" --format json)
PROJECT_TITLE=$(printf '%s\n' "$PROJECT_JSON" | jq -er .title)
[[ "$PROJECT_TITLE" == "$EXPECTED_PROJECT_TITLE" ]] ||
    die "refusing Project #$PROJECT_NUMBER '$PROJECT_TITLE'; expected '$EXPECTED_PROJECT_TITLE'"

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
if [[ "$SKIP_BUILD" == false ]]; then
    step "Building the local Wrighty CLI"
    dotnet build "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION" --nologo
fi
[[ -f "$CLI_DLL" ]] || die "local CLI output '$CLI_DLL' was not found"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-claim-fencing-live.XXXXXX")
CACHE_A="$RUN_ROOT/cache-installation-a"
CACHE_B="$RUN_ROOT/cache-installation-b"
mkdir -p "$CACHE_A" "$CACHE_B"
cp "$CONFIG_PATH" "$RUN_ROOT/.wrighty.json"

RUN_SUFFIX="$(date -u +%H%M%S)-$$"
ISSUE_TITLE="[claim fencing live $RUN_SUFFIX] disposable CLI matrix"
ISSUE_NUMBER=""
ITEM_ID=""
LAST_OUTPUT=""
LAST_STATUS=0
LAST_STDERR="$RUN_ROOT/last.stderr"

cleanup() {
    local original_status=$?
    local cleanup_status=0
    trap - EXIT
    set +e

    if [[ "$KEEP_ISSUE" == false ]]; then
        if [[ -z "$ISSUE_NUMBER" ]]; then
            ISSUE_NUMBER=$(gh issue list \
                --repo "$REPOSITORY" \
                --state all \
                --limit 100 \
                --json number,title \
                --jq ".[] | select(.title == \"$ISSUE_TITLE\") | .number" 2>/dev/null | head -n 1)
        fi
        if [[ -n "$ISSUE_NUMBER" ]]; then
            local actual_title
            actual_title=$(gh issue view "$ISSUE_NUMBER" \
                --repo "$REPOSITORY" \
                --json title \
                --jq .title 2>/dev/null)
            if [[ "$actual_title" == "$ISSUE_TITLE" ]]; then
                printf '\n==> Deleting disposable issue #%s\n' "$ISSUE_NUMBER"
                gh issue delete "$ISSUE_NUMBER" --repo "$REPOSITORY" --yes >/dev/null ||
                    cleanup_status=1
            else
                printf 'warning: refusing to delete issue #%s because its title changed\n' \
                    "$ISSUE_NUMBER" >&2
                cleanup_status=1
            fi
        fi
    elif [[ -n "$ISSUE_NUMBER" ]]; then
        printf '\nKept disposable issue: https://github.com/%s/issues/%s\n' \
            "$REPOSITORY" "$ISSUE_NUMBER"
    fi

    case "$RUN_ROOT" in
        "${TMPDIR:-/tmp}"/wrighty-claim-fencing-live.*)
            rm -rf "$RUN_ROOT"
            ;;
        *)
            printf 'warning: refusing to remove unexpected temporary path %s\n' "$RUN_ROOT" >&2
            cleanup_status=1
            ;;
    esac

    if ((original_status != 0)); then
        exit "$original_status"
    fi
    exit "$cleanup_status"
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

project_item() {
    local items
    items=$(gh project item-list "$PROJECT_NUMBER" \
        --owner "$PROJECT_OWNER" \
        --limit 1000 \
        --format json)
    printf '%s\n' "$items" |
        jq -ec \
            --arg repository "$REPOSITORY" \
            --argjson issue "$ISSUE_NUMBER" \
            '[.items[] | select(.content.repository == $repository and .content.number == $issue)] |
             if length == 1 then .[0] else error("expected exactly one matching Project item") end'
}

assert_project_state() {
    local expected_status=$1
    local expected_priority=$2
    local expected_kind=$3
    local expected_claimant=$4
    local item
    item=$(project_item)
    printf '%s\n' "$item" |
        jq -e \
            --arg status "$expected_status" \
            --arg priority "$expected_priority" \
            --arg kind "$expected_kind" \
            --arg claimant "$expected_claimant" \
            '.status == $status and
             .priority == $priority and
             ((.["current claimant kind"] // "") == $kind) and
             ((.["current claimant"] // "") == $claimant)' >/dev/null ||
        die "Project item did not match status=$expected_status priority=$expected_priority claimant=$expected_kind/$expected_claimant"
}

AGENT_A="agent-a-$RUN_SUFFIX"
HUMAN_B="human-b-$RUN_SUFFIX"
AUTOMATION_C="auto-c-$RUN_SUFFIX"
OTHER_INSTALLATION="agent-d-$RUN_SUFFIX"
HUMAN_E="human-e-$RUN_SUFFIX"
HUMAN_F="human-f-$RUN_SUFFIX"

step "Validating the configured Project schema"
dotnet "$CLI_DLL" init --config "$CONFIG_PATH" --check >/dev/null
pass "Project #$PROJECT_NUMBER schema is valid"

step "Creating one disposable fixture issue"
FIXTURE_LABEL="wrighty-fixture"
gh label create "$FIXTURE_LABEL" \
    --repo "$REPOSITORY" \
    --description "Disposable Wrighty integration fixture" \
    --color "BFD4F2" \
    --force >/dev/null
ISSUE_URL=$(gh issue create \
    --repo "$REPOSITORY" \
    --title "$ISSUE_TITLE" \
    --body "Disposable live claim-fencing integration item. The test script deletes this issue." \
    --label "$FIXTURE_LABEL")
ISSUE_NUMBER=${ISSUE_URL##*/}
[[ "$ISSUE_NUMBER" =~ ^[1-9][0-9]*$ ]] ||
    die "GitHub returned an unexpected issue URL '$ISSUE_URL'"
ITEM_ID="github:$REPOSITORY#$ISSUE_NUMBER"

PROJECT_ID=$(printf '%s\n' "$PROJECT_JSON" | jq -er .id)
PROJECT_ITEM_ID=$(gh project item-add "$PROJECT_NUMBER" \
    --owner "$PROJECT_OWNER" \
    --url "$ISSUE_URL" \
    --format json \
    --jq .id)
PROJECT_FIELDS=$(gh project field-list "$PROJECT_NUMBER" \
    --owner "$PROJECT_OWNER" \
    --limit 100 \
    --format json)
STATUS_FIELD_ID=$(printf '%s\n' "$PROJECT_FIELDS" |
    jq -er '.fields[] | select(.name == "Status") | .id')
TODO_OPTION_ID=$(printf '%s\n' "$PROJECT_FIELDS" |
    jq -er '.fields[] | select(.name == "Status") | .options[] | select(.name == "Todo") | .id')
PRIORITY_FIELD_ID=$(printf '%s\n' "$PROJECT_FIELDS" |
    jq -er '.fields[] | select(.name == "Priority") | .id')
P1_OPTION_ID=$(printf '%s\n' "$PROJECT_FIELDS" |
    jq -er '.fields[] | select(.name == "Priority") | .options[] | select(.name == "P1") | .id')
gh project item-edit \
    --id "$PROJECT_ITEM_ID" \
    --project-id "$PROJECT_ID" \
    --field-id "$STATUS_FIELD_ID" \
    --single-select-option-id "$TODO_OPTION_ID" >/dev/null
gh project item-edit \
    --id "$PROJECT_ITEM_ID" \
    --project-id "$PROJECT_ID" \
    --field-id "$PRIORITY_FIELD_ID" \
    --single-select-option-id "$P1_OPTION_ID" >/dev/null
pass "created $ITEM_ID in Project #$PROJECT_NUMBER"

step "Exact reconnect and same-installation claimant separation"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --agent-type codex \
    --session-id "$AGENT_A" \
    --json
TOKEN_A=$(json_result '.result.claimToken')
assert_equal "Acquired" "$(json_result '.result.outcome')" "initial claim outcome"
assert_project_state "Todo" "P1" "Agent" "$AGENT_A"

expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --claim-token "$TOKEN_A" \
    --agent-type codex \
    --session-id "$AGENT_A" \
    --json
assert_equal "AlreadyOwned" "$(json_result '.result.outcome')" "reconnect outcome"
assert_equal "$TOKEN_A" "$(json_result '.result.claimToken')" "reconnect token"

expect_error "CLAIM_HELD_BY_LOCAL_CLAIMANT" 6 \
    wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --json
pass "same installation did not imply the same claimant"

step "Explicit takeover and old-generation fencing"
expect_success wrighty_with_cache "$CACHE_A" takeover "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --yes \
    --json
TOKEN_B=$(json_result '.result.claimToken')
assert_equal "TakenOver" "$(json_result '.result.outcome')" "takeover outcome"
assert_not_equal "$TOKEN_A" "$TOKEN_B" "takeover token rotation"
assert_project_state "Todo" "P1" "Human" "$HUMAN_B"

OLD_HANDLE=(
    --claimant-kind agent
    --claimant-id "$AGENT_A"
    --claim-token "$TOKEN_A"
    --agent-type codex
    --session-id "$AGENT_A"
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
assert_project_state "Todo" "P1" "Human" "$HUMAN_B"
pass "old edit, move, finish, archive, and release were fenced"

step "Current-generation mutation, restoration, and release"
expect_success wrighty_with_cache "$CACHE_A" edit "$ITEM_ID" \
    --status "In Progress" \
    --priority P2 \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --claim-token "$TOKEN_B" \
    --json
assert_project_state "In Progress" "P2" "Human" "$HUMAN_B"

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
assert_project_state "Todo" "P1" "" ""
pass "current claimant mutated, restored, and released the item"

step "Confirmed same-installation override release"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind automation \
    --claimant-id "$AUTOMATION_C" \
    --json
assert_project_state "Todo" "P1" "Automation" "$AUTOMATION_C"

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
assert_project_state "Todo" "P1" "" ""
pass "override release required confirmation and cleared the claim"

step "Cross-installation denial"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --agent-type codex \
    --session-id "$AGENT_A" \
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
    --session-id "$AGENT_A" \
    --json
assert_project_state "Todo" "P1" "" ""
pass "a second simulated installation could neither take over nor override-release"

step "Concurrent takeover commands"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind agent \
    --claimant-id "$AGENT_A" \
    --agent-type codex \
    --session-id "$AGENT_A" \
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

SUCCESS_COUNT=0
for status in "$STATUS_E" "$STATUS_F"; do
    if ((status == 0)); then
        SUCCESS_COUNT=$((SUCCESS_COUNT + 1))
    elif ((status != 6)); then
        die "concurrent takeover returned unexpected exit $status"
    fi
done
((SUCCESS_COUNT >= 1)) || die "both concurrent takeover commands failed"

for output in "$OUT_E" "$OUT_F"; do
    if jq -e '.error != null' "$output" >/dev/null 2>&1; then
        [[ "$(jq -r '.error.code' "$output")" == "CLAIM_STALE" ]] ||
            die "losing concurrent takeover did not return CLAIM_STALE"
    fi
done

CONCURRENT_ITEM=$(project_item)
WINNING_CLAIMANT=$(printf '%s\n' "$CONCURRENT_ITEM" | jq -er '.["current claimant"]')
WINNING_TOKEN=""
for output in "$OUT_E" "$OUT_F"; do
    if [[ "$(jq -r '.result.claimantId // empty' "$output")" == "$WINNING_CLAIMANT" ]]; then
        WINNING_TOKEN=$(jq -er '.result.claimToken' "$output")
    fi
done
[[ -n "$WINNING_TOKEN" ]] ||
    die "the resolved concurrent claimant did not match a successful takeover result"

expect_success wrighty_with_cache "$CACHE_A" release "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$WINNING_CLAIMANT" \
    --claim-token "$WINNING_TOKEN" \
    --json
assert_project_state "Todo" "P1" "" ""
if ((SUCCESS_COUNT == 1)); then
    pass "overlapping takeovers produced one winner and one CLAIM_STALE loser"
else
    pass "GitHub serialized both takeover commands; the final resolved handle was verified"
fi

step "Validating the live v2 event history"
COMMENTS=$(gh api \
    "repos/$REPOSITORY/issues/$ISSUE_NUMBER/comments?per_page=100" \
    --paginate \
    --slurp)
for event_type in acquired takenOver released overrideReleased; do
    printf '%s\n' "$COMMENTS" |
        jq -e \
            --arg marker "wrighty-claim:v2" \
            --arg event "\"eventType\":\"$event_type\"" \
            'any(.[][]; ((.body // "") | contains($marker)) and ((.body // "") | contains($event)))' \
            >/dev/null ||
        die "live comment history did not contain event type '$event_type'"
done
pass "server-backed history contains acquisition, takeover, release, and override-release events"

printf '\nGitHub claim-fencing live validation passed.\n'
printf 'Repository: %s\n' "$REPOSITORY"
printf 'Project:    %s (#%s)\n' "$PROJECT_TITLE" "$PROJECT_NUMBER"
printf 'Issue:      #%s%s\n' "$ISSUE_NUMBER" \
    "$([[ "$KEEP_ISSUE" == true ]] && printf ' (kept)' || true)"
