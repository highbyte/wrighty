#!/usr/bin/env bash
#
# walkthrough-context-launch-local.sh — approved context on the LOCAL MARKDOWN backend.
#
# The GitHub walkthroughs show a maintainer approving discussion on an issue. There is no such
# gesture here: a Local Markdown store is machine-local and edited by its operator directly, so the
# item's own content is the approved content. That makes this walkthrough about the part an operator
# will actually run into — what happens to a session when the item it was given changes underneath
# it.
#
# Nothing outside a temporary directory is touched. No network, no GitHub, no billing: a fake vendor
# on PATH stands in for an agent and records whether it was executed, which is the assertion worth
# making — not "the worker reported a refusal" but "no agent process came into existence".
#
# The GitHub counterparts are scripts/walkthrough-context-approval-github.sh (how content becomes
# approved) and scripts/walkthrough-context-launch-github.sh (approval deciding a launch).

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# shellcheck source=scripts/walkthrough-lib.sh
source "$SCRIPT_DIR/walkthrough-lib.sh"

BUILD_CONFIGURATION="Debug"
SKIP_BUILD=false
KEEP_FIXTURE=false
AUTO=false

usage() {
    printf '%s\n' \
        "Usage: scripts/walkthrough-context-launch-local.sh [options]" \
        "" \
        "Shows how an approved context behaves on the Local Markdown backend: a self-approved item" \
        "launches, an edit blocks the resume of the session that was given the old text, and a" \
        "fresh session is the way forward. Uses a disposable store; nothing is billed." \
        "" \
        "Options:" \
        "  --configuration NAME    Build configuration; defaults to Debug." \
        "  --skip-build            Use the existing local build output." \
        "  --keep-fixture          Do not delete the temporary store on exit." \
        "  --auto                  Make the edits itself instead of asking you to." \
        "  -h, --help              Show this help."
    return
}

