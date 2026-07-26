#!/usr/bin/env bash
#
# prototype-session-context-retention.sh — does a RESUMED vendor session still hold turn 1?
#
# This settles the push-vs-pull question for plan 030's resume prompts. If a resumed session
# reliably retains the context supplied at launch, then re-sending the whole approved snapshot on
# every resume is pure duplication, and a change manifest plus on-demand retrieval is correct. If
# vendors silently compact turn 1 away, the resume prompt has to carry more.
#
# Method, per vendor:
#   1. Start a session whose FIRST prompt carries three random UUID sentinels — one near the start
#      of the content, one in the middle, one near the end — plus one semantic fact. The content is
#      padded to a realistic approved-context size (default 100000 characters, plan 030's proposed
#      maxTotalCharacters).
#   2. Resume it N times with a trivial prompt that never repeats the sentinels.
#   3. On the final resume, ask for all three sentinels verbatim and for the semantic fact.
#   4. Score which sentinels came back.
#
# The four outcomes are what actually drive the design decision:
#   retained    all three sentinels verbatim  -> pull is safe; re-sending is waste
#   partial     some sentinels, position-dependent -> the U-shaped attention effect is real here
#   gist        no sentinels but the semantic fact survives -> compaction summarised turn 1;
#               verbatim requirements (exact acceptance criteria) cannot be trusted to survive
#   lost        neither -> the resume prompt must carry the context
#
# Probe validity: the sentinels are random UUIDs that exist ONLY in the conversation. They are
# never written to disk and never appear in a file the agent could read, so a correct answer
# cannot come from tool use — only from retained context. The session runs in an empty temporary
# directory for the same reason.
#
# LIVE and BILLED: this starts real vendor sessions and spends real agent turns. Set
# WRIGHTY_RUN_SESSION_RETENTION_LIVE=1 to acknowledge that.
#
# WHEN TO RE-RUN
#   * After upgrading any vendor CLI. Context management changes without announcement, and this is
#     the most fragile premise in plan 030: if resumed sessions stop retaining their launch context,
#     decision 20's delta resume degrades SILENTLY — the agent simply has less than the design
#     assumes, and nothing else in the repository would notice.
#   * Before phase 5 implements the delta resume prompt, to confirm the premise still holds.
#   * To fill in Claude under --pressure, which phase 0 could not measure (the API returned 529 on
#     every attempt).
#
# This is a premise check, not a Wrighty regression test. A failure does not mean the code broke;
# it means an external assumption changed and the plan decision resting on it needs revisiting —
# see finding F8. Never wire this into CI: every run spends real agent turns.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

AGENTS="claude codex copilot"
CONTEXT_CHARS=100000
RESUME_TURNS=8
PRESSURE_CHARS=0
KEEP_TRANSCRIPTS=false
RECORD_PATH="$REPO_ROOT/.wrighty-prototype/session-retention-observations.json"

usage() {
    printf '%s\n' \
        "Usage: scripts/prototype-session-context-retention.sh [options]" \
        "" \
        "Measure whether a resumed vendor session still holds the context supplied at launch." \
        "LIVE and BILLED: set WRIGHTY_RUN_SESSION_RETENTION_LIVE=1 to acknowledge." \
        "" \
        "Options:" \
        "  --agents \"a b\"      Vendors to probe; defaults to 'claude codex copilot'." \
        "  --context-chars N   Size of the turn-1 context; defaults to 100000." \
        "  --resume-turns N    Trivial resume turns before the recall turn; defaults to 8." \
        "  --pressure N        Pad each resume turn with N characters to force the window toward" \
        "                      compaction. Default 0 keeps resumes trivial, which measures the" \
        "                      BEST case for retention. Use this to characterise the failure mode." \
        "  --keep-transcripts  Preserve raw vendor output for inspection." \
        "  -h, --help          Show this help."
    return
}

log() { printf 'retention: %s\n' "$*" >&2; return; }
die() { printf 'retention: error: %s\n' "$*" >&2; exit 1; }

