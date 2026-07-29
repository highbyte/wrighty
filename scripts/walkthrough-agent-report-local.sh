#!/usr/bin/env bash
#
# walkthrough-agent-report-local.sh — what an agent is given, and what it sends back.
#
# The context walkthroughs stop at the moment a launch is admitted. This one follows the round trip
# from there: the prompt Wrighty renders, how it reaches the vendor, the report the agent returns,
# and every surface that report can afterwards be read from.
#
# Nothing outside a temporary directory is touched, and nothing is billed. A fake vendor stands in
# for an agent — but unlike the other walkthroughs it captures the prompt it was handed and answers
# with a real report block, because the round trip is the thing being shown.
#
# Local Markdown deliberately: everything here works without a network, and the parts that only
# GitHub can show — publishing a report comment, and a resume carrying only newly approved
# discussion — are in scripts/walkthrough-agent-report-github.sh.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# shellcheck source=scripts/walkthrough-lib.sh
source "$SCRIPT_DIR/walkthrough-lib.sh"

BUILD_CONFIGURATION="Debug"
SKIP_BUILD=false
KEEP_FIXTURE=false
AUTO=false
AGENT="claude"
REAL_AGENT=false

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-agent-report-local.sh [options]" \
        "" \
        "Follows one run end to end on the Local Markdown backend: the prompt Wrighty renders, its" \
        "delivery on standard input, the agent's structured report, and reading that report back" \
        "from the CLI and the dashboard. Uses a disposable store." \
        "" \
        "Options:" \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --real-agent NAME       Run the actual vendor CLI (claude, codex or copilot) instead of" \
        "                          a fake. This one DOES consume your agent quota, and the agent" \
        "                          edits files in the disposable fixture — never your repository." \
        "  --keep-fixture          Do not delete the temporary store on exit." \
        "  --auto                  Run without pausing." \
        "  -h, --help              Show this help."
    return
}

while (($# > 0)); do
    case "$1" in
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
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

case "$AGENT" in
    claude|codex|copilot) ;;
    *) die "unknown agent '$AGENT'; expected claude, codex or copilot" ;;
esac

require_command dotnet
require_command git
require_command jq

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
wt_build_cli "$CLI_PROJECT" "$CLI_DLL" "$SKIP_BUILD" "$BUILD_CONFIGURATION"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-agent-report.XXXXXX")
FIXTURE_REPO="$RUN_ROOT/repo"
FAKE_BIN="$RUN_ROOT/fake-bin"
PROMPT_CAPTURE="$RUN_ROOT/prompt-given-to-agent.txt"
mkdir -p "$FIXTURE_REPO" "$FAKE_BIN"

cleanup() {
    local status=$?
    if [[ "$KEEP_FIXTURE" == true ]]; then
        printf '\nfixture kept at %s\n' "$RUN_ROOT"
    elif [[ "$RUN_ROOT" == *wrighty-agent-report.* ]]; then
        rm -rf "$RUN_ROOT"
    fi
    return $status
}
trap cleanup EXIT

if [[ "$REAL_AGENT" == true ]]; then
    REAL_VENDOR=$(command -v "$AGENT") || die "the '$AGENT' CLI is not on PATH"

    # A tee in front of the real vendor, so a live run still shows what it was handed. Shadowing the
    # vendor's own name is what puts this in the worker's path without the worker knowing.
    cat >"$FAKE_BIN/$AGENT" <<WRAPPER
#!/usr/bin/env bash
set -euo pipefail
tee "\${WRIGHTY_PROMPT_CAPTURE:?}" | "$REAL_VENDOR" "\$@"
WRAPPER
    chmod +x "$FAKE_BIN/$AGENT"

    # A real agent is told to call \`wrighty finish\`, so it needs a wrighty to call. In a real
    # installation that is on PATH already; here the CLI is a build output, so it needs a shim —
    # pinned to this fixture's store so nothing the agent runs can reach another repository.
    cat >"$FAKE_BIN/wrighty" <<SHIM
#!/usr/bin/env bash
set -euo pipefail
cd "$FIXTURE_REPO"
exec dotnet "$CLI_DLL" "\$@"
SHIM
    chmod +x "$FAKE_BIN/wrighty"
else
    # This fake reads its prompt from standard input and keeps it, which is what lets the walkthrough
    # show the delivery rather than assert it. It answers with a report block in the contract's shape.
    cat >"$FAKE_BIN/$AGENT" <<'FAKE_AGENT'
#!/usr/bin/env bash
set -euo pipefail
cat >"${WRIGHTY_PROMPT_CAPTURE:?}"
python3 - <<'PY'
import json
report = {
    "summary": "Added the retry budget setting and wired it through the worker.",
    "changes": ["src/Worker/RetryBudget.cs"],
    "verification": ["dotnet test — 12 passed"],
    "decisions": ["The description and the approved comment disagreed on the cap; followed the comment."],
    "requestedInput": ["Should the cap apply per item or per worker?"],
    "remainingWork": ["The CLI flag is not wired yet"],
    "references": [],
}
text = ("I have made a start but need one decision before finishing.\n\n"
        "```wrighty-report\n" + json.dumps(report, indent=2) + "\n```")
