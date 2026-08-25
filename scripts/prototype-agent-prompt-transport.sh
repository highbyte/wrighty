#!/usr/bin/env bash
#
# prototype-agent-prompt-transport.sh — plan 030 phase 0 vendor prompt-transport prototype.
#
# Plan 030 decision 14 stops Wrighty putting approved issue bodies and discussion into vendor
# command-line arguments: argv is size-limited and visible in process listings and diagnostics.
# The preferred transport order is vendor standard input, then a private machine-local temporary
# file, then argv for small fixed control text only.
#
# This script measures what each installed vendor actually supports so the observation record can
# name one selected safe transport per adapter instead of assuming one. Wrighty's current adapters
# use standard input, but the probe remains useful when a vendor CLI changes that contract.
#
# Two tiers, because one of them costs money:
#
#   Tier 1 (default, free): platform argv ceiling measured against a real exec, vendor versions,
#           and each CLI's own help text scanned for stdin / prompt-file support. No agent session
#           is started, so nothing is billed.
#   Tier 2 (--live): actually starts a short agent session per vendor per transport and checks the
#           reply and JSON output. This SPENDS REAL AGENT TOKENS on every installed vendor. Set
#           WRIGHTY_RUN_AGENT_TRANSPORT_LIVE=1 to acknowledge that.
#
# Results are appended to an observation record shared with the GitHub prototype gate.
#
# WHEN TO RE-RUN
#   * After upgrading any vendor CLI. Finding F7 recorded standard input working for Claude and
#     Codex and unavailable for Copilot; if Copilot gains a stdin or prompt-file path, the phase 5
#     observation should be refreshed before changing an adapter's transport.
#   * Before phase 5 wires the prompt transport, to confirm the selected transport per adapter.
#
# This is a premise check, not a Wrighty regression test. A failure does not mean the code broke;
# it means a vendor changed what it accepts. Do not wire the --live tier into CI: it spends real
# agent tokens on every installed vendor.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

VENDORS=(claude codex copilot opencode)
RECORD_PATH="$REPO_ROOT/.wrighty-prototype/prompt-transport-observations.json"
LIVE=false
WORKDIR=""

die() { printf 'transport: error: %s\n' "$*" >&2; exit 1; }
log() { printf 'transport: %s\n' "$*" >&2; }

usage() {
    cat >&2 <<'USAGE'
Usage: scripts/prototype-agent-prompt-transport.sh [options]

Measures how each installed agent CLI can receive a worker prompt (plan 030 phase 0, decision 14).

Options:
  --vendor NAME     Probe only this vendor (repeatable). Default: claude, codex, copilot, opencode.
  --record PATH     Where to write the observation record.
  --live            Also start a real agent session per vendor per transport. SPENDS TOKENS.
                    Requires WRIGHTY_RUN_AGENT_TRANSPORT_LIVE=1.
  -h, --help        Show this help.
USAGE
}

