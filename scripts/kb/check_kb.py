"""Check KB source paths, folder indexes, and optional Git diff coverage."""

import argparse
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit


GENERATED = {".git", "bin", "obj", "node_modules", "__pycache__", "images"}
FRAGMENTS = {"_Sidebar.md", "_Footer.md"}
CODE_LINE = re.compile(r"^\*\*Code:\*\*\s*(.*)$", re.MULTILINE)
LINK = re.compile(r"(?<!!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")


def authored_text(text):
    lines = []
    fence = None
    for line in text.splitlines():
        marker = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", line)
        if fence:
            if marker and marker[1][0] == fence[0] and len(marker[1]) >= len(fence) and not marker[2].strip():
                fence = None
        elif marker:
            fence = marker[1]
        else:
            lines.append(line)
    return "\n".join(lines)


def normalize_path(value):
    value = value.replace("\\", "/").rstrip("/")
    path = PurePosixPath(value)
    if not value or path.is_absolute() or ".." in path.parts or ":" in value:
        raise ValueError(f"not a repository-relative path: {value!r}")
    if any(char in value for char in "*?[]"):
        raise ValueError(f"wildcards are not source paths: {value!r}")
    return path.as_posix()


def pages(root):
    found = []
    for directory, folders, files in os.walk(root / "docs"):
        folders[:] = sorted(name for name in folders if name not in GENERATED)
        found.extend(
            Path(directory) / name
            for name in sorted(files)
            if name.endswith(".md") and name not in FRAGMENTS
        )
    return found


def code_map(root, documents, errors):
    coverage = {}
    for page in documents:
        text = authored_text(page.read_text(encoding="utf-8-sig"))
        for line in CODE_LINE.findall(text):
            paths = re.findall(r"`([^`]+)`", line)
            if not paths:
                errors.append(f"{page.relative_to(root)}: Code line needs backtick paths")
            for value in paths:
                try:
                    path = normalize_path(value)
                except ValueError as error:
                    errors.append(f"{page.relative_to(root)}: {error}")
                    continue
                coverage.setdefault(path, []).append(page.relative_to(root).as_posix())
                if not (root / path).exists():
                    errors.append(f"{page.relative_to(root)}: Code path does not exist: {path}")
    return coverage


def index_errors(root, documents):
    errors = []
    folders = {page.parent for page in documents}
    for folder in sorted(folders):
        index = folder / "README.md"
        if not index.is_file():
            errors.append(f"{folder.relative_to(root)}: missing README.md index")
            continue
        targets = set()
        for link in LINK.findall(authored_text(index.read_text(encoding="utf-8-sig"))):
            parsed = urlsplit(link)
            if parsed.scheme or parsed.netloc or not parsed.path:
                continue
            target = folder / unquote(parsed.path).replace("\\", "/")
            # Existing wiki source uses extensionless page links.
            if not target.exists() and target.with_suffix(".md").is_file():
                target = target.with_suffix(".md")
            targets.add(target.resolve())
            if target.name == "README.md":
                targets.add(target.parent.resolve())
        children = [
            page for page in documents if page.parent == folder and page != index
        ]
        children.extend(
            child for child in folder.iterdir()
            if child.is_dir() and child.name not in GENERATED
        )
        for child in sorted(children):
            if child.resolve() not in targets:
                errors.append(
                    f"{index.relative_to(root)}: index does not list {child.name}"
                )
    return errors


def changed_paths(root, base):
    result = subprocess.run(
        ["git", "diff", "--name-status", "-z", "--find-renames", f"{base}...HEAD", "--"],
        cwd=root, capture_output=True, text=True, encoding="utf-8", check=True,
    )
    fields = result.stdout.split("\0")
    changes = []
    offset = 0
    while offset < len(fields) and fields[offset]:
        status = fields[offset]
        old = fields[offset + 1]
        offset += 2
        if status.startswith(("R", "C")):
            new = fields[offset]
            offset += 1
            if status.startswith("R"):
                changes.append(("D", old))
            changes.append(("A", new))
        else:
            changes.append((status[0], old))
    return changes


def diff_warnings(coverage, changes):
    warnings = []
    for status, value in changes:
        path = normalize_path(value)
        if status == "D":
            for named, documents in coverage.items():
                if named == path or named.startswith(path + "/"):
                    warnings.append(
                        f"deleted or renamed Code path {named}: {', '.join(documents)}"
                    )
            continue
        parts = PurePosixPath(path).parts
        if parts[0] == "docs" or any(part in GENERATED for part in parts):
            continue
        if path.endswith((".md", ".png", ".jpg", ".svg", ".ico")):
            continue
        if not any(path == named or path.startswith(named + "/") for named in coverage):
            warnings.append(f"changed file has no Code coverage: {path}")
    return warnings


def check(root, base=None):
    documents = pages(root)
    errors = [] if documents else ["docs: no Markdown pages found"]
    coverage = code_map(root, documents, errors)
    errors.extend(index_errors(root, documents))
    warnings = diff_warnings(coverage, changed_paths(root, base)) if base else []
    return errors, warnings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--diff", metavar="REF", help="check committed branch changes against REF")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    try:
        errors, warnings = check(root, args.diff)
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"ERROR: cannot check KB: {error}", file=sys.stderr)
        return 1
    for error in errors:
        print(f"ERROR: {error}")
    for warning in warnings:
        print(f"WARNING: {warning}")
    print(f"KB: {len(errors)} error(s), {len(warnings)} warning(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
