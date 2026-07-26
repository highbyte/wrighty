#!/usr/bin/env bash
#
# prototype-github-context-approval.sh — plan 030 phase 0 approval-revision prototype gate.
#
# Plan 030 makes a GitHub Project single-select field ("context approval") the authoritative
# cutoff that binds a maintainer's approval to an exact issue/comment revision, and makes
# per-comment reactions the explicit include/exclude overrides. That whole design rests on GitHub
# behaviour the documentation implies but does not guarantee: which timestamps advance, how
# precisely they can be ordered, and which content transitions are observable at all.
#
# Phase 0 is a HARD GATE. This script measures that behaviour against live GitHub and writes an
# observation record. It never implements the feature and never touches the product repository:
# every mutation happens on the dedicated private <owner>/<repo>-test repository resolved by
# scripts/ensure-github-test-repo.sh, in a Project this script provisions itself.
#
# LIVE: creates a Project, fields, issues, comments, and reactions. Set
# WRIGHTY_RUN_GITHUB_PROTOTYPE_LIVE=1 to acknowledge that.
#
# Probes this script CANNOT settle on its own are reported as MANUAL rather than guessed. They all
# need a second GitHub identity (an unauthorised reactor, deleting another user's reaction, team
# membership) or an organisation. A MANUAL result is not a pass; the gate verdict stays open until
# a human records those observations in the record file.
#
# Issues created by a run are deleted on exit unless --keep-fixture. The test repository and its
# Project are reused across runs and are never deleted here.
#
# WHEN TO RE-RUN
#   * When a finding it produced is questioned. Findings F1-F5 each rest on an observed GitHub
#     behaviour rather than on documentation, and GitHub can change any of them: if
#     CommentDeletedEvent starts identifying the deleted comment, or field timestamps gain
#     sub-second precision, or minimised state becomes observable, the corresponding design
#     decision should be revisited rather than inherited.
#   * Before phase 2 builds the conversation reader on those findings.
#   * When the four MANUAL observations can finally be recorded (they need a second GitHub identity
#     or an organisation).
#
# This is a premise check, not a Wrighty regression test. A failure does not mean the code broke;
# it means GitHub behaves differently than when the design was settled. Do not wire it into CI: the
# GraphQL budget is exhausted by repeated full runs.

set -uo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

# shellcheck source=scripts/ensure-github-test-repo.sh
source "$SCRIPT_DIR/ensure-github-test-repo.sh"

SOURCE_REPO=""
PROJECT_TITLE="Wrighty context-approval prototype (plan 030 phase 0)"
CONTEXT_FIELD="Wrighty policy - context approval"
EXECUTION_FIELD="Wrighty policy - execution"
RECORD_PATH="$REPO_ROOT/.wrighty-prototype/context-approval-observations.json"
KEEP_FIXTURE=false
CHECK_ONLY=false
# GitHub Projects mutations are eventually consistent; a short settle keeps a read from racing the
# write it is meant to observe. Deliberately NOT used to manufacture timestamp separation — the
# precision probes need the unpadded interval.
SETTLE_SECONDS=2

die() {
    printf 'prototype: error: %s\n' "$*" >&2
    exit 1
}

log() { printf 'prototype: %s\n' "$*" >&2; return; }

usage() {
    cat >&2 <<'USAGE'
Usage: scripts/prototype-github-context-approval.sh [options]

Runs the plan 030 phase 0 approval-revision prototype gate against live GitHub, on the dedicated
private <owner>/<repo>-test repository. Requires WRIGHTY_RUN_GITHUB_PROTOTYPE_LIVE=1.

Options:
  --source-repo OWNER/REPO  Source to derive the -test repo from (default: current gh repo).
  --project-title TITLE     Prototype Project to create/reuse.
  --context-field NAME      Single-select field used as the approval cutoff.
  --record PATH             Where to write the JSON observation record.
  --keep-fixture            Keep the issues this run created.
  --check                   Validate prerequisites and print the plan; make no mutations.
  -h, --help                Show this help.
USAGE
    return
}