print(json.dumps({"type": "result", "subtype": "success", "is_error": False,
                  "session_id": "report-walkthrough", "result": text}))
PY
FAKE_AGENT
    chmod +x "$FAKE_BIN/$AGENT"
fi

# `wr` comes from walkthrough-lib.sh and runs the CLI inside the fixture store.

# Small, concrete and self-contained, so a real agent can finish it in one run and the walkthrough
# can check the work rather than only the paperwork.
ITEM_BODY="Add a '## Retries' section to README.md in this repository. It should state that a failed \
run is retried at most three times. Keep it to two sentences."

# ---------------------------------------------------------------------------------------------

printf '\n%sWhat an agent is given, and what it sends back%s\n' "$C_BOLD" "$C_RESET"
explain "One run, followed from the prompt Wrighty renders to the report you can read afterwards."
if [[ "$REAL_AGENT" == true ]]; then
    explain "Running the real $AGENT CLI, through a wrapper that keeps a copy of what it was handed."
    explain "This consumes your $AGENT quota. The agent works in a disposable fixture repository."
else
    explain "A fake vendor stands in for an agent: it keeps the prompt it was handed and answers with"
    explain "a real report block, so both halves of the exchange are visible. Nothing is billed."
fi
if [[ "$AUTO" == false ]]; then
    begin_walkthrough
else
    explain "auto mode: no pauses"
fi

step "Provisioning a disposable Local Markdown store"
(
    cd "$FIXTURE_REPO"
    git init -q -b main
    git config user.name "Wrighty walkthrough"
    git config user.email "walkthrough@example.invalid"
    printf '# Report walkthrough fixture\n' >README.md
    git add README.md
    git commit -q -m "Initialize walkthrough fixture"
) || die "failed to initialize the fixture repository"
wr init --backend local-markdown --local-path .wrighty \
    --status Todo --status "In Progress" --status Done \
    --priority P0 --priority P1 --priority P2 --yes >/dev/null 2>&1 || die "wrighty init failed"

ITEM_ID=$(wr create --title "Document the retry behaviour in the README" \
    --body "$ITEM_BODY" \
    --status Todo --priority P1 --auto --agent "$AGENT" --json | jq -r '.result.id')
[[ -n "$ITEM_ID" && "$ITEM_ID" != "null" ]] || die "could not create the item"
pass "created $ITEM_ID"
explain "Store: $FIXTURE_REPO"

step "1. What the agent will be told, before anything runs"
explain "The prompt is not the item. It carries the approved content, a trust boundary saying that"
explain "content is data rather than instructions, and the report contract. You can read it first:"
printf '\n'
wr context "$ITEM_ID" --prompt | sed -n '1,12p' | sed 's/^/    /'
printf '    …\n\n'
PROMPT_BYTES=$(wr context "$ITEM_ID" --prompt | wc -c | tr -d ' ')
pass "the rendered prompt is $PROMPT_BYTES bytes"
explain "Read it in full any time with:  wrighty context $ITEM_ID --prompt"

step "2. Run the worker, and see what actually reached the vendor"
explain "The prompt travels on standard input, never in the command line: an argument list is"
explain "readable by every process on this machine and Wrighty prints it in its own events."
(cd "$FIXTURE_REPO" && env PATH="$FAKE_BIN:$PATH" WRIGHTY_PROMPT_CAPTURE="$PROMPT_CAPTURE" \
    dotnet "$CLI_DLL" worker --json --yes --once --item "$ITEM_ID" --fresh \
    --agent "$AGENT" --workspace-mode current >"$RUN_ROOT/events.jsonl" 2>"$RUN_ROOT/events.err")
jq -r '"    " + .type' <"$RUN_ROOT/events.jsonl" 2>/dev/null | grep -vE 'running|renewed|waiting'

if [[ -s "$PROMPT_CAPTURE" ]]; then
    pass "the vendor received $(wc -c <"$PROMPT_CAPTURE" | tr -d ' ') bytes on standard input"
else
    fail "the vendor received nothing on standard input"
fi
if grep -q 'retried at most three times' "$PROMPT_CAPTURE" 2>/dev/null; then
    pass "it contains the approved description"
else
    fail "the approved description did not reach the agent"
fi
if grep -q 'Trust boundary' "$PROMPT_CAPTURE" 2>/dev/null; then
    pass "and the trust boundary that governs it"
else
    fail "the trust boundary is missing"