while (($# > 0)); do
    case "$1" in
        --agents) (($# >= 2)) || die "--agents requires a value"; AGENTS=$2; shift 2 ;;
        --context-chars) (($# >= 2)) || die "--context-chars requires a value"; CONTEXT_CHARS=$2; shift 2 ;;
        --resume-turns) (($# >= 2)) || die "--resume-turns requires a value"; RESUME_TURNS=$2; shift 2 ;;
        --pressure) (($# >= 2)) || die "--pressure requires a character count"; PRESSURE_CHARS=$2; shift 2 ;;
        --keep-transcripts) KEEP_TRANSCRIPTS=true; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

[[ "${WRIGHTY_RUN_SESSION_RETENTION_LIVE:-}" == "1" ]] ||
    die "set WRIGHTY_RUN_SESSION_RETENTION_LIVE=1 to acknowledge this starts real, billed vendor sessions"

command -v jq >/dev/null 2>&1 || die "required command 'jq' was not found"

WORK_DIR=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-retention.XXXXXX")
TRANSCRIPTS="$WORK_DIR/transcripts"
mkdir -p "$TRANSCRIPTS"

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$KEEP_TRANSCRIPTS" == true ]]; then
        printf '\nKept transcripts: %s\n' "$TRANSCRIPTS" >&2
        exit "$status"
    fi
    case "$WORK_DIR" in
        "${TMPDIR:-/tmp}"/wrighty-retention.*) rm -rf "$WORK_DIR" ;;
        *) printf 'refusing to remove unexpected path %s\n' "$WORK_DIR" >&2 ;;
    esac
    exit "$status"
}
trap cleanup EXIT

OBSERVATIONS=()

new_uuid() {
    if command -v uuidgen >/dev/null 2>&1; then
        uuidgen | tr '[:upper:]' '[:lower:]'
    else
        od -x /dev/urandom | head -1 | awk '{print $2$3"-"$4"-"$5"-"$6"-"$7$8$9}'
    fi
    return
}

FILLER_UNIT="This line is padding that represents ordinary approved discussion text. "

make_filler() {
    local want=$1 out="" unit_len=${#FILLER_UNIT}
    local repeats=$(( want / unit_len + 1 ))
    out=$(printf "%${repeats}s" "" | sed "s/ /$FILLER_UNIT/g")
    printf '%s' "${out:0:want}"
    return
}

# Builds the turn-1 prompt: sentinels at three positions inside padded content that stands in for
# an approved issue body and comment set.
build_initial_prompt() {
    local first=$1 middle=$2 last=$3 fact=$4 target=$5
    local per_block=$(( (target - 2000) / 3 ))
    ((per_block > 0)) || per_block=200

    cat <<PROMPT
You are taking part in a context-retention measurement. Do not use any tools. Do not write files.
Simply acknowledge this message by replying exactly: ACKNOWLEDGED

The following is a simulated approved work-item context. Read it and retain it.

=== BEGIN APPROVED CONTEXT ===
SENTINEL-ALPHA: $first

$(make_filler "$per_block")

SENTINEL-BRAVO: $middle

$(make_filler "$per_block")

REQUIREMENT NOTE: the retry-limit ticket for this task is $fact.

$(make_filler "$per_block")

SENTINEL-CHARLIE: $last
=== END APPROVED CONTEXT ===

Reply with exactly: ACKNOWLEDGED
PROMPT
    return
}

RECALL_PROMPT='Without using any tools, answer from the earlier conversation only.
1. Quote the value of SENTINEL-ALPHA exactly.
2. Quote the value of SENTINEL-BRAVO exactly.
3. Quote the value of SENTINEL-CHARLIE exactly.
4. Quote the retry-limit ticket given in the REQUIREMENT NOTE exactly.
If you cannot find one of these in the conversation, write UNAVAILABLE for that item. Do not guess.'

TRIVIAL_PROMPT='Reply with exactly: OK'

# A resume turn. With --pressure it carries filler so the window actually grows, which is what a
# real loop does through tool calls and results. The filler is varied per turn so it cannot be
# collapsed by caching or deduplication — identical padding would understate the pressure.
resume_prompt() {
    local turn=$1
    if ((PRESSURE_CHARS <= 0)); then
        printf '%s' "$TRIVIAL_PROMPT"
        return
    fi
    printf 'Turn %s. The block below is filler standing in for accumulated tool output. Ignore its contents.\n\n=== FILLER %s ===\n%s\n=== END FILLER %s ===\n\nReply with exactly: OK' \
        "$turn" "$turn" "$(make_filler "$PRESSURE_CHARS")" "$turn"
}

# --- vendor drivers ---------------------------------------------------------------------------
# Each returns the session id on stdout for start_*, and raw output for resume_*.

start_claude() {
    local prompt=$1 session=$2 out=$3
    claude -p "$prompt" --session-id "$session" --output-format json >"$out" 2>&1
    printf '%s' "$session"
    return
}
resume_claude() {
    local prompt=$1 session=$2 out=$3
    claude -p "$prompt" --resume "$session" --output-format json >"$out" 2>&1
    return
}

start_codex() {
    local prompt=$1 _session=$2 out=$3
    codex exec --json --skip-git-repo-check --sandbox read-only -C "$WORK_DIR" "$prompt" >"$out" 2>&1
    # Codex assigns its own thread id and announces it as thread.started.
    grep -o '"thread_id":"[^"]*"' "$out" | head -1 | cut -d'"' -f4
    return
}
resume_codex() {
    local prompt=$1 session=$2 out=$3
    codex exec --json --skip-git-repo-check --sandbox read-only -C "$WORK_DIR" \
        resume "$session" "$prompt" >"$out" 2>&1
    return
}

start_copilot() {
    local prompt=$1 session=$2 out=$3
    copilot -p "$prompt" -n "$session" --output-format json --no-remote -C "$WORK_DIR" >"$out" 2>&1
    printf '%s' "$session"
    return
}
resume_copilot() {
    local prompt=$1 session=$2 out=$3
    copilot -p "$prompt" "--resume=$session" --output-format json --no-remote -C "$WORK_DIR" >"$out" 2>&1
    return
}

# A failed turn must never be scored. An overloaded or errored vendor call produces empty output,
# which greps as "no sentinel found" and would be recorded as a confident LOST that never happened.
# A false RETAINED is not possible — a failed call cannot emit a UUID it never saw — so this only
# needs to catch the negative direction, but it must catch it reliably.
turn_failed() {
    local out=$1
    [[ -s "$out" ]] || return 0
    grep -qE '"is_error"[[:space:]]*:[[:space:]]*true|"api_error_status"|API Error: [0-9]+|"type"[[:space:]]*:[[:space:]]*"error"|error: ' "$out" && return 0
    return 1
}

# A context-overflow refusal is a RESULT, not a failed measurement: it says the loop breaks loudly
# rather than degrading silently. Retrying it is pointless and scoring it as a transient error
# would discard the finding.
turn_overflowed() {
    local out=$1
    [[ -s "$out" ]] || return 1
    grep -qiE 'context (window|length|limit)|maximum context|too (many tokens|long)|exceeds? .{0,20}(token|context)|prompt is too large|input length' "$out"
}

# Runs one turn, retrying a transient vendor failure a few times before giving up.
# Returns 0 on success, 2 on a context-overflow refusal, 1 on a transient failure that outlived
# its retries.
run_turn() {
    local fn=$1 prompt=$2 session=$3 out=$4 attempt
    for attempt in 1 2 3; do
        "$fn" "$prompt" "$session" "$out" || true
        turn_failed "$out" || return 0
        turn_overflowed "$out" && return 2
        ((attempt < 3)) && { log "  transient vendor failure; retry $attempt/2 in 20s"; sleep 20; }
    done
    return 1
}

observe() {
    local agent=$1 verdict=$2 detail=$3 evidence=$4
    OBSERVATIONS+=("$(jq -n --arg a "$agent" --arg v "$verdict" --arg d "$detail" --arg e "$evidence" \
        '{agent:$a, verdict:$v, detail:$d, evidence:$e}')")
    local marker
    case "$verdict" in
        retained) marker="RETAINED" ;;
        partial)  marker="PARTIAL " ;;
        gist)     marker="GIST    " ;;
        lost)     marker="LOST    " ;;
        overflow) marker="OVERFLOW" ;;
        *)        marker="ERROR   " ;;
    esac
    printf '  [%s] %-9s %s\n' "$marker" "$agent" "$detail"
    [[ -n "$evidence" ]] && printf '            %s\n' "$evidence"
    return
}