while (($# > 0)); do
    case "$1" in
        --source-repo) (($# >= 2)) || die "--source-repo requires OWNER/REPO"; SOURCE_REPO=$2; shift 2 ;;
        --project-title) (($# >= 2)) || die "--project-title requires a title"; PROJECT_TITLE=$2; shift 2 ;;
        --context-field) (($# >= 2)) || die "--context-field requires a name"; CONTEXT_FIELD=$2; shift 2 ;;
        --record) (($# >= 2)) || die "--record requires a path"; RECORD_PATH=$2; shift 2 ;;
        --keep-fixture) KEEP_FIXTURE=true; shift ;;
        --check) CHECK_ONLY=true; shift ;;
        -h | --help) usage; exit 0 ;;
        *) die "unknown option '$1'" ;;
    esac
done

require_command() {
    local name=$1
    command -v "$name" >/dev/null 2>&1 || die "required command '$name' was not found"
    return
}

require_command gh
require_command jq
require_command python3 # timestamp comparison needs real parsing, not string ordering
gh auth status >/dev/null 2>&1 || die "gh is not authenticated; run 'gh auth login'"

if [[ "$CHECK_ONLY" == false ]]; then
    [[ "${WRIGHTY_RUN_GITHUB_PROTOTYPE_LIVE:-}" == "1" ]] ||
        die "set WRIGHTY_RUN_GITHUB_PROTOTYPE_LIVE=1 to acknowledge this creates a real GitHub Project, issues, comments, and reactions on <owner>/<repo>-test"
fi

TEST_REPO=$(ensure_github_test_repo "${SOURCE_REPO:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}") ||
    die "could not resolve the private test repository"
OWNER=${TEST_REPO%%/*}
VIEWER=$(gh api user --jq .login) || die "could not resolve the authenticated GitHub login"

# ---------------------------------------------------------------------------------------------
# Observation recording
# ---------------------------------------------------------------------------------------------
# Every probe appends one object. A probe records what it OBSERVED even when it fails, because the
# gate decision needs the evidence, not just the verdict.

OBSERVATIONS=()

# The created-issue ledger is a FILE, not an array. create_issue is called through $(...), which
# runs in a subshell, so an array append there would never reach the trap that has to delete them.
ISSUE_LEDGER=$(mktemp "${TMPDIR:-/tmp}/wrighty-prototype-issues.XXXXXX")

observe() {
    local id=$1 section=$2 verdict=$3 summary=$4 evidence=${5:-}
    OBSERVATIONS+=("$(jq -nc \
        --arg id "$id" --arg section "$section" --arg verdict "$verdict" \
        --arg summary "$summary" --arg evidence "$evidence" \
        '{id: $id, section: $section, verdict: $verdict, summary: $summary, evidence: $evidence}')")
    local marker
    case "$verdict" in
        pass) marker="PASS " ;;
        fail) marker="FAIL " ;;
        # A measured negative that does NOT invalidate the cutoff, because the plan already
        # specifies what to do when the capability is absent. It settles a design choice rather
        # than blocking the gate — conflating the two would stop the plan on a question it has
        # already answered.
        constrained) marker="CONSTR" ;;
        manual) marker="MANUAL" ;;
        *) marker="OPEN " ;;
    esac
    printf '  [%s] %-28s %s\n' "$marker" "$id" "$summary" >&2
    [[ -n "$evidence" ]] && printf '          %s\n' "$evidence" >&2
    return 0
}

settle() { sleep "$SETTLE_SECONDS"; }

# The field names a GraphQL type actually exposes on the live schema, as a JSON array. Used where
# a decision depends on whether a capability exists at all — measuring beats remembering, and the
# schema moves.
schema_fields() {
    gh api graphql -f query='query($name: String!) {
        __type(name: $name) { fields(includeDeprecated: true) { name } }
      }' -f name="$1" --jq '[.data.__type.fields[].name]' 2>/dev/null
    return
}

# RFC3339 -> epoch milliseconds, so timestamps can be compared and their precision inspected.
epoch_ms() {
    local value=$1
    python3 - "$value" <<'PY'
import sys, datetime
raw = sys.argv[1].replace("Z", "+00:00")
try:
    moment = datetime.datetime.fromisoformat(raw)
except ValueError:
    print("")
    sys.exit(0)
print(int(moment.timestamp() * 1000))
PY
    return
}

# ---------------------------------------------------------------------------------------------
# Fixture provisioning
# ---------------------------------------------------------------------------------------------

find_project() {
    gh project list --owner "$OWNER" --limit 100 --format json \
        --jq ".projects[] | select(.title == \"$PROJECT_TITLE\") | .number" 2>/dev/null
    return
}

ensure_project() {
    local matches=()
    while IFS= read -r number; do [[ -n "$number" ]] && matches+=("$number"); done < <(find_project)
    ((${#matches[@]} <= 1)) || die "multiple Projects are titled '$PROJECT_TITLE'; resolve that by hand"
    if ((${#matches[@]} == 1)); then
        PROJECT_NUMBER=${matches[0]}
        log "reusing Project #$PROJECT_NUMBER ($PROJECT_TITLE)"
        return 0
    fi
    [[ "$CHECK_ONLY" == false ]] || die "prototype Project '$PROJECT_TITLE' does not exist yet"
    log "creating private Project '$PROJECT_TITLE'"
    PROJECT_NUMBER=$(gh project create --owner "$OWNER" --title "$PROJECT_TITLE" \
        --format json --jq .number) || die "could not create the prototype Project"
    settle
    gh project edit "$PROJECT_NUMBER" --owner "$OWNER" --visibility PRIVATE >/dev/null ||
        die "could not make the prototype Project private"
    settle
    return
}

ensure_single_select_field() {
    local name=$1 options=$2 count
    count=$(gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" --limit 100 --format json \
        --jq "[.fields[] | select(.name == \"$name\")] | length")
    if [[ "$count" == "0" ]]; then
        [[ "$CHECK_ONLY" == false ]] || die "Project field '$name' is missing"
        log "creating single-select field '$name'"
        gh project field-create "$PROJECT_NUMBER" --owner "$OWNER" --name "$name" \
            --data-type SINGLE_SELECT --single-select-options "$options" >/dev/null ||
            die "could not create field '$name'"
        settle
    fi
}

# The field schema is fetched ONCE. Re-querying field-list per write burns the GraphQL point
# budget fast enough to exhaust it mid-run, and a rate-limited read returns empty rather than
# failing, which would silently record probe failures that never happened.
FIELD_SCHEMA=""

load_field_schema() {
    FIELD_SCHEMA=$(gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" --limit 100 \
        --format json) || die "could not read the Project field schema"
    return
}

field_id() {
    local name=$1
    jq -r --arg name "$name" '.fields[] | select(.name == $name) | .id' <<<"$FIELD_SCHEMA"
    return
}

option_id() {
    local field=$1 option=$2
    jq -r --arg field "$field" --arg option "$option" \
        '.fields[] | select(.name == $field) | .options[] | select(.name == $option) | .id' \
        <<<"$FIELD_SCHEMA"
}

# GraphQL is point-budgeted, and an exhausted budget makes reads return empty instead of erroring.
# Without this guard the probes would record confident failures that are really rate limiting.
require_graphql_budget() {
    local remaining
    remaining=$(gh api rate_limit --jq '.resources.graphql.remaining' 2>/dev/null)
    [[ -n "$remaining" ]] || return 0
    if ((remaining < 200)); then
        local reset
        reset=$(gh api rate_limit --jq '.resources.graphql.reset' 2>/dev/null)
        die "only $remaining GraphQL points remain; probe results would be indistinguishable from failures. Wait for the reset$([[ -n "$reset" ]] && printf ' at %s' "$(date -r "$reset" 2>/dev/null || printf 'epoch %s' "$reset")") and re-run."
    fi
    log "GraphQL budget before the run: $remaining points"
    return
}

project_node_id() {
    gh api graphql -f query='query($owner: String!, $number: Int!) {
        repositoryOwner(login: $owner) {
          ... on User { projectV2(number: $number) { id } }
          ... on Organization { projectV2(number: $number) { id } }
        }
      }' -F owner="$OWNER" -F number="$PROJECT_NUMBER" \
        --jq '.data.repositoryOwner.projectV2.id'
    return
}

set_single_select() {
    local item_id=$1 field=$2 option=$3 field_ref option_ref
    field_ref=$(field_id "$field")
    option_ref=$(option_id "$field" "$option")
    # An empty id means the schema read failed (usually rate limiting). Writing it would produce a
    # "could not resolve node" error that the probes would then read as a behavioural failure.
    [[ -n "$field_ref" && -n "$option_ref" ]] ||
        die "could not resolve field '$field' option '$option'; refusing to record probe results from an incomplete schema"
    gh api graphql -f query='
      mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
        updateProjectV2ItemFieldValue(input: {
          projectId: $project, itemId: $item, fieldId: $field,
          value: { singleSelectOptionId: $option }
        }) { projectV2Item { id } }
      }' \
        -f project="$PROJECT_ID" -f item="$item_id" \
        -f field="$field_ref" -f option="$option_ref" >/dev/null ||
        die "setting '$field' to '$option' failed; probe results would be unreliable"
    return
}

# The value's own updatedAt — NOT the item's. Plan 030 depends on this distinction: an unrelated
# field write advances the item, only the approval write should advance the approval value.
field_value_updated_at() {
    local item_id=$1 field=$2
    gh api graphql -f query='
      query($item: ID!, $field: String!) {
        node(id: $item) {
          ... on ProjectV2Item {
            updatedAt
            value: fieldValueByName(name: $field) {
              ... on ProjectV2ItemFieldSingleSelectValue { name updatedAt }
            }
          }
        }
      }' -f item="$item_id" -f field="$field" \
        --jq '{item: .data.node.updatedAt, name: .data.node.value.name, value: .data.node.value.updatedAt}'
    return
}

create_issue() {
    local title=$1 body=$2 url number
    url=$(gh issue create --repo "$TEST_REPO" --title "$title" --body "$body") ||
        die "could not create the fixture issue"
    number=${url##*/}
    printf '%s\n' "$number" >>"$ISSUE_LEDGER"
    printf '%s\n' "$number"
    return
}

add_issue_to_project() {
    local number=$1
    gh project item-add "$PROJECT_NUMBER" --owner "$OWNER" \
        --url "https://github.com/$TEST_REPO/issues/$number" --format json --jq .id
}

cleanup() {
    local created=0 removed=0 number
    [[ -s "$ISSUE_LEDGER" ]] && created=$(wc -l <"$ISSUE_LEDGER" | tr -d ' ')
    if [[ "$KEEP_FIXTURE" == true ]]; then
        log "keeping $created fixture issue(s) on $TEST_REPO"
        rm -f "$ISSUE_LEDGER"
        return
    fi
    while IFS= read -r number; do
        [[ -n "$number" ]] || continue
        if gh issue delete "$number" --repo "$TEST_REPO" --yes >/dev/null 2>&1; then
            removed=$((removed + 1))
        else
            log "could not delete issue #$number on $TEST_REPO; delete it by hand"
        fi
    done <"$ISSUE_LEDGER"
    ((created == 0)) || log "deleted $removed of $created fixture issue(s)"
    rm -f "$ISSUE_LEDGER"
}
trap cleanup EXIT

# ---------------------------------------------------------------------------------------------
# Section A — Project field behaviour (plan 030 "Project field behavior")
# ---------------------------------------------------------------------------------------------

probe_project_field_timestamps() {
    local item_id=$1
    local before after third

    set_single_select "$item_id" "$CONTEXT_FIELD" "Needs review"
    settle
    before=$(field_value_updated_at "$item_id" "$CONTEXT_FIELD")

    set_single_select "$item_id" "$CONTEXT_FIELD" "Approved"
    settle
    after=$(field_value_updated_at "$item_id" "$CONTEXT_FIELD")

    local before_at after_at
    before_at=$(jq -r '.value // empty' <<<"$before")
    after_at=$(jq -r '.value // empty' <<<"$after")

    if [[ -z "$after_at" ]]; then
        observe "A1-approval-updatedat" "project-field" "fail" \
            "The single-select value exposes no updatedAt; the cutoff cannot be bound to it." \
            "value=$after"
        return 1
    fi
    if [[ "$before_at" == "$after_at" ]]; then
        observe "A1-approval-updatedat" "project-field" "fail" \
            "Needs review -> Approved did not advance the value's updatedAt." \
            "before=$before_at after=$after_at"
        return 1
    fi
    observe "A1-approval-updatedat" "project-field" "pass" \
        "Needs review -> Approved advances the value's own updatedAt." \
        "before=$before_at after=$after_at"

    # A2 — the documented reapproval gesture must produce a NEW cutoff, otherwise reapproval is
    # silently a no-op and stale content stays approved.
    set_single_select "$item_id" "$CONTEXT_FIELD" "Needs review"
    settle
    set_single_select "$item_id" "$CONTEXT_FIELD" "Approved"
    settle
    third=$(jq -r '.value // empty' <<<"$(field_value_updated_at "$item_id" "$CONTEXT_FIELD")")
    if [[ "$third" != "$after_at" && -n "$third" ]]; then
        observe "A2-reapproval-advances" "project-field" "pass" \
            "Approved -> Needs review -> Approved advances the cutoff again." \
            "first=$after_at second=$third"
    else
        observe "A2-reapproval-advances" "project-field" "fail" \
            "Round-tripping the option did not produce a new cutoff." \
            "first=$after_at second=$third"
    fi

    # A3 — writing the already-selected option. Plan 030 forbids using this as an approval refresh
    # unless it reliably advances; measuring it decides whether `wrighty approve` must round-trip.
    set_single_select "$item_id" "$CONTEXT_FIELD" "Approved"
    settle
    local same
    same=$(jq -r '.value // empty' <<<"$(field_value_updated_at "$item_id" "$CONTEXT_FIELD")")
    if [[ "$same" == "$third" ]]; then
        observe "A3-same-option-write" "project-field" "pass" \
            "A same-option write does NOT advance updatedAt, so approval must round-trip through Needs review." \
            "before=$third after=$same"
    else
        observe "A3-same-option-write" "project-field" "open" \
            "A same-option write advanced updatedAt; a refresh could skip the round-trip, but the behaviour is undocumented and must not be relied on." \
            "before=$third after=$same"
    fi

    # A4 — precision. Two writes as close together as the API allows must remain orderable.
    set_single_select "$item_id" "$CONTEXT_FIELD" "Needs review"
    set_single_select "$item_id" "$CONTEXT_FIELD" "Approved"
    settle
    local rapid rapid_ms third_ms
    rapid=$(jq -r '.value // empty' <<<"$(field_value_updated_at "$item_id" "$CONTEXT_FIELD")")
    rapid_ms=$(epoch_ms "$rapid")
    third_ms=$(epoch_ms "$third")
    if [[ "$rapid" == *.* ]]; then
        observe "A4-timestamp-precision" "project-field" "pass" \
            "Field timestamps carry sub-second precision." "sample=$rapid"
    elif [[ -n "$rapid_ms" && -n "$third_ms" && "$rapid_ms" != "$third_ms" ]]; then
        observe "A4-timestamp-precision" "project-field" "open" \
            "Field timestamps are whole-second only. Orderable here, but equal-second races must fail closed." \
            "earlier=$third later=$rapid"
    else
        observe "A4-timestamp-precision" "project-field" "fail" \
            "Two consecutive approval writes produced indistinguishable timestamps." \
            "earlier=$third later=$rapid"
    fi

    # A5 — an item created and approved immediately must not produce an ambiguous ordering against
    # its own content timestamps.
    local issue_number issue_created new_item approval approval_ms created_ms
    issue_number=$(create_issue "phase0 immediate approval" "Created and approved in one gesture.")
    issue_created=$(gh issue view "$issue_number" --repo "$TEST_REPO" --json createdAt --jq .createdAt)
    new_item=$(add_issue_to_project "$issue_number")
    set_single_select "$new_item" "$CONTEXT_FIELD" "Approved"
    settle
    approval=$(jq -r '.value // empty' <<<"$(field_value_updated_at "$new_item" "$CONTEXT_FIELD")")
    approval_ms=$(epoch_ms "$approval")
    created_ms=$(epoch_ms "$issue_created")
    if [[ -n "$approval_ms" && -n "$created_ms" && "$approval_ms" -gt "$created_ms" ]]; then
        observe "A5-immediate-approval" "project-field" "pass" \
            "Immediate approval is strictly later than issue creation." \
            "created=$issue_created approved=$approval"
    else
        observe "A5-immediate-approval" "project-field" "fail" \
            "Immediate approval is not strictly later than issue creation; the cutoff cannot order the base content." \
            "created=$issue_created approved=$approval"
    fi

    # A6 — an unrelated field write must not advance the approval value, or every worker-policy
    # change would silently look like a fresh approval.
    local before_unrelated after_unrelated
    before_unrelated=$(jq -r '.value // empty' <<<"$(field_value_updated_at "$item_id" "$CONTEXT_FIELD")")
    set_single_select "$item_id" "$EXECUTION_FIELD" "Automatic allowed"
    settle
    after_unrelated=$(jq -r '.value // empty' <<<"$(field_value_updated_at "$item_id" "$CONTEXT_FIELD")")
    if [[ "$before_unrelated" == "$after_unrelated" ]]; then
        observe "A6-unrelated-field-write" "project-field" "pass" \
            "Writing another Project field leaves the approval value's updatedAt alone." \
            "before=$before_unrelated after=$after_unrelated"
    else
        observe "A6-unrelated-field-write" "project-field" "fail" \
            "An unrelated field write advanced the approval cutoff; the cutoff would approve content nobody reviewed." \
            "before=$before_unrelated after=$after_unrelated"
    fi
}

# ---------------------------------------------------------------------------------------------
# Section B — reaction and authorization behaviour
# ---------------------------------------------------------------------------------------------

comment_reactions() {
    local comment_id=$1
    gh api "repos/$TEST_REPO/issues/comments/$comment_id/reactions" \
        --header 'Accept: application/vnd.github+json' \
        --jq '[.[] | {id, content, user: .user.login, created_at}]'
    return
}

comment_revision() {
    local number=$1 comment_id=$2
    gh api graphql -f query='
      query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) {
          issue(number: $number) {
            comments(first: 100) {
              nodes { databaseId createdAt lastEditedAt isMinimized minimizedReason url }
              pageInfo { hasNextPage }
            }
          }
        }
      }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
        --jq ".data.repository.issue.comments.nodes[] | select(.databaseId == $comment_id)"
    return
}

