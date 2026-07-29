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

# The walkthrough helper uses the Projects REST API throughout. `gh project` is convenient, but it
# is a GraphQL client underneath; a walkthrough that repeatedly provisions and inspects fixtures can
# otherwise consume the same point budget it is meant to test Wrighty against.
GPL_API_VERSION="2026-03-10"
GPL_OWNER_PATH=""
GPL_PROJECT_PATH=""
GPL_PROJECT_LISTING=""

# The Project's field schema, cached by gpl_load_field_schema. Values are read from here rather
# than re-queried per write.
GPL_FIELD_SCHEMA=""

# gpl_api <gh api arguments> — call GitHub with the Projects REST API version pinned.
gpl_api() {
    gh api --header "X-GitHub-Api-Version: $GPL_API_VERSION" "$@"
}

# Resolves whether OWNER is a user or organization and keeps the corresponding REST path.
gpl_resolve_owner() {
    local scope
    for scope in users orgs; do
        if GPL_PROJECT_LISTING=$(gpl_api \
            "$scope/$OWNER/projectsV2?per_page=100" --paginate --slurp 2>/dev/null); then
            GPL_OWNER_PATH="$scope/$OWNER"
            return 0
        fi
    done
    return 1
}

# Echoes the number of the Project with PROJECT_TITLE, or nothing. Pure lookup: safe to capture.
gpl_project_number() {
    jq -r --arg title "$PROJECT_TITLE" \
        '[.[][] | select(.title == $title)][0].number // empty' <<<"$GPL_PROJECT_LISTING"
    return
}

# Sets PROJECT_NUMBER, creating the Project when it does not exist.
#
# It assigns rather than echoes on purpose. A function whose stdout is captured must not also
# narrate to stdout, and callers narrate — capturing would fold the narration, colour codes and
# all, into the value.
gpl_ensure_project() {
    local created payload
    gpl_resolve_owner ||
        die "could not resolve '$OWNER' through the GitHub Projects REST API"
    PROJECT_NUMBER=$(gpl_project_number)
    if [[ -z "$PROJECT_NUMBER" ]]; then
        payload=$(jq -cn --arg title "$PROJECT_TITLE" '{title: $title}')
        created=$(gpl_api --method POST --input - "$GPL_OWNER_PATH/projectsV2" \
            <<<"$payload") ||
            die "could not create the Project '$PROJECT_TITLE'"
        PROJECT_NUMBER=$(jq -er '.number' <<<"$created") ||
            die "created Project '$PROJECT_TITLE' did not return its number"
    fi
    [[ -n "$PROJECT_NUMBER" ]] || die "could not resolve the number of Project '$PROJECT_TITLE'"
    GPL_PROJECT_PATH="$GPL_OWNER_PATH/projectsV2/$PROJECT_NUMBER"
    return
}

