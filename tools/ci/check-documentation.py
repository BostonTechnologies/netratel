#!/usr/bin/env python3
"""Check documentation placement and local Markdown destinations without dependencies.

Local Markdown fragments use rendered heading/HTML anchors. Fenced/indented code, inline code, comments,
external URLs, and variable/template destinations are excluded from link checks.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
import html
import unicodedata
from pathlib import Path
from urllib.parse import unquote, urlsplit


ROOT_FILES = {"README.md", "THIRD-PARTY-NOTICES.txt"}
# release-build.yml copies this file to the Compose archive root beside docs/.
# Validate its shipped destinations from that root, not its source directory.
ARCHIVE_ROOT_DOCUMENTS = {"release/INSTALL.md"}


def prose_only(markdown: str) -> str:
    """Blank code/comments while preserving offsets for useful line numbers."""
    lines = markdown.splitlines(keepends=True)
    fence: tuple[str, int] | None = None
    for index, line in enumerate(lines):
        marker = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", line.rstrip("\r\n"))
        if fence:
            if marker and marker[1][0] == fence[0] and len(marker[1]) >= fence[1] and not marker[2].strip():
                fence = None
            lines[index] = re.sub(r"[^\r\n]", " ", line)
        elif marker:
            fence = (marker[1][0], len(marker[1]))
            lines[index] = re.sub(r"[^\r\n]", " ", line)
        elif line.startswith(("    ", "\t")):
            lines[index] = re.sub(r"[^\r\n]", " ", line)
    prose = "".join(lines)
    prose = re.sub(r"<!--[\s\S]*?-->", lambda match: re.sub(r"[^\r\n]", " ", match[0]), prose)
    # Code spans close with the same number of backticks as their opener.
    cursor = 0
    while cursor < len(prose):
        if prose[cursor] != "`":
            cursor += 1
            continue
        end = cursor
        while end < len(prose) and prose[end] == "`":
            end += 1
        marker = prose[cursor:end]
        closer = re.search(r"(?<!`)" + re.escape(marker) + r"(?!`)", prose[end:])
        if closer:
            close_end = end + closer.end()
            prose = prose[:cursor] + re.sub(r"[^\r\n]", " ", prose[cursor:close_end]) + prose[close_end:]
            cursor = close_end
        else:
            cursor = end
    return prose


def destination_at(text: str, start: int) -> tuple[str, int] | None:
    """Read a Markdown destination, including angle brackets and balanced ()."""
    cursor = start
    while cursor < len(text) and text[cursor].isspace():
        cursor += 1
    start = cursor
    if cursor < len(text) and text[cursor] == "<":
        end = text.find(">", cursor + 1)
        if end != -1 and "\n" not in text[cursor:end]:
            return text[cursor + 1:end], end + 1
        return None
    depth = 0
    while cursor < len(text):
        character = text[cursor]
        if character == "\\" and cursor + 1 < len(text):
            cursor += 2
            continue
        if character == "(":
            depth += 1
        elif character == ")":
            if depth == 0:
                break
            depth -= 1
        elif character.isspace() and depth == 0:
            break
        cursor += 1
    return text[start:cursor], cursor


def local_destinations(prose: str):
    """Yield destinations from inline/image links and reference definitions."""
    # Checking definitions directly also covers full, collapsed, and shortcut
    # reference links without confusing ordinary prose in square brackets.
    for match in re.finditer(r"^ {0,3}\[[^\]\n]+\]:[ \t]*", prose, re.MULTILINE):
        destination = destination_at(prose, match.end())
        if destination:
            yield match.start(), destination[0]
    for match in re.finditer(r"(?<!\\)\]\(", prose):
        # A closing bracket alone is not a link; require an opening label.
        line_start = prose.rfind("\n", 0, match.start()) + 1
        if "[" not in prose[line_start:match.start()]:
            continue
        destination = destination_at(prose, match.end())
        if not destination:
            continue
        target, end = destination
        tail = prose[end:]
        if re.match(r"\s*(?:\"[^\"]*\"|'[^']*'|\([^)]*\))?\s*\)", tail):
            yield match.start(), target


def exact_path_exists(path: Path) -> bool:
    """Do not let a case-insensitive workstation hide broken Linux links."""
    current = Path(path.anchor)
    for component in path.parts[1:]:
        if component == ".":
            continue
        if component == "..":
            current = current.parent
            continue
        if not current.is_dir() or component not in {entry.name for entry in current.iterdir()}:
            return False
        current /= component
    return current.exists()


def markdown_anchors(markdown: str) -> set[str]:
    """Resolve GFM heading slugs, duplicate suffixes and explicit HTML anchors."""
    lines = markdown.splitlines()
    fence: tuple[str, int] | None = None
    for index, line in enumerate(lines):
        marker = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", line)
        if fence:
            if marker and marker[1][0] == fence[0] and len(marker[1]) >= fence[1] and not marker[2].strip():
                fence = None
            lines[index] = ""
        elif marker:
            fence = (marker[1][0], len(marker[1]))
            lines[index] = ""
        elif line.startswith(("    ", "\t")):
            lines[index] = ""
    visible = re.sub(r"<!--[\s\S]*?-->", "", "\n".join(lines))
    html_tags = re.findall(r"<[A-Za-z][^>]*>", prose_only(visible))
    anchors = {html.unescape(match[2]) for tag in html_tags for match in re.finditer(
        r"\b(?:id|name)\s*=\s*([\"'])(.*?)\1", tag, re.IGNORECASE
    )}
    counts: dict[str, int] = {}
    visible_lines = visible.splitlines()
    for index, line in enumerate(visible_lines):
        heading = re.match(r"^ {0,3}#{1,6}(?:\s+|$)(.*?)(?:\s+#+\s*)?$", line)
        text = heading[1] if heading else None
        if text is None and index + 1 < len(visible_lines) and line.strip() and re.fullmatch(r" {0,3}(?:=+|-+)\s*", visible_lines[index + 1]):
            text = line.strip()
        if text is None:
            continue
        # Rendering retains link labels and code text, but not their markup.
        text = re.sub(r"!?\[([^\]]+)\]\([^)]*\)", r"\1", text)
        text = re.sub(r"<[^>]*>", "", text)
        text = html.unescape(text).strip().lower()
        slug = "".join(character for character in text if character in " -_" or unicodedata.category(character)[0] in "LNM").replace(" ", "-")
        duplicate = counts.get(slug, 0)
        counts[slug] = duplicate + 1
        anchors.add(slug + (f"-{duplicate}" if duplicate else ""))
    return anchors


def check_repository(root: Path, *, packaged: bool = False, require_docs: bool = True) -> list[str]:
    errors: list[str] = []
    anchor_cache: dict[Path, set[str]] = {}
    docs = root / "docs"
    if not require_docs:
        pass
    elif not docs.is_dir():
        errors.append("missing docs/ directory")
    else:
        if not (docs / "README.md").is_file():
            errors.append("missing docs/README.md documentation index")
        for entry in sorted(docs.iterdir()):
            if not entry.is_dir() and entry.name not in ROOT_FILES:
                errors.append(f"docs/{entry.name}: documentation must live in a categorized subdirectory")

    tracked = [str(path.relative_to(root)) for path in root.rglob("*.md")] if packaged else subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=root
    ).decode().split("\0")
    # Include untracked Markdown so this check also helps before staging moves.
    markdown_paths = sorted({path for path in tracked if path.lower().endswith(".md")})
    for relative in markdown_paths:
        source = root / relative
        if not source.is_file():
            continue  # A tracked file may have been moved/deleted in the worktree.
        prose = prose_only(source.read_text(encoding="utf-8"))
        for offset, raw_target in local_destinations(prose):
            target = re.sub(r"\\([\\`*{}\[\]()#+.!_<> -])", r"\1", raw_target)
            # These are documented examples, not resolvable repository paths.
            if not target or re.search(r"[{}<>$]", target):
                continue
            if re.match(r"^[A-Za-z][A-Za-z0-9+.-]*:", target) or target.startswith("//"):
                continue
            parsed = urlsplit(target)
            if not parsed.path and not parsed.fragment:
                continue
            local_path = unquote(parsed.path)
            base = root if relative in ARCHIVE_ROOT_DOCUMENTS else source.parent
            candidate = source if not local_path else root / local_path.lstrip("/") if local_path.startswith("/") else base / local_path
            if not exact_path_exists(candidate):
                line = prose.count("\n", 0, offset) + 1
                errors.append(f"{relative}:{line}: missing local link target {raw_target!r} (paths are case-sensitive)")
            elif parsed.fragment and candidate.suffix.lower() == ".md":
                candidate = candidate.resolve()
                if candidate not in anchor_cache:
                    anchor_cache[candidate] = markdown_anchors(candidate.read_text(encoding="utf-8"))
                if unquote(parsed.fragment) not in anchor_cache[candidate]:
                    line = prose.count("\n", 0, offset) + 1
                    errors.append(f"{relative}:{line}: missing Markdown anchor in {raw_target!r}")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, help="Check an extracted distribution directory instead of the Git checkout")
    parser.add_argument("--links-only", action="store_true", help="For distributions without the categorized docs tree")
    args = parser.parse_args()
    root = args.root.resolve() if args.root else Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
    errors = check_repository(root, packaged=args.root is not None, require_docs=not args.links_only)
    for error in errors:
        print(f"documentation validation: {error}", file=sys.stderr)
    if errors:
        return 1
    print("documentation placement and local Markdown links passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