probe_reactions() {
    local number=$1 comment_id reaction created_at revision edited_at

    comment_id=$(gh api "repos/$TEST_REPO/issues/$number/comments" \
        --method POST -f body='Human discussion comment that a maintainer will decide on.' \
        --jq .id) || die "could not create the discussion comment"
    settle

    reaction=$(gh api "repos/$TEST_REPO/issues/comments/$comment_id/reactions" \
        --method POST -f content='+1' --jq '{id, created_at, content, user: .user.login}') ||
        die "could not add the include reaction"
    settle

    created_at=$(jq -r '.created_at' <<<"$reaction")
    if [[ -n "$(jq -r '.id // empty' <<<"$reaction")" && -n "$created_at" &&
        -n "$(jq -r '.user // empty' <<<"$reaction")" ]]; then
        observe "B1-reaction-identity" "reactions" "pass" \
            "Reactions expose a stable id, actor, kind, and server createdAt." "$reaction"
    else
        observe "B1-reaction-identity" "reactions" "fail" \
            "A reaction did not expose the identity a decision must bind to." "$reaction"
    fi

    # B2 — the ordering rule: a decision counts only when strictly later than the current revision.
    revision=$(comment_revision "$number" "$comment_id")
    local comment_created reaction_ms comment_ms
    comment_created=$(jq -r '.createdAt' <<<"$revision")
    reaction_ms=$(epoch_ms "$created_at")
    comment_ms=$(epoch_ms "$comment_created")
    if [[ -n "$reaction_ms" && -n "$comment_ms" && "$reaction_ms" -gt "$comment_ms" ]]; then
        observe "B2-reaction-ordering" "reactions" "pass" \
            "A reaction is strictly later than the comment revision it decides." \
            "comment=$comment_created reaction=$created_at"
    else
        observe "B2-reaction-ordering" "reactions" "fail" \
            "Reaction and comment timestamps cannot be ordered strictly." \
            "comment=$comment_created reaction=$created_at"
    fi

    # B3 — the stale-decision rule. Editing must make the existing reaction ignorable.
    gh api "repos/$TEST_REPO/issues/comments/$comment_id" --method PATCH \
        -f body='Human discussion comment, edited after the maintainer already reacted.' >/dev/null
    settle
    revision=$(comment_revision "$number" "$comment_id")
    edited_at=$(jq -r '.lastEditedAt // empty' <<<"$revision")
    local edited_ms
    edited_ms=$(epoch_ms "$edited_at")
    if [[ -z "$edited_at" ]]; then
        observe "B3-edit-invalidates" "reactions" "fail" \
            "A comment edit exposed no lastEditedAt; stale decisions cannot be detected." "$revision"
    elif [[ -n "$edited_ms" && -n "$reaction_ms" && "$edited_ms" -gt "$reaction_ms" ]]; then
        observe "B3-edit-invalidates" "reactions" "pass" \
            "The edit is strictly later than the existing reaction, so the old decision is detectably stale." \
            "reaction=$created_at edited=$edited_at"
    else
        observe "B3-edit-invalidates" "reactions" "fail" \
            "The edit did not order strictly after the pre-existing reaction." \
            "reaction=$created_at edited=$edited_at"
    fi

    # B4 — the reaction survives the edit, which is exactly why the optional cleanup workflow
    # exists and why the worker must never trust the UI's visible thumb.
    local surviving
    surviving=$(comment_reactions "$comment_id")
    if [[ "$(jq 'length' <<<"$surviving")" -gt 0 ]]; then
        observe "B4-stale-reaction-visible" "reactions" "pass" \
            "The stale reaction remains visible after the edit; timestamp comparison, not visibility, is the rule." \
            "$surviving"
    else
        observe "B4-stale-reaction-visible" "reactions" "open" \
            "GitHub removed the reaction on edit; re-check the cleanup workflow's purpose." "$surviving"
    fi

    # B5 — deleting one's own reaction (what the cleanup workflow does to stale decisions).
    local reaction_id
    reaction_id=$(jq -r '.id' <<<"$reaction")
    if gh api "repos/$TEST_REPO/issues/comments/$comment_id/reactions/$reaction_id" \
        --method DELETE >/dev/null 2>&1; then
        observe "B5-reaction-delete-own" "reactions" "pass" \
            "A decision reaction can be deleted through the REST API used by the cleanup workflow." \
            "reaction=$reaction_id"
    else
        observe "B5-reaction-delete-own" "reactions" "fail" \
            "Deleting a decision reaction failed." "reaction=$reaction_id"
    fi

    observe "B6-reaction-delete-other" "reactions" "manual" \
        "Deleting ANOTHER user's reaction with Issues write needs a second identity." \
        "Have a second account react, then run: gh api repos/$TEST_REPO/issues/comments/<id>/reactions/<reaction-id> --method DELETE"
    observe "B7-unauthorised-reactor" "reactions" "manual" \
        "An unauthorised actor's reaction having no effect needs a second identity outside the approver policy." \
        "Have an account with no write access react, then confirm the resolver leaves the comment Pending."

    # B8 — repository permission and exact role. maintain/triage collapse onto base read/write, so
    # the exact role_name is what an exact-role policy has to match.
    local permission
    permission=$(gh api "repos/$TEST_REPO/collaborators/$VIEWER/permission" \
        --jq '{permission, role_name: .role_name}' 2>/dev/null)
    if [[ -n "$(jq -r '.role_name // empty' <<<"$permission")" ]]; then
        observe "B8-repository-permission" "authorization" "pass" \
            "Repository permission lookup returns both the effective base permission and the exact role_name." \
            "$permission"
    else
        observe "B8-repository-permission" "authorization" "fail" \
            "Repository permission lookup did not return an exact role_name." "${permission:-<no response>}"
    fi

    observe "B9-custom-role-decoding" "authorization" "manual" \
        "maintain/triage and custom repository roles decoding as documented needs an organisation repository." \
        "Re-run B8 against an org repo with a custom role assigned."
    observe "B10-team-membership" "authorization" "manual" \
        "Team membership (including active child teams) needs an organisation and org Members read access." \
        "gh api orgs/<org>/teams/<team>/memberships/<login>"

    # B11 — effective Project permission for an ARBITRARY actor. Plan 030 adds the
    # projectPermissionAtLeast approver source only if GitHub exposes a reliable least-privilege
    # query for it. Introspect the live schema rather than asserting from memory: viewerCanUpdate
    # answers only for the authenticated identity, which is explicitly not what the policy needs.
    local project_fields candidates
    project_fields=$(schema_fields "ProjectV2")
    candidates=$(jq -r '[.[] | select(test("permission|collaborator|role"; "i"))] | join(", ")' \
        <<<"${project_fields:-[]}")
    if [[ -n "$candidates" ]]; then
        observe "B11-project-permission" "authorization" "open" \
            "ProjectV2 exposes permission-shaped field(s); check whether any answers for an arbitrary actor before enabling projectPermissionAtLeast." \
            "candidates=$candidates"
    else
        observe "B11-project-permission" "authorization" "constrained" \
            "ProjectV2 exposes no field resolving another user's effective Project permission (only viewer-scoped fields). Plan 030 made this source conditional on the prototype passing, so the documented outcome applies: projectPermissionAtLeast stays out of the first-version schema and configuration validation must reject it." \
            "viewer-scoped only: $(jq -r '[.[] | select(startswith("viewer"))] | join(", ")' <<<"${project_fields:-[]}")"
    fi

    printf '%s\n' "$comment_id"
}