gpl_load_field_schema() {
    GPL_FIELD_SCHEMA=$(gpl_api \
        "$GPL_PROJECT_PATH/fields?per_page=100" --paginate --slurp |
        jq -ce '
            {
              fields: [
                .[][] |
                {
                  id,
                  name,
                  dataType: (.data_type | ascii_upcase),
                  options: [
                    (.options // [])[] |
                    {
                      id,
                      name: (.name | if type == "object" then .raw else . end),
                      description: (
                        .description |
                        if type == "object" then (.raw // "") else (. // "") end
                      ),
                      color: (.color // "GRAY")
                    }
                  ]
                }
              ]
            }') || die "could not read the Project field schema"
    return
}

# gpl_ensure_single_select <field name> <comma-separated options>
# Creates the field when missing, then refreshes the cached schema either way.
gpl_ensure_single_select() {
    local name=$1 options=$2 existing payload
    gpl_load_field_schema
    existing=$(jq -r --arg name "$name" \
        '[.fields[] | select(.name == $name)] | length' <<<"$GPL_FIELD_SCHEMA")
    if [[ "$existing" == "0" ]]; then
        payload=$(jq -cn --arg name "$name" --arg options "$options" '
            {
              name: $name,
              data_type: "single_select",
              single_select_options: (
                $options | split(",") |
                map({name: ., color: "GRAY", description: ""})
              )
            }')
        gpl_api --method POST --input - "$GPL_PROJECT_PATH/fields" \
            <<<"$payload" >/dev/null ||
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
#
# Returns 0 when the Project could be read, whether or not the issue is on it, and non-zero when the
# read itself failed — writing gh's own message to stderr. Those are different facts and the caller
# usually wants to say different things about them.
#
# The previous form piped gh straight into jq, so the exit status was jq's: a rate-limited read and
# an absent item were both "empty output, status 0". Every caller then reported the absence, which
# is how a run that had just created issue #156 came to be told #156 was not on the Project.
gpl_item_id() {
    local listing status
    listing=$(gpl_api \
        --method GET "$GPL_PROJECT_PATH/items" \
        -f per_page=100 \
        -f q="repo:$TEST_REPO is:issue" \
        --paginate --slurp 2>&1)
    status=$?
    if ((status != 0)); then
        printf '%s\n' "$listing" >&2
        return "$status"
    fi
    printf '%s\n' "$listing" |
        jq -r --arg n "$1" --arg repository "$TEST_REPO" \
            '.[][] |
             select(.content.repository.full_name == $repository and
                    (.content.number | tostring) == $n) |
             .id' |
        head -1
    return 0
}

# gpl_require_budget — refuse to start when the GraphQL budget cannot cover a run.
#
# Called before anything is created. Provisioning a Project schema and then dying part-way leaves
# issues behind for the operator to clean up, and the reason it stopped is the one thing the
# failure at that point is least able to explain.
gpl_require_budget() {
    local budget remaining reset reset_label
    budget=$(gpl_api rate_limit \
        --jq '[.resources.graphql.remaining, .resources.graphql.reset] | @tsv' \
        2>/dev/null) || return 0
    IFS=$'\t' read -r remaining reset <<<"$budget"
    [[ -n "$remaining" ]] || return 0
    if ((remaining < 1000)); then
        reset_label=${reset:+"epoch $reset"}
        reset_label=${reset_label:-"the next hour"}
        die "only $remaining GraphQL points remain and this walkthrough needs roughly a thousand; \
it would create issues and then fail part-way. The budget resets at $reset_label."
    fi
    return 0
}

# gpl_set_single_select <issue number> <field name> <option name>
#
# Refuses rather than writing when an id cannot be resolved. An empty id produces a "could not
# resolve node" error that reads like a behavioural failure, when the real cause is usually a
# schema read that came back empty.
gpl_set_single_select() {
    local number=$1 field=$2 option=$3 field_id option_id item_id payload
    field_id=$(gpl_field_id "$field")
    option_id=$(gpl_option_id "$field" "$option")
    [[ -n "$field_id" && -n "$option_id" ]] ||
        die "could not resolve field '$field' option '$option'; refusing to write from an incomplete schema"
    # A failed read and an absent item say different things, so they are reported differently.
    item_id=$(gpl_item_id "$number") ||
        die "could not read Project #$PROJECT_NUMBER to find issue #$number, so whether it is on \
the Project is unknown."
    [[ -n "$item_id" ]] || die "issue #$number is not on Project #$PROJECT_NUMBER"
    payload=$(jq -cn \
        --argjson field "$field_id" \
        --arg option "$option_id" \
        '{fields: [{id: $field, value: $option}]}')
    gpl_api --method PATCH --input - "$GPL_PROJECT_PATH/items/$item_id" \
        <<<"$payload" >/dev/null ||
        die "could not set '$field' to '$option' on issue #$number"
    return
}

# gpl_create_issue <title> <body> — creates the issue, adds it to the Project, echoes its number.
#
# The number is appended to ISSUE_LEDGER, a FILE rather than an array, because this is called
# through $(...) and therefore runs in a subshell: an array append here would never reach the
# caller's cleanup trap, and the issues would silently accumulate.
gpl_create_issue() {
    local title=$1 body=$2 url number issue_id payload
    url=$(gh issue create --repo "$TEST_REPO" --title "$title" --body "$body") ||
        die "could not create the issue"
    number=${url##*/}
    printf '%s\n' "$number" >>"$ISSUE_LEDGER"
    issue_id=$(gpl_api "repos/$TEST_REPO/issues/$number" --jq '.id') ||
        die "could not resolve issue #$number's REST identifier"
    payload=$(jq -cn --argjson id "$issue_id" '{type: "Issue", id: $id}')
    gpl_api --method POST --input - "$GPL_PROJECT_PATH/items" \
        <<<"$payload" >/dev/null ||
        die "could not add issue #$number to Project #$PROJECT_NUMBER"

    # Wait for the item to become queryable before returning. Projects is eventually consistent:
    # item-add succeeds while item-list still does not list it, so a caller that writes a field
    # immediately fails with "not on the Project" — which reads like a provisioning bug rather than
    # a race. Guaranteeing the postcondition here means no caller has to know that.
    local attempt found
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
        # A read that fails is not a read that found nothing, so do not turn an API error into an
        # eventual-consistency retry.
        found=$(gpl_item_id "$number") ||
            die "issue #$number was created, but Project #$PROJECT_NUMBER could not be read to \
confirm it was added."
        [[ -n "$found" ]] && break
        sleep 2
    done
    [[ -n "$found" ]] ||
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
