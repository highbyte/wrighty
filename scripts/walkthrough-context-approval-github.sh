#!/usr/bin/env bash
#
# walkthrough-context-approval-github.sh — see approved context resolve against live GitHub.
#
# Wrighty will only give an unattended agent issue content a maintainer has approved. Approval is a
# Project single-select field; the instant it was set is both the approval of the current title and
# body and the cutoff before which comments are covered. This walkthrough drives that end to end so
# you can watch what an agent would actually be given, and what makes Wrighty refuse.
#
# You drive the Project UI; the script reads back what Wrighty makes of it. Nothing here claims,
# launches, or spends an agent turn — 'wrighty context' is read-only.
#
# WHAT THIS CANNOT SHOW YET, and will say so at the point it matters:
#   * Per-comment thumbs-up/down decisions. Resolving whether an actor is allowed to decide needs
#     authorization work that is not implemented, so every reaction is currently inert. Only the
#     batch cutoff decides anything.
#   * Wrighty's own handover comments being hidden from task context — same cause. Use a fresh
#     issue, as this script does; on an issue with prior worker activity the handover would show up
#     as an undecided comment.
#
# LIVE: creates a Project, a field, and issues on the dedicated private <owner>/<repo>-test
# repository (see scripts/ensure-github-test-repo.sh). It never touches the product repository.
# Set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge that.
#
# Issues created this run are deleted on exit unless the run fails or --keep-fixture is given; a
# failed run keeps them so you can see the state that produced it. The test repository and its
# Project are reused across runs and are never deleted here.

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
PROJECT_TITLE="Wrighty context approval walkthrough"
CONTEXT_FIELD="Wrighty policy - context approval"

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-context-approval-github.sh [options]" \
        "" \
        "Interactive walkthrough of approved context against live GitHub, on the dedicated" \
        "private <owner>/<repo>-test repository. Requires WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1." \
        "" \
        "Options:" \
        "  --configuration NAME      Build configuration; defaults to Debug." \
        "  --skip-build              Use the existing local build output." \
        "  --source-repo OWNER/REPO  Source to derive the -test repo from." \
        "  --keep-fixture            Keep the issues this run created." \
        "  --auto                    Perform every change itself instead of asking you to. Useful" \
        "                            as an unattended regression check; you learn less." \
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

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-context-walkthrough.XXXXXX")
ISSUE_LEDGER="$RUN_ROOT/created-issues"
: >"$ISSUE_LEDGER"
RUN_COMPLETED=false