fi
ARGS=$(jq -r 'select(.type=="started") | (.arguments // []) | join(" ")' <"$RUN_ROOT/events.jsonl")
if grep -q 'retried at most three times' <<<"$ARGS"; then
    fail "the approved content leaked into the command line: $ARGS"
else
    pass "and none of it appears in the command line: $ARGS"
fi

step "3. What the agent sent back"
explain "The prompt asks for a report block. Wrighty parses it, keeps its own observed outcome, and"
explain "stores the agent's account beside it — whether or not it is published anywhere."
printf '\n'
wr get "$ITEM_ID" | sed -n '/Last run/,/^Session/p' | sed 's/^/    /'
REPORTED=$(wr get "$ITEM_ID" --json | jq -r '.result.session.lastRun.agentReport.summary // empty')
if [[ -n "$REPORTED" ]]; then
    pass "the report is stored and readable: \"$REPORTED\""
else
    fail "no report was stored"
fi
DISPOSITION=$(wr get "$ITEM_ID" --json | jq -r '.result.session.lastRun.disposition // empty')
PROCESS=$(wr get "$ITEM_ID" --json | jq -r '.result.session.lastRun.outcome // empty')
if [[ -n "$DISPOSITION" && -n "$PROCESS" ]]; then
    pass "the two outcomes are reported separately: Wrighty says $DISPOSITION, the vendor says $PROCESS"
else
    fail "the run recorded no disposition of its own, only the vendor's '$PROCESS'"
fi
explain "Those two lines are the trust split in miniature. A vendor exits cleanly whenever it stops"
explain "tidily, including to ask a question — so its result is never a verdict on the work. Only the"
explain "disposition is Wrighty's own, and only Wrighty can decide a run finished."

if [[ "$REAL_AGENT" == true ]]; then
    step "3b. Did the agent actually do the work?"
    explain "A report is an account, not evidence. The fixture is a real repository, so the claim is"
    explain "checkable — this is the check the report cannot make on its own behalf."
    if grep -qi 'retr' "$FIXTURE_REPO/README.md" 2>/dev/null; then
        pass "README.md now mentions retries"
        printf '\n'
        sed 's/^/    /' "$FIXTURE_REPO/README.md"
        printf '\n'
    else
        fail "README.md was not changed, whatever the report says"
    fi
    STATUS_NOW=$(wr get "$ITEM_ID" --json | jq -r '.result.status // empty')
    explain "Item status is now '$STATUS_NOW' — the agent was told to call 'wrighty finish' only when"
    explain "the work is genuinely complete, so this is the agent's own judgement of that."
fi

step "4. Reading it on the dashboard"
explain "The same report appears on the item in the local web dashboard, under the last run."
manual \
    "cd '$FIXTURE_REPO'" \
    "dotnet '$CLI_DLL' web" \
    "" \
    "Open the printed URL, choose '$ITEM_ID', and look for the Agent report block." \
    "Stop the server with Ctrl+C when you are done."
if [[ "$AUTO" == false ]]; then
    pause
else
    explain "auto: skipping the manual dashboard step"
fi

step "5. Recovering a context the agent has lost"
explain "A resumed agent is expected to still hold its context. If it does not, it can ask for the"
explain "one revision it was launched with — and for nothing else."
DIGEST=$(wr context "$ITEM_ID" --json | jq -r '.result.revision.digest')
if wr context "$ITEM_ID" --revision "$DIGEST" >/dev/null 2>&1; then
    pass "the pinned revision is served: ${DIGEST:0:26}…"
else
    fail "the pinned revision was refused"
fi

explain "Now change the item, as a maintainer might while the run is in flight:"
ITEM_FILE=$(find "$FIXTURE_REPO/.wrighty" -name '*.md' -not -name '_*' | head -1)
printf '\nActually, make the cap per worker rather than per item.\n' >>"$ITEM_FILE"
if wr context "$ITEM_ID" --revision "$DIGEST" >"$RUN_ROOT/retrieval.txt" 2>&1; then
    fail "the superseded revision was still served"
else
    pass "the same request is now refused: $(grep -o 'CONTEXT_[A-Z_]*' "$RUN_ROOT/retrieval.txt" | head -1)"
fi
explain "That refusal is the point. An agent can recover what it was approved to have, and can never"
explain "acquire a newer approval, an edited description, or comments nobody has decided on."

step "What this showed"
explain "The agent was given the approved content rather than sent to read the item; it arrived on"
explain "standard input and stayed out of the command line and the events; the report it returned is"
explain "stored and readable without publishing anything; and the outcome remained Wrighty's to"
explain "decide. Publishing that report as an issue comment, and a resume carrying only newly"
explain "approved discussion, need GitHub — see scripts/walkthrough-agent-report-github.sh."

printf '\n%s%d passed, %d failed%s\n' "$C_BOLD" "$PASS_COUNT" "$FAIL_COUNT" "$C_RESET"
((FAIL_COUNT == 0)) || exit 1
exit 0