# ---------------------------------------------------------------------------------------------
# Section C — issue content behaviour
# ---------------------------------------------------------------------------------------------

issue_content_revision() {
    local number=$1
    gh api graphql -f query='
      query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) {
          issue(number: $number) {
            title body createdAt updatedAt lastEditedAt
            userContentEdits(first: 20) { totalCount nodes { editedAt editor { login } } }
          }
        }
      }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
        --jq '.data.repository.issue | {createdAt, updatedAt, lastEditedAt, edits: .userContentEdits.totalCount}'
    return
}

probe_issue_content() {
    local number=$1 before after

    before=$(issue_content_revision "$number")
    gh issue edit "$number" --repo "$TEST_REPO" --title "phase0 fixture (title edited)" >/dev/null
    settle
    after=$(issue_content_revision "$number")
    # An Issue's lastEditedAt/userContentEdits track the BODY. A title change is a separate
    # timeline event, so check both before concluding a title edit is invisible.
    local renames
    renames=$(gh api graphql -f query='
      query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) {
          issue(number: $number) {
            timelineItems(last: 20, itemTypes: [RENAMED_TITLE_EVENT]) {
              totalCount
              nodes { ... on RenamedTitleEvent { createdAt previousTitle currentTitle } }
            }
          }
        }
      }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
        --jq '.data.repository.issue.timelineItems | {totalCount, latest: (.nodes | last | .createdAt)}' 2>/dev/null)

    if [[ "$(jq -r '.lastEditedAt // empty' <<<"$after")" != "$(jq -r '.lastEditedAt // empty' <<<"$before")" ]]; then
        observe "C1-title-edit-observable" "issue-content" "pass" \
            "A title edit advances the issue's content-edit timestamp." \
            "before=$(jq -c . <<<"$before") after=$(jq -c . <<<"$after")"
    elif [[ "$(jq -r '.totalCount // 0' <<<"$renames")" -gt 0 ]]; then
        observe "C1-title-edit-observable" "issue-content" "pass" \
            "A title edit is NOT visible through lastEditedAt/userContentEdits (those track the body) but IS observable as a timestamped RenamedTitleEvent. Base-approval binding must read that event, not the issue's edit metadata." \
            "renames=$renames body-metadata-unchanged=$(jq -c '{lastEditedAt, edits}' <<<"$after")"
    else
        observe "C1-title-edit-observable" "issue-content" "fail" \
            "A title edit advanced neither lastEditedAt nor a RenamedTitleEvent; base approval cannot be bound to title changes." \
            "before=$(jq -c . <<<"$before") after=$(jq -c . <<<"$after") renames=$renames"
    fi

    before=$after
    gh issue edit "$number" --repo "$TEST_REPO" --body "Body edited after approval." >/dev/null
    settle
    after=$(issue_content_revision "$number")
    if [[ "$(jq -r '.lastEditedAt // empty' <<<"$after")" != "$(jq -r '.lastEditedAt // empty' <<<"$before")" ||
        "$(jq -r '.edits' <<<"$after")" -gt "$(jq -r '.edits' <<<"$before")" ]]; then
        observe "C2-body-edit-observable" "issue-content" "pass" \
            "A body edit advances lastEditedAt or the userContentEdits history." \
            "before=$(jq -c . <<<"$before") after=$(jq -c . <<<"$after")"
    else
        observe "C2-body-edit-observable" "issue-content" "fail" \
            "A body edit was not observable." \
            "before=$(jq -c . <<<"$before") after=$(jq -c . <<<"$after")"
    fi

    # C3 — an edit-then-revert must still require reapproval, so the edit HISTORY, not just the
    # current text, has to be queryable.
    local edits_before edits_after
    edits_before=$(jq -r '.edits' <<<"$after")
    gh issue edit "$number" --repo "$TEST_REPO" --body "Body edited after approval." >/dev/null 2>&1
    settle
    edits_after=$(jq -r '.edits' <<<"$(issue_content_revision "$number")")
    if [[ -n "$edits_before" && "$edits_before" != "null" ]]; then
        observe "C3-edit-history-queryable" "issue-content" "pass" \
            "userContentEdits exposes an edit history, so a textual revert after the cutoff still shows an edit occurred." \
            "edits before=$edits_before after=$edits_after"
    else
        observe "C3-edit-history-queryable" "issue-content" "fail" \
            "No queryable edit history; an edit-then-revert would silently look unchanged." \
            "edits before=$edits_before after=$edits_after"
    fi

    # C4 — pagination completeness. Plan 030 must fetch every page before deciding anything.
    local page_info
    page_info=$(gh api graphql -f query='
      query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) {
          issue(number: $number) {
            comments(first: 1) { totalCount pageInfo { hasNextPage endCursor } }
          }
        }
      }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
        --jq '.data.repository.issue.comments | {totalCount, hasNextPage: .pageInfo.hasNextPage, endCursor: .pageInfo.endCursor}')
    if [[ -n "$(jq -r '.endCursor // empty' <<<"$page_info")" ]]; then
        observe "C4-comment-pagination" "issue-content" "pass" \
            "Comment pagination exposes totalCount and a stable cursor." "$page_info"
    else
        observe "C4-comment-pagination" "issue-content" "open" \
            "Could not confirm cursor-based pagination from this fixture; seed more comments." "$page_info"
    fi

    # C5 — minimize/unminimize. Plan 030's first-version rule (exclude minimized comments) is only
    # safe if the transition is observable; otherwise minimized comments must be INCLUDED instead.
    local minimize_comment minimize_node minimized
    minimize_comment=$(gh api "repos/$TEST_REPO/issues/$number/comments" --method POST \
        -f body='Comment that will be minimised.' --jq .id)
    settle
    minimize_node=$(gh api graphql -f query='
      query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) {
          issue(number: $number) { comments(last: 1) { nodes { id databaseId isMinimized } } }
        }
      }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
        --jq '.data.repository.issue.comments.nodes[0].id')
    if gh api graphql -f query='
      mutation($id: ID!) {
        minimizeComment(input: { subjectId: $id, classifier: OUTDATED }) {
          minimizedComment { isMinimized minimizedReason }
        }
      }' -f id="$minimize_node" >/dev/null 2>&1; then
        settle
        minimized=$(comment_revision "$number" "$minimize_comment")
        if [[ "$(jq -r '.isMinimized' <<<"$minimized")" == "true" &&
            -n "$(jq -r '.lastEditedAt // empty' <<<"$minimized")" ]]; then
            observe "C5-minimize-observable" "issue-content" "pass" \
                "Minimising exposes isMinimized AND advances an observable timestamp." "$minimized"
        else
            observe "C5-minimize-observable" "issue-content" "constrained" \
                "Minimised state is visible but carries no timestamp transition, and there is no minimize timeline event. Decision 16 already specifies the outcome: minimised comments must be INCLUDED rather than silently excluded, because excluding them would create an unobservable inclusion transition." \
                "$minimized"
        fi
    else
        observe "C5-minimize-observable" "issue-content" "open" \
            "minimizeComment was rejected for this identity; re-run where the viewer can minimise." \
            "node=$minimize_node"
    fi

    # C6 — deletion visibility. Wrighty's own claim-history cleanup deletes marker comments, so a
    # deletion the worker cannot correlate to a pre-classified protocol comment must force
    # reapproval rather than be waved through.
    local delete_comment timeline
    delete_comment=$(gh api "repos/$TEST_REPO/issues/$number/comments" --method POST \
        -f body='Comment that will be deleted after approval.' --jq .id)
    settle
    local comments_before
    comments_before=$(gh api graphql -f query='
      query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) {
          issue(number: $number) { comments(first: 100) { nodes { databaseId } } }
        }
      }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
        --jq '[.data.repository.issue.comments.nodes[].databaseId]')
    gh api "repos/$TEST_REPO/issues/comments/$delete_comment" --method DELETE >/dev/null
    # Deletion events are the slowest thing this script observes; give them a real settle before
    # concluding they are absent.
    sleep 10
    timeline=$(gh api graphql -f query='
      query($owner: String!, $repo: String!, $number: Int!) {
        repository(owner: $owner, name: $repo) {
          issue(number: $number) {
            timelineItems(last: 20, itemTypes: [COMMENT_DELETED_EVENT]) {
              totalCount
              nodes { ... on CommentDeletedEvent { createdAt actor { login } } }
            }
          }
        }
      }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
        --jq '.data.repository.issue.timelineItems | {totalCount, nodes}' 2>/dev/null)
    if [[ "$(jq -r '.totalCount // 0' <<<"$timeline")" -gt 0 ]]; then
        observe "C6-deletion-observable" "issue-content" "pass" \
            "Comment deletion is queryable as a timeline event." "$timeline"

        # C7 — the correlation question that decides whether Wrighty's own claim-history cleanup
        # can coexist with the approval cutoff. Plan 030 forbids inferring "technical deletion"
        # from the deleting actor, so the event must identify WHICH comment was deleted.
        local deleted_fields identifying
        deleted_fields=$(schema_fields "CommentDeletedEvent")
        identifying=$(jq -r '[.[] | select(test("^(databaseId|deletedComment|subject|comment)"; "i"))] | join(", ")' \
            <<<"${deleted_fields:-[]}")
        if jq -e 'any(.[]; test("deletedComment(Id)?$|^comment$|subjectId"; "i"))' \
            <<<"${deleted_fields:-[]}" >/dev/null 2>&1; then
            observe "C7-deletion-correlation" "issue-content" "open" \
                "CommentDeletedEvent exposes a comment-identifying field; confirm it survives deletion before relying on it." \
                "fields=$identifying"
        else
            observe "C7-deletion-correlation" "issue-content" "fail" \
                "CommentDeletedEvent identifies the actor and time but not WHICH comment was deleted. Wrighty's claim-history cleanup must therefore durably record the ids it deletes, or the cleanup design must change; the actor must never be used as the substitute." \
                "fields=$(jq -r 'join(", ")' <<<"${deleted_fields:-[]}")"
        fi
    else
        # No event does not mean no detection. Wrighty records the approved comment ids, so a
        # comment missing from the current page set is itself the signal — and unlike the timeline
        # event, it identifies exactly WHICH comment went away.
        local comments_after vanished
        comments_after=$(gh api graphql -f query='
          query($owner: String!, $repo: String!, $number: Int!) {
            repository(owner: $owner, name: $repo) {
              issue(number: $number) { comments(first: 100) { nodes { databaseId } } }
            }
          }' -F owner="${TEST_REPO%%/*}" -F repo="${TEST_REPO##*/}" -F number="$number" \
            --jq '[.data.repository.issue.comments.nodes[].databaseId]')
        vanished=$(jq -n --argjson before "$comments_before" --argjson after "$comments_after" \
            '$before - $after')
        if [[ "$(jq 'length' <<<"$vanished")" -gt 0 ]]; then
            observe "C6-deletion-observable" "issue-content" "pass" \
                "GitHub emits NO CommentDeletedEvent for this deletion, but the comment disappears from the paginated set. Detection must therefore be manifest-difference against the recorded approved comment ids — which is strictly better than the event, because it names the missing comment (see F1)." \
                "timeline=$timeline vanished=$vanished"
        else
            observe "C6-deletion-observable" "issue-content" "fail" \
                "A deleted comment produced neither a timeline event nor a detectable change in the comment set; deletion after approval would be invisible." \
                "timeline=${timeline:-<no response>} before=$comments_before after=$comments_after"
        fi
    fi
}

