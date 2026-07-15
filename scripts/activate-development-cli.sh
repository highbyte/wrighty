#!/usr/bin/env bash

# Source this file so the function it defines remains in the current shell:
#   source scripts/activate-development-cli.sh

if [[ -n "${ZSH_VERSION:-}" ]]; then
    case "${ZSH_EVAL_CONTEXT:-}" in
        *:file) ;;
        *)
            printf '%s\n' "error: this script must be sourced: source scripts/activate-development-cli.sh" >&2
            exit 2
            ;;
    esac
    _wrighty_dev_script=${(%):-%x}
elif [[ -n "${BASH_VERSION:-}" ]]; then
    if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
        printf '%s\n' "error: this script must be sourced: source scripts/activate-development-cli.sh" >&2
        exit 2
    fi
    _wrighty_dev_script=${BASH_SOURCE[0]}
else
    printf '%s\n' "error: this script supports Bash and Zsh" >&2
    return 2
fi

_wrighty_dev_script_dir=$(cd "$(dirname "$_wrighty_dev_script")" && pwd)
_wrighty_dev_root=$(cd "$_wrighty_dev_script_dir/.." && pwd)
_wrighty_dev_configuration=${WRIGHTY_DEV_CONFIGURATION:-Debug}
_wrighty_dev_project="$_wrighty_dev_root/src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj"
_wrighty_dev_output_dir="$_wrighty_dev_root/src/Highbyte.Wrighty.Cli/bin/$_wrighty_dev_configuration/net10.0"
_wrighty_dev_dll="$_wrighty_dev_output_dir/wrighty.dll"
_wrighty_dev_executable="$_wrighty_dev_output_dir/wrighty"

if [[ "${WRIGHTY_DEV_NO_BUILD:-0}" != "1" ]]; then
    if ! command dotnet build "$_wrighty_dev_project" --configuration "$_wrighty_dev_configuration" --nologo; then
        unset _wrighty_dev_script _wrighty_dev_script_dir _wrighty_dev_root
        unset _wrighty_dev_configuration _wrighty_dev_project _wrighty_dev_output_dir
        unset _wrighty_dev_dll _wrighty_dev_executable
        return 1
    fi
elif [[ ! -f "$_wrighty_dev_dll" ]]; then
    printf 'error: development artifact does not exist: %s\n' "$_wrighty_dev_dll" >&2
    unset _wrighty_dev_script _wrighty_dev_script_dir _wrighty_dev_root
    unset _wrighty_dev_configuration _wrighty_dev_project _wrighty_dev_output_dir
    unset _wrighty_dev_dll _wrighty_dev_executable
    return 1
fi

if [[ ! -x "$_wrighty_dev_executable" ]]; then
    printf 'error: development executable does not exist or is not executable: %s\n' \
        "$_wrighty_dev_executable" >&2
    unset _wrighty_dev_script _wrighty_dev_script_dir _wrighty_dev_root
    unset _wrighty_dev_configuration _wrighty_dev_project _wrighty_dev_output_dir
    unset _wrighty_dev_dll _wrighty_dev_executable
    return 1
fi

WRIGHTY_DEV_DLL=$_wrighty_dev_dll

# A shell function is convenient in this shell, but child processes cannot normally inherit it.
# Prepending the apphost directory to PATH also makes `wrighty` available to agent CLIs launched
# after activation and to the command shells those agents create.
if [[ -z "${WRIGHTY_DEV_ORIGINAL_PATH+x}" ]]; then
    WRIGHTY_DEV_ORIGINAL_PATH=$PATH
fi
PATH="$_wrighty_dev_output_dir:$WRIGHTY_DEV_ORIGINAL_PATH"
export PATH

wrighty() {
    command dotnet "$WRIGHTY_DEV_DLL" "$@"
}

wrighty_deactivate() {
    if [[ -n "${WRIGHTY_DEV_ORIGINAL_PATH+x}" ]]; then
        PATH=$WRIGHTY_DEV_ORIGINAL_PATH
        export PATH
    fi
    unset -f wrighty
    unset -f wrighty_deactivate
    unset WRIGHTY_DEV_DLL WRIGHTY_DEV_ORIGINAL_PATH
    printf '%s\n' "Development Wrighty command deactivated."
}

printf 'Development Wrighty command activated (%s).\n' "$_wrighty_dev_configuration"
printf '%s\n' "Try: wrighty --help"
printf '%s\n' "Deactivate with: wrighty_deactivate"

unset _wrighty_dev_script _wrighty_dev_script_dir _wrighty_dev_root
unset _wrighty_dev_configuration _wrighty_dev_project _wrighty_dev_output_dir
unset _wrighty_dev_dll _wrighty_dev_executable
