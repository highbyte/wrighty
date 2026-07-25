#!/usr/bin/env bash
#
# Interactive live-provider walkthrough for usage-exhaustion recovery against the GitHub backend.
#
# This is the GitHub counterpart to walkthrough-worker-usage-recovery.sh. It provisions issues and
# Project items only in the dedicated private <owner>/<repo>-test repository, then verifies the
# authoritative issue label, optional Project recovery fields, single handover comment, local
# provider circuit, explicit probe, and retained same-agent retry.
#
# LIVE: this creates real issues and Project values and drives a real vendor CLI. The issues created
# by a successful run are deleted on exit; the private test repository and Project are reused.
# Set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge those mutations.

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
SKIP_PROBE=false
ASSUME_AGENT="claude"
RETRY_MINUTES=130
MAX_ATTEMPTS=5
RESUME_MODE=""
SOURCE_REPO=""
PROJECT_TITLE="Wrighty usage recovery walkthrough (current schema)"

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-worker-usage-recovery-github.sh [options]" \
        "" \
        "Guided live test of GitHub-native usage recovery on a dedicated private" \
        "<owner>/<repo>-test repository. The selected agent must initially be usage-limited." \
        "Set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge live mutations." \
        "" \
        "Options:" \
        "  --agent NAME             Exhausted vendor: claude, codex, or copilot (default: claude)." \
        "  --source-repo OWNER/REPO Source used to derive the -test repo (default: current gh repo)." \
        "  --project-title TITLE    Project to create/reuse (default: '$PROJECT_TITLE')." \
        "  --retry-minutes N        Fallback delay when no exact reset is parsed (default: 130)." \
        "  --max-attempts N         Bounded retry attempts (default: 5)." \
        "  --resume-mode MODE       manual (override timer) or automatic (wait until due)." \
        "                           Prompted interactively when omitted." \
        "  --skip-probe             Skip the explicit provider-capacity probe checkpoint." \
        "  --configuration NAME     Build configuration; defaults to Debug." \
        "  --skip-build             Use the existing local build output." \
        "  --keep-fixture           Keep the temporary clone and created issues." \
        "  -h, --help               Show this help."
    return 0
}

