#!/usr/bin/env bash
#
# github-project-lib.sh — shared GitHub Project provisioning for the interactive walkthroughs.
#
# Sourced, never executed. It defines functions and touches nothing on its own, so a caller can
# source it before deciding whether to make any remote call at all.
#
# It deliberately does NOT provide the Wrighty field schema. `wrighty init` owns that, and a second
# implementation here would drift from it silently. What lives here is only what the walkthroughs
# need on top: creating or reusing a Project by title, adding a single-select field, setting a
# field value on one item, and tracking the issues a run created so it can clean up after itself.
#
# Callers must define, before calling anything:
#   OWNER          the Project owner login
#   TEST_REPO      owner/repo the issues are created on
#   PROJECT_TITLE  the Project to create or reuse
#   ISSUE_LEDGER   path to a file recording the issue numbers this run created
#
# and must provide `die`. Sourcing scripts/walkthrough-lib.sh first satisfies that.

# The Project's field schema, cached by gpl_load_field_schema. Values are read from here rather
# than re-queried per write: gh project field-list is a GraphQL call, and re-issuing it for every
# field lookup burns the point budget fast enough to exhaust it mid-run — where reads start
# returning empty rather than failing, which then looks like a missing field.
GPL_FIELD_SCHEMA=""

# Echoes the number of the Project with PROJECT_TITLE, or nothing. Pure lookup: safe to capture.
gpl_project_number() {
    gh project list --owner "$OWNER" --format json --limit 100 |
        jq -r --arg title "$PROJECT_TITLE" '.projects[] | select(.title == $title) | .number' | head -1
    return
}

# Sets PROJECT_NUMBER, creating the Project when it does not exist.
#
# It assigns rather than echoes on purpose. A function whose stdout is captured must not also
# narrate to stdout, and callers narrate — capturing would fold the narration, colour codes and
# all, into the value.
gpl_ensure_project() {
    PROJECT_NUMBER=$(gpl_project_number)
    if [[ -z "$PROJECT_NUMBER" ]]; then
        gh project create --owner "$OWNER" --title "$PROJECT_TITLE" --format json >/dev/null ||
            die "could not create the Project '$PROJECT_TITLE'"
        PROJECT_NUMBER=$(gpl_project_number)
    fi
    [[ -n "$PROJECT_NUMBER" ]] || die "could not resolve the number of Project '$PROJECT_TITLE'"
    return
}

gpl_load_field_schema() {
    GPL_FIELD_SCHEMA=$(gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" \
        --limit 100 --format json) || die "could not read the Project field schema"
    return
}

# gpl_ensure_single_select <field name> <comma-separated options>
# Creates the field when missing, then refreshes the cached schema either way.
gpl_ensure_single_select() {
    local name=$1 options=$2 existing
    existing=$(gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" --limit 100 --format json |
        jq -r --arg name "$name" '[.fields[] | select(.name == $name)] | length')
    if [[ "$existing" == "0" ]]; then
        gh project field-create "$PROJECT_NUMBER" --owner "$OWNER" \
            --name "$name" --data-type SINGLE_SELECT --single-select-options "$options" >/dev/null ||
            die "could not create the '$name' field"
    fi
    gpl_load_field_schema
    return
}

gpl_field_id() {
    jq -r --arg n "$1" '.fields[] | select(.name == $n) | .id' <<<"$GPL_FIELD_SCHEMA"
    return
}

gpl_option_id() {
    jq -r --arg n "$1" --arg o "$2" \
        '.fields[] | select(.name == $n) | .options[] | select(.name == $o) | .id' <<<"$GPL_FIELD_SCHEMA"
    return
}

# gpl_item_id <issue number> — the Project item id for one issue, or nothing.
gpl_item_id() {
    gh project item-list "$PROJECT_NUMBER" --owner "$OWNER" --format json --limit 200 |
        jq -r --arg n "$1" '.items[] | select((.content.number|tostring) == $n) | .id' | head -1
    return
}

# gpl_set_single_select <issue number> <field name> <option name>
#
# Refuses rather than writing when an id cannot be resolved. An empty id produces a "could not
# resolve node" error that reads like a behavioural failure, when the real cause is usually a
# schema read that came back empty.
gpl_set_single_select() {
    local number=$1 field=$2 option=$3 field_id option_id item_id project_id
    field_id=$(gpl_field_id "$field")
    option_id=$(gpl_option_id "$field" "$option")
    [[ -n "$field_id" && -n "$option_id" ]] ||
        die "could not resolve field '$field' option '$option'; refusing to write from an incomplete schema"
    item_id=$(gpl_item_id "$number")
    [[ -n "$item_id" ]] || die "issue #$number is not on Project #$PROJECT_NUMBER"
    project_id=$(gh project view "$PROJECT_NUMBER" --owner "$OWNER" --format json --jq .id) ||
        die "could not resolve the Project node id"
    gh api graphql -f query='
      mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
        updateProjectV2ItemFieldValue(input: {
          projectId: $project, itemId: $item, fieldId: $field,
          value: { singleSelectOptionId: $option }
        }) { projectV2Item { id } }
      }' -f project="$project_id" -f item="$item_id" \
         -f field="$field_id" -f option="$option_id" >/dev/null ||
        die "could not set '$field' to '$option' on issue #$number"
    return
}

# gpl_create_issue <title> <body> — creates the issue, adds it to the Project, echoes its number.
#
# The number is appended to ISSUE_LEDGER, a FILE rather than an array, because this is called
# through $(...) and therefore runs in a subshell: an array append here would never reach the
# caller's cleanup trap, and the issues would silently accumulate.
gpl_create_issue() {
    local title=$1 body=$2 url number
    url=$(gh issue create --repo "$TEST_REPO" --title "$title" --body "$body") ||
        die "could not create the issue"
    number=${url##*/}
    printf '%s\n' "$number" >>"$ISSUE_LEDGER"
    gh project item-add "$PROJECT_NUMBER" --owner "$OWNER" --url "$url" --format json >/dev/null ||
        die "could not add issue #$number to Project #$PROJECT_NUMBER"

    # Wait for the item to become queryable before returning. Projects is eventually consistent:
    # item-add succeeds while item-list still does not list it, so a caller that writes a field
    # immediately fails with "not on the Project" — which reads like a provisioning bug rather than
    # a race. Guaranteeing the postcondition here means no caller has to know that.
    local attempt
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        [[ -n "$(gpl_item_id "$number")" ]] && break
        sleep 2
    done
    [[ -n "$(gpl_item_id "$number")" ]] ||
        die "issue #$number was added to Project #$PROJECT_NUMBER but never became queryable"

    printf '%s\n' "$number"
    return
}

# gpl_delete_ledger_issues — deletes every issue this run created. Callers decide whether to call
# it; retaining on failure is usually right, because the state that produced a failure is what is
# worth looking at.
gpl_delete_ledger_issues() {
    local number
    [[ -s "$ISSUE_LEDGER" ]] || return 0
    while IFS= read -r number; do
        [[ -n "$number" ]] || continue
        gh issue delete "$number" --repo "$TEST_REPO" --yes >/dev/null 2>&1 ||
            printf 'could not delete issue #%s on %s; delete it by hand\n' "$number" "$TEST_REPO" >&2
    done <"$ISSUE_LEDGER"
    return 0
}
