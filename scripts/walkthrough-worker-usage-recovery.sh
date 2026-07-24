#!/usr/bin/env bash
#
# Interactive live-provider walkthrough for usage-exhaustion recovery.
#
# This is intentionally separate from the worker-completion walkthroughs. It needs a real
# temporarily exhausted vendor account, spans the provider reset window, and verifies the
# retry-scheduled state before allowing the retained session to continue.
#
# The script provisions only a disposable Local Markdown repository. You run the two live
# `wrighty worker` commands in a second terminal; this process remains open to verify the
# durable state between them. No GitHub resource is used.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# shellcheck source=scripts/walkthrough-lib.sh
source "$SCRIPT_DIR/walkthrough-lib.sh"

BUILD_CONFIGURATION="Debug"
SKIP_BUILD=false
KEEP_FIXTURE=false
ASSUME_AGENT="claude"
RETRY_MINUTES=130
MAX_ATTEMPTS=5
RESUME_MODE=""

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-worker-usage-recovery.sh [options]" \
        "" \
        "Guided live test of usage-exhaustion classification, deferred retry, and same-agent" \
        "session recovery in a disposable Local Markdown repository." \
        "" \
        "Start this while the selected agent account is currently usage-limited. The first" \
        "worker run must encounter that real limit; after the provider reset, a second run" \
        "resumes the retained vendor session and completes a tiny fixture item." \
        "" \
        "Options:" \
        "  --agent NAME            Exhausted vendor: claude, codex, or copilot (default: claude)." \
        "  --retry-minutes N       Fallback delay when no exact reset is parsed (default: 130)." \
        "  --max-attempts N        Bounded retry attempts (default: 5)." \
        "  --resume-mode MODE      manual (override the timer) or automatic (wait until due)." \
        "                          Prompted interactively when omitted." \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-fixture          Keep the temporary repository and worktree on success." \
        "  -h, --help              Show this help."
}

