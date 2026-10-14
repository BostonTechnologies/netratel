#!/usr/bin/env python3
"""Resolve copied documentation links against installed artifacts or immutable source."""
from __future__ import annotations

import argparse
import importlib.util
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import quote, unquote, urlsplit


CHECKER_PATH = Path(__file__).with_name("check-documentation.py")
_spec = importlib.util.spec_from_file_location("documentation_checker", CHECKER_PATH)
checker = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(checker)


def prepare_bundle(repository: Path, bundle: Path, commit: str, documents: dict[str, str] | None = None) -> int:
    """Preserve one source while adapting relative destinations to archive layout."""
    if not re.fullmatch(r"[0-9a-fA-F]{40}", commit):
        raise ValueError("Documentation source must identify the complete evaluated release commit SHA.")
    repository, bundle = repository.resolve(), bundle.resolve()
    documents = documents or {}
    source_to_installed: dict[str, Path] = {}
    installed_to_source: dict[Path, Path] = {}
    for installed in sorted(bundle.rglob("*.md")):
        relative = installed.relative_to(bundle).as_posix()
        source_relative = documents.get(relative, "release/INSTALL.md" if relative == "INSTALL.md" else relative)
        source = (repository / source_relative).resolve()
        if not source.is_relative_to(repository) or not source.is_file():
            raise ValueError(f"No canonical documentation source for {relative}")
        source_to_installed[source_relative] = installed
        installed_to_source[installed] = source

    changed = 0
    for installed, source in installed_to_source.items():
        markdown = source.read_text(encoding="utf-8")
        prose = checker.prose_only(markdown)
        replacements: list[tuple[int, int, str]] = []
        for offset, raw_target in checker.local_destinations(prose):
            target = re.sub(r"\\([\\`*{}\[\]()#+.!_<> -])", r"\1", raw_target)
            if not target or re.search(r"[{}<>$]", target) or re.match(r"^[A-Za-z][A-Za-z0-9+.-]*:", target) or target.startswith("//"):
                continue
            parsed = urlsplit(target)
            if not parsed.path:
                continue  # Same-document fragments survive the content-preserving copy.
            path = unquote(parsed.path)
            source_relative = source.relative_to(repository).as_posix()
            source_base = repository if source_relative in checker.ARCHIVE_ROOT_DOCUMENTS else source.parent
            destination = (repository / path.lstrip("/") if path.startswith("/") else source_base / path).resolve()
            if not destination.is_relative_to(repository) or not destination.exists():
                raise ValueError(f"{source_relative}: local documentation destination does not exist: {raw_target}")
            canonical = destination.relative_to(repository).as_posix()
            installed_destination = source_to_installed.get(canonical)
            if installed_destination is None:
                distributed_path = canonical.removeprefix("release/") if canonical.startswith("release/") else canonical
                candidate = bundle / distributed_path
                if candidate.exists():
                    installed_destination = candidate
            suffix = ("?" + parsed.query if parsed.query else "") + ("#" + parsed.fragment if parsed.fragment else "")
            if installed_destination is not None:
                rendered = quote(os.path.relpath(installed_destination, installed.parent), safe="/._-") + suffix
            else:
                kind = "tree" if destination.is_dir() else "blob"
                rendered = f"https://github.com/BostonTechnologies/netratel/{kind}/{commit}/{quote(canonical, safe='/._-')}" + suffix
            if rendered == raw_target:
                continue
            # Offsets come from masked prose and therefore match the original source.
            start = offset + 2 if prose[offset:offset + 2] == "](" else re.match(r"^ {0,3}\[[^\]\n]+\]:[ \t]*", prose[offset:]).end() + offset
            item = checker.destination_at(prose, start)
            if item is None or item[0] != raw_target:
                raise ValueError("Unable to preserve the original Markdown destination span.")
            end = item[1]
            if prose[end - 1:end] == ">":
                end -= 1
            replacements.append((end - len(raw_target), end, rendered))
        for start, end, rendered in sorted(replacements, reverse=True):
            markdown = markdown[:start] + rendered + markdown[end:]
        installed.write_text(markdown, encoding="utf-8")
        changed += bool(replacements)

    errors = checker.check_repository(bundle, packaged=True, require_docs=(bundle / "docs").is_dir())
    if errors:
        raise ValueError("Packaged documentation link validation failed:\n" + "\n".join(errors))
    return changed


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--document", action="append", default=[], metavar="INSTALLED=SOURCE", help="Canonical repository source for a relocated standalone guide")
    args = parser.parse_args()
    mappings: dict[str, str] = {}
    for mapping in args.document:
        if "=" not in mapping:
            parser.error("--document requires INSTALLED=SOURCE")
        installed, source = mapping.split("=", 1)
        if not installed or not source or installed in mappings:
            parser.error("--document mappings must be nonempty and unique")
        mappings[installed] = source
    repository = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    try:
        count = prepare_bundle(repository, args.bundle, args.commit, mappings)
    except ValueError as error:
        parser.error(str(error))
    print(f"packaged documentation links passed; adapted {count} guides to installed paths or source {args.commit}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
