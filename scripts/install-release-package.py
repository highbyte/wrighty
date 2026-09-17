#!/usr/bin/env python3
"""Install Wrighty on a disposable release runner with bounded transient retries."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
import time


DELAYS = (10, 30)
# Denials and integrity failures take precedence even when another line mentions a
# transient error. Unknown errors stop too; never retry arbitrary installer failures.
PERMANENT = re.compile(
    r"(?:checksum|sha-?256|hash|signature|attestation)[^\n]*(?:mismatch|invalid|failed|does not match)"
    r"|(?:permission|access) (?:is )?denied|unauthorized|forbidden"
    r"|(?:HTTP[^\n]*|returned (?:an )?error:|status code(?: does not indicate success)?:)\s*\(?(?:401|403|404)\b"
    r"|certificate[^\n]*(?:invalid|expired|verify failed)"
    r"|no available formula|couldn.t find manifest|unknown command|invalid argument",
    re.IGNORECASE,
)
TRANSIENT = re.compile(
    r"broken pipe|connection (?:reset|aborted|timed out)|remote end hung up unexpectedly"
    r"|could not resolve host|temporary failure in name resolution"
    r"|(?:operation|request) timed out"
    r"|(?:HTTP[^\n]*|returned (?:an )?error:|status code(?: does not indicate success)?:)\s*\(?50[234]\b"
    r"|curl: \((?:5|6|7|18|28|52|55|56)\)",
    re.IGNORECASE,
)


def retryable(exit_code: int, output: str) -> bool:
    return (
        exit_code > 0
        and exit_code not in (130, 143)
        and not PERMANENT.search(output)
        and bool(TRANSIENT.search(output))
    )


def run(command: list[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, encoding="utf-8", errors="replace", check=False,
    )


def brew_path(option: str) -> Path:
    result = run(["brew", option])
    if result.returncode != 0 or not result.stdout.strip():
        raise RuntimeError(f"Could not inspect Homebrew {option}: {result.stdout}")
    return Path(result.stdout.strip())


def package_paths(manager: str, scoop_root: Path | None) -> tuple[Path, Path]:
    if manager == "homebrew":
        return brew_path("--cellar") / "wrighty", brew_path("--prefix") / "bin" / "wrighty"
    if scoop_root is None:
        raise RuntimeError("Scoop installation requires --scoop-root.")
    return scoop_root / "apps" / "wrighty", scoop_root / "shims" / "wrighty.exe"


def present(path: Path) -> bool:
    # Include dangling links left by interrupted installations.
    return path.exists() or path.is_symlink()


def inspect_installation(package: Path, cli: Path, expected_version: str) -> bool:
    """Return false only for an absent install; ambiguous/partial state must stop."""
    if not present(package) and not present(cli):
        return False
    if not package.is_dir() or not cli.is_file():
        raise RuntimeError("Partial Wrighty installation detected; refusing another install attempt.")
    result = run([str(cli), "--version"])
    if result.returncode != 0 or result.stdout.strip() != expected_version:
        raise RuntimeError(
            f"Installed Wrighty failed version verification (expected {expected_version!r}, "
            f"exit {result.returncode}): {result.stdout.strip()}"
        )
    return True


def report(message: str) -> None:
    print(message, flush=True)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as destination:
            destination.write(message + "\n\n")


def install(
    manager: str, command: list[str], package: Path, cli: Path,
    expected_version: str, log_directory: Path,
) -> None:
    if present(package) or present(cli):
        raise RuntimeError("Wrighty is already present; this check requires a clean disposable runner.")
    log_directory.mkdir(parents=True, exist_ok=True)
    for attempt in range(1, len(DELAYS) + 2):
        report(f"{manager}: Wrighty installation attempt {attempt}/3.")
        result = run(command)
        log = log_directory / f"{manager}-attempt-{attempt}.log"
        # Record the original failure before inspecting state or retrying. No environment dump.
        log.write_text(f"Exit code: {result.returncode}\n{result.stdout}", encoding="utf-8")
        print(result.stdout, end="" if result.stdout.endswith("\n") else "\n", flush=True)
        if result.returncode == 0:
            if not inspect_installation(package, cli, expected_version):
                raise RuntimeError("Installer exited successfully but Wrighty is missing.")
            report(f"{manager}: installed and version verified on attempt {attempt}/3; functional smoke test still required.")
            return
        if not retryable(result.returncode, result.stdout):
            raise RuntimeError(f"{manager}: non-retryable installation failure (exit {result.returncode}); see {log}.")
        # A transient error may happen after installation. Verify exact version/commit
        # before accepting that state; never repair a partial or wrong-version install.
        if inspect_installation(package, cli, expected_version):
            report(f"{manager}: command failed on attempt {attempt}/3, but installation completed and version verified; functional smoke test still required.")
            return
        if attempt > len(DELAYS):
            raise RuntimeError(f"{manager}: transient installation failure persisted after 3 attempts; see {log}.")
        delay = DELAYS[attempt - 1]
        report(f"{manager}: recognized transient failure (exit {result.returncode}); retrying in {delay}s. Log: {log.name}.")
        time.sleep(delay)
        # Recheck after the delay as well, so a late-completing installer is not run twice.
        if inspect_installation(package, cli, expected_version):
            report(f"{manager}: installation completed during retry delay and version verified; functional smoke test still required.")
            return


def install_command(manager: str) -> list[str]:
    if manager == "homebrew":
        return ["brew", "install", "--verbose", "highbyte/tap/wrighty"]
    # The workflow supplies SCOOP_CMD as a path; never interpolate it into shell code.
    if not os.environ.get("SCOOP_CMD"):
        raise RuntimeError("Scoop installation requires SCOOP_CMD.")
    return [
        "pwsh", "-NoProfile", "-NonInteractive", "-Command",
        "$ErrorActionPreference = 'Stop'; & $env:SCOOP_CMD install highbyte/wrighty; exit $LASTEXITCODE",
    ]


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manager", choices=("homebrew", "scoop"), required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--log-directory", type=Path, required=True)
    parser.add_argument("--scoop-root", type=Path)
    args = parser.parse_args()
    package, cli = package_paths(args.manager, args.scoop_root)
    separator = "." if "+" in args.version else "+"
    install(args.manager, install_command(args.manager), package, cli,
            f"{args.version}{separator}{args.source_sha}", args.log_directory)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError) as error:
        report(f"Package installation stopped: {error}")
        sys.exit(1)