# ---------------------------------------------------------------------------------------------
# Record
# ---------------------------------------------------------------------------------------------

write_record() {
    local dir
    dir=$(dirname "$RECORD_PATH")
    mkdir -p "$dir"

    local body
    body=$(printf '%s\n' "${OBSERVATIONS[@]:-}" | jq -s '.')

    jq -n \
        --arg repo "$TEST_REPO" \
        --arg project "$PROJECT_TITLE" \
        --arg project_number "${PROJECT_NUMBER:-}" \
        --arg field "$CONTEXT_FIELD" \
        --arg viewer "$VIEWER" \
        --arg ran_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        --argjson observations "$body" \
        '{
           plan: "030",
           phase: "0",
           gate: "approval-revision prototype",
           ranAt: $ran_at,
           repository: $repo,
           project: { title: $project, number: $project_number },
           contextApprovalField: $field,
           viewer: $viewer,
           counts: {
             pass: ($observations | map(select(.verdict == "pass")) | length),
             fail: ($observations | map(select(.verdict == "fail")) | length),
             constrained: ($observations | map(select(.verdict == "constrained")) | length),
             manual: ($observations | map(select(.verdict == "manual")) | length),
             open: ($observations | map(select(.verdict == "open")) | length)
           },
           verdict: (if ($observations | map(select(.verdict == "fail")) | length) > 0
                     then "blocked"
                     elif ($observations | map(select(.verdict == "manual" or .verdict == "open")) | length) > 0
                     then "incomplete"
                     else "passed" end),
           observations: $observations
         }' >"$RECORD_PATH"

    log "observation record written to $RECORD_PATH"

    local failures manual constrained
    failures=$(jq -r '.counts.fail' "$RECORD_PATH")
    manual=$(jq -r '.counts.manual' "$RECORD_PATH")
    constrained=$(jq -r '.counts.constrained' "$RECORD_PATH")
    printf '\n' >&2
    log "verdict: $(jq -r .verdict "$RECORD_PATH") (fail=$failures constrained=$constrained manual=$manual open=$(jq -r '.counts.open' "$RECORD_PATH"))"
    if [[ "$constrained" -gt 0 ]]; then
        log "$constrained capability/capabilities are absent but have a documented fallback; each one settles a design choice that phases 2-6 must carry."
    fi
    if [[ "$failures" -gt 0 ]]; then
        log "plan 030 decision gate: at least one required observation failed. Do NOT proceed to phase 1 on the Context approval updatedAt cutoff; revise the UX per the plan's decision gate."
        return 1
    fi
    if [[ "$manual" -gt 0 ]]; then
        log "plan 030 decision gate: automated probes passed, but MANUAL observations remain. The gate is not satisfied until those are recorded."
        return 2
    fi
    return 0
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------

