#!/usr/bin/env python3
"""Fail CI if source control regains release binaries or signing material."""

from __future__ import annotations

import pathlib
import subprocess
import sys


ROOT = pathlib.Path(__file__).resolve().parents[1]
FORBIDDEN_SUFFIXES = {
    ".apk", ".aab", ".msi", ".exe", ".dll", ".pdb",
    ".pfx", ".p12", ".jks", ".keystore",
}
RETIRED_ENTRYPOINTS = ("index.php", "login.php", "signup.php", "time.php")
RETIRED_BODY = "require __DIR__ . '/retired.php';"


def tracked_files() -> list[pathlib.Path]:
    result = subprocess.run(
        ["git", "-c", f"safe.directory={ROOT.as_posix()}", "ls-files", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    return [ROOT / item.decode("utf-8") for item in result.stdout.split(b"\0") if item]


def main() -> int:
    failures: list[str] = []
    for path in tracked_files():
        relative = path.relative_to(ROOT).as_posix()
        if path.suffix.lower() in FORBIDDEN_SUFFIXES:
            failures.append(f"tracked release/signing artifact: {relative}")
        if "/packages/" in f"/{relative}/":
            failures.append(f"tracked restored package payload: {relative}")

    for name in RETIRED_ENTRYPOINTS:
        path = ROOT / "api" / name
        if not path.is_file() or RETIRED_BODY not in path.read_text(encoding="utf-8"):
            failures.append(f"legacy PHP entrypoint is not fail-closed: api/{name}")

    if failures:
        print("release hygiene validation failed:", file=sys.stderr)
        for failure in failures:
            print(f"- {failure}", file=sys.stderr)
        return 1
    print("release hygiene validation passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
