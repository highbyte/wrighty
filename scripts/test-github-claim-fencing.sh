#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)
# shellcheck source=scripts/ensure-github-test-repo.sh
source "$SCRIPT_DIR/ensure-github-test-repo.sh"

EXPECTED_PROJECT_TITLE="Wrighty integration fixture"
GITHUB_PROJECTS_REST_API_VERSION="2026-03-10"
CONFIG_PATH="$REPO_ROOT/.wrighty.integration-fixture.json"
BUILD_CONFIGURATION="Debug"
KEEP_ISSUE=false
SKIP_BUILD=false

usage() {
    printf '%s\n' \
        "Usage: scripts/test-github-claim-fencing.sh [options]" \
        "" \
        "Run opt-in, mutating GitHub Project and claim-fencing integration tests through the locally" \
        "built Wrighty CLI. The script requires the configured repository to be private," \
        "writable, and named OWNER/REPO-test. It also requires this exact Project:" \
        "  Project: Wrighty integration fixture" \
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
STATUS_FIELD=$(jq -r '.github.statusField // "Status"' "$CONFIG_PATH")
PRIORITY_FIELD=$(jq -r '.github.priorityField // "Priority"' "$CONFIG_PATH")
EXECUTION_POLICY_FIELD=$(jq -r \
    '.github.executionPolicyField // "Wrighty policy - execution"' "$CONFIG_PATH")
AGENT_POLICY_FIELD=$(jq -r \
    '.github.agentPolicyField // "Wrighty policy - agent"' "$CONFIG_PATH")
CREATION_ATTEMPT_FIELD=$(jq -r \
    '.github.creationAttemptIdField // "Wrighty creation - attempt ID"' "$CONFIG_PATH")
CLAIMANT_TYPE_FIELD=$(jq -r \
    '.github.claimantTypeField // "Wrighty claim - claimant type"' "$CONFIG_PATH")
CLAIMANT_FIELD=$(jq -r \
    '.github.claimantField // "Wrighty claim - claimant"' "$CONFIG_PATH")

[[ "$PROJECT_OWNER" == "${REPOSITORY%%/*}" ]] ||
    die "refusing Project owner '$PROJECT_OWNER'; expected '${REPOSITORY%%/*}'"
[[ "$PROJECT_NUMBER" =~ ^[1-9][0-9]*$ ]] ||
    die "github.projectNumber must be a positive integer"

gh auth status >/dev/null
assert_github_test_repo "$REPOSITORY" >/dev/null ||
    die "refusing repository '$REPOSITORY'; use a private writable repository ending in -test"

PROJECT_REST_PATH=""
PROJECT_JSON=""
for owner_scope in users orgs; do
    candidate_path="$owner_scope/$PROJECT_OWNER/projectsV2/$PROJECT_NUMBER"
    if PROJECT_JSON=$(gh api \
        --header "X-GitHub-Api-Version: $GITHUB_PROJECTS_REST_API_VERSION" \
        "$candidate_path" 2>/dev/null); then
        PROJECT_REST_PATH=$candidate_path
        break
    fi
done
[[ -n "$PROJECT_REST_PATH" ]] ||
    die "Project $PROJECT_OWNER/$PROJECT_NUMBER was not available through the GitHub Projects REST API"
PROJECT_TITLE=$(printf '%s\n' "$PROJECT_JSON" | jq -er .title)
[[ "$PROJECT_TITLE" == "$EXPECTED_PROJECT_TITLE" ]] ||
    die "refusing Project #$PROJECT_NUMBER '$PROJECT_TITLE'; expected '$EXPECTED_PROJECT_TITLE'"

PROJECT_FIELDS=$(gh api \
    --header "X-GitHub-Api-Version: $GITHUB_PROJECTS_REST_API_VERSION" \
    "$PROJECT_REST_PATH/fields?per_page=100" \
    --paginate \
    --slurp |
    jq -ce '[.[][]]')
for required_field in \
    "$STATUS_FIELD" \
    "$PRIORITY_FIELD" \
    "$EXECUTION_POLICY_FIELD" \
    "$AGENT_POLICY_FIELD" \
    "$CREATION_ATTEMPT_FIELD" \
    "$CLAIMANT_TYPE_FIELD" \
    "$CLAIMANT_FIELD"; do
    printf '%s\n' "$PROJECT_FIELDS" |
        jq -e --arg name "$required_field" 'any(.[]; .name == $name)' >/dev/null ||
        die "Project field '$required_field' was not returned by the GitHub Projects REST API"
done
PROJECT_READ_FIELD_IDS=$(printf '%s\n' "$PROJECT_FIELDS" |
    jq -r \
        --arg status "$STATUS_FIELD" \
        --arg priority "$PRIORITY_FIELD" \
        --arg execution "$EXECUTION_POLICY_FIELD" \
        --arg agent "$AGENT_POLICY_FIELD" \
        --arg attempt "$CREATION_ATTEMPT_FIELD" \
        --arg claimantType "$CLAIMANT_TYPE_FIELD" \
        --arg claimant "$CLAIMANT_FIELD" \
        '[.[] |
          select(.name == $status or
                 .name == $priority or
                 .name == $execution or
                 .name == $agent or
                 .name == $attempt or
                 .name == $claimantType or
                 .name == $claimant) |
          .id] | unique | join(",")')

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
        local issue_numbers
        issue_numbers=$(gh issue list \
            --repo "$REPOSITORY" \
            --state all \
            --limit 100 \
            --json number,title \
            --jq ".[] | select(.title == \"$ISSUE_TITLE\") | .number" 2>/dev/null)
        if [[ -z "$issue_numbers" && -n "$ISSUE_NUMBER" ]]; then
            issue_numbers=$ISSUE_NUMBER
        fi
        while IFS= read -r issue_number; do
            [[ -n "$issue_number" ]] || continue
            local actual_title
            actual_title=$(gh issue view "$issue_number" \
                --repo "$REPOSITORY" \
                --json title \
                --jq .title 2>/dev/null)
            if [[ "$actual_title" == "$ISSUE_TITLE" ]]; then
                printf '\n==> Deleting disposable issue #%s\n' "$issue_number"
                gh issue delete "$issue_number" --repo "$REPOSITORY" --yes >/dev/null ||
                    cleanup_status=1
            else
                printf 'warning: refusing to delete issue #%s because its title changed\n' \
                    "$issue_number" >&2
                cleanup_status=1
            fi
        done <<<"$issue_numbers"
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
    items=$(gh api \
        --header "X-GitHub-Api-Version: $GITHUB_PROJECTS_REST_API_VERSION" \
        --method GET \
        "$PROJECT_REST_PATH/items" \
        -f per_page=100 \
        -f q="repo:$REPOSITORY is:issue" \
        -f fields="$PROJECT_READ_FIELD_IDS" \
        --paginate \
        --slurp)
    printf '%s\n' "$items" |
        jq -ec \
            --arg repository "$REPOSITORY" \
            --argjson issue "$ISSUE_NUMBER" \
            '[.[][] |
              select(.content.repository.full_name == $repository and
                     .content.number == $issue)] |
             if length == 1 then .[0] else error("expected exactly one matching Project item") end'
}