while (($# > 0)); do
    case "$1" in
        --configuration) (($# >= 2)) || die "--configuration requires a value"; BUILD_CONFIGURATION=$2; shift 2 ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        --auto) AUTO=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

require_command dotnet
require_command git
require_command jq

CLI_PROJECT="$REPO_ROOT/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
CLI_DLL="$REPO_ROOT/src/Highbyte.Wrighty.Cli/bin/$BUILD_CONFIGURATION/net10.0/wrighty.dll"
wt_build_cli "$CLI_PROJECT" "$CLI_DLL" "$SKIP_BUILD" "$BUILD_CONFIGURATION"

RUN_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-context-local.XXXXXX")
FIXTURE_REPO="$RUN_ROOT/repo"
FAKE_BIN="$RUN_ROOT/fake-bin"
SPAWN_MARKER="$RUN_ROOT/vendor-spawned"
mkdir -p "$FIXTURE_REPO" "$FAKE_BIN"

cleanup() {
    local status=$?
    if [[ "$KEEP_FIXTURE" == true ]]; then
        printf '\nfixture kept at %s\n' "$RUN_ROOT"
    elif [[ "$RUN_ROOT" == *wrighty-context-local.* ]]; then
        rm -rf "$RUN_ROOT"
    fi
    return $status
}
trap cleanup EXIT

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

wr_worker() {
    local events="$RUN_ROOT/worker.jsonl"
    rm -f "$SPAWN_MARKER"
    (cd "$FIXTURE_REPO" && env PATH="$FAKE_BIN:$PATH" \
        WRIGHTY_TEST_SPAWN_MARKER="$SPAWN_MARKER" \
        dotnet "$CLI_DLL" worker --json --yes --once --item "$ITEM_ID" "$@" \
            --agent claude --workspace-mode current >"$events" 2>"$events.stderr")
    jq -r '"    " + .type + " | " + ((.message // "") | .[0:150])' <"$events" 2>/dev/null |
        grep -vE "^\s+(running|renewed|waiting) " || sed 's/^/    /' "$events"
    return
}

vendor_started() { [[ -f "$SPAWN_MARKER" ]]; }

# The refusal code the worker reported, from the skipped-policy event's message.
refusal_code() {
    grep -o 'CONTEXT_[A-Z_]*' "$RUN_ROOT/worker.jsonl" 2>/dev/null | head -1
    return
}

item_file() {
    find "$FIXTURE_REPO/.wrighty" -name '*.md' -not -name '_*' 2>/dev/null | head -1
    return
}

# ---------------------------------------------------------------------------------------------
# Walkthrough
# ---------------------------------------------------------------------------------------------

printf '\n%sApproved context on the Local Markdown backend%s\n' "$C_BOLD" "$C_RESET"
explain "There is no approval field here. The store is yours and you edit it directly, so the item's"
explain "own content is the approved content — it needs no separate gesture to become approved."
explain "What does change is what happens to a session after you edit the item it was given."
explain "A fake vendor stands in for an agent; nothing here is billed."
if [[ "$AUTO" == false ]]; then
    begin_walkthrough
else
    explain "auto mode: making the edits itself, with no pauses"
fi

step "Provisioning a disposable Local Markdown store"
explain "Location: $FIXTURE_REPO"
(
    cd "$FIXTURE_REPO"
    git init -q -b main
    git config user.name "Wrighty walkthrough"
    git config user.email "walkthrough@example.invalid"
    printf '# Context walkthrough fixture\n\nDisposable store for manual testing.\n' >README.md
    git add README.md
    git commit -q -m "Initialize walkthrough fixture"
) || die "failed to initialize the fixture git repository"

(cd "$FIXTURE_REPO" && dotnet "$CLI_DLL" init --backend local-markdown --local-path .wrighty \
    --status Todo --status "In Progress" --status Done \
    --priority P0 --priority P1 --priority P2 --yes >/dev/null 2>&1) ||
    die "wrighty init failed"

ITEM_ID=$((cd "$FIXTURE_REPO" && dotnet "$CLI_DLL" create \
    --title "Add a retry note to the README" \
    --body "Describe the worker's retry behaviour in one short paragraph." \
    --status Todo --priority P1 --auto --agent claude \
    --json) | jq -r '.result.id') || die "could not create the item"
[[ -n "$ITEM_ID" && "$ITEM_ID" != "null" ]] || die "could not read the created item id"
ITEM_FILE=$(item_file)
pass "created $ITEM_ID"
explain "Its Markdown file: $ITEM_FILE"

step "1. A local item is its own approved context"
explain "No field was set and nothing was approved by hand. Ask Wrighty what context this item has:"
printf '\n'
(cd "$FIXTURE_REPO" && dotnet "$CLI_DLL" context "$ITEM_ID" 2>&1 | sed 's/^/    /')
printf '\n'
explain "The approval source reads backend-local, which is how a reader tells this apart from a"
explain "maintainer having approved a revision on a tracker. The discussion is empty rather than"
explain "invented: a store with no comments has no comments to approve."
CONTEXT_SOURCE=$((cd "$FIXTURE_REPO" && dotnet "$CLI_DLL" context "$ITEM_ID" --json) |
    jq -r '.result.approval.source // "MISSING"')
if [[ "$CONTEXT_SOURCE" == "backend-local" ]]; then
    pass "the item is approved by the backend itself ($CONTEXT_SOURCE)"
else
    fail "expected a backend-local approval, got '$CONTEXT_SOURCE'"
fi

step "2. The worker launches, because that context is approved"
wr_worker --fresh
if vendor_started; then
    pass "the agent ran, and asked for attention rather than finishing"
else
    fail "the agent did not start on a self-approved item"
fi

step "3. What the launch recorded"
explain "Before starting the agent, the launch recorded what it supplied — hashes and identifiers"
explain "only, never the text. That record is what a later resume is judged against."
DIGEST=$(jq -r --arg id "$ITEM_ID" \
    '.items[] | select(.session != null) | .context.manifest.digest // empty' \
    "$FIXTURE_REPO/.wrighty/.wrighty-runtime-v1.json" 2>/dev/null | head -1)
if [[ -n "$DIGEST" ]]; then
    pass "recorded with the session: ${DIGEST:0:26}…"
else
    fail "the agent ran but no context was recorded, so this session could never resume"
fi

step "4. Resuming an unchanged item"
explain "Nothing has changed, so the resume is judged against a context identical to the one the"
explain "session already holds."
wr_worker
if vendor_started; then
    pass "the session resumed"
else
    fail "the resume did not start the agent: $(refusal_code)"
fi

step "5. Now edit the item — this is the part that is new"
explain "On GitHub an edit is caught because it happens after an approval. Here there is no approval"
explain "to fall behind, so the item's title and body hashes are the whole of the evidence: change"
explain "either and the running session is holding text that no longer exists."
if [[ "$AUTO" == true ]]; then
    # Edited in the file rather than through `wrighty edit`, which is claim-fenced and would refuse
    # while the session holds the item. That is the point: a local operator edits their own
    # Markdown, and the store has no way to stop them.
    explain "auto: rewriting the body in $ITEM_FILE"
    printf '\nActually, describe the usage-exhaustion behaviour instead.\n' >>"$ITEM_FILE" ||
        die "could not edit the item file"
else
    manual \
        "Open the item's Markdown file in your editor:" \
        "  $ITEM_FILE" \
        "" \
        "Change its body text to something different — a sentence is enough — and save." \
        "Leave the front matter alone; the status and policy fields are not what this is about."
    pause
fi
pass "the item now says something different from what the running session was given"

step "6. Resuming it yourself proceeds — and says that it did"
explain "You edited this item and you are asking for it by name, so you have already made the"
explain "judgement the rule would otherwise make for you. The resume goes ahead."
explain "It is not silent about it: the run reports that it continued across a change an unattended"
explain "worker would have refused, so the log never reads as though nothing had changed."
wr_worker
OVERRIDE=$(grep -o 'CONTEXT_RESUME_SUPERSEDED' "$RUN_ROOT/worker.jsonl" 2>/dev/null | head -1)
if ! vendor_started; then
    fail "the operator-requested resume was refused: $(refusal_code)"
elif [[ -n "$OVERRIDE" ]]; then
    pass "resumed, and reported it as $OVERRIDE"
else
    fail "the session resumed across an edit without reporting it"
fi

step "7. An unattended worker would not have done that"
explain "The rule this relaxes is about unattended behaviour, so it still holds where nobody has"
explain "decided anything — an automatic retry after a provider limit, for instance, resumes a"
explain "session Wrighty scheduled itself, and refuses across an edit like this one."
note "That path needs a real exhausted provider account to stage, so it is not run here;"
note "ExecutionContextLaunchCheckTests covers it directly."
explain "A fresh session is always available too, and starts from the current text:"
# --override because the paused session still holds a resumable claim, which is the whole reason
# it could be resumed at all. Failing here silently would make the next step look like a context
# refusal when it is really CLAIM_HELD.
(cd "$FIXTURE_REPO" && dotnet "$CLI_DLL" release "$ITEM_ID" --override --yes >/dev/null 2>&1) ||
    die "could not release the paused claim before starting a fresh session"
wr_worker --fresh
if vendor_started; then
    pass "a fresh session started on the edited item"
else
    fail "a fresh session was refused as well: $(refusal_code)"
fi

step "What this shows about the Local Markdown backend"
explain "Nothing new to approve: an item is approved by being in your store."
explain "Editing an item you are not running is free. Editing one whose session is paused is a"
explain "decision — yours to make by resuming it, and reported when you do."
note "Unlike GitHub there is no discussion to append a clarification to, so editing the item is"
note "the only way to clarify one here. That is why an operator-requested resume is allowed to"
note "carry it: forbidding it would leave local sessions no way to be clarified at all."

printf '\n%s%d passed, %d failed%s\n' "$C_BOLD" "$PASS_COUNT" "$FAIL_COUNT" "$C_RESET"
((FAIL_COUNT == 0)) || exit 1
exit 0