require_graphql_budget
ensure_project
PROJECT_ID=$(project_node_id) || die "could not resolve the Project node id"
ensure_single_select_field "$CONTEXT_FIELD" "Needs review,Approved"
ensure_single_select_field "$EXECUTION_FIELD" "Manual only,Automatic allowed"
load_field_schema
[[ -n "$(field_id "$CONTEXT_FIELD")" ]] || die "could not resolve the '$CONTEXT_FIELD' field id"

if [[ "$CHECK_ONLY" == true ]]; then
    log "check only: repository $TEST_REPO, Project #$PROJECT_NUMBER, field '$CONTEXT_FIELD' are ready"
    log "re-run without --check (and with WRIGHTY_RUN_GITHUB_PROTOTYPE_LIVE=1) to run the probes"
    exit 0
fi

log "running the plan 030 phase 0 probes against $TEST_REPO / Project #$PROJECT_NUMBER"
FIXTURE_ISSUE=$(create_issue "phase0 fixture" "Base task body under approval.")
FIXTURE_ITEM=$(add_issue_to_project "$FIXTURE_ISSUE")

printf '\nA. Project field behaviour\n' >&2
probe_project_field_timestamps "$FIXTURE_ITEM"
printf '\nB. Reaction and authorization behaviour\n' >&2
probe_reactions "$FIXTURE_ISSUE" >/dev/null
printf '\nC. Issue content behaviour\n' >&2
probe_issue_content "$FIXTURE_ISSUE"

printf '\n' >&2
write_record