project_field_value() {
    local item=$1
    local field_name=$2
    printf '%s\n' "$item" |
        jq -r \
            --arg name "$field_name" \
            '
            def field_text:
                if . == null then ""
                elif type == "string" then .
                elif type == "number" then tostring
                elif type == "object" and (.name? | type) == "string" then .name
                elif type == "object" and (.name? | type) == "object" then (.name.raw // "")
                elif type == "object" and (.raw? | type) == "string" then .raw
                else ""
                end;
            ([.fields[]? | select(.name == $name) | .value | field_text][0] // "")
            '
}

assert_project_state() {
    local expected_status=$1
    local expected_priority=$2
    local expected_kind=$3
    local expected_claimant=$4
    local item
    item=$(project_item)
    assert_equal "$expected_status" \
        "$(project_field_value "$item" "$STATUS_FIELD")" \
        "Project status"
    assert_equal "$expected_priority" \
        "$(project_field_value "$item" "$PRIORITY_FIELD")" \
        "Project priority"
    assert_equal "$expected_kind" \
        "$(project_field_value "$item" "$CLAIMANT_TYPE_FIELD")" \
        "Project claimant type"
    assert_equal "$expected_claimant" \
        "$(project_field_value "$item" "$CLAIMANT_FIELD")" \
        "Project claimant"
}

assert_project_creation_state() {
    local expected_attempt=$1
    local item
    item=$(project_item)
    assert_equal "Automatic allowed" \
        "$(project_field_value "$item" "$EXECUTION_POLICY_FIELD")" \
        "Project execution policy"
    assert_equal "Codex" \
        "$(project_field_value "$item" "$AGENT_POLICY_FIELD")" \
        "Project agent policy"
    assert_equal "$expected_attempt" \
        "$(project_field_value "$item" "$CREATION_ATTEMPT_FIELD")" \
        "Project creation-attempt ID"
}

assert_archive_scope() {
    local expected=$1
    if [[ "$expected" == true ]]; then
        expect_success wrighty_with_cache "$CACHE_A" list --archived --json
    else
        expect_success wrighty_with_cache "$CACHE_A" list --json
    fi
    printf '%s\n' "$LAST_OUTPUT" |
        jq -e --arg id "$ITEM_ID" --argjson archived "$expected" \
            'any(.result[]; .id == $id and .archived == $archived)' >/dev/null ||
        die "work item '$ITEM_ID' was not found with archived=$expected"
}

AGENT_A="agent-a-$RUN_SUFFIX"
HUMAN_B="human-b-$RUN_SUFFIX"
AUTOMATION_C="auto-c-$RUN_SUFFIX"
OTHER_INSTALLATION="agent-d-$RUN_SUFFIX"
HUMAN_E="human-e-$RUN_SUFFIX"
HUMAN_F="human-f-$RUN_SUFFIX"

step "Validating the configured Project schema"
wrighty_with_cache "$CACHE_A" init --config "$CONFIG_PATH" --check >/dev/null
pass "Project #$PROJECT_NUMBER schema is valid"

step "Creating one disposable fixture issue through Wrighty"
expect_success wrighty_with_cache "$CACHE_A" creation-attempt new --json
CREATION_ATTEMPT_ID=$(json_result '.result.creationAttemptId')
expect_success wrighty_with_cache "$CACHE_A" create \
    --title "$ISSUE_TITLE" \
    --body "Disposable live claim-fencing integration item. The test script deletes this issue." \
    --status Todo \
    --priority P1 \
    --auto \
    --agent codex \
    --creation-attempt-id "$CREATION_ATTEMPT_ID" \
    --json
ITEM_ID=$(json_result '.result.id')
ISSUE_URL=$(json_result '.result.url')
ISSUE_NUMBER=${ISSUE_URL##*/}
[[ "$ISSUE_NUMBER" =~ ^[1-9][0-9]*$ ]] ||
    die "GitHub returned an unexpected issue URL '$ISSUE_URL'"
assert_equal "github:$REPOSITORY#$ISSUE_NUMBER" "$ITEM_ID" "created work-item ID"
assert_equal "$CREATION_ATTEMPT_ID" \
    "$(json_result '.result.creationAttemptId')" \
    "reported creation-attempt ID"
assert_equal "created" "$(json_result '.result.disposition')" "initial create disposition"
assert_project_state "Todo" "P1" "" ""
assert_project_creation_state "$CREATION_ATTEMPT_ID"

expect_success wrighty_with_cache "$CACHE_B" create \
    --title "$ISSUE_TITLE" \
    --body "Disposable live claim-fencing integration item. The test script deletes this issue." \
    --status Todo \
    --priority P1 \
    --auto \
    --agent codex \
    --creation-attempt-id "$CREATION_ATTEMPT_ID" \
    --json
assert_equal "$ITEM_ID" "$(json_result '.result.id')" "resumed work-item ID"
assert_equal "resumed" "$(json_result '.result.disposition')" "replayed create disposition"
pass "created $ITEM_ID through REST-backed Project writes and resumed it without duplication"

step "Successful archive and unarchive"
expect_success wrighty_with_cache "$CACHE_A" claim "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --json
ARCHIVE_TOKEN=$(json_result '.result.claimToken')
expect_success wrighty_with_cache "$CACHE_A" archive "$ITEM_ID" \
    --claimant-kind human \
    --claimant-id "$HUMAN_B" \
    --claim-token "$ARCHIVE_TOKEN" \
    --json
assert_equal "true" "$(json_result '.result.archived')" "archive result"
assert_equal "true" "$(json_result '.result.changed')" "archive changed state"
assert_archive_scope true

expect_success wrighty_with_cache "$CACHE_A" unarchive "$ITEM_ID" --json
assert_equal "false" "$(json_result '.result.archived')" "unarchive result"
assert_equal "true" "$(json_result '.result.changed')" "unarchive changed state"
assert_archive_scope false
assert_project_state "Todo" "P1" "" ""
assert_project_creation_state "$CREATION_ATTEMPT_ID"
pass "archive and unarchive preserved Project fields and released the claim"

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

RELEASED_CONCURRENT_WINNER=false
for output in "$OUT_E" "$OUT_F"; do
    candidate_claimant=$(jq -r '.result.claimantId // empty' "$output")
    candidate_token=$(jq -r '.result.claimToken // empty' "$output")
    if [[ -z "$candidate_claimant" || -z "$candidate_token" ]]; then
        continue
    fi

    capture wrighty_with_cache "$CACHE_A" release "$ITEM_ID" \
        --claimant-kind human \
        --claimant-id "$candidate_claimant" \
        --claim-token "$candidate_token" \
        --json
    if ((LAST_STATUS == 0)); then
        printf '%s\n' "$LAST_OUTPUT" |
            jq -e '.schemaVersion == 1 and .result != null' >/dev/null ||
            fail_last "concurrent winner release did not return a versioned result"
        RELEASED_CONCURRENT_WINNER=true
        break
    fi
    [[ "$(printf '%s\n' "$LAST_OUTPUT" | jq -r '.error.code // empty')" == "CLAIM_STALE" ]] ||
        fail_last "successful takeover result could not release or report CLAIM_STALE"
done
[[ "$RELEASED_CONCURRENT_WINNER" == true ]] ||
    die "none of the successful takeover results held the final claim generation"
assert_project_state "Todo" "P1" "" ""
if ((SUCCESS_COUNT == 1)); then
    pass "overlapping takeovers produced one winner and one CLAIM_STALE loser"
else
    pass "GitHub serialized both takeover commands; the final resolved handle was verified"
fi

step "Validating the live v3 event history"
COMMENTS=$(gh api \
    "repos/$REPOSITORY/issues/$ISSUE_NUMBER/comments?per_page=100" \
    --paginate \
    --slurp)
for event_type in acquired takenOver released overrideReleased; do
    printf '%s\n' "$COMMENTS" |
        jq -e \
            --arg marker "wrighty-claim:v3" \
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