SELECTED=()
while (($# > 0)); do
    case "$1" in
        --vendor) (($# >= 2)) || die "--vendor requires a name"; SELECTED+=("$2"); shift 2 ;;
        --record) (($# >= 2)) || die "--record requires a path"; RECORD_PATH=$2; shift 2 ;;
        --live) LIVE=true; shift ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done
((${#SELECTED[@]} > 0)) && VENDORS=("${SELECTED[@]}")

command -v jq >/dev/null 2>&1 || die "required command 'jq' was not found"

if [[ "$LIVE" == true ]]; then
    [[ "${WRIGHTY_RUN_AGENT_TRANSPORT_LIVE:-}" == "1" ]] ||
        die "set WRIGHTY_RUN_AGENT_TRANSPORT_LIVE=1 to acknowledge that --live starts real, billed agent sessions"
fi

OBSERVATIONS=()

observe() {
    local id=$1 vendor=$2 verdict=$3 summary=$4 evidence=${5:-}
    OBSERVATIONS+=("$(jq -nc \
        --arg id "$id" --arg vendor "$vendor" --arg verdict "$verdict" \
        --arg summary "$summary" --arg evidence "$evidence" \
        '{id: $id, vendor: $vendor, verdict: $verdict, summary: $summary, evidence: $evidence}')")
    local marker
    case "$verdict" in
        pass) marker="PASS " ;;
        fail) marker="FAIL " ;;
        manual) marker="MANUAL" ;;
        skip) marker="SKIP " ;;
        *) marker="OPEN " ;;
    esac
    printf '  [%s] %-30s %s\n' "$marker" "$id" "$summary" >&2
    [[ -n "$evidence" ]] && printf '           %s\n' "$evidence" >&2
    return 0
}

cleanup() { [[ -n "$WORKDIR" && -d "$WORKDIR" ]] && rm -rf "$WORKDIR"; }
trap cleanup EXIT

WORKDIR=$(mktemp -d "${TMPDIR:-/tmp}/wrighty-transport-XXXXXX") || die "could not create a scratch directory"

# ---------------------------------------------------------------------------------------------
# Platform argv ceiling
# ---------------------------------------------------------------------------------------------
# Decision 14's first claim is that a full discussion can exceed the argv limit. Measure the real
# exec ceiling on this host rather than quoting ARG_MAX, which overstates what a single argument
# plus environment can carry.

probe_argv_ceiling() {
    local declared low=1024 high=$((8 * 1024 * 1024)) mid best=0
    declared=$(getconf ARG_MAX 2>/dev/null || echo "unknown")

    while ((low <= high)); do
        mid=$(((low + high) / 2))
        if /usr/bin/env true "$(head -c "$mid" /dev/zero | tr '\0' 'x')" 2>/dev/null; then
            best=$mid
            low=$((mid + 1))
        else
            high=$((mid - 1))
        fi
    done

    local limits="declared ARG_MAX=$declared; largest single argument accepted by exec=$best bytes"
    # Plan 030's initial proposed aggregate limit is 100,000 characters of approved context.
    if ((best < 100000)); then
        observe "T0-argv-ceiling" "platform" "fail" \
            "A single argument cannot carry the plan's proposed 100,000-character context limit; argv transport is unusable for full context." \
            "$limits"
    else
        observe "T0-argv-ceiling" "platform" "open" \
            "A single argument can hold the proposed 100,000-character limit on this host, but argv still exposes content in process listings and is not portable across hosts. Decision 14's preference order stands on disclosure grounds, not only on size." \
            "$limits"
    fi
}

# ---------------------------------------------------------------------------------------------
# Per-vendor capability scan (free)
# ---------------------------------------------------------------------------------------------

vendor_version() {
    local vendor=$1
    "$vendor" --version 2>/dev/null | head -1 || printf 'unknown\n'
}

vendor_help() {
    local vendor=$1
    if [[ "$vendor" == "opencode" ]]; then
        { opencode --help 2>&1; opencode run --help 2>&1; } | tr -d '\r'
    else
        { "$vendor" --help 2>&1; "$vendor" exec --help 2>&1; } | tr -d '\r'
    fi
}

probe_vendor_capabilities() {
    local vendor=$1

    if ! command -v "$vendor" >/dev/null 2>&1; then
        observe "T1-$vendor-installed" "$vendor" "skip" \
            "$vendor is not installed on this host; its transport must be measured where it is." ""
        return 1
    fi

    local version help
    version=$(vendor_version "$vendor")
    help=$(vendor_help "$vendor")
    observe "T1-$vendor-installed" "$vendor" "pass" "$vendor is installed." "version=$version"

    # Standard input — decision 14's first choice.
    local stdin_evidence
    stdin_evidence=$(grep -iE -- '(^|[^a-z])(stdin|standard input|read from|--input|[[:space:]]-[[:space:]])' \
        <<<"$help" | sed 's/^[[:space:]]*//' | sort -u | head -6)
    if [[ -n "$stdin_evidence" ]]; then
        observe "T2-$vendor-stdin-documented" "$vendor" "open" \
            "$vendor's help mentions standard input; confirm with --live that a piped prompt runs headlessly and still emits parsable JSON." \
            "$(tr '\n' '|' <<<"$stdin_evidence")"
    else
        observe "T2-$vendor-stdin-documented" "$vendor" "fail" \
            "$vendor's help documents no standard-input prompt path; decision 14's preferred transport may be unavailable for this adapter." \
            "no stdin-shaped option found in --help"
    fi

    # A prompt file the vendor itself reads. If no vendor supports one, the temporary-file option
    # in decision 14 means "a file the AGENT is pointed at", which is a different contract with
    # different cleanup and permission consequences — worth recording explicitly.
    local file_evidence
    # Deliberately narrow: the flag must name a prompt/instruction/message file. A generic --file
    # for attaching resources is a different feature and must not be recorded as a prompt transport.
    file_evidence=$(grep -iE -- '--(prompt|instruction|message|system[-_]?prompt)[-_]?file' \
        <<<"$help" | sed 's/^[[:space:]]*//' | sort -u | head -4)
    if [[ -n "$file_evidence" ]]; then
        observe "T3-$vendor-prompt-file" "$vendor" "open" \
            "$vendor documents a prompt/instruction file option; confirm its size and permission behaviour." \
            "$(tr '\n' '|' <<<"$file_evidence")"
    else
        observe "T3-$vendor-prompt-file" "$vendor" "fail" \
            "$vendor has no native prompt-file option, so decision 14's temporary-file transport would mean pointing the agent at a file to read rather than the runner supplying it. That changes the cleanup and permission contract and must be designed explicitly." \
            "no prompt-file option found in --help"
    fi
    return 0
}

# ---------------------------------------------------------------------------------------------
# Live transport probes (billed)
# ---------------------------------------------------------------------------------------------
# A prompt that proves the vendor received the WHOLE prompt, not just its first line: the marker is
# at the end, after filler that would be truncated by any size limit on the way in.

live_prompt() {
    local marker=$1 filler_bytes=${2:-0}
    printf 'Reply with exactly one word and nothing else.\n'
    if ((filler_bytes > 0)); then
        printf 'Ignore the following filler.\n'
        head -c "$filler_bytes" /dev/zero | tr '\0' 'x'
        printf '\n'
    fi
    printf 'The word to reply with is: %s\n' "$marker"
}

probe_live_transport() {
    local vendor=$1
    command -v "$vendor" >/dev/null 2>&1 || return 0

    local marker="WRIGHTY${RANDOM}" output status
    local prompt_file="$WORKDIR/$vendor-prompt.txt"
    (umask 077; live_prompt "$marker" >"$prompt_file")

    local perms
    perms=$(stat -f '%Lp' "$prompt_file" 2>/dev/null || stat -c '%a' "$prompt_file" 2>/dev/null)
    if [[ "$perms" == "600" ]]; then
        observe "T4-$vendor-tempfile-permissions" "$vendor" "pass" \
            "A prompt file created under umask 077 is user-only, outside the repository and worktree." \
            "path=$WORKDIR mode=$perms"
    else
        observe "T4-$vendor-tempfile-permissions" "$vendor" "fail" \
            "The prompt file is not user-only." "mode=${perms:-unknown}"
    fi

    # argv transport — today's behaviour, measured so the record has a baseline to compare against.
    output=$(run_vendor_argv "$vendor" "$(live_prompt "$marker")" 2>&1)
    status=$?
    check_live_result "T5-$vendor-argv" "$vendor" "argv" "$marker" "$status" "$output"

    # stdin transport — decision 14's preferred option.
    output=$(run_vendor_stdin "$vendor" <"$prompt_file" 2>&1)
    status=$?
    check_live_result "T6-$vendor-stdin" "$vendor" "standard input" "$marker" "$status" "$output"
}

run_vendor_argv() {
    local vendor=$1 prompt=$2
    case "$vendor" in
        claude) claude -p "$prompt" --output-format json --tools "" ;;
        codex) codex exec --json --skip-git-repo-check --sandbox read-only -C "$WORKDIR" "$prompt" ;;
        copilot) copilot -p "$prompt" --output-format json --no-remote -C "$WORKDIR" ;;
        opencode)
            OPENCODE_CONFIG_CONTENT='{"permission":{"*":"deny"}}' \
                opencode run --pure --format json --auto --agent build --dir "$WORKDIR" "$prompt"
            ;;
        *) return 127 ;;
    esac
}