probe_agent() {
    local agent=$1
    command -v "$agent" >/dev/null 2>&1 || {
        observe "$agent" "error" "$agent is not installed; skipped." ""
        return
    }

    local alpha bravo charlie fact session prompt out
    alpha=$(new_uuid); bravo=$(new_uuid); charlie=$(new_uuid)
    # A distinctive token, NOT a bare number. A two-digit value collides by chance with timestamps,
    # token counts, and message ids in the raw vendor JSON, which scores a total loss as a partial
    # "gist" survival — the harness must not be able to find the fact unless the model wrote it.
    fact="RL-$(printf '%06d' $((RANDOM * 30 + RANDOM % 30000)))"
    session=$(new_uuid)
    prompt=$(build_initial_prompt "$alpha" "$bravo" "$charlie" "$fact" "$CONTEXT_CHARS")

    log "$agent: starting a session with ${#prompt} characters of context"
    out="$TRANSCRIPTS/$agent-turn-01.out"
    local attempt started=""
    for attempt in 1 2 3; do
        started=$("start_$agent" "$prompt" "$session" "$out") || true
        if [[ -n "$started" ]] && ! turn_failed "$out"; then break; fi
        started=""
        ((attempt < 3)) && { log "  transient vendor failure on start; retry $attempt/2 in 20s"; sleep 20; }
    done
    if [[ -z "$started" ]]; then
        observe "$agent" "error" \
            "Could not start a session after retries; nothing was measured." \
            "$(grep -oE 'API Error: [0-9]+[^"]*' "$out" | head -1 || tail -c 200 "$out" 2>/dev/null | tr '\n' ' ')"
        return
    fi
    session=$started

    local turn
    for ((turn = 1; turn <= RESUME_TURNS; turn++)); do
        out=$(printf '%s/%s-turn-%02d.out' "$TRANSCRIPTS" "$agent" "$((turn + 1))")
        run_turn "resume_$agent" "$(resume_prompt "$turn")" "$session" "$out"
        case $? in
            0) ;;
            2)
                observe "$agent" "overflow" \
                    "The session refused resume turn $turn on context length. The loop fails loudly rather than degrading silently — but it fails, so an unbounded continuation loop has a hard ceiling." \
                    "survived $((turn - 1)) padded resume(s) at ${PRESSURE_CHARS}c each on top of ${CONTEXT_CHARS}c"
                return
                ;;
            *)
                observe "$agent" "error" \
                    "Resume turn $turn failed after retries; the session is incomplete and cannot be scored." \
                    "$(grep -oE 'API Error: [0-9]+[^"]*|"type"[[:space:]]*:[[:space:]]*"error"[^,]*' "$out" | head -1)"
                return
                ;;
        esac
        log "$agent: resume turn $turn/$RESUME_TURNS"
    done

    out=$(printf '%s/%s-recall.out' "$TRANSCRIPTS" "$agent")
    run_turn "resume_$agent" "$RECALL_PROMPT" "$session" "$out"
    case $? in
        0) ;;
        2)
            observe "$agent" "overflow" \
                "The session refused the recall turn on context length after surviving every padded resume." \
                "context: ${CONTEXT_CHARS}c + ${RESUME_TURNS}x${PRESSURE_CHARS}c"
            return
            ;;
        *)
            observe "$agent" "error" \
                "The recall turn failed after retries; scoring it would record a LOST that never happened." \
                "$(grep -oE 'API Error: [0-9]+[^"]*' "$out" | head -1)"
            return
            ;;
    esac

    local found=0 positions=""
    grep -qF "$alpha" "$out" && { found=$((found + 1)); positions="${positions}alpha "; }
    grep -qF "$bravo" "$out" && { found=$((found + 1)); positions="${positions}bravo "; }
    grep -qF "$charlie" "$out" && { found=$((found + 1)); positions="${positions}charlie "; }
    local has_fact=false
    grep -qF "$fact" "$out" && has_fact=true

    local evidence="recalled: ${positions:-none}; retry-limit fact: $has_fact; turns: $((RESUME_TURNS + 2)); context: ${CONTEXT_CHARS}c"
    ((PRESSURE_CHARS > 0)) &&
        evidence="$evidence; pressure: ${PRESSURE_CHARS}c/turn (~$((PRESSURE_CHARS * RESUME_TURNS + CONTEXT_CHARS))c total)"
    if ((found == 3)); then
        observe "$agent" "retained" \
            "All three sentinels came back verbatim after $RESUME_TURNS resumes. Re-sending the snapshot on resume would be duplication." \
            "$evidence"
    elif ((found > 0)); then
        observe "$agent" "partial" \
            "Only some sentinels survived, so retention is position-dependent — verbatim requirements cannot be assumed to survive a resume." \
            "$evidence"
    elif [[ "$has_fact" == true ]]; then
        observe "$agent" "gist" \
            "No sentinel survived verbatim but the requirement fact did: turn 1 was summarised, not retained. Exact acceptance criteria must be re-supplied or retrievable." \
            "$evidence"
    else
        observe "$agent" "lost" \
            "Neither the sentinels nor the requirement fact survived; the resume prompt must carry the approved context." \
            "$evidence"
    fi
}