while (($# > 0)); do
    case "$1" in
        --agent) (($# >= 2)) || die "--agent requires a value"; ASSUME_AGENT=$2; shift 2 ;;
        --retry-minutes) (($# >= 2)) || die "--retry-minutes requires a value"; RETRY_MINUTES=$2; shift 2 ;;
        --max-attempts) (($# >= 2)) || die "--max-attempts requires a value"; MAX_ATTEMPTS=$2; shift 2 ;;
        --resume-mode) (($# >= 2)) || die "--resume-mode requires a value"; RESUME_MODE=$2; shift 2 ;;
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

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
require_command rg
wt_resolve_agent "$ASSUME_AGENT"
require_command "$ASSUME_AGENT"

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
ACTIVATE_SCRIPT="$REPO_ROOT/scripts/activate-development-cli.sh"

wt_build_cli "$CLI_PROJECT" "$CLI_DLL" "$SKIP_BUILD" "$BUILD_CONFIGURATION"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-usage-recovery-walkthrough.XXXXXX")
FIXTURE_REPO="$RUN_ROOT/repo"
WORKTREE_ROOT="$RUN_ROOT/worktrees"
mkdir -p "$FIXTURE_REPO"

cleanup() {
    local status=$?
    if [[ "$KEEP_FIXTURE" == true || $status -ne 0 || $FAIL_COUNT -gt 0 ]]; then
        printf '\nfixture kept for inspection at %s\n' "$RUN_ROOT"
    elif [[ "$RUN_ROOT" == *wrighty-usage-recovery-walkthrough.* ]]; then
        rm -rf "$RUN_ROOT"
    fi
    return $status
}
trap cleanup EXIT

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
        { rm -f "$tmp"; die "failed to write the usage-recovery worker configuration"; }
}

load_detail() {
    wr get "$ITEM_USAGE" --json 2>/dev/null
}

show_detail_on_failure() {
    printf '\nCurrent item state:\n'
    wr get "$ITEM_USAGE" || true
    printf '\nWorker status:\n'
    wr status || true
}

verify_scheduled_state() {
    local detail=$1 item_number item_file runtime_state
    if ! printf '%s' "$detail" | jq -e --arg agent "$ASSUME_AGENT" '
        (.result.worker.state == "retry-scheduled") and
        (.result.worker.activity == "retry-scheduled") and
        (.result.claim.state == "Unclaimed") and
        (.result.session.agentType == $agent) and
        (.result.session.available == true) and
        (.result.session.lastRun.outcome == "failed") and
        (.result.session.lastRun.failure.kind == "usage-exhausted"
          or .result.session.lastRun.failure.kind == "rate-limited") and
        (.result.session.lastRun.failure.isRetryable == true) and
        (.result.worker.dispatch.state == "retry-scheduled") and
        (.result.worker.dispatch.attempt >= 1) and
        (.result.worker.dispatch.maxAttempts >= .result.worker.dispatch.attempt) and
        (.result.worker.dispatch.fromCurrentInstallation == true)
        ' >/dev/null 2>&1; then
        fail "the live provider stop was not projected as a resumable retry-scheduled failure"
        return 1
    fi

    item_number=${ITEM_USAGE#local:}
    item_file=$(rg -l '^wrighty-worker-state: retry-scheduled$' \
        "$FIXTURE_REPO/.wrighty/items" 2>/dev/null | head -n1)
    [[ -n "$item_file" ]] &&
        pass "portable Local Markdown state is retry-scheduled" ||
        { fail "retry-scheduled frontmatter was not found"; return 1; }

    runtime_state="$FIXTURE_REPO/.wrighty/.runtime-state.json"
    jq -e --arg id "$item_number" '
        (.claims[$id] == null) and
        (.sessions[$id].dispatch.state == "retry-scheduled") and
        (.sessions[$id].dispatch.notBefore != null)
        ' "$runtime_state" >/dev/null 2>&1 &&
        pass "the exact retry is machine-local and the worker claim was released" ||
        { fail "the runtime sidecar did not retain an unclaimed deferred dispatch"; return 1; }

    wr status --json 2>/dev/null |
        jq -e --arg id "$ITEM_USAGE" 'any(.result.retries[]?; .id == $id)' \
            >/dev/null 2>&1 &&
        pass "wrighty status groups the item under scheduled retries" ||
        { fail "wrighty status did not expose the scheduled retry"; return 1; }

    pass "the normalized failure, retained session, attempt bound, and timer are visible in get"
}

verify_pre_due_skip() {
    local before=$1 output rc after fresh
    output=$(wr worker --once --agent "$ASSUME_AGENT" --yes --json)
    rc=$?
    if ((rc != 0)); then
        fail "the pre-due worker probe exited with status $rc"
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
          and .providerAvailability.state == "unavailable-until")
        ' >/dev/null 2>&1; then
        fail "the normal worker did not report the open provider circuit"
        return 1
    fi
    after=$(load_detail) || {
        fail "could not reload the item after the pre-due probe"
        return 1
    }
    if ! jq -n -e --argjson before "$before" --argjson after "$after" '
        ($after.result.worker.state == "retry-scheduled") and
        ($after.result.worker.dispatch.attempt == $before.result.worker.dispatch.attempt) and
        ($after.result.worker.dispatch.notBefore == $before.result.worker.dispatch.notBefore)
        ' >/dev/null 2>&1; then
        fail "the pre-due worker changed the deferred retry"
        return 1
    fi
    fresh=$(wr get "$ITEM_FRESH" --json 2>/dev/null) || {
        fail "could not reload the fresh circuit-breaker item"
        return 1
    }
    if ! printf '%s' "$fresh" | jq -e '
        (.result.status == "Todo") and
        (.result.claim.state == "Unclaimed") and
        (.result.session.available == false)
        ' >/dev/null 2>&1; then
        fail "the open provider circuit did not leave fresh work untouched"
        return 1
    fi
    pass "the open provider circuit skipped fresh work before claim, workspace, or spawn"
    pass "the original future retry remained unchanged"
}

verify_completed_state() {
    local detail=$1 item_number runtime_state
    if ! printf '%s' "$detail" | jq -e '
        (.result.status == "Done") and
        (.result.worker.state == null) and
        (.result.worker.activity == "completed") and
        (.result.claim.state == "Unclaimed") and
        (.result.worker.dispatch == null) and
        (.result.session.lastRun.outcome == "succeeded")
        ' >/dev/null 2>&1; then
        return 1
    fi

    item_number=${ITEM_USAGE#local:}
    runtime_state="$FIXTURE_REPO/.wrighty/.runtime-state.json"
    jq -e --arg id "$item_number" '
        (.claims[$id] == null) and
        (.sessions[$id].dispatch == null) and
        (.sessions[$id].outcome != null)
        ' "$runtime_state" >/dev/null 2>&1 ||
        return 1
    if rg -q '^wrighty-worker-state:' "$FIXTURE_REPO/.wrighty/items" 2>/dev/null; then
        return 1
    fi

    pass "the retained vendor session completed the item after capacity returned"
    pass "successful reacquisition cleared both portable and machine-local retry state"
}

step "Provisioning a disposable usage-recovery fixture"
explain "Repository: $FIXTURE_REPO"
explain "Worktrees: $WORKTREE_ROOT"
explain "Fallback retry delay: $RETRY_MINUTES minute(s); exact provider reset wins when parsed"

(
    cd "$FIXTURE_REPO"
    git init -q -b main
    git config user.name "Wrighty walkthrough"
    git config user.email "walkthrough@example.invalid"
    printf '# Usage recovery walkthrough\n\nDisposable repository for a live provider-limit test.\n' >README.md
    git add README.md
    git commit -q -m "Initialize usage recovery fixture"
) || die "failed to initialize the fixture repository"

wr init --backend local-markdown --local-path .wrighty \
    --status Todo --status "In Progress" --status Done \
    --priority P0 --priority P1 --priority P2 --yes >/dev/null 2>&1 ||
    die "wrighty init failed"
write_usage_config
wt_install_and_commit_skill

ITEM_USAGE=$(create_item "Complete the usage recovery probe" \
    "Create RECOVERED.md in the repository root with one line saying that the retained session recovered, then finish this item.") ||
    die "could not create the usage-recovery item"
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

DETAIL=$(load_detail) || die "could not read $ITEM_USAGE after the live worker run"
if ! verify_scheduled_state "$DETAIL"; then
    show_detail_on_failure
    die "the initial live-provider compatibility check failed; the fixture has been preserved"
fi

NOT_BEFORE=$(printf '%s' "$DETAIL" | jq -r '.result.worker.dispatch.notBefore')
ATTEMPT=$(printf '%s' "$DETAIL" | jq -r '.result.worker.dispatch.attempt')
FAILURE_KIND=$(printf '%s' "$DETAIL" | jq -r '.result.session.lastRun.failure.kind')
FAILURE_CONFIDENCE=$(printf '%s' "$DETAIL" | jq -r '.result.session.lastRun.failure.confidence')

ITEM_FRESH=$(create_item "Do not start while provider usage is exhausted" \
    "This item verifies the provider circuit breaker. Leave it untouched during this walkthrough.") ||
    die "could not create the fresh provider-circuit item"
pass "created fresh circuit-breaker item $ITEM_FRESH"

step "Observed deferred recovery"
explain "Failure: $FAILURE_KIND ($FAILURE_CONFIDENCE)"
explain "Attempt: $ATTEMPT of $MAX_ATTEMPTS"
explain "Not before: $NOT_BEFORE"

verify_pre_due_skip "$DETAIL" || {
    show_detail_on_failure
    die "the pre-due circuit check failed; the fixture has been preserved"
}

step "Inspect the recovery surfaces"
manual \
    "cd '$FIXTURE_REPO'" \
    "source '$ACTIVATE_SCRIPT'" \
    "wrighty get '$ITEM_USAGE'" \
    "wrighty status" \
    "wrighty list" \
    "wrighty web" \
    "" \
    "The web command is optional; stop it with Ctrl-C after inspecting the retry badge/detail."
pause

if [[ -z "$RESUME_MODE" ]]; then
    printf '\n%sHow should the recovery be tested?%s [manual/automatic] (default manual): ' \
        "$C_BOLD" "$C_RESET"
    read -r RESUME_MODE
    [[ -z "$RESUME_MODE" ]] && RESUME_MODE="manual"
    case "$RESUME_MODE" in
        manual | automatic) ;;
        *) die "recovery mode must be manual or automatic" ;;
    esac
fi

while true; do
    step "Resume after provider capacity returns"
    if [[ "$RESUME_MODE" == "automatic" ]]; then
        explain "Wait until both the provider has reset and the recorded not-before time has passed:"
        explain "$NOT_BEFORE"
        manual \
            "cd '$FIXTURE_REPO'" \
            "source '$ACTIVATE_SCRIPT'" \
            "wrighty worker --once --yes --json" \
            "" \
            "This omits --item so the normal due-retry selector must reacquire and resume it."
    else
        explain "Wait until the provider reports capacity again. This explicit item command may run before the timer."
        manual \
            "cd '$FIXTURE_REPO'" \
            "source '$ACTIVATE_SCRIPT'" \
            "wrighty worker --item '$ITEM_USAGE' --once --yes --json" \
            "" \
            "This deliberately tests the operator's retry-now override and same-agent resume."
    fi
    pause

    DETAIL=$(load_detail) || die "could not read $ITEM_USAGE after the recovery attempt"
    if verify_completed_state "$DETAIL"; then
        break
    fi

    if printf '%s' "$DETAIL" | jq -e '
        .result.worker.state == "retry-scheduled" and
        (.result.session.lastRun.failure.kind == "usage-exhausted"
          or .result.session.lastRun.failure.kind == "rate-limited")
        ' >/dev/null 2>&1; then
        ATTEMPT=$(printf '%s' "$DETAIL" | jq -r '.result.worker.dispatch.attempt')
        NOT_BEFORE=$(printf '%s' "$DETAIL" | jq -r '.result.worker.dispatch.notBefore')
        note "$ASSUME_AGENT still reported no capacity; Wrighty safely rescheduled attempt $ATTEMPT"
        explain "Next not-before time: $NOT_BEFORE"
        if ((ATTEMPT >= MAX_ATTEMPTS)); then
            show_detail_on_failure
            die "the configured retry-attempt limit has been reached"
        fi
        RESUME_MODE="manual"
        continue
    fi

    fail "the post-reset run neither completed nor returned to a safe retry-scheduled state"
    show_detail_on_failure
    die "live recovery verification failed; the fixture has been preserved"
done

step "Walkthrough complete"
explain "Passed checks: $PASS_COUNT"
explain "The disposable fixture will be removed unless --keep-fixture was supplied."

if ((FAIL_COUNT > 0)); then exit 1; fi
