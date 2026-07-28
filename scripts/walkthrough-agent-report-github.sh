#!/usr/bin/env bash
#
# walkthrough-agent-report-github.sh — the two comments Wrighty writes, and what a resume carries.
#
# Its companion (walkthrough-agent-report-local.sh) follows one run from prompt to stored report
# without a network. This one shows the parts that only exist on a shared tracker:
#
#   * publishing is off until you turn it on, and the report is stored either way;
#   * the handover comment and the run report are different things, written for different readers;
#   * a resume carries the discussion approved since the launch, and nothing else.
#
# By default no real agent runs and nothing is billed: a fake vendor stands in for one, keeping the
# prompt it was handed and answering with a report block, because both halves of that exchange are
# the subject. Pass --real-agent to drive an actual vendor CLI instead.
#
# LIVE: creates a Project, fields, issues and comments on the dedicated private <owner>/<repo>-test
# repository, and clones it to a temporary directory. It never touches the product repository. Set
# WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge that.
#
# Issues created this run are deleted on exit unless the run fails or --keep-fixture is given.
#
# It is not cheap against the GraphQL point budget: `wrighty init` provisions the whole Project
# schema and each worker run reads the conversation twice. Check
# `gh api graphql -f query='{ rateLimit { remaining resetAt } }'` before assuming a failure means
# something else.

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
AGENT="claude"
REAL_AGENT=false
PROJECT_TITLE="Wrighty agent report walkthrough"
CONTEXT_FIELD="Wrighty policy - context approval"
EXECUTION_FIELD="Wrighty policy - execution"

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-agent-report-github.sh [options]" \
        "" \
        "Shows what Wrighty writes back to a GitHub issue after a run — the handover comment and" \
        "the run report — when each appears, and what a resumed agent is given. Runs against the" \
        "dedicated private <owner>/<repo>-test repository with a fake vendor, so nothing is billed." \
        "Requires WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1." \
        "" \
        "Options:" \
        "  --configuration NAME      Build configuration; defaults to Debug." \
        "  --skip-build              Use the existing local build output." \
        "  --source-repo OWNER/REPO  Source to derive the -test repo from." \
        "  --real-agent NAME         Run the actual vendor CLI (claude, codex or copilot) instead" \
        "                            of a fake. This DOES consume your agent quota. The agent works" \
        "                            in a throwaway clone whose push URL is disabled." \
        "  --keep-fixture            Keep the issue this run created." \
        "  --auto                    Do the operator's steps itself instead of asking you to." \
        "  -h, --help                Show this help."
    return
}