run_vendor_stdin() {
    local vendor=$1
    case "$vendor" in
        claude) claude -p --output-format json --tools "" ;;
        codex) codex exec --json --skip-git-repo-check --sandbox read-only -C "$WORKDIR" - ;;
        copilot) copilot -p --output-format json --no-remote -C "$WORKDIR" ;;
        opencode)
            OPENCODE_CONFIG_CONTENT='{"permission":{"*":"deny"}}' \
                opencode run --pure --format json --auto --agent build --dir "$WORKDIR"
            ;;
        *) return 127 ;;
    esac
}

check_live_result() {
    local id=$1 vendor=$2 transport=$3 marker=$4 status=$5 output=$6

    if ((status != 0)); then
        observe "$id" "$vendor" "fail" \
            "$vendor rejected the prompt over $transport (exit $status)." \
            "$(printf '%s' "$output" | tail -3 | tr '\n' '|')"
        return
    fi
    if ! grep -q "$marker" <<<"$output"; then
        observe "$id" "$vendor" "fail" \
            "$vendor accepted the prompt over $transport but the reply did not contain the end-of-prompt marker, so the whole prompt did not arrive." \
            "$(printf '%s' "$output" | tail -3 | tr '\n' '|')"
        return
    fi
    if jq -e . >/dev/null 2>&1 <<<"$output" || grep -q '^{' <<<"$output"; then
        observe "$id" "$vendor" "pass" \
            "$vendor accepted the whole prompt over $transport and still emitted parsable structured output." \
            "transport=$transport"
    else
        observe "$id" "$vendor" "open" \
            "$vendor accepted the whole prompt over $transport, but the output was not parsable as JSON; existing output interpretation must be re-checked before selecting this transport." \
            "transport=$transport"
    fi
}