while (($# > 0)); do
    case "$1" in
        --agent) (($# >= 2)) || die "--agent requires a value"; ASSUME_AGENT=$2; shift 2 ;;
        --source-repo) (($# >= 2)) || die "--source-repo requires OWNER/REPO"; SOURCE_REPO=$2; shift 2 ;;
        --project-title) (($# >= 2)) || die "--project-title requires a title"; PROJECT_TITLE=$2; shift 2 ;;
        --retry-minutes) (($# >= 2)) || die "--retry-minutes requires a value"; RETRY_MINUTES=$2; shift 2 ;;
        --max-attempts) (($# >= 2)) || die "--max-attempts requires a value"; MAX_ATTEMPTS=$2; shift 2 ;;
        --resume-mode) (($# >= 2)) || die "--resume-mode requires a value"; RESUME_MODE=$2; shift 2 ;;
        --skip-probe) SKIP_PROBE=true; shift ;;
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

[[ "${WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE:-}" == "1" ]] ||
    die "set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge real issues, Project values, and live provider requests on <owner>/<repo>-test"
[[ "$RETRY_MINUTES" =~ ^[0-9]+$ ]] &&
    ((RETRY_MINUTES >= 1 && RETRY_MINUTES <= 1440)) ||
    die "--retry-minutes must be an integer from 1 through 1440"
[[ "$MAX_ATTEMPTS" =~ ^[0-9]+$ ]] &&
    ((MAX_ATTEMPTS >= 1 && MAX_ATTEMPTS <= 20)) ||
    die "--max-attempts must be an integer from 1 through 20"
case "$RESUME_MODE" in
    "" | manual | automatic) ;;
    *) die "--resume-mode must be manual or automatic" ;;
esac

require_command dotnet
require_command git
require_command jq
require_command gh
gh auth status >/dev/null 2>&1 || die "gh is not authenticated; run 'gh auth login'"
wt_resolve_agent "$ASSUME_AGENT"
require_command "$ASSUME_AGENT"
begin_walkthrough

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
ACTIVATE_SCRIPT="$REPO_ROOT/scripts/activate-development-cli.sh"

wt_build_cli "$CLI_PROJECT" "$CLI_DLL" "$SKIP_BUILD" "$BUILD_CONFIGURATION"

[[ -n "$SOURCE_REPO" ]] || SOURCE_REPO=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
    die "could not determine the current gh repository; pass --source-repo OWNER/REPO"
step "Ensuring the private integration-test repository exists"
TEST_REPO=$(ensure_github_test_repo "$SOURCE_REPO") ||
    die "could not ensure the private <owner>/<repo>-test repository"
explain "Test repository: $TEST_REPO"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-usage-recovery-walkthrough-gh.XXXXXX")
FIXTURE_REPO="$RUN_ROOT/repo"
WORKTREE_ROOT="$RUN_ROOT/worktrees"
CREATED_ITEMS=()
RUN_TAG="$(date -u +%Y%m%dT%H%M%SZ)-$$"

cleanup() {
    local status=$?
    if [[ "$KEEP_FIXTURE" == true || $status -ne 0 || $FAIL_COUNT -gt 0 ]]; then
        printf '\nfixture clone kept for inspection at %s\n' "$RUN_ROOT"
        printf 'created issues left in place on %s: %s\n' "$TEST_REPO" "${CREATED_ITEMS[*]:-none}"
        return $status
    fi

    local item number
    for item in "${CREATED_ITEMS[@]:-}"; do
        [[ -n "$item" ]] || continue
        number=${item##*\#}
        [[ "$number" =~ ^[0-9]+$ ]] || continue
        gh issue delete "$number" --repo "$TEST_REPO" --yes >/dev/null 2>&1 || true
    done
    [[ "$RUN_ROOT" == *wrighty-usage-recovery-walkthrough-gh.* ]] && rm -rf "$RUN_ROOT"
    return $status
}
trap cleanup EXIT

step "Cloning $TEST_REPO"
gh repo clone "$TEST_REPO" "$FIXTURE_REPO" -- -q || die "failed to clone $TEST_REPO"
(
    cd "$FIXTURE_REPO"
    git config user.name "Wrighty walkthrough"
    git config user.email "walkthrough@example.invalid"
    if ! git rev-parse --verify HEAD >/dev/null 2>&1; then
        git switch -c main 2>/dev/null || git checkout -b main
        printf '# Usage recovery walkthrough\n\nDisposable live GitHub integration fixture.\n' >README.md
        git add README.md
        git commit -q -m "Initialize usage recovery walkthrough fixture"
        git push -q -u origin main
    fi
) || die "failed to prepare the fixture clone"

step "Initializing the GitHub tracker on $TEST_REPO"
INIT_OUTPUT=$(wr init --backend github --repository "$TEST_REPO" \
    --project-title "$PROJECT_TITLE" --skip-issue-forms --yes 2>&1)
INIT_STATUS=$?
if ((INIT_STATUS != 0)); then
    printf '%s\n' "$INIT_OUTPUT" >&2
    die "wrighty init (github) failed for $TEST_REPO"
fi

write_usage_config() {
    local cfg="$FIXTURE_REPO/.wrighty.json" tmp
    tmp=$(mktemp) || die "mktemp failed"
    jq \
        --arg worktrees "$WORKTREE_ROOT" \
        --arg agent "$ASSUME_AGENT" \
        --argjson retryMinutes "$RETRY_MINUTES" \
        --argjson maxAttempts "$MAX_ATTEMPTS" \
        '
        .worker = {
          defaultAgent: $agent,
          worktreeRoot: $worktrees,
          branchFormat: "wrighty-worker/{id}-{unique}",
          handoverComment: "full",
          shareLocalPaths: false,
          completion: {commit: "inspect", integration: "merge-local"},
          usageFailure: {
            action: "retry",
            initialRetryMinutes: $retryMinutes,
            backoffMultiplier: 2,
            maxRetryHours: 24,
            maxAttempts: $maxAttempts,
            resetGraceMinutes: 2
          }
        }
        | .archive = {onStatuses: []}
        ' "$cfg" >"$tmp" 2>/dev/null && mv "$tmp" "$cfg" ||
        { rm -f "$tmp"; die "failed to write the GitHub usage-recovery configuration"; }
    return 0
}
write_usage_config

wt_install_and_commit_skill
explain "The skill fixture commit stays in the temporary clone; the recovery walkthrough does not push worker branches."

PROJECT_NUMBER=$(jq -er '.github.projectNumber' "$FIXTURE_REPO/.wrighty.json") ||
    die "GitHub configuration has no project number"
DISPATCH_STATE_FIELD=$(jq -r '.github.dispatchStateField // "Wrighty dispatch - state"' "$FIXTURE_REPO/.wrighty.json")
DISPATCH_NOT_BEFORE_FIELD=$(jq -r '.github.dispatchNotBeforeField // "Wrighty dispatch - not before"' "$FIXTURE_REPO/.wrighty.json")
DISPATCH_AGENT_FIELD=$(jq -r '.github.dispatchAgentField // "Wrighty dispatch - agent"' "$FIXTURE_REPO/.wrighty.json")
DISPATCH_DETAIL_FIELD=$(jq -r '.github.dispatchDetailField // "Wrighty dispatch - detail"' "$FIXTURE_REPO/.wrighty.json")

load_detail() {
    wr get "$ITEM_USAGE" --json 2>/dev/null
    return $?
}

wait_for_worker_result() {
    local previous_ended_at=$1 deadline detail claim_state ended_at announced=false
    deadline=$((SECONDS + 600))
    while ((SECONDS < deadline)); do
        detail=$(load_detail) || {
            sleep 2
            continue
        }
        claim_state=$(printf '%s' "$detail" | jq -r '.result.claim.state')
        ended_at=$(printf '%s' "$detail" | jq -r '.result.session.lastRun.endedAt // ""')
        if [[ "$claim_state" == "Unclaimed" &&
            -n "$ended_at" &&
            "$ended_at" != "$previous_ended_at" ]]; then
            return 0
        fi
        if [[ "$announced" == false ]]; then
            note "the checkpoint arrived before the second-terminal worker finished; waiting for its claim and run outcome to settle"
            announced=true
        fi
        sleep 2
    done
    fail "the second-terminal worker did not finish within 10 minutes"
    return 1
}

show_detail_on_failure() {
    printf '\nCurrent item state:\n'
    wr get "$ITEM_USAGE" || true
    printf '\nWorker status:\n'
    wr status || true
    return 0
}

github_issue_number() {
    printf '%s\n' "${1##*\#}"
    return 0
}

github_issue_json() {
    local item=$1
    gh issue view "$(github_issue_number "$item")" --repo "$TEST_REPO" \
        --json url,title,labels,state 2>/dev/null
    return $?
}

github_project_fields() {
    local item=$1 owner repo issue_number
    owner=${TEST_REPO%%/*}
    repo=${TEST_REPO#*/}
    issue_number=$(github_issue_number "$item")
    gh api graphql \
        -F owner="$owner" \
        -F name="$repo" \
        -F issueNumber="$issue_number" \
        -f query='
          query($owner: String!, $name: String!, $issueNumber: Int!) {
            repository(owner: $owner, name: $name) {
              issue(number: $issueNumber) {
                projectItems(first: 20) {
                  nodes {
                    project { number }
                    fieldValues(first: 100) {
                      nodes {
                        __typename
                        ... on ProjectV2ItemFieldSingleSelectValue {
                          name
                          field { ... on ProjectV2SingleSelectField { name } }
                        }
                        ... on ProjectV2ItemFieldTextValue {
                          text
                          field { ... on ProjectV2Field { name } }
                        }
                      }
                    }
                  }
                }
              }
            }
          }' 2>/dev/null |
        jq -cer --argjson project "$PROJECT_NUMBER" '
            .data.repository.issue.projectItems.nodes[]
            | select(.project.number == $project)
            | [.fieldValues.nodes[]
               | if .__typename == "ProjectV2ItemFieldSingleSelectValue" and .field.name != null
                 then {key: .field.name, value: .name}
                 elif .__typename == "ProjectV2ItemFieldTextValue" and .field.name != null
                 then {key: .field.name, value: .text}
                 else empty end]
            | from_entries
        ' | head -n1
    return $?
}

github_handover_comments() {
    local item=$1
    gh api "repos/$TEST_REPO/issues/$(github_issue_number "$item")/comments" 2>/dev/null |
        jq -c '[.[] | select(.body | contains("<!-- wrighty-handover:v1 -->"))]'
    return $?
}

verify_scheduled_state() {
    local detail=$1 issue fields comments comment count expected_agent
    if ! printf '%s' "$detail" | jq -e --arg agent "$ASSUME_AGENT" '
        (.result.pendingDispatch.state == "retry-scheduled") and
        (.result.operationalStatus == "retry-scheduled") and
        (.result.claim.state == "Unclaimed") and
        (.result.session.agent == $agent) and
        (.result.session.available == true) and
        (.result.session.lastRun.outcome == "failed") and
        (.result.session.lastRun.failure.kind == "usage-exhausted"
          or .result.session.lastRun.failure.kind == "rate-limited") and
        (.result.session.lastRun.failure.isRetryable == true) and
        (.result.pendingDispatch.attempt >= 1) and
        (.result.pendingDispatch.maxAttempts >= .result.pendingDispatch.attempt) and
        (.result.pendingDispatch.fromCurrentInstallation == true)
        ' >/dev/null 2>&1; then
        fail "the live provider stop was not projected as a resumable retry-scheduled failure"
        return 1
    fi

    wr status --json 2>/dev/null |
        jq -e --arg id "$ITEM_USAGE" 'any(.result.retries[]?; .id == $id)' \
            >/dev/null 2>&1 &&
        pass "wrighty status groups the GitHub item under scheduled retries" ||
        { fail "wrighty status did not expose the scheduled retry"; return 1; }

    issue=$(github_issue_json "$ITEM_USAGE") || {
        fail "could not read the GitHub issue"
        return 1
    }
    printf '%s' "$issue" | jq -e '
        any(.labels[]?; .name == "wrighty:dispatch-state=retry-scheduled")
        ' >/dev/null 2>&1 &&
        pass "the authoritative GitHub issue label is retry-scheduled" ||
        { fail "the authoritative retry-scheduled issue label is missing"; return 1; }

    fields=$(github_project_fields "$ITEM_USAGE") || {
        fail "could not read the Project recovery fields"
        return 1
    }
    expected_agent="$(tr '[:lower:]' '[:upper:]' <<<"${ASSUME_AGENT:0:1}")${ASSUME_AGENT:1}"
    printf '%s' "$fields" | jq -e \
        --arg dispatchState "$DISPATCH_STATE_FIELD" \
        --arg notBeforeField "$DISPATCH_NOT_BEFORE_FIELD" \
        --arg dispatchAgent "$DISPATCH_AGENT_FIELD" \
        --arg dispatchDetail "$DISPATCH_DETAIL_FIELD" \
        --arg notBefore "$(printf '%s' "$detail" | jq -r '.result.pendingDispatch.notBefore')" \
        --arg agent "$expected_agent" \
        --arg attempt "$(printf '%s' "$detail" | jq -r '.result.pendingDispatch.attempt')" \
        --arg max "$(printf '%s' "$detail" | jq -r '.result.pendingDispatch.maxAttempts')" '
        def normalize_iso8601:
          capture("^(?<whole>[^.]+)(?:[.](?<fraction>[0-9]+))?(?<offset>Z|[+-][0-9]{2}:[0-9]{2})$")
          | .fraction = ((.fraction // "") | sub("0+$"; ""))
          | .whole
            + (if .fraction == "" then "" else "." + .fraction end)
            + (if .offset == "Z" then "+00:00" else .offset end);
        (.[$dispatchState] == "Retry scheduled") and
        ((.[$notBeforeField] | normalize_iso8601) == ($notBefore | normalize_iso8601)) and
        (.[$dispatchAgent] == $agent) and
        (.[$dispatchDetail] | contains("attempt \($attempt) of \($max)"))
        ' >/dev/null 2>&1 &&
        pass "all four display-only Project recovery fields match the local dispatch" ||
        { fail "the Project recovery projection is missing or inconsistent: $fields"; return 1; }

    comments=$(github_handover_comments "$ITEM_USAGE") || {
        fail "could not read the GitHub handover comment"
        return 1
    }
    count=$(printf '%s' "$comments" | jq 'length')
    [[ "$count" == "1" ]] ||
        { fail "expected one marker-identified handover comment, found $count"; return 1; }
    comment=$(printf '%s' "$comments" | jq -r '.[0].body')
    if ! grep -Fq "### Wrighty handover — retry scheduled" <<<"$comment" ||
        ! grep -Fq "**Recovery decision**" <<<"$comment" ||
        ! grep -Fq "**Provider capacity**" <<<"$comment" ||
        ! grep -Fq "**Worker policy**" <<<"$comment" ||
        ! grep -Fq "wrighty provider probe $ASSUME_AGENT" <<<"$comment" ||
        ! grep -Fq "wrighty worker --item $ITEM_USAGE --yes" <<<"$comment"; then
        fail "the single handover comment is missing recovery, provider, policy, or action content"
        return 1
    fi
    pass "the single GitHub handover comment exposes sanitized probe and retry-now actions"
    pass "the retained session, attempt bound, timer, and installation ownership are visible in get"
    return 0
}

verify_pre_due_skip() {
    local before=$1 output rc after fresh
    output=$(wr worker --once --agent "$ASSUME_AGENT" --yes --json)
    rc=$?
    if ((rc != 0)); then
        fail "the pre-due worker check exited with status $rc"
        return 1
    fi
    if printf '%s\n' "$output" | jq -s -e '
        any(.[]; .type == "started" or .type == "resumed" or .type == "retry-started")
        ' >/dev/null 2>&1; then
        fail "a normal worker spawned the provider before the retry was due"
        return 1
    fi
    if ! printf '%s\n' "$output" | jq -s -e '
        any(.[]; .type == "provider-unavailable"
          and .providerCapacity.state == "unavailable-until")
        ' >/dev/null 2>&1; then
        fail "the normal worker did not report the open provider circuit"
        return 1
    fi
    after=$(load_detail) || return 1
    jq -n -e --argjson before "$before" --argjson after "$after" '
        ($after.result.pendingDispatch.state == "retry-scheduled") and
        ($after.result.pendingDispatch.attempt == $before.result.pendingDispatch.attempt) and
        ($after.result.pendingDispatch.notBefore == $before.result.pendingDispatch.notBefore)
        ' >/dev/null 2>&1 ||
        { fail "the pre-due worker changed the deferred retry"; return 1; }
    fresh=$(wr get "$ITEM_FRESH" --json 2>/dev/null) || return 1
    printf '%s' "$fresh" | jq -e '
        (.result.status == "Todo") and
        (.result.operationalStatus == "ready") and
        (.result.claim.state == "Unclaimed") and
        (.result.hasRecordedWorktree == false) and
        ((.result.session == null) or (.result.session.available == false))
        ' >/dev/null 2>&1 ||
        { fail "the provider circuit did not leave fresh GitHub work untouched"; return 1; }
    pass "the open provider circuit skipped fresh GitHub work before claim, workspace, or spawn"
    pass "the original future retry and Project presentation remained unchanged"
    return 0
}

verify_probe_did_not_mutate_item() {
    local before=$1 after provider before_attempt after_attempt before_not_before after_not_before
    after=$(load_detail) || return 1
    if ! jq -n -e --argjson before "$before" --argjson after "$after" '
        ($after.result.pendingDispatch.state == $before.result.pendingDispatch.state) and
        ($after.result.pendingDispatch.attempt == $before.result.pendingDispatch.attempt) and
        ($after.result.pendingDispatch.notBefore == $before.result.pendingDispatch.notBefore) and
        ($after.result.claim.state == "Unclaimed")
        ' >/dev/null 2>&1; then
        before_attempt=$(printf '%s' "$before" | jq -r '.result.pendingDispatch.attempt // "none"')
        after_attempt=$(printf '%s' "$after" | jq -r '.result.pendingDispatch.attempt // "none"')
        before_not_before=$(printf '%s' "$before" |
            jq -r '.result.pendingDispatch.notBefore // "none"')
        after_not_before=$(printf '%s' "$after" |
            jq -r '.result.pendingDispatch.notBefore // "none"')
        if [[ "$before_attempt" != "$after_attempt" ||
            "$before_not_before" != "$after_not_before" ]]; then
            fail "the scheduled item advanced while waiting for the probe (attempt " \
                "$before_attempt -> $after_attempt; not-before $before_not_before -> " \
                "$after_not_before). An item worker/retry command ran; a provider probe alone " \
                "cannot advance an item."
        else
            fail "the scheduled item or claim changed while waiting for the provider probe"
        fi
        return 1
    fi

    provider=$(wr status --json 2>/dev/null |
        jq -c --arg agent "$ASSUME_AGENT" \
            '.result.providerCapacity[]? | select(.agent == $agent)' | head -n1)
    if [[ -n "$provider" ]]; then
        pass "the explicit probe left or extended the installation-local provider circuit"
        explain "Probe result: $(printf '%s' "$provider" | jq -r '.state')"
    else
        pass "the explicit probe made provider capacity available"
    fi
    pass "the provider probe did not claim or mutate the GitHub item"
    return 0
}

verify_completed_state() {
    local detail=$1 issue fields comments provider
    printf '%s' "$detail" | jq -e '
        (.result.status == "Done") and
        (.result.operationalStatus == "completed") and
        (.result.claim.state == "Unclaimed") and
        (.result.pendingDispatch == null) and
        (.result.session.lastRun.outcome == "succeeded")
        ' >/dev/null 2>&1 || return 1

    issue=$(github_issue_json "$ITEM_USAGE") || return 1
    printf '%s' "$issue" | jq -e '
        all(.labels[]?; (.name | startswith("wrighty:dispatch-state=") | not))
        ' >/dev/null 2>&1 || return 1

    fields=$(github_project_fields "$ITEM_USAGE") || return 1
    printf '%s' "$fields" | jq -e \
        --arg dispatchState "$DISPATCH_STATE_FIELD" \
        --arg notBefore "$DISPATCH_NOT_BEFORE_FIELD" \
        --arg dispatchAgent "$DISPATCH_AGENT_FIELD" \
        --arg dispatchDetail "$DISPATCH_DETAIL_FIELD" '
        (has($dispatchState) | not) and
        (has($notBefore) | not) and
        (has($dispatchAgent) | not) and
        (has($dispatchDetail) | not)
        ' >/dev/null 2>&1 || return 1

    comments=$(github_handover_comments "$ITEM_USAGE") || return 1
    [[ "$(printf '%s' "$comments" | jq 'length')" == "1" ]] || return 1
    printf '%s' "$comments" | jq -er '.[0].body' |
        grep -Fq "### Wrighty handover — completed" || return 1

    provider=$(wr status --json 2>/dev/null |
        jq -c --arg agent "$ASSUME_AGENT" \
            '.result.providerCapacity[]? | select(.agent == $agent)' | head -n1)
    [[ -z "$provider" ]] || return 1

    pass "the retained vendor session completed after provider capacity returned"
    pass "successful recovery cleared the issue label and all Project recovery fields"
    pass "the single handover comment was updated in place to the completed phase"
    pass "successful execution closed the installation-local provider circuit"
    return 0
}

verify_attempt_limit_state() {
    local detail=$1 issue fields comments comment fresh
    printf '%s' "$detail" | jq -e --arg agent "$ASSUME_AGENT" '
        (.result.pendingDispatch.state == "needs-attention") and
        (.result.operationalStatus == "needs-attention") and
        (.result.claim.state == "Unclaimed") and
        (.result.session.agent == $agent) and
        (.result.session.available == true) and
        (.result.session.lastRun.outcome == "failed") and
        (.result.session.lastRun.failure.kind == "usage-exhausted"
          or .result.session.lastRun.failure.kind == "rate-limited")
        ' >/dev/null 2>&1 || return 1

    issue=$(github_issue_json "$ITEM_USAGE") || return 1
    printf '%s' "$issue" | jq -e '
        any(.labels[]?; .name == "wrighty:dispatch-state=needs-attention") and
        all(.labels[]?; .name != "wrighty:dispatch-state=retry-scheduled")
        ' >/dev/null 2>&1 || return 1

    fields=$(github_project_fields "$ITEM_USAGE") || return 1
    printf '%s' "$fields" | jq -e \
        --arg dispatchState "$DISPATCH_STATE_FIELD" \
        --arg notBefore "$DISPATCH_NOT_BEFORE_FIELD" \
        --arg dispatchAgent "$DISPATCH_AGENT_FIELD" \
        --arg dispatchDetail "$DISPATCH_DETAIL_FIELD" '
        (.[$dispatchState] == "Needs attention") and
        (has($notBefore) | not) and
        (has($dispatchAgent) | not) and
        (has($dispatchDetail) | not)
        ' >/dev/null 2>&1 || return 1

    comments=$(github_handover_comments "$ITEM_USAGE") || return 1
    [[ "$(printf '%s' "$comments" | jq 'length')" == "1" ]] || return 1
    comment=$(printf '%s' "$comments" | jq -er '.[0].body') || return 1
    if ! grep -Fq "### Wrighty handover — needs attention" <<<"$comment" ||
        ! grep -Fq "**Provider capacity**" <<<"$comment" ||
        ! grep -Fq "**Worker policy**" <<<"$comment" ||
        ! grep -Fq "wrighty edit $ITEM_USAGE --takeover" <<<"$comment" ||
        ! grep -Fq "wrighty worker --item $ITEM_USAGE --yes" <<<"$comment"; then
        return 1
    fi

    fresh=$(wr get "$ITEM_FRESH" --json 2>/dev/null) || return 1
    printf '%s' "$fresh" | jq -e '
        (.result.status == "Todo") and
        (.result.operationalStatus == "ready") and
        (.result.claim.state == "Unclaimed") and
        (.result.hasRecordedWorktree == false) and
        ((.result.session == null) or (.result.session.available == false))
        ' >/dev/null 2>&1 || return 1

    pass "the final configured retry transitioned the item to needs-attention"
    pass "the authoritative issue label and Project activity show the bounded stop"
    pass "the single handover comment was updated in place with operator actions"
    pass "the retained session remains resumable and fresh work remains untouched"
    return 0
}

step "Provisioning GitHub usage-recovery items"
ITEM_USAGE=$(create_item "[$RUN_TAG] Complete the usage recovery probe" \
    "Create RECOVERED.md in the repository root with one line saying that the retained session recovered, then finish this item.") ||
    die "could not create the GitHub usage-recovery item"
CREATED_ITEMS+=("$ITEM_USAGE")
pass "created live recovery item $ITEM_USAGE"

step "Start the provider-limited run"
explain "Run this while $ASSUME_AGENT is still reporting exhausted usage."
manual \
    "cd '$FIXTURE_REPO'" \
    "source '$ACTIVATE_SCRIPT'" \
    "wrighty worker --item '$ITEM_USAGE' --agent '$ASSUME_AGENT' --workspace-mode worktree --once --yes --json" \
    "" \
    "The final event should be retry-scheduled, not a generic failed event."
pause

wait_for_worker_result "" || {
    show_detail_on_failure
    die "the initial worker command did not settle; the fixture has been preserved"
}
DETAIL=$(load_detail) || die "could not read $ITEM_USAGE after the live worker run"
if ! verify_scheduled_state "$DETAIL"; then
    show_detail_on_failure
    die "the initial GitHub recovery presentation check failed; the fixture has been preserved"
fi

NOT_BEFORE=$(printf '%s' "$DETAIL" | jq -r '.result.pendingDispatch.notBefore')
ATTEMPT=$(printf '%s' "$DETAIL" | jq -r '.result.pendingDispatch.attempt')
FAILURE_KIND=$(printf '%s' "$DETAIL" | jq -r '.result.session.lastRun.failure.kind')
FAILURE_CONFIDENCE=$(printf '%s' "$DETAIL" | jq -r '.result.session.lastRun.failure.confidence')

ITEM_FRESH=$(create_item "[$RUN_TAG] Do not start while provider usage is exhausted" \
    "This item verifies the provider circuit breaker. Leave it untouched during this walkthrough.") ||
    die "could not create the fresh GitHub provider-circuit item"
CREATED_ITEMS+=("$ITEM_FRESH")
pass "created fresh circuit-breaker item $ITEM_FRESH"

step "Observed deferred GitHub recovery"
explain "Failure: $FAILURE_KIND ($FAILURE_CONFIDENCE)"
explain "Attempt: $ATTEMPT of $MAX_ATTEMPTS"
explain "Not before: $NOT_BEFORE"
verify_pre_due_skip "$DETAIL" || {
    show_detail_on_failure
    die "the pre-due GitHub circuit check failed; the fixture has been preserved"
}

if [[ "$SKIP_PROBE" == false ]]; then
    step "Probe provider capacity without touching the item"
    explain "This starts one bounded $ASSUME_AGENT request and may consume subscription usage."
    manual \
        "cd '$FIXTURE_REPO'" \
        "source '$ACTIVATE_SCRIPT'" \
        "wrighty provider probe '$ASSUME_AGENT' --yes --json" \
        "" \
        "A still-limited result extends the circuit; a successful result closes it." \
        "Neither result may claim or change $ITEM_USAGE."
    pause
    verify_probe_did_not_mutate_item "$DETAIL" || {
        show_detail_on_failure
        die "the explicit provider probe check failed; the fixture has been preserved"
    }
    DETAIL=$(load_detail) || die "could not reload $ITEM_USAGE after the probe"
fi

ISSUE_URL=$(github_issue_json "$ITEM_USAGE" | jq -r '.url')
step "Inspect the native GitHub and CLI recovery surfaces"
manual \
    "open '$ISSUE_URL'" \
    "cd '$FIXTURE_REPO'" \
    "source '$ACTIVATE_SCRIPT'" \
    "wrighty get '$ITEM_USAGE'" \
    "wrighty status" \
    "wrighty list" \
    "" \
    "On GitHub, inspect the retry-scheduled label, the four Project recovery fields," \
    "and the single Wrighty handover comment. Do not edit the display-only fields or label."
pause

if [[ -z "$RESUME_MODE" ]]; then
    printf '\n%sHow should the recovery be tested?%s [manual/automatic] (default manual): ' \
        "$C_BOLD" "$C_RESET"
    IFS= read -r RESUME_MODE </dev/tty ||
        die "the interactive walkthrough requires a controlling terminal"
    [[ -z "$RESUME_MODE" ]] && RESUME_MODE="manual"
    case "$RESUME_MODE" in
        manual | automatic) ;;
        *) die "recovery mode must be manual or automatic" ;;
    esac
fi

RECOVERY_OUTCOME=""
while true; do
    PREVIOUS_ENDED_AT=$(printf '%s' "$DETAIL" |
        jq -r '.result.session.lastRun.endedAt // ""')
    CHECKPOINT_MODE="$RESUME_MODE"
    step "Resume after provider capacity returns"
    if [[ "$RESUME_MODE" == "automatic" ]]; then
        explain "Wait until both the provider reset and the recorded not-before time:"
        explain "$NOT_BEFORE"
        manual \
            "cd '$FIXTURE_REPO'" \
            "source '$ACTIVATE_SCRIPT'" \
            "wrighty worker --once --yes --json" \
            "" \
            "This omits --item so normal due-retry selection must reacquire the GitHub item."
    else
        explain "Wait until provider capacity returns. This explicit action may run before the timer."
        manual \
            "cd '$FIXTURE_REPO'" \
            "source '$ACTIVATE_SCRIPT'" \
            "wrighty worker --item '$ITEM_USAGE' --once --yes --json" \
            "" \
            "This tests the retry-now override and resumes the retained same-agent session."
    fi
    pause

    if [[ "$CHECKPOINT_MODE" == "automatic" ]]; then
        CHECKPOINT_DETAIL=$(load_detail) ||
            die "could not read $ITEM_USAGE after the automatic recovery checkpoint"
        CURRENT_ENDED_AT=$(printf '%s' "$CHECKPOINT_DETAIL" |
            jq -r '.result.session.lastRun.endedAt // ""')
        CURRENT_CLAIM_STATE=$(printf '%s' "$CHECKPOINT_DETAIL" |
            jq -r '.result.claim.state')
        if [[ "$CURRENT_CLAIM_STATE" == "Unclaimed" &&
            "$CURRENT_ENDED_AT" == "$PREVIOUS_ENDED_AT" ]]; then
            DETAIL="$CHECKPOINT_DETAIL"
            note "no due retry ran; the item remains scheduled for $NOT_BEFORE"
            explain "Wait until that time, or restart with --resume-mode manual to test retry-now."
            continue
        fi
    fi

    wait_for_worker_result "$PREVIOUS_ENDED_AT" || {
        show_detail_on_failure
        die "the recovery worker command did not settle; the fixture has been preserved"
    }
    DETAIL=$(load_detail) || die "could not read $ITEM_USAGE after the recovery attempt"
    if verify_completed_state "$DETAIL"; then
        RECOVERY_OUTCOME="completed"
        break
    fi

    if verify_attempt_limit_state "$DETAIL"; then
        RECOVERY_OUTCOME="attempt-limit"
        break
    fi

    if printf '%s' "$DETAIL" | jq -e '
        .result.pendingDispatch.state == "retry-scheduled" and
        (.result.session.lastRun.failure.kind == "usage-exhausted"
          or .result.session.lastRun.failure.kind == "rate-limited")
        ' >/dev/null 2>&1; then
        ATTEMPT=$(printf '%s' "$DETAIL" | jq -r '.result.pendingDispatch.attempt')
        NOT_BEFORE=$(printf '%s' "$DETAIL" | jq -r '.result.pendingDispatch.notBefore')
        note "$ASSUME_AGENT still reported no capacity; Wrighty safely rescheduled attempt $ATTEMPT"
        explain "Next not-before time: $NOT_BEFORE"
        if ((ATTEMPT == MAX_ATTEMPTS)); then
            note "retry $ATTEMPT is the final scheduled attempt and has not run yet"
            explain "One more capacity failure must stop automatic recovery at needs-attention."
        fi
        RESUME_MODE="manual"
        continue
    fi

    fail "the post-reset run neither completed nor returned to retry-scheduled"
    show_detail_on_failure
    die "live GitHub recovery verification failed; the fixture has been preserved"
done

step "GitHub usage-recovery walkthrough complete"
explain "Passed checks: $PASS_COUNT"
if [[ "$RECOVERY_OUTCOME" == "attempt-limit" ]]; then
    explain "Outcome: provider capacity stayed unavailable through all $MAX_ATTEMPTS retries."
    explain "Wrighty stopped automatic recovery at needs-attention with the session retained."
else
    explain "Outcome: provider capacity returned and the retained session completed."
fi
explain "Created issues will be deleted unless --keep-fixture was supplied."

if ((FAIL_COUNT > 0)); then exit 1; fi
