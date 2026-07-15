#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)

RECREATE=false
CHECK_ONLY=false
OWNER=""
REPOSITORY=""
PROJECT_TITLE="Wrighty Pagination Fixture"
FIXTURE_LABEL="wrighty-pagination-fixture"
ISSUE_PREFIX="[pagination fixture] "
ITEM_COUNT=101
DELAY_SECONDS=1
CONFIG_PATH="$REPO_ROOT/.github-pagination-fixture.json"

usage() {
    printf '%s\n' \
        "Usage: scripts/seed-github-pagination-fixture.sh [options]" \
        "" \
        "Create or validate a persistent private GitHub repository and Project used only" \
        "for opt-in live pagination tests. Normal mode is idempotent and non-destructive." \
        "" \
        "Options:" \
        "  --owner LOGIN            Repository and Project owner; defaults to the gh user." \
        "  --repo OWNER/REPO        Dedicated private repository; default is" \
        "                           OWNER/wrighty-scale-fixture." \
        "  --project-title TITLE    Exact persistent Project title." \
        "  --items COUNT            Fixture issue count; defaults to 101." \
        "  --delay SECONDS          Pause after each remote mutation; defaults to 1." \
        "  --config PATH            Generated Wrighty configuration path." \
        "  --check                  Validate only; never create or repair resources." \
        "  --recreate               Delete only the exact fixture Project and labelled" \
        "                           fixture issues, then seed them again. The repository" \
        "                           itself is never deleted." \
        "  -h, --help               Show this help." \
        "" \
        "The script does not run the live xUnit test. Initial seeding creates many GitHub" \
        "issues and Project items and can take several minutes. Re-running a valid fixture" \
        "performs validation reads but no remote writes."
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command '$1' was not found"
}

validate_title() {
    case "$1" in
        *$'\n'*|*$'\r'*|*$'\t'*) die "fixture titles cannot contain tabs or line breaks" ;;
        *) ;;
    esac
}

pause_after_mutation() {
    if [[ "$DELAY_SECONDS" != "0" ]]; then
        sleep "$DELAY_SECONDS"
    fi
}

