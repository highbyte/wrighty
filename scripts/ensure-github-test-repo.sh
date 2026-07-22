#!/usr/bin/env bash
#
# ensure-github-test-repo.sh — resolve, create, and validate the dedicated private
# integration-test repository derived from a source repository as <owner>/<repo>-test.
#
# This is the substrate the GitHub integration scripts (claim fencing, pagination fixtures,
# and the future GitHub completion walkthrough) run their mutating tests against, so they
# never touch the real project. It is deliberately small and reusable two ways:
#
#   1. Standalone — ensure the test repo exists and print its OWNER/REPO name:
#        scripts/ensure-github-test-repo.sh                     # derive from the current gh repo
#        scripts/ensure-github-test-repo.sh --source-repo owner/wrighty
#        scripts/ensure-github-test-repo.sh --require           # assert it exists; never create
#        scripts/ensure-github-test-repo.sh --name-only         # just print the name (no gh, no auth)
#
#   2. Sourced — reuse the functions from another script:
#        source "$(dirname "${BASH_SOURCE[0]}")/ensure-github-test-repo.sh"
#        TEST_REPO=$(github_test_repo_name owner/wrighty)       # pure derivation, no network
#        TEST_REPO=$(ensure_github_test_repo owner/wrighty)     # create if missing, then validate
#        assert_github_test_repo owner/wrighty-test             # validate an existing one
#
# Progress and log lines go to stderr; the resolved OWNER/REPO is the only thing written to
# stdout, so `TEST_REPO=$(ensure_github_test_repo ...)` captures cleanly.
#
# Safety: the resolved name MUST end in "-test" and the repo MUST be private before any
# caller is allowed to treat it as a test target. This helper only ever resolves, creates,
# and validates — it never deletes or recreates. Teardown belongs to the individual fixture
# scripts, which can call assert_github_test_repo first to confirm what they are tearing down.
#
# Sourcing is side-effect free: shell options are only changed when the script is executed
# directly, and every function returns non-zero on failure instead of exiting.

_egtr_log() { printf 'ensure-github-test-repo: %s\n' "$*" >&2; }
_egtr_die() { printf 'ensure-github-test-repo: error: %s\n' "$*" >&2; return 1; }

_egtr_require_command() {
    command -v "$1" >/dev/null 2>&1 || _egtr_die "required command '$1' was not found"
}

# github_test_repo_name <owner/repo> -> echoes <owner>/<repo>-test.
# Pure string derivation with no network calls. Idempotent when the source already ends
# in -test, so it is safe to feed a test repo back through it.
github_test_repo_name() {
    local source=${1:-}
    [[ -n "$source" ]] || { _egtr_die "a source OWNER/REPO is required"; return 1; }
    [[ "$source" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ||
        { _egtr_die "source '$source' must use OWNER/REPO format"; return 1; }
    local owner=${source%%/*}
    local name=${source#*/}
    if [[ "$name" == *-test ]]; then
        printf '%s/%s\n' "$owner" "$name"
    else
        printf '%s/%s-test\n' "$owner" "$name"
    fi
}

# _egtr_resolve_source [override] -> echoes the source OWNER/REPO, from the override when
# given, otherwise from the current gh repository.
_egtr_resolve_source() {
    local override=${1:-}
    if [[ -n "$override" ]]; then
        printf '%s\n' "$override"
        return 0
    fi
    _egtr_require_command gh || return 1
    gh repo view --json nameWithOwner --jq .nameWithOwner 2>/dev/null ||
        _egtr_die "could not determine the current gh repository; pass --source-repo OWNER/REPO"
}

# assert_github_test_repo <owner/repo-test> -> validate an existing test repo.
# Fails unless the repo exists, its name ends in -test, it is private, and the caller has
# write access. Echoes the repo on success.
assert_github_test_repo() {
    local repo=${1:-}
    [[ -n "$repo" ]] || { _egtr_die "a test OWNER/REPO is required"; return 1; }
    [[ "$repo" == *-test ]] ||
        { _egtr_die "refusing to target '$repo': the test repository name must end in -test"; return 1; }
    _egtr_require_command gh || return 1
    _egtr_require_command jq || return 1

    local json
    json=$(gh repo view "$repo" --json nameWithOwner,isPrivate,viewerPermission 2>/dev/null) ||
        { _egtr_die "test repository '$repo' does not exist or is not accessible"; return 1; }

    local is_private permission
    is_private=$(printf '%s' "$json" | jq -r '.isPrivate')
    permission=$(printf '%s' "$json" | jq -r '.viewerPermission // empty')

    [[ "$is_private" == "true" ]] ||
        { _egtr_die "test repository '$repo' is not private; refusing to use it for mutating tests"; return 1; }
    case "$permission" in
        ADMIN | MAINTAIN | WRITE) ;;
        *) _egtr_die "you lack write access to '$repo' (permission: ${permission:-none})"; return 1 ;;
    esac

    _egtr_log "validated private test repository $repo (access: $permission)"
    printf '%s\n' "$repo"
}