while (($# > 0)); do
    case "$1" in
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --source-repo) (($# >= 2)) || die "--source-repo requires OWNER/REPO"; SOURCE_REPO=$2; shift 2 ;;
        --real-agent)
            (($# >= 2)) || die "--real-agent requires an agent name"
            AGENT=$2
            REAL_AGENT=true
            shift 2 ;;
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
case "$AGENT" in
    claude|codex|copilot) ;;
    *) die "unknown agent '$AGENT'; expected claude, codex or copilot" ;;
esac
gh auth status >/dev/null 2>&1 || die "gh is not authenticated; run 'gh auth login'"

[[ "${WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE:-}" == "1" ]] ||
    die "set WRIGHTY_RUN_GITHUB_WALKTHROUGH_LIVE=1 to acknowledge this creates real GitHub resources on <owner>/<repo>-test"

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
wt_build_cli "$CLI_PROJECT" "$CLI_DLL" "$SKIP_BUILD" "$BUILD_CONFIGURATION"

TEST_REPO=$(ensure_github_test_repo "${SOURCE_REPO:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}") ||
    die "could not resolve the private test repository"
OWNER=${TEST_REPO%%/*}

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-agent-report-gh.XXXXXX")
ISSUE_LEDGER="$RUN_ROOT/created-issues"
CLONE="$RUN_ROOT/repo"
FAKE_BIN="$RUN_ROOT/fake-bin"
CACHE="$RUN_ROOT/cache"
PROMPT_CAPTURE="$RUN_ROOT/prompt-given-to-agent.txt"
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

if [[ "$REAL_AGENT" == true ]]; then
    REAL_VENDOR=$(command -v "$AGENT") || die "the '$AGENT' CLI is not on PATH"

    # A tee in front of the real vendor, so a live run still shows what it was handed.
    cat >"$FAKE_BIN/$AGENT" <<WRAPPER
#!/usr/bin/env bash
set -euo pipefail
tee "\${WRIGHTY_PROMPT_CAPTURE:?}" | "$REAL_VENDOR" "\$@"
WRAPPER
    chmod +x "$FAKE_BIN/$AGENT"

    # A real agent is told to call \`wrighty finish\`, so it needs a wrighty to call — pinned to this
    # run's config so nothing it does can reach another repository or another Project.
    cat >"$FAKE_BIN/wrighty" <<SHIM
#!/usr/bin/env bash
set -euo pipefail
export WRIGHTY_CONFIG_PATH="$CONFIG_PATH"
export WRIGHTY_CACHE_DIR="$CACHE"
cd "$CLONE"
exec dotnet "$CLI_DLL" "\$@"
SHIM
    chmod +x "$FAKE_BIN/wrighty"
else
# This fake keeps the prompt it was handed, which is what lets step 5 show the resume delta rather
# than assert it, and answers with a report block in the contract's shape.
cat >"$FAKE_BIN/$AGENT" <<'FAKE_CLAUDE'
#!/usr/bin/env bash
set -euo pipefail
session_id=""
argv=("$@")
for index in "${!argv[@]}"; do
    case "${argv[$index]}" in
        --session-id|--resume) session_id=${argv[$((index + 1))]:-} ;;
    esac
done
[[ -n "$session_id" ]] || session_id="fake-claude-session"
cat >"${WRIGHTY_PROMPT_CAPTURE:?}"
python3 - "$session_id" <<'PY'
import json, sys
report = {
    "summary": "Read the item and stopped: the retry cap is not stated anywhere.",
    "changes": [],
    "verification": [],
    "decisions": ["An approved comment says not to invent the cap, so I did not."],
    "requestedInput": ["What is the maximum number of retries?"],
    "remainingWork": ["Write the section once the cap is known"],
    "references": [],
}
text = ("I cannot finish: the retry cap is not stated and I was told not to guess it.\n\n"
        "```wrighty-report\n" + json.dumps(report, indent=2) + "\n```")
print(json.dumps({"type": "result", "subtype": "success", "is_error": False,
                  "session_id": sys.argv[1], "result": text}))
PY
FAKE_CLAUDE
chmod +x "$FAKE_BIN/$AGENT"
fi

wrighty() {
    env PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_CONFIG_PATH="$CONFIG_PATH" \
        WRIGHTY_CACHE_DIR="$CACHE" \
        WRIGHTY_PROMPT_CAPTURE="$PROMPT_CAPTURE" \
        dotnet "$CLI_DLL" "$@"
    return
}

# The report mode is a repository setting, so switching it means rewriting the config the way an
# operator would — there is no flag for it, deliberately: publishing to a shared surface is a
# decision about the repository, not about one run.
write_config() {
    local report_mode=$1
    cat >"$CONFIG_PATH" <<CONFIG
{
  "backend": "github",
  "github": {
    "repository": "$TEST_REPO",
    "projectOwner": "$OWNER",
    "projectNumber": $PROJECT_NUMBER,
    "gitHubHost": "github.com"
  },
  "worker": {
    "sessionReportMode": "$report_mode"
  }
}
CONFIG
    return
}

# The fixture task is genuinely blocked: the work is clear but one number is deliberately withheld,
# and an approved comment says not to invent it. That is what makes step 1 a pause rather than a
# completion — the walkthrough is about the handover, the resume, and the delta between them, and a
# real agent that simply finished on the first run would have nothing to hand over.
ISSUE_BODY="Add a '## Retries' section to README.md in this repository stating the maximum number \
of times a failed run is retried. Keep it to two sentences."
FIRST_COMMENT="The exact cap is still being decided, so do not guess it. If this item does not \
state a number, stop and ask for it rather than picking one yourself."
ANSWER_COMMENT="Answering the question: the cap is 5 retries."

run_worker() {
    local events="$RUN_ROOT/worker.jsonl"
    rm -f "$PROMPT_CAPTURE"
    (cd "$CLONE" && wrighty worker --json --yes --once --item "$ISSUE_ID" "$@" \
        --agent "$AGENT" --workspace-mode current >"$events" 2>"$events.stderr")
    jq -r '"    " + .type + " | " + ((.message // "") | .[0:160])' <"$events" 2>/dev/null |
        grep -vE "^\s+(running|renewed|waiting) " || sed 's/^/    /' "$events"
    return
}

# Counts issue comments carrying a given Wrighty marker.
marker_count() {
    gh api "repos/$TEST_REPO/issues/$ISSUE_NUMBER/comments" --paginate \
        --jq "[.[] | select(.body | contains(\"$1\"))] | length" 2>/dev/null || printf '0'
    return
}

# gh_human_comment_ids — ids of comments a person wrote, newest last.
#
# Wrighty's own comments carry a marker, so excluding them leaves exactly what the operator added.
# This is how the walkthrough can check that a clarification reached the agent without dictating
# what the clarification had to say: in interactive use the operator writes their own words, and an
# assertion that greps for the sentence the --auto path types fails an otherwise perfect run.
gh_human_comment_ids() {
    gh api "repos/$TEST_REPO/issues/$ISSUE_NUMBER/comments" --paginate \
        --jq '.[] | select((.body | test("wrighty-(handover|claim|session-report):")) | not) | .id' \
        2>/dev/null
    return
}

approve_context() {
    gpl_set_single_select "$ISSUE_NUMBER" "$CONTEXT_FIELD" "Needs review"
    sleep 2
    gpl_set_single_select "$ISSUE_NUMBER" "$CONTEXT_FIELD" "Approved"
    sleep 3
    return
}

# ---------------------------------------------------------------------------------------------

printf '\n%sWhat Wrighty writes back, and what a resume carries%s\n' "$C_BOLD" "$C_RESET"
explain "Two comments, one setting, and a resumed prompt."
if [[ "$REAL_AGENT" == true ]]; then
    explain "Running the real $AGENT CLI, through a wrapper that keeps a copy of what it was handed."
    explain "This consumes your $AGENT quota. The clone it works in cannot push."
else
    explain "A fake vendor stands in for a real agent: it keeps the prompt it was handed and answers"
    explain "with a report, so both directions are visible. Nothing is billed."
fi
explain "Repository: $TEST_REPO"
if [[ "$AUTO" == false ]]; then
    begin_walkthrough
else
    explain "auto mode: doing the operator's steps itself, with no pauses"
fi

step "Provisioning"
# Before anything is created, so an exhausted budget stops the run rather than littering it.
gpl_require_budget
gpl_ensure_project
pass "Project #$PROJECT_NUMBER"

git clone --quiet "https://github.com/$TEST_REPO.git" "$CLONE" 2>/dev/null ||
    die "could not clone $TEST_REPO"
# Nothing here ever pushes, and with a real agent working in this clone that has to be true whatever
# the agent decides to do. Breaking the push URL makes it true rather than assumed.
git -C "$CLONE" remote set-url --push origin "no-push://disabled" 2>/dev/null || true

# Off first, because that is the default and step 1 is about what the default does.
write_config "off"
(cd "$CLONE" && wrighty init --yes --json >/dev/null) ||
    die "could not initialise the Project schema; run wrighty init against $TEST_REPO by hand"
gpl_ensure_single_select "$CONTEXT_FIELD" "Needs review,Approved"
pass "Project schema initialised"

ISSUE_NUMBER=$(gpl_create_issue \
    "Walkthrough: what the agent reports back" \
    "$ISSUE_BODY")
ISSUE_ID="github:$TEST_REPO#$ISSUE_NUMBER"
ISSUE_URL="https://github.com/$TEST_REPO/issues/$ISSUE_NUMBER"
gpl_set_single_select "$ISSUE_NUMBER" "$EXECUTION_FIELD" "Automatic allowed"
sleep 2
gh issue comment "$ISSUE_NUMBER" --repo "$TEST_REPO" \
    --body "$FIRST_COMMENT" >/dev/null
sleep 2
approve_context
pass "created $ISSUE_URL, approved, with one approved comment"

step "1. A run with publishing off"
explain "Publishing is off unless a repository turns it on, because it writes to a surface everyone"
explain "on the issue reads. The report is still produced and still stored — off decides who sees it,"
explain "not whether it exists."
run_worker --fresh
REPORT_COMMENTS=$(marker_count "wrighty-session-report:v1")
HANDOVER_COMMENTS=$(marker_count "wrighty-handover:v1")
if [[ "$REPORT_COMMENTS" == "0" ]]; then
    pass "no run report was published"
else
    fail "a run report was published with sessionReportMode off"
fi
STORED=$(cd "$CLONE" && wrighty get "$ISSUE_ID" --json |
    jq -r '.result.session.lastRun.agentReport.summary // empty')
if [[ -n "$STORED" ]]; then
    pass "and it was stored all the same: \"$STORED\""
else
    fail "the report was neither published nor stored"
fi

step "2. The handover comment, which is a different thing"
explain "The agent stopped to ask a question, so Wrighty posted a handover: a short comment telling"
explain "a human that this item is waiting on them. That is not the run report, and it is not"
explain "governed by the report setting — a paused run has to say so or it is simply abandoned."
if (( HANDOVER_COMMENTS > 0 )); then
    pass "a handover comment was posted"
else
    fail "the run paused without telling anyone on the issue"
fi
printf '\n'
gh api "repos/$TEST_REPO/issues/$ISSUE_NUMBER/comments" \
    --jq '.[-1].body' 2>/dev/null | sed 's/^/    /' | head -20
printf '\n'
explain "Note it quotes the agent's closing words but not its report block. A fenced block quoted"
explain "inside a comment would close the comment's own fence and break the rest of it."

step "3. Turn publishing on, and resume"
explain "Same repository, one setting changed. 'all' publishes a report for every run; 'completed'"
explain "publishes only for runs Wrighty observed reach the item's completion status."
write_config "all"
pass "worker.sessionReportMode = all"
COMMENTS_BEFORE_ANSWER=$(gh_human_comment_ids | tr '\n' ' ')
explain "The handover comment from step 2 is discussion nobody has decided on, so it blocks the"
explain "resume until the approval cutoff moves past it — the operator's move anyway, after reading"
explain "what the agent asked."
if [[ "$AUTO" == true ]]; then
    explain "auto: answering the agent's question, then re-approving"
    gh issue comment "$ISSUE_NUMBER" --repo "$TEST_REPO" \
        --body "$ANSWER_COMMENT" >/dev/null
    sleep 2
    approve_context
else
    manual \
        "Read the handover comment on $ISSUE_URL and answer it — add a comment supplying" \
        "the missing retry cap. Any wording and any number; for example:" \
        "    $ANSWER_COMMENT" \
        "" \
        "Then set '$CONTEXT_FIELD' to 'Needs review' and back to 'Approved'." \
        "Both steps: selecting the value the field already holds renews nothing, so the" \
        "approval instant — and with it the cutoff — would not move."
    pause
fi

step "4. What the resumed agent was given"
explain "A resume does not repeat the context. The agent already holds it, and paying to re-send it"
explain "would also invite it to re-do settled work. It is given what was approved since it started."
run_worker
if [[ -s "$PROMPT_CAPTURE" ]]; then
    pass "the resumed agent received $(wc -c <"$PROMPT_CAPTURE" | tr -d ' ') bytes on standard input"
else
    fail "the resume sent the agent nothing"
fi
printf '\n'
# From the delta onward. The trust boundary above it is the same wall the fresh launch showed, and
# printing it again would bury the one section this step exists to show.
sed -n '/Context you already have/,$p' "$PROMPT_CAPTURE" | sed 's/^/    /' | head -28
printf '\n'
# By identity, not by wording. The prompt cites each entry's comment URL, so the operator's own
# clarification can be recognised whatever they chose to write in it.
ANSWER_IDS=""
for comment_id in $(gh_human_comment_ids); do
    case " $COMMENTS_BEFORE_ANSWER " in
        *" $comment_id "*) ;;
        *) ANSWER_IDS="$ANSWER_IDS $comment_id" ;;
    esac
done
if [[ -z "${ANSWER_IDS// }" ]]; then
    fail "no new comment was added, so there was nothing for the resume to carry"
elif [[ -z "$(for id in $ANSWER_IDS; do grep -q "$id" "$PROMPT_CAPTURE" || echo missing; done)" ]]; then
    pass "the comment you added reached the resumed agent (issuecomment-${ANSWER_IDS## })"
else
    fail "the comment you added did not reach the resumed agent (expected$ANSWER_IDS)"
fi
# Only the part before the delta. An approved comment may legitimately quote the description back —
# a person answering a question often does — and that is the operator's content, not Wrighty
# re-sending context it already supplied. Grepping the whole prompt cannot tell the two apart.
sed -n '1,/## New approved discussion/p' "$PROMPT_CAPTURE" >"$RUN_ROOT/resume-preamble.txt"
if grep -q 'stating the maximum number' "$RUN_ROOT/resume-preamble.txt" 2>/dev/null; then
    fail "the resume re-sent the original description the agent already has"
else
    pass "and not the description it was already given"
    if grep -q 'stating the maximum number' "$PROMPT_CAPTURE" 2>/dev/null; then
        explain "(the description does appear further down, quoted inside a newly approved comment)"
    fi
fi
explain "That answer is the whole reason to resume: the agent stopped because a number was missing,"
explain "and now it has it. Where a later entry contradicts an earlier one rather than answering it,"
explain "the prompt tells the agent to follow the later one and to report that it did — a conflict"
explain "resolved silently is a conflict nobody knows about."
note "Look at what else came through: Wrighty's own handover comment from step 2, fenced as"
note "work-item content and presented as new guidance. Re-approving the batch swept it in along"
note "with your answer. Wrighty does not yet recognise its own comments as protocol rather than"
note "discussion, so they neither identify themselves to the cutoff nor get filtered on the way"
note "back out. It is inert here — the agent is told to treat all of it as data — but it is noise"
note "the agent pays for and could misread. The authorization work that fixes it is not built."

step "5. Both comments, side by side"
explain "The report is published now. Open the issue and read the two together:"
REPORT_COMMENTS=$(marker_count "wrighty-session-report:v1")
if (( REPORT_COMMENTS > 0 )); then
    pass "a run report is now published on the issue"
else
    fail "publishing was on but no run report appeared"
fi
explain "They are not two versions of the same thing. The handover is a status comment — one per"
explain "item, overwritten on every run — so the one you will find now says 'completed', not the"
explain "paused state it held in step 2. The report is a record — one per run, appended, never"
explain "rewritten. That is why the handover cannot serve as the history and the report cannot serve"
explain "as the status."
manual \
    "Open $ISSUE_URL and read the two comments Wrighty wrote:" \
    "" \
    "  The handover asks a person to do something. It is short, addressed to you, and it" \
    "  describes the item as it stands right now — the earlier text is gone." \
    "" \
    "  The run report records one run and stays. It leads with what Wrighty observed —" \
    "  outcome, agent, vendor process — and puts the agent's account under a heading saying" \
    "  whose account it is. Read the 'Checks the agent says it ran' heading and note what it" \
    "  does not say: that Wrighty ran them."
if [[ "$AUTO" == false ]]; then
    pause
else
    explain "auto: skipping the side-by-side reading"
fi

step "What this showed"
explain "Publishing is a repository decision and off by default, while storing the report is not"
explain "conditional on it. The handover and the report answer different questions and are written"
explain "for different readers. And a resume carries the delta — what was approved since the launch —"
explain "rather than the context again, with an instruction to follow the later of two entries that"
explain "disagree and to say so afterwards."
note "Not shown: 'completed' mode, which publishes only for runs that reached the completion"
note "status. Staging that needs an agent that actually finishes the work; SessionReportMode is"
note "covered directly in the unit tests."

RUN_COMPLETED=true
printf '\n%s%d passed, %d failed%s\n' "$C_BOLD" "$PASS_COUNT" "$FAIL_COUNT" "$C_RESET"
((FAIL_COUNT == 0)) || exit 1
exit 0