while (($# > 0)); do
    case "$1" in
        --owner)
            (($# >= 2)) || die "--owner requires a login"
            OWNER=$2
            shift 2
            ;;
        --repo)
            (($# >= 2)) || die "--repo requires OWNER/REPO"
            REPOSITORY=$2
            shift 2
            ;;
        --project-title)
            (($# >= 2)) || die "--project-title requires a title"
            PROJECT_TITLE=$2
            shift 2
            ;;
        --items)
            (($# >= 2)) || die "--items requires a positive integer"
            ITEM_COUNT=$2
            shift 2
            ;;
        --delay)
            (($# >= 2)) || die "--delay requires a non-negative number"
            DELAY_SECONDS=$2
            shift 2
            ;;
        --config)
            (($# >= 2)) || die "--config requires a path"
            CONFIG_PATH=$2
            shift 2
            ;;
        --check)
            CHECK_ONLY=true
            shift
            ;;
        --recreate)
            RECREATE=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown option '$1'"
            ;;
    esac
done

[[ "$ITEM_COUNT" =~ ^[1-9][0-9]*$ ]] || die "--items must be a positive integer"
((ITEM_COUNT > 100 && ITEM_COUNT <= 9999)) ||
    die "--items must be between 101 and 9999 so the fixture crosses a page boundary"
[[ "$DELAY_SECONDS" =~ ^[0-9]+([.][0-9]+)?$ ]] ||
    die "--delay must be a non-negative number"
[[ "$CHECK_ONLY" == false || "$RECREATE" == false ]] ||
    die "--check and --recreate cannot be used together"
validate_title "$PROJECT_TITLE"

require_command gh
require_command jq
require_command dotnet
gh auth status >/dev/null

if [[ -z "$OWNER" ]]; then
    OWNER=$(gh api user --jq .login)
fi
if [[ -z "$REPOSITORY" ]]; then
    REPOSITORY="$OWNER/wrighty-scale-fixture"
fi
[[ "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
    die "repository must use OWNER/REPO format"
[[ "${REPOSITORY%%/*}" == "$OWNER" ]] ||
    die "--repo owner must match --owner for this persistent personal fixture"
CONFIG_DIRECTORY=$(dirname "$CONFIG_PATH")
[[ -d "$CONFIG_DIRECTORY" ]] || die "the parent directory for --config must exist"
CONFIG_PATH="$(cd "$CONFIG_DIRECTORY" && pwd)/$(basename "$CONFIG_PATH")"

repo_json=""
if repo_json=$(gh repo view "$REPOSITORY" --json nameWithOwner,isPrivate,hasIssuesEnabled 2>/dev/null); then
    [[ "$(jq -r .isPrivate <<< "$repo_json")" == "true" ]] ||
        die "existing fixture repository '$REPOSITORY' is not private"
    [[ "$(jq -r .hasIssuesEnabled <<< "$repo_json")" == "true" ]] ||
        die "issues are disabled in existing fixture repository '$REPOSITORY'"
    printf 'Reusing private fixture repository %s.\n' "$REPOSITORY"
elif [[ "$CHECK_ONLY" == true ]]; then
    die "fixture repository '$REPOSITORY' is missing; run the seed script without --check"
else
    printf 'Creating private fixture repository %s...\n' "$REPOSITORY"
    gh repo create "$REPOSITORY" \
        --private \
        --disable-wiki \
        --description "Persistent Wrighty pagination test fixtures" >/dev/null
    pause_after_mutation
fi

find_projects() {
    gh project list --owner "$OWNER" --limit 1000 --format json \
        --template '{{range .projects}}{{printf "%v\t%s\n" .number .title}}{{end}}' |
        while IFS=$'\t' read -r number title; do
            if [[ "$title" == "$PROJECT_TITLE" ]]; then
                printf '%s\n' "$number"
            fi
        done
}

delete_fixture_resources() {
    local project_number
    while IFS= read -r project_number; do
        [[ -n "$project_number" ]] || continue
        printf 'Deleting fixture Project #%s (%s)...\n' "$project_number" "$PROJECT_TITLE"
        gh project delete "$project_number" --owner "$OWNER" >/dev/null
        pause_after_mutation
    done < <(find_projects)

    local issue_number
    while IFS= read -r issue_number; do
        [[ -n "$issue_number" ]] || continue
        printf 'Deleting labelled fixture issue #%s...\n' "$issue_number"
        gh issue delete "$issue_number" --repo "$REPOSITORY" --yes
        pause_after_mutation
    done < <(gh issue list \
        --repo "$REPOSITORY" \
        --state all \
        --label "$FIXTURE_LABEL" \
        --limit 1000 \
        --json number \
        --jq '.[].number')
}

if [[ "$RECREATE" == true ]]; then
    printf 'Recreating only the persistent pagination Project and labelled issues.\n'
    delete_fixture_resources
fi

PROJECT_MATCHES=()
while IFS= read -r project_number; do
    [[ -n "$project_number" ]] && PROJECT_MATCHES+=("$project_number")
done < <(find_projects)

if ((${#PROJECT_MATCHES[@]} > 1)); then
    die "multiple Projects are named '$PROJECT_TITLE'; no automatic cleanup was attempted"
fi
if ((${#PROJECT_MATCHES[@]} == 0)); then
    if [[ "$CHECK_ONLY" == true ]]; then
        die "fixture Project '$PROJECT_TITLE' is missing; run the seed script without --check"
    fi
    printf 'Creating private Project %s...\n' "$PROJECT_TITLE"
    PROJECT_NUMBER=$(gh project create \
        --owner "$OWNER" \
        --title "$PROJECT_TITLE" \
        --format json \
        --jq .number)
    pause_after_mutation
    gh project edit "$PROJECT_NUMBER" \
        --owner "$OWNER" \
        --visibility PRIVATE >/dev/null
    pause_after_mutation
else
    PROJECT_NUMBER=${PROJECT_MATCHES[0]}
    printf 'Reusing Project #%s (%s).\n' "$PROJECT_NUMBER" "$PROJECT_TITLE"
fi

PROJECT_PUBLIC=$(gh api graphql \
    -f query='query($owner: String!, $number: Int!) {
      repositoryOwner(login: $owner) {
        ... on User { projectV2(number: $number) { public } }
        ... on Organization { projectV2(number: $number) { public } }
      }
    }' \
    -F owner="$OWNER" \
    -F number="$PROJECT_NUMBER" \
    --jq '.data.repositoryOwner.projectV2.public')
[[ "$PROJECT_PUBLIC" == "false" ]] ||
    die "fixture Project #$PROJECT_NUMBER is not private"

field_count() {
    local field_name=$1
    gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" --limit 100 \
        --format json --jq ".fields | map(select(.name == \"$field_name\")) | length"
}

field_type() {
    local field_name=$1
    gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" --limit 100 \
        --format json --jq ".fields[] | select(.name == \"$field_name\") | .type"
}

ensure_single_select_field() {
    local field_name=$1
    local options=$2
    local count
    count=$(field_count "$field_name")
    if [[ "$count" == "0" ]]; then
        [[ "$CHECK_ONLY" == false ]] || die "Project field '$field_name' is missing"
        printf 'Creating %s field...\n' "$field_name"
        gh project field-create "$PROJECT_NUMBER" \
            --owner "$OWNER" \
            --name "$field_name" \
            --data-type SINGLE_SELECT \
            --single-select-options "$options" >/dev/null
        pause_after_mutation
        return
    fi

    [[ "$count" == "1" ]] || die "Project contains duplicate '$field_name' fields"
    [[ "$(field_type "$field_name")" == "ProjectV2SingleSelectField" ]] ||
        die "Project field '$field_name' has the wrong type"

    local available
    available=$(gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" --limit 100 \
        --format json --jq ".fields[] | select(.name == \"$field_name\") | .options[].name")
    local required
    IFS=',' read -r -a required_options <<< "$options"
    for required in "${required_options[@]}"; do
        if ! printf '%s\n' "$available" | grep -Fxq "$required"; then
            die "Project field '$field_name' is missing option '$required'; use --recreate"
        fi
    done
}

ensure_single_select_field "Status" "Todo,In Progress,Done"
ensure_single_select_field "Priority" "P0,P1,P2,P3"

write_config() {
    local temporary="$CONFIG_PATH.tmp.$$"
    printf '%s\n' \
        '{' \
        '  "backend": "github",' \
        '  "defaultPickFrom": "Todo",' \
        '  "defaultPickTo": "In Progress",' \
        '  "defaultFinishTo": "Done",' \
        '  "leaseMinutes": 60,' \
        '  "archive": { "onStatuses": [] },' \
        '  "github": {' \
        "    \"repository\": \"$REPOSITORY\"," \
        "    \"projectOwner\": \"$OWNER\"," \
        "    \"projectNumber\": $PROJECT_NUMBER," \
        '    "linkRepository": true,' \
        '    "statusField": "Status",' \
        '    "priorityField": "Priority",' \
        '    "agentTypeField": "Current agent type",' \
        '    "sessionIdField": "Current session ID",' \
        '    "creationAttemptIdField": "Creation attempt ID",' \
        '    "claimHistoryLimit": 10,' \
        '    "gitHubHost": "github.com"' \
        '  }' \
        '}' > "$temporary"
    if [[ ! -f "$CONFIG_PATH" ]] || ! cmp -s "$temporary" "$CONFIG_PATH"; then
        mv "$temporary" "$CONFIG_PATH"
        printf 'Wrote fixture configuration %s.\n' "$CONFIG_PATH"
    else
        rm -f "$temporary"
    fi
}

if [[ "$CHECK_ONLY" == false ]]; then
    write_config
    printf 'Initializing Project fields and repository link...\n'
    dotnet run --project "$REPO_ROOT/src/Highbyte.Wrighty.Cli" -- \
        init --config "$CONFIG_PATH" >/dev/null
else
    [[ -f "$CONFIG_PATH" ]] ||
        die "fixture configuration '$CONFIG_PATH' is missing; run the seed script without --check"
    dotnet run --project "$REPO_ROOT/src/Highbyte.Wrighty.Cli" -- \
        init --config "$CONFIG_PATH" --check >/dev/null
fi

label_json=""
if label_json=$(gh api "repos/$REPOSITORY/labels/$FIXTURE_LABEL" 2>/dev/null); then
    [[ "$(jq -r .description <<< "$label_json")" == \
        "Persistent Wrighty pagination fixture" ]] ||
        die "existing label '$FIXTURE_LABEL' has an unexpected description"
elif [[ "$CHECK_ONLY" == true ]]; then
    die "fixture label '$FIXTURE_LABEL' is missing"
else
    gh label create "$FIXTURE_LABEL" \
        --repo "$REPOSITORY" \
        --description "Persistent Wrighty pagination fixture" \
        --color "BFD4F2" >/dev/null
    pause_after_mutation
fi

load_fixture_issues() {
    # `gh issue list` can briefly omit newly-created labelled issues while
    # GitHub's search index catches up. Query the repository Issues API so the
    # post-seed validation observes the source data directly.
    gh api --paginate --slurp -X GET "repos/$REPOSITORY/issues" \
        -f state=all \
        -f labels="$FIXTURE_LABEL" \
        -F per_page=100 |
        jq '[.[][] | select(has("pull_request") | not) | {
          number,
          title,
          url: .html_url
        }]'
}

validate_fixture_issue_set() {
    local issues=$1
    local invalid
    invalid=$(jq \
        --arg prefix "$ISSUE_PREFIX" \
        --argjson target "$ITEM_COUNT" \
        '[.[] | select(
          (.title | startswith($prefix) | not) or
          ((.title[($prefix | length):] | test("^[0-9]{4}$")) | not) or
          ((.title[($prefix | length):] | tonumber) < 1) or
          ((.title[($prefix | length):] | tonumber) > $target)
        )] | length' <<< "$issues")
    [[ "$invalid" == "0" ]] ||
        die "$invalid labelled issues do not use the expected deterministic fixture title"
    local unique
    unique=$(jq '[.[].title] | unique | length' <<< "$issues")
    local total
    total=$(jq 'length' <<< "$issues")
    [[ "$unique" == "$total" ]] ||
        die "duplicate deterministic fixture issue titles were found"
    ((total <= ITEM_COUNT)) ||
        die "found $total fixture issues but expected only $ITEM_COUNT; use --recreate to rebuild"
}

ISSUES_JSON=$(load_fixture_issues)
validate_fixture_issue_set "$ISSUES_JSON"

for ordinal in $(seq 1 "$ITEM_COUNT"); do
    title=$(printf '%s%04d' "$ISSUE_PREFIX" "$ordinal")
    if jq -e --arg title "$title" '.[] | select(.title == $title)' \
        <<< "$ISSUES_JSON" >/dev/null; then
        continue
    fi
    if [[ "$CHECK_ONLY" == true ]]; then
        die "fixture issue '$title' is missing; run the seed script without --check"
    fi
    printf 'Creating fixture issue %d/%d...\n' "$ordinal" "$ITEM_COUNT"
    gh issue create \
        --repo "$REPOSITORY" \
        --title "$title" \
        --body "Persistent read-only pagination fixture item $ordinal of $ITEM_COUNT." \
        --label "$FIXTURE_LABEL" >/dev/null
    pause_after_mutation
done

for attempt in {1..6}; do
    ISSUES_JSON=$(load_fixture_issues)
    observed_issue_count=$(jq 'length' <<< "$ISSUES_JSON")
    if [[ "$observed_issue_count" == "$ITEM_COUNT" ]]; then
        break
    fi
    if [[ "$attempt" == "6" ]]; then
        die "GitHub reports $observed_issue_count fixture issues after seeding; expected $ITEM_COUNT"
    fi
    printf 'GitHub currently reports %s/%s fixture issues; retrying validation...\n' \
        "$observed_issue_count" "$ITEM_COUNT"
    sleep 2
done
validate_fixture_issue_set "$ISSUES_JSON"

load_project_items() {
    gh project item-list "$PROJECT_NUMBER" \
        --owner "$OWNER" \
        --limit 1000 \
        --format json
}

PROJECT_ITEMS_JSON=$(load_project_items)
unexpected_items=$(jq \
    --arg repo "$REPOSITORY" \
    --arg prefix "$ISSUE_PREFIX" \
    '[.items[] | select(
      .content.type != "Issue" or
      .content.repository != $repo or
      (.content.title | startswith($prefix) | not)
    )] | length' <<< "$PROJECT_ITEMS_JSON")
[[ "$unexpected_items" == "0" ]] ||
    die "Project contains $unexpected_items non-fixture items; no automatic cleanup was attempted"

while IFS=$'\t' read -r issue_number issue_url; do
    if jq -e --argjson number "$issue_number" \
        '.items[] | select(.content.number == $number)' \
        <<< "$PROJECT_ITEMS_JSON" >/dev/null; then
        continue
    fi
    if [[ "$CHECK_ONLY" == true ]]; then
        die "fixture issue #$issue_number is missing from Project #$PROJECT_NUMBER"
    fi
    printf 'Adding fixture issue #%s to Project #%s...\n' "$issue_number" "$PROJECT_NUMBER"
    gh project item-add "$PROJECT_NUMBER" \
        --owner "$OWNER" \
        --url "$issue_url" >/dev/null
    pause_after_mutation
done < <(jq -r '.[] | [.number, .url] | @tsv' <<< "$ISSUES_JSON")

PROJECT_ITEMS_JSON=$(load_project_items)
project_item_count=$(jq '.items | length' <<< "$PROJECT_ITEMS_JSON")
[[ "$project_item_count" == "$ITEM_COUNT" ]] ||
    die "Project contains $project_item_count items; expected $ITEM_COUNT"
unique_project_issues=$(jq '[.items[].content.number] | unique | length' <<< "$PROJECT_ITEMS_JSON")
[[ "$unique_project_issues" == "$ITEM_COUNT" ]] ||
    die "Project contains duplicate fixture issue membership"

sentinel_title=$(printf '%s%04d' "$ISSUE_PREFIX" "$ITEM_COUNT")
sentinel_item_id=$(jq -r --arg title "$sentinel_title" \
    '.items[] | select(.content.title == $title) | .id' <<< "$PROJECT_ITEMS_JSON")
[[ -n "$sentinel_item_id" && "$sentinel_item_id" != "null" ]] ||
    die "could not resolve the final-page sentinel Project item"

PROJECT_ID=$(gh project view "$PROJECT_NUMBER" --owner "$OWNER" --format json --jq .id)
FIELDS_JSON=$(gh project field-list "$PROJECT_NUMBER" --owner "$OWNER" --limit 100 --format json)
STATUS_FIELD_ID=$(jq -r '.fields[] | select(.name == "Status") | .id' <<< "$FIELDS_JSON")
IN_PROGRESS_OPTION_ID=$(jq -r \
    '.fields[] | select(.name == "Status") | .options[] | select(.name == "In Progress") | .id' \
    <<< "$FIELDS_JSON")
PRIORITY_FIELD_ID=$(jq -r '.fields[] | select(.name == "Priority") | .id' <<< "$FIELDS_JSON")
P1_OPTION_ID=$(jq -r \
    '.fields[] | select(.name == "Priority") | .options[] | select(.name == "P1") | .id' \
    <<< "$FIELDS_JSON")
[[ -n "$STATUS_FIELD_ID" && -n "$IN_PROGRESS_OPTION_ID" &&
   -n "$PRIORITY_FIELD_ID" && -n "$P1_OPTION_ID" ]] ||
    die "required Status/Priority fields or options are missing"

sentinel_status=$(jq -r --arg title "$sentinel_title" \
    '.items[] | select(.content.title == $title) | (.status // "")' <<< "$PROJECT_ITEMS_JSON")
sentinel_priority=$(jq -r --arg title "$sentinel_title" \
    '.items[] | select(.content.title == $title) | (.priority // "")' <<< "$PROJECT_ITEMS_JSON")

if [[ "$sentinel_status" != "In Progress" ]]; then
    [[ "$CHECK_ONLY" == false ]] || die "final-page sentinel Status is not 'In Progress'"
    printf 'Setting final-page sentinel Status...\n'
    gh project item-edit \
        --id "$sentinel_item_id" \
        --project-id "$PROJECT_ID" \
        --field-id "$STATUS_FIELD_ID" \
        --single-select-option-id "$IN_PROGRESS_OPTION_ID" >/dev/null
    pause_after_mutation
fi
if [[ "$sentinel_priority" != "P1" ]]; then
    [[ "$CHECK_ONLY" == false ]] || die "final-page sentinel Priority is not 'P1'"
    printf 'Setting final-page sentinel Priority...\n'
    gh project item-edit \
        --id "$sentinel_item_id" \
        --project-id "$PROJECT_ID" \
        --field-id "$PRIORITY_FIELD_ID" \
        --single-select-option-id "$P1_OPTION_ID" >/dev/null
    pause_after_mutation
fi

if [[ "$CHECK_ONLY" == false ]]; then
    dotnet run --project "$REPO_ROOT/src/Highbyte.Wrighty.Cli" -- \
        init --config "$CONFIG_PATH" --check >/dev/null
fi

PROJECT_URL=$(gh project view "$PROJECT_NUMBER" --owner "$OWNER" --format json --jq .url)
printf '%s\n' \
    "Persistent GitHub pagination fixture is valid:" \
    "  Repository: $REPOSITORY" \
    "  Project:    $PROJECT_URL" \
    "  Items:      $ITEM_COUNT" \
    "  Sentinel:   $sentinel_title (In Progress / P1)" \
    "  Config:     $CONFIG_PATH" \
    "" \
    "The live pagination test was not run."