cleanup() {
    local status=$?
    trap - EXIT
    # Retain on failure: the state that produced a failed step is exactly what is worth looking at,
    # and these live on GitHub where you can still open them.
    if [[ "$KEEP_FIXTURE" == true || "$RUN_COMPLETED" == false ]]; then
        if [[ -s "$ISSUE_LEDGER" ]]; then
            note "keeping $(wc -l <"$ISSUE_LEDGER" | tr -d ' ') issue(s) on $TEST_REPO for inspection"
            sed 's|^|  https://github.com/'"$TEST_REPO"'/issues/|' "$ISSUE_LEDGER"
        fi
        # The configuration is kept with them. Retaining the issues while deleting the only config
        # that can read them would leave nothing actually inspectable.
        note "keeping the configuration so you can keep querying:"
        printf '  WRIGHTY_CONFIG_PATH=%s \\\n    dotnet %s context %s\n' \
            "$CONFIG_PATH" "$CLI_DLL" "${ISSUE_ID:-<id>}"
    else
        gpl_delete_ledger_issues
        rm -rf "$RUN_ROOT"
    fi
    exit "$status"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------------------------
# Walkthrough-specific helpers
# ---------------------------------------------------------------------------------------------
# Project provisioning lives in scripts/github-project-lib.sh, shared with the launch walkthrough.

set_context_approval() {
    gpl_set_single_select "$ISSUE_NUMBER" "$CONTEXT_FIELD" "$1"
    return
}

# Re-approval must change the value. Writing the option already held advances no timestamp, so it
# would renew nothing; the walkthrough demonstrates that rather than only asserting it.
reapprove() {
    set_context_approval "Needs review"
    sleep 2
    set_context_approval "Approved"
    sleep 2
    return
}

# Asks you to make a change, or makes it itself under --auto. The manual path is the default
# because the actions ARE the lesson: doing them yourself is how the behaviour becomes obvious
# rather than merely asserted. Returns non-zero in manual mode so the caller falls through to its
# own instruction block.
do_or_ask() {
    local description=$1
    shift
    if [[ "$AUTO" == true ]]; then
        explain "auto: $description"
        "$@" >/dev/null || die "could not $description"
        sleep 2
        return 0
    fi
    return 1
}

wrighty() { WRIGHTY_CONFIG_PATH="$CONFIG_PATH" dotnet "$CLI_DLL" "$@"; }

# Deliberately two functions rather than one that both prints and returns. Printing to stdout while
# the caller captures stdout means the caller swallows the very output the walkthrough exists to
# show.
print_context() {
    wrighty context "$ISSUE_ID" 2>&1 | sed 's/^/    /'
    return
}

# Returns the refusal code, empty when genuinely approved, or INVOCATION_FAILED when the command
# did not produce a readable answer at all. Those must not be conflated: an unparseable invocation
# yielding "no code" would otherwise read as approved, which is the most permissive reading of a
# failure and exactly the wrong default.
context_code() {
    local json
    json=$(wrighty context "$ISSUE_ID" --json 2>/dev/null)
    if [[ -z "$json" ]] || ! jq -e . >/dev/null 2>&1 <<<"$json"; then
        printf 'INVOCATION_FAILED\n'
        return
    fi
    if jq -e '.result.approved == true' >/dev/null 2>&1 <<<"$json"; then
        printf '\n'
        return
    fi
    jq -r '.result.code // .error.code // "INVOCATION_FAILED"' <<<"$json"
    return
}

expect_context() {
    local expected=$1 description=$2 actual
    print_context
    actual=$(context_code)
    if [[ "$expected" == "approved" && -z "$actual" ]]; then
        pass "$description"
    elif [[ "$actual" == "$expected" ]]; then
        pass "$description"
    else
        fail "$description (expected '${expected}', got '${actual:-approved}')"
    fi
    return
}

digest() {
    wrighty context "$ISSUE_ID" --json 2>/dev/null | jq -r '.result.revision.digest // ""'
    return
}

# ---------------------------------------------------------------------------------------------
# Walkthrough
# ---------------------------------------------------------------------------------------------

printf '\n%sApproved context on GitHub%s\n' "$C_BOLD" "$C_RESET"
explain "Wrighty gives an unattended agent only content a maintainer has approved."
explain "You will drive the Project field; this script reads back what Wrighty makes of it."
explain "Repository: $TEST_REPO"
if [[ "$AUTO" == false ]]; then
    begin_walkthrough
else
    explain "auto mode: performing every change itself, with no pauses"
fi

gpl_ensure_project
gpl_ensure_single_select "$CONTEXT_FIELD" "Needs review,Approved"
pass "Project #$PROJECT_NUMBER has the '$CONTEXT_FIELD' field"

ISSUE_NUMBER=$(gpl_create_issue \
    "Walkthrough: approved context" \
    "The worker should retry a failed step once before giving up.")
ISSUE_ID="github:$TEST_REPO#$ISSUE_NUMBER"
ISSUE_URL="https://github.com/$TEST_REPO/issues/$ISSUE_NUMBER"

CONFIG_PATH="$RUN_ROOT/.wrighty.json"
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
pass "created $ISSUE_URL"

explain "You can query this item yourself at any pause, from another terminal:"
printf '\n  WRIGHTY_CONFIG_PATH=%s \\\n    dotnet %s context %s\n\n' \
    "$CONFIG_PATH" "$CLI_DLL" "$ISSUE_ID"
explain "Add --json for the machine-readable form. It is read-only: it never claims or launches."
explain "Pass --keep-fixture to keep the issue and this configuration after the run ends."

step "1. An unapproved item gives an agent nothing"
explain "The approval field is unset, so no content is approved for an unattended run."
expect_context "CONTEXT_APPROVAL_UNAVAILABLE" "an unapproved item is refused"

step "2. Approve the current content"
if [[ "$AUTO" == true ]]; then
    explain "auto: setting '$CONTEXT_FIELD' to 'Approved'"
    set_context_approval "Approved"
    sleep 2
else
    manual \
        "Open: $ISSUE_URL" \
        "Open the Project: https://github.com/users/$OWNER/projects/$PROJECT_NUMBER" \
        "" \
        "Set '$CONTEXT_FIELD' to 'Approved' for this issue."
    pause
fi
expect_context "approved" "the approved item resolves"
BASE_DIGEST=$(digest)
explain "Revision: $BASE_DIGEST"
explain "That digest identifies the exact text an agent would be given."

step "3. Edit the body and watch approval fall away"
explain "Editing the issue body invalidates the approval that covered the older text."
if ! do_or_ask "edit the body" gh issue edit "$ISSUE_NUMBER" --repo "$TEST_REPO" \
    --body "The worker should retry a failed step TWICE, with backoff."; then
    manual \
        "Open: $ISSUE_URL" \
        "" \
        "Edit the issue body — change 'once' to 'twice', or anything you like." \
        "Leave the approval field alone."
    pause
fi
expect_context "CONTEXT_BASE_NEEDS_REVIEW" "an edited body needs renewed approval"

step "4a. The field still says 'Approved', and that is the problem"
explain "Look at the Project now: the field reads 'Approved', yet Wrighty just refused the item."
explain "Nothing in the UI suggests anything is wrong. That is the trap this step exists to show."
explain "auto: writing '$CONTEXT_FIELD' = 'Approved' through the API, the value it already has"
set_context_approval "Approved"
sleep 3
expect_context "CONTEXT_BASE_NEEDS_REVIEW" "writing the value it already holds renews nothing"
explain "Setting the field to the value it already holds does not advance its timestamp, so there"
explain "is no new approval instant and the edited body stays uncovered."

step "4b. Renewing approval means changing the value"
explain "The Projects UI gives no way to re-select the current value, so renewing approval there"
explain "means clearing the field and setting it again — which is a real change, and works."
if [[ "$AUTO" == true ]]; then
    explain "auto: Needs review, then Approved"
    reapprove
else
    manual \
        "Open the Project: https://github.com/users/$OWNER/projects/$PROJECT_NUMBER" \
        "" \
        "Set '$CONTEXT_FIELD' to 'Needs review', then back to 'Approved'." \
        "(Clearing the value and picking 'Approved' again does the same thing — the UI makes you" \
        " do one or the other, because it will not let you re-pick the value already set.)"
    pause
fi
expect_context "approved" "changing the value renews the approval"
NEW_DIGEST=$(digest)
explain "Revision: $NEW_DIGEST"
if [[ -n "$BASE_DIGEST" && "$BASE_DIGEST" != "$NEW_DIGEST" ]]; then
    pass "the revision changed with the content"
else
    fail "the revision did not change after the body was edited"
fi
note "A maintainer has no way to know this from the UI alone: the field goes on showing 'Approved'"
note "after an edit that invalidated it. Wrighty's refusal message therefore spells out the remedy,"
note "and the optional edit workflow resets the field to 'Needs review' so the board stops showing"
note "a value that is no longer true."

step "5. A comment added after approval blocks the launch"
explain "It is not silently omitted: dropping an unreviewed comment would narrow the approved task"
explain "with nobody choosing to, and the agent would never learn the requirement existed."
if ! do_or_ask "add a comment" gh issue comment "$ISSUE_NUMBER" --repo "$TEST_REPO" \
    --body "Also make the backoff configurable, defaulting to 5 seconds."; then
    manual \
        "Open: $ISSUE_URL" \
        "" \
        "Add a comment — anything, for example:" \
        "  Also make the backoff configurable, defaulting to 5 seconds."
    pause
fi
expect_context "CONTEXT_COMMENT_PENDING" "an undecided comment blocks"
explain "The refusal names the comment's URL so you can go and decide it."

step "6. Re-approve to cover the comment"
explain "A fresh approval sets a new cutoff, and every comment older than it is covered."
if [[ "$AUTO" == true ]]; then
    explain "auto: Needs review, then Approved"
    reapprove
else
    manual \
        "In the Project, take '$CONTEXT_FIELD' through 'Needs review' and back to 'Approved'."
    pause
fi
expect_context "approved" "the comment is now covered by the batch"
INCLUDED=$(wrighty context "$ISSUE_ID" --json 2>/dev/null | jq -r '.result.discussion.included // 0')
if [[ "$INCLUDED" == "1" ]]; then
    pass "the comment is included in what an agent would be given"
else
    fail "expected 1 included comment, got '$INCLUDED'"
fi

step "7. A title edit also falls away"
explain "A title change advances neither the issue's edit timestamp nor its edit history — it is"
explain "visible only as a rename event. Reading the issue's edit metadata alone would miss it."
if ! do_or_ask "edit the title" gh issue edit "$ISSUE_NUMBER" --repo "$TEST_REPO" \
    --title "Walkthrough: approved context (renamed)"; then
    manual \
        "Open: $ISSUE_URL" \
        "" \
        "Rename the issue — change its title to anything else." \
        "Leave the approval field alone."
    pause
fi
expect_context "CONTEXT_BASE_NEEDS_REVIEW" "an edited title needs renewed approval"

step "Not covered yet"
note "Per-comment thumbs-up/down decisions are inert: resolving whether an actor may decide needs"
note "authorization work that is not implemented. Only the batch cutoff decides anything today."
note "For the same reason, Wrighty's own handover comments are not yet recognised as protocol, so"
note "an issue with prior worker activity would show them as undecided comments."

printf '\n%s%d passed, %d failed%s\n' "$C_BOLD" "$PASS_COUNT" "$FAIL_COUNT" "$C_RESET"
((FAIL_COUNT == 0)) && RUN_COMPLETED=true
((FAIL_COUNT == 0)) || exit 1
exit 0
