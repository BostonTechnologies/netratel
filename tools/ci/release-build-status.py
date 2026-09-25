#!/usr/bin/env python3
"""Classify tag-triggered release-build runs for publication preflight."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


def classify_runs(runs: list[dict[str, Any]], tag: str, revision: str) -> dict[str, str]:
    """Return the next safe action for runs matching the approved tag and commit."""
    matching = sorted(
        (
            run
            for run in runs
            if run.get("headSha") == revision
            and run.get("headBranch") == tag
            and run.get("event") == "push"
        ),
        key=lambda run: run.get("createdAt", ""),
        reverse=True,
    )

    successful = next(
        (
            run
            for run in matching
            if run.get("status") == "completed" and run.get("conclusion") == "success"
        ),
        None,
    )
    if successful is not None:
        return {
            "state": "success",
            "run_id": str(successful.get("databaseId", "")),
            "url": str(successful.get("url", "")),
        }

    active = next(
        (
            run
            for run in matching
            if run.get("status") in {"queued", "in_progress"}
        ),
        None,
    )
    if active is not None:
        return {
            "state": "active",
            "run_id": str(active.get("databaseId", "")),
            "status": str(active.get("status", "")),
            "url": str(active.get("url", "")),
        }

    failed = next(
        (
            run
            for run in matching
            if run.get("status") == "completed" and run.get("conclusion") != "success"
        ),
        None,
    )
    if failed is not None:
        return {
            "state": "failed",
            "run_id": str(failed.get("databaseId", "")),
            "conclusion": str(failed.get("conclusion", "")),
            "url": str(failed.get("url", "")),
        }

    return {"state": "waiting"}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", required=True)
    parser.add_argument("--revision", required=True)
    args = parser.parse_args()

    try:
        runs = json.load(sys.stdin)
    except json.JSONDecodeError as error:
        raise SystemExit(f"Release build run input is not valid JSON: {error}") from error
    if not isinstance(runs, list) or any(not isinstance(run, dict) for run in runs):
        raise SystemExit("Release build run input must be a JSON array of objects.")

    print(json.dumps(classify_runs(runs, args.tag, args.revision), separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
