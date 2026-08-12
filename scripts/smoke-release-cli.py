#!/usr/bin/env python3
"""Exercise a released Wrighty CLI against a disposable Local Markdown store."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import stat
import subprocess
import tempfile
import zipfile
from pathlib import Path
from typing import Any


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--cli", type=Path)
    source.add_argument("--archive", type=Path)
    parser.add_argument("--checksum", type=Path)
    parser.add_argument("--rid")
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-sha", required=True)
    arguments = parser.parse_args()
    if arguments.archive and (not arguments.checksum or not arguments.rid):
        parser.error("--archive requires --checksum and --rid")
    if arguments.cli and (arguments.checksum or arguments.rid):
        parser.error("--checksum and --rid can be used only with --archive")
    return arguments


def run(
    cli: Path,
    working_directory: Path,
    *arguments: str,
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        [str(cli), *arguments],
        cwd=working_directory,
        capture_output=True,
        check=False,
        encoding="utf-8",
        text=True,
    )
    if result.returncode != 0:
        command = " ".join((cli.name, *arguments))
        raise RuntimeError(
            f"{command} exited {result.returncode}\n"
            f"stdout:\n{result.stdout}\n"
            f"stderr:\n{result.stderr}"
        )
    return result


def run_json(cli: Path, working_directory: Path, *arguments: str) -> Any:
    result = run(cli, working_directory, *arguments, "--json")
    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError(
            f"{cli.name} {' '.join(arguments)} returned invalid JSON"
        ) from error

    if payload.get("schemaVersion") != 1 or "result" not in payload:
        raise RuntimeError(
            f"{cli.name} {' '.join(arguments)} returned an unexpected response"
        )
    return payload["result"]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def expected_informational_version(version: str, source_sha: str) -> str:
    separator = "." if "+" in version else "+"
    return f"{version}{separator}{source_sha}"


def verify_checksum(archive: Path, checksum: Path) -> None:
    fields = checksum.read_text(encoding="utf-8").strip().split()
    require(len(fields) == 2, f"Invalid checksum file: {checksum}")
    require(fields[1] == archive.name, "Checksum names the wrong release archive")

    digest = hashlib.sha256()
    with archive.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    require(digest.hexdigest() == fields[0], "Release archive checksum does not match")


def smoke(cli: Path, version: str, source_sha: str) -> None:
    cli = cli.resolve()
    require(cli.is_file(), f"Wrighty executable does not exist: {cli}")

    with tempfile.TemporaryDirectory(prefix="wrighty-release-smoke-") as temporary:
        root = Path(temporary)

        actual_version = run(cli, root, "--version").stdout.strip()
        expected_version = expected_informational_version(version, source_sha)
        require(
            actual_version == expected_version,
            f"Expected Wrighty version {expected_version!r}, got {actual_version!r}",
        )

        initialized = run_json(
            cli,
            root,
            "init",
            "--backend",
            "local-markdown",
            "--local-path",
            "store",
            "--yes",
        )
        require(initialized["backend"] == "local-markdown", "Unexpected backend")
        require(initialized["initialized"] is True, "Local Markdown was not initialized")
        require(initialized["valid"] is True, "Local Markdown initialization is invalid")

        attempt = run_json(cli, root, "creation-attempt", "new")
        attempt_id = attempt["creationAttemptId"]

        created = run_json(
            cli,
            root,
            "create",
            "--title",
            "Release smoke item",
            "--body",
            "Created by the release smoke test.",
            "--priority",
            "P2",
            "--creation-attempt-id",
            attempt_id,
        )
        item_id = created["id"]
        require(created["item"]["status"] == "Worker queue", "Unexpected initial status")
        require(created["item"]["priority"] == "P2", "Unexpected initial priority")

        items = run_json(cli, root, "list")
        require(
            any(item["id"] == item_id for item in items),
            "Created item is missing from list",
        )

        fetched = run_json(cli, root, "get", item_id)
        require(fetched["title"] == "Release smoke item", "Unexpected created title")

        claim = run_json(
            cli,
            root,
            "claim",
            item_id,
            "--claimant-kind",
            "automation",
            "--claimant-id",
            "release-smoke",
        )
        require(claim["outcome"] == "Acquired", "Smoke claim was not acquired")

        edited = run_json(
            cli,
            root,
            "edit",
            item_id,
            "--body",
            "Edited by the release smoke test.",
            "--claimant-kind",
            "automation",
            "--claimant-id",
            claim["claimantId"],
            "--claim-token",
            claim["claimToken"],
        )
        require(
            edited["item"]["body"] == "Edited by the release smoke test.",
            "Smoke edit was not persisted",
        )

        finished = run_json(
            cli,
            root,
            "finish",
            item_id,
            "--claimant-kind",
            "automation",
            "--claimant-id",
            claim["claimantId"],
            "--claim-token",
            claim["claimToken"],
        )
        require(finished["disposition"] == "finished", "Smoke item was not finished")
        require(finished["item"]["status"] == "Done", "Unexpected completion status")
        require(finished["claimReleased"] is True, "Completion did not release claim")

        final_item = run_json(cli, root, "get", item_id)
        require(final_item["status"] == "Done", "Final status was not persisted")
        require(final_item["claim"]["state"] == "Unclaimed", "Final claim was not released")

        store = Path(initialized["localPath"])
        require((store / ".wrighty-runtime-v1.json").is_file(), "Runtime state is missing")
        require(
            any((store / "items").glob("*.md")),
            "Local Markdown item file is missing",
        )


def smoke_archive(
    archive: Path,
    checksum: Path,
    rid: str,
    version: str,
    source_sha: str,
) -> None:
    archive = archive.resolve()
    checksum = checksum.resolve()
    require(archive.is_file(), f"Release archive does not exist: {archive}")
    require(checksum.is_file(), f"Release checksum does not exist: {checksum}")
    verify_checksum(archive, checksum)

    with tempfile.TemporaryDirectory(prefix="wrighty-release-archive-") as temporary:
        extraction_root = Path(temporary)
        with zipfile.ZipFile(archive) as release:
            release.extractall(extraction_root)

        cli = extraction_root / ("wrighty.exe" if rid.startswith("win-") else "wrighty")
        require(cli.is_file(), f"Released Wrighty executable is missing for {rid}")
        if os.name != "nt":
            cli.chmod(cli.stat().st_mode | stat.S_IXUSR)
        smoke(cli, version, source_sha)


def main() -> int:
    arguments = parse_arguments()
    try:
        if arguments.archive:
            smoke_archive(
                arguments.archive,
                arguments.checksum,
                arguments.rid,
                arguments.version,
                arguments.source_sha,
            )
        else:
            smoke(arguments.cli, arguments.version, arguments.source_sha)
    except (KeyError, OSError, RuntimeError, zipfile.BadZipFile) as error:
        print(f"Release smoke test failed: {error}")
        return 1

    print(
        "Release smoke test passed: version, Local Markdown lifecycle, "
        "stored state, and cleanup"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