printf '\nSession context retention after resume\n\n'
for agent in $AGENTS; do
    probe_agent "$agent"
done

mkdir -p "$(dirname "$RECORD_PATH")"

# A narrow or failed run must not clobber a broader measured one. The validation runs that probe a
# single vendor would otherwise silently replace a full three-vendor record with a worse one.
if [[ -f "$RECORD_PATH" ]]; then
    EXISTING_SCORED=$(jq -r '.scored // ([.observations[] | select(.verdict != "error")] | length)' \
        "$RECORD_PATH" 2>/dev/null || printf '0')
    THIS_SCORED=$(printf '%s\n' "${OBSERVATIONS[@]}" |
        jq -s '[.[] | select(.verdict != "error")] | length')
    if ((THIS_SCORED < EXISTING_SCORED)); then
        RECORD_PATH="${RECORD_PATH%.json}.partial.json"
        log "this run scored $THIS_SCORED agent(s) against an existing record's $EXISTING_SCORED;"
        log "writing to $(basename "$RECORD_PATH") rather than replacing the broader result"
    fi
fi

printf '%s\n' "${OBSERVATIONS[@]}" | jq -s \
    --arg turns "$RESUME_TURNS" --arg chars "$CONTEXT_CHARS" --arg pressure "$PRESSURE_CHARS" \
    '{
       purpose: "plan 030 resume prompt: does a resumed vendor session retain the launch context?",
       resumeTurns: ($turns | tonumber),
       contextCharacters: ($chars | tonumber),
       pressureCharactersPerTurn: ($pressure | tonumber),
       measures: (if ($pressure | tonumber) > 0
                  then "failure mode under window pressure"
                  else "best case: trivial resume turns, no accumulated tool output" end),
       observations: .,
       scored: (. | map(select(.verdict != "error")) | length),
       errored: (. | map(select(.verdict == "error")) | length),
       verdict: (if (. | map(select(.verdict != "error")) | length) == 0
                 then "inconclusive-no-agent-was-measured"
                 elif (. | map(select(.verdict == "overflow")) | length) > 0
                 then "loop-has-a-hard-context-ceiling"
                 elif (. | map(select(.verdict == "lost" or .verdict == "gist")) | length) > 0
                 then "resume-must-carry-context"
                 elif (. | map(select(.verdict == "retained")) | length) == (. | map(select(.verdict != "error")) | length)
                 then "pull-is-safe"
                 else "mixed" end)
     }' >"$RECORD_PATH"

printf '\n'
log "observation record written to $RECORD_PATH"
log "verdict: $(jq -r .verdict "$RECORD_PATH")"