# ensure_github_test_repo <owner/repo> [description] -> derive the test repo from the source,
# create it private when missing, then validate. Echoes the resolved OWNER/REPO.
ensure_github_test_repo() {
    local source=${1:-}
    local description=${2:-Disposable Wrighty integration-test repository (auto-created; safe to delete).}
    [[ -n "$source" ]] || { _egtr_die "a source OWNER/REPO is required"; return 1; }
    _egtr_require_command gh || return 1
    _egtr_require_command jq || return 1
    gh auth status >/dev/null 2>&1 || { _egtr_die "gh is not authenticated; run 'gh auth login'"; return 1; }

    local repo
    repo=$(github_test_repo_name "$source") || return 1

    if gh repo view "$repo" --json nameWithOwner >/dev/null 2>&1; then
        _egtr_log "test repository $repo already exists"
    else
        _egtr_log "creating private test repository $repo"
        gh repo create "$repo" --private --description "$description" >/dev/null ||
            { _egtr_die "failed to create '$repo'"; return 1; }
    fi

    assert_github_test_repo "$repo"
}

_egtr_usage() {
    cat >&2 <<'USAGE'
Usage: ensure-github-test-repo.sh [options]

Resolve, create, and validate the dedicated private <owner>/<repo>-test integration
repository. Prints the resolved OWNER/REPO on stdout; all logs go to stderr.

Options:
  --source-repo OWNER/REPO   Source repository to derive from (default: current gh repo).
  --require, --no-create     Assert the test repo already exists and is valid; never create.
  --name-only                Only derive and print the name; makes no gh calls (no auth needed).
  --description TEXT          Description used when creating the repository.
  -h, --help                 Show this help.
USAGE
}

_egtr_main() {
    local source="" mode="ensure" description=""
    while (($#)); do
        case "$1" in
            --source-repo)
                (($# >= 2)) || { _egtr_die "--source-repo requires OWNER/REPO"; return 1; }
                source=$2
                shift 2
                ;;
            --require | --no-create)
                mode="assert"
                shift
                ;;
            --name-only)
                mode="name"
                shift
                ;;
            --description)
                (($# >= 2)) || { _egtr_die "--description requires text"; return 1; }
                description=$2
                shift 2
                ;;
            -h | --help)
                _egtr_usage
                return 0
                ;;
            *)
                _egtr_die "unknown option '$1'"
                return 1
                ;;
        esac
    done

    source=$(_egtr_resolve_source "$source") || return 1

    case "$mode" in
        name)
            github_test_repo_name "$source"
            ;;
        assert)
            local repo
            repo=$(github_test_repo_name "$source") || return 1
            assert_github_test_repo "$repo"
            ;;
        ensure)
            if [[ -n "$description" ]]; then
                ensure_github_test_repo "$source" "$description"
            else
                ensure_github_test_repo "$source"
            fi
            ;;
        *)
            _egtr_die "internal error: unknown mode '$mode'"
            return 1
            ;;
    esac
}

# Only run (and only change shell options) when executed directly; sourcing exposes the
# functions without side effects.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    set -euo pipefail
    _egtr_main "$@"
fi