# ---------------------------------------------------------------------------------------------
# Record
# ---------------------------------------------------------------------------------------------

write_record() {
    mkdir -p "$(dirname "$RECORD_PATH")"
    local body
    body=$(printf '%s\n' "${OBSERVATIONS[@]:-}" | jq -s '.')
    jq -n \
        --arg ran_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        --arg host "$(uname -srm)" \
        --argjson live "$([[ "$LIVE" == true ]] && echo true || echo false)" \
        --argjson observations "$body" \
        '{
           plan: "030",
           phase: "0",
           gate: "vendor prompt transport",
           ranAt: $ran_at,
           host: $host,
           liveSessionsRun: $live,
           counts: {
             pass: ($observations | map(select(.verdict == "pass")) | length),
             fail: ($observations | map(select(.verdict == "fail")) | length),
             open: ($observations | map(select(.verdict == "open")) | length),
             skip: ($observations | map(select(.verdict == "skip")) | length)
           },
           selectedTransportPerAdapter: "unresolved until a --live run records one passing transport per installed vendor",
           observations: $observations
         }' >"$RECORD_PATH"
    log "observation record written to $RECORD_PATH"
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------

printf '\nPlatform\n' >&2
probe_argv_ceiling

for vendor in "${VENDORS[@]}"; do
    printf '\n%s\n' "$vendor" >&2
    probe_vendor_capabilities "$vendor" || continue
    [[ "$LIVE" == true ]] && probe_live_transport "$vendor"
done

printf '\n' >&2
if [[ "$LIVE" == false ]]; then
    log "tier 1 only: no agent session was started and nothing was billed."
    log "the selected transport per adapter stays UNRESOLVED until --live runs; plan 030 phase 0 requires one recorded safe transport per adapter."
fi
write_record
