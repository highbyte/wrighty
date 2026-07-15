#!/usr/bin/env python3
"""Write the Wrighty Homebrew Formula and Scoop manifest for one release."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


REPOSITORY = "highbyte/wrighty"
SCOOP_VERSION = "$version"
VERSION_PATTERN = re.compile(
    r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z.-]+)?$"
)
HASH_PATTERN = re.compile(r"^[0-9a-f]{64}$")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--homebrew-directory", type=Path, required=True)
    parser.add_argument("--scoop-directory", type=Path, required=True)
    parser.add_argument("--osx-arm64-hash", required=True)
    parser.add_argument("--linux-x64-hash", required=True)
    parser.add_argument("--linux-arm64-hash", required=True)
    parser.add_argument("--win-x64-hash", required=True)
    parser.add_argument("--win-arm64-hash", required=True)
    return parser.parse_args()


def validate(arguments: argparse.Namespace) -> None:
    if not VERSION_PATTERN.fullmatch(arguments.version):
        raise ValueError("--version must be a semantic version without a leading v")
    if arguments.tag not in (arguments.version, f"v{arguments.version}"):
        raise ValueError("--tag must equal --version, optionally prefixed with v")

    for name, value in vars(arguments).items():
        if name.endswith("_hash") and not HASH_PATTERN.fullmatch(value.lower()):
            raise ValueError(f"--{name.replace('_', '-')} must be a SHA-256 hash")


def release_url(tag: str, version: str, rid: str) -> str:
    return f"https://github.com/{REPOSITORY}/releases/download/{tag}/wrighty-{version}-{rid}.zip"


def write_homebrew_formula(arguments: argparse.Namespace) -> None:
    formula_path = arguments.homebrew_directory / "Formula" / "wrighty.rb"
    formula_path.parent.mkdir(parents=True, exist_ok=True)
    formula_path.write_text(
        f'''class Wrighty < Formula
  desc "Local-first work coordination for developers and coding agents"
  homepage "https://github.com/{REPOSITORY}"
  version "{arguments.version}"

  on_macos do
    on_arm do
      url "{release_url(arguments.tag, arguments.version, "osx-arm64")}"
      sha256 "{arguments.osx_arm64_hash.lower()}"
    end
  end

  on_linux do
    on_intel do
      url "{release_url(arguments.tag, arguments.version, "linux-x64")}"
      sha256 "{arguments.linux_x64_hash.lower()}"
    end
    on_arm do
      url "{release_url(arguments.tag, arguments.version, "linux-arm64")}"
      sha256 "{arguments.linux_arm64_hash.lower()}"
    end
  end

  def install
    bin.install "wrighty"
    bin.install "skills"
  end

  test do
    assert_match "Wrighty", shell_output("#{{bin}}/wrighty --help")
  end
end
''',
        encoding="utf-8",
    )


def write_scoop_manifest(arguments: argparse.Namespace) -> None:
    manifest_path = arguments.scoop_directory / "bucket" / "wrighty.json"
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    autoupdate_tag = (
        SCOOP_VERSION if arguments.tag == arguments.version else f"v{SCOOP_VERSION}"
    )
    manifest = {
        "version": arguments.version,
        "description": "Local-first work coordination for developers and coding agents",
        "homepage": f"https://github.com/{REPOSITORY}",
        "architecture": {
            "64bit": {
                "url": release_url(arguments.tag, arguments.version, "win-x64"),
                "hash": arguments.win_x64_hash.lower(),
            },
            "arm64": {
                "url": release_url(arguments.tag, arguments.version, "win-arm64"),
                "hash": arguments.win_arm64_hash.lower(),
            },
        },
        "bin": "wrighty.exe",
        "checkver": {"github": f"https://github.com/{REPOSITORY}"},
        "autoupdate": {
            "architecture": {
                "64bit": {
                    "url": release_url(
                        autoupdate_tag,
                        SCOOP_VERSION,
                        "win-x64",
                    )
                },
                "arm64": {
                    "url": release_url(
                        autoupdate_tag,
                        SCOOP_VERSION,
                        "win-arm64",
                    )
                },
            }
        },
    }
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    arguments = parse_arguments()
    validate(arguments)
    write_homebrew_formula(arguments)
    write_scoop_manifest(arguments)


if __name__ == "__main__":
    main()
