#!/usr/bin/env python3
"""Mirror docs/ to the GitHub Wiki from a fresh origin default-branch worktree."""

from __future__ import annotations

import argparse
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath
from urllib.parse import quote, unquote, urlsplit, urlunsplit


def git(repo: Path, *args: str, check: bool = True) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True,
        text=True,
        check=False,
    )
    if check and result.returncode:
        detail = result.stderr.strip() or result.stdout.strip()
        raise RuntimeError(f"git {' '.join(args)} failed: {detail}")
    return result.stdout.strip()


def get_default_branch(repo: Path) -> str:
    head = git(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD", check=False)
    if head.startswith("origin/"):
        return head.removeprefix("origin/")

    result = subprocess.run(
        ["git", "-C", str(repo), "ls-remote", "--symref", "origin", "HEAD"],
        capture_output=True,
        text=True,
        check=False,
    )
    match = re.search(r"^ref: refs/heads/(\S+)\s+HEAD$", result.stdout, re.MULTILINE)
    if result.returncode or not match:
        raise RuntimeError("Could not determine the default branch from origin/HEAD.")
    return match.group(1)


def get_remote_urls(repo: Path) -> tuple[str, str]:
    remote = git(repo, "config", "--get", "remote.origin.url")
    ssh = re.match(r"^(?P<user>[^@]+)@(?P<host>[^:]+):(?P<path>.+)$", remote)
    if ssh:
        path = ssh.group("path").removesuffix(".git")
        return f"https://{ssh.group('host')}/{path}", f"{ssh.group('user')}@{ssh.group('host')}:{path}.wiki.git"

    parsed = urlsplit(remote)
    if not parsed.scheme or not parsed.netloc or not parsed.path.strip("/"):
        raise RuntimeError(f"Unsupported origin URL: {remote}")
    path = parsed.path.rstrip("/").removesuffix(".git")
    web_url = urlunsplit(("https", parsed.netloc, path, "", ""))
    wiki_url = urlunsplit((parsed.scheme, parsed.netloc, f"{path}.wiki.git", "", ""))
    return web_url, wiki_url


def page_names(docs_root: Path) -> dict[str, str]:
    sources = sorted(
        path for path in docs_root.rglob("*")
        if path.is_file() and path.suffix.lower() == ".md"
    )
    names: dict[str, str] = {}
    seen: dict[str, str] = {}
    for source in sources:
        relative = PurePosixPath(source.relative_to(docs_root).as_posix())
        parts = list(relative.parts)
        parts[-1] = Path(parts[-1]).stem
        source_name = relative.as_posix()
        folded_source = source_name.casefold()
        if folded_source in {"readme.md", "wiki/home.md"}:
            name = "Home"
        elif folded_source == "wiki/_sidebar.md":
            name = "_Sidebar"
        elif folded_source == "wiki/_footer.md":
            name = "_Footer"
        elif folded_source.startswith("wiki/"):
            name = parts[-1]
        else:
            name = "-".join(parts)
        name = re.sub(r"[^A-Za-z0-9._-]+", "-", name).strip("-")
        key = name.casefold()
        if key in {"home", "_sidebar", "_footer"} and folded_source not in {
            "readme.md", "wiki/home.md", "wiki/_sidebar.md", "wiki/_footer.md"
        }:
            raise RuntimeError(f"Documentation page uses reserved wiki name {name!r}: {relative}")
        if key in seen:
            previous = seen[key]
            if key == "home" and {previous.casefold(), folded_source} == {"readme.md", "wiki/home.md"}:
                names[source_name] = name
                continue
            previous_is_wiki = previous.casefold().startswith("wiki/")
            current_is_wiki = folded_source.startswith("wiki/")
            if previous_is_wiki != current_is_wiki:
                renamed = "docs-" + name
                renamed_key = renamed.casefold()
                if renamed_key in seen:
                    raise RuntimeError(f"Documentation page names collide at {renamed!r}.")
                if current_is_wiki:
                    names[previous] = renamed
                    seen[renamed_key] = previous
                    seen.pop(key)
                    seen[key] = source_name
                else:
                    name = renamed
                    key = renamed_key
            else:
                raise RuntimeError(
                    f"Documentation pages collide as {name!r}: {previous} and {source_name}"
                )
        seen[key] = source_name
        names[source_name] = name
    if "README.md" not in names:
        raise RuntimeError(f"Missing documentation index: {docs_root / 'README.md'}")
    return names


def _is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _page_for_path(
    candidate: Path,
    docs_root: Path,
    names: dict[str, str],
) -> str | None:
    possibilities = [candidate]
    if candidate.is_dir():
        possibilities = [candidate / "README.md", candidate / "start-here.md"]
        possibilities.extend(sorted(candidate.glob("*.md")))
    elif candidate.suffix.lower() != ".md" and not candidate.exists():
        possibilities.append(candidate.with_suffix(".md") if candidate.suffix else Path(f"{candidate}.md"))

    for possible in possibilities:
        try:
            relative = possible.resolve().relative_to(docs_root.resolve()).as_posix()
        except ValueError:
            continue
        if relative in names:
            return names[relative]
        folded = relative.casefold()
        match = next((name for source, name in names.items() if source.casefold() == folded), None)
        if match:
            return match
    return None


def _split_target(target: str) -> tuple[str, str, str]:
    if target.startswith("<") and ">" in target:
        close = target.index(">")
        return target[1:close], target[:1], target[close + 1 :]
    return target, "", ""


def rewrite_target(
    target: str,
    source: Path,
    repo_root: Path,
    docs_root: Path,
    names: dict[str, str],
    repo_web_url: str,
    branch: str,
) -> str:
    raw_url, opening, closing = _split_target(target)
    parsed = urlsplit(raw_url)
    repo = urlsplit(repo_web_url)
    wiki_prefix = repo.path.rstrip("/") + "/wiki"
    if (
        parsed.scheme in {"http", "https"}
        and parsed.netloc.casefold() == repo.netloc.casefold()
        and parsed.path.casefold().startswith(wiki_prefix.casefold())
    ):
        wiki_name = unquote(parsed.path[len(wiki_prefix) :].strip("/"))
        wiki_source = docs_root / "wiki" / f"{wiki_name}.md" if wiki_name else docs_root / "wiki" / "Home.md"
        page = _page_for_path(wiki_source, docs_root, names)
        if not page:
            page = next((name for name in names.values() if name.casefold() == wiki_name.casefold()), None)
        if page:
            source_page = names[source.relative_to(docs_root).as_posix()]
            destination = "" if page == source_page else quote(page, safe="-._~")
            return urlunsplit(("", "", destination, parsed.query, parsed.fragment))
        return target
    if parsed.scheme or parsed.netloc or not parsed.path:
        return target

    source_page = names[source.relative_to(docs_root).as_posix()]
    path = Path(unquote(parsed.path.replace("/", "\\"))) if "\\" in parsed.path else Path(unquote(parsed.path))
    candidate = (repo_root / path.lstrip("\\/")) if parsed.path.startswith("/") else (source.parent / path)
    candidate = candidate.resolve()

    page = _page_for_path(candidate, docs_root, names)
    if page:
        destination = "" if page == source_page else quote(page, safe="-._~")
        return urlunsplit(("", "", destination, parsed.query, parsed.fragment)) or target

    if _is_within(candidate, docs_root) and candidate.is_file():
        relative_asset = candidate.relative_to(docs_root).as_posix()
        asset_url = "assets/" + "/".join(quote(part, safe="-._~") for part in relative_asset.split("/"))
        return opening + urlunsplit(("", "", asset_url, parsed.query, parsed.fragment)) + closing

    if _is_within(candidate, repo_root) and candidate.exists():
        relative = candidate.relative_to(repo_root).as_posix()
        kind = "tree" if candidate.is_dir() else "blob"
        blob = f"{repo_web_url}/{kind}/{quote(branch, safe='-._~/')}/{quote(relative, safe='-._~/')}"
        return urlunsplit(("", "", blob, parsed.query, parsed.fragment))

    return target


def _link_destination(text: str, start: int) -> tuple[int, int, str] | None:
    """Return destination bounds and value for an inline Markdown link."""
    cursor = start + 1
    depth = 1
    escaped = False
    while cursor < len(text):
        char = text[cursor]
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                break
        cursor += 1
    if cursor == len(text):
        return None

    inner_start = start + 1
    inner_end = cursor
    token_start = inner_start
    while token_start < inner_end and text[token_start].isspace():
        token_start += 1
    if token_start == inner_end:
        return None

    if text[token_start] == "<":
        token_end = text.find(">", token_start + 1, inner_end)
        if token_end < 0:
            return None
        return token_start, token_end + 1, text[token_start : token_end + 1]

    token_end = token_start
    nested = 0
    escaped = False
    while token_end < inner_end:
        char = text[token_end]
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == "(":
            nested += 1
        elif char == ")":
            if nested == 0:
                break
            nested -= 1
        elif char.isspace() and nested == 0:
            break
        token_end += 1
    return token_start, token_end, text[token_start:token_end]


def _rewrite_inline_links(line: str, rewrite) -> str:
    result: list[str] = []
    cursor = 0
    while cursor < len(line):
        if line[cursor] == "`":
            run_end = cursor
            while run_end < len(line) and line[run_end] == "`":
                run_end += 1
            marker = line[cursor:run_end]
            close = line.find(marker, run_end)
            if close >= 0:
                result.append(line[cursor : close + len(marker)])
                cursor = close + len(marker)
                continue

        bracket = cursor + 1 if line[cursor] == "!" else cursor
        if bracket >= len(line) or line[bracket] != "[":
            result.append(line[cursor])
            cursor += 1
            continue

        label_end = bracket + 1
        nesting = 1
        escaped = False
        while label_end < len(line) and nesting:
            char = line[label_end]
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == "[":
                nesting += 1
            elif char == "]":
                nesting -= 1
            label_end += 1
        if nesting or label_end >= len(line) or line[label_end] != "(":
            result.append(line[cursor])
            cursor += 1
            continue

        destination = _link_destination(line, label_end)
        if not destination:
            result.append(line[cursor])
            cursor += 1
            continue
        start, end, value = destination
        raw_url, opening, closing = _split_target(value)
        updated = rewrite(raw_url)
        result.append(line[cursor:start])
        result.append(opening + updated + closing)
        cursor = end
    return "".join(result)


def rewrite_markdown(
    text: str,
    source: Path,
    repo_root: Path,
    docs_root: Path,
    names: dict[str, str],
    repo_web_url: str,
    branch: str,
) -> str:
    def rewrite(target: str) -> str:
        return rewrite_target(target, source, repo_root, docs_root, names, repo_web_url, branch)

    output: list[str] = []
    fence_char = ""
    fence_size = 0
    for line in text.splitlines(keepends=True):
        fence = re.match(r"^\s*(`{3,}|~{3,})", line)
        if fence:
            marker = fence.group(1)
            if not fence_char:
                fence_char, fence_size = marker[0], len(marker)
            elif marker[0] == fence_char and len(marker) >= fence_size:
                fence_char, fence_size = "", 0
            output.append(line)
            continue
        if fence_char:
            output.append(line)
            continue

        reference = re.match(r"^(\s*\[[^]]+\]:\s*)(<[^>]+>|\S+)", line)
        if reference:
            value = reference.group(2)
            raw_url, opening, closing = _split_target(value)
            output.append(line[: reference.start(2)] + opening + rewrite(raw_url) + closing + line[reference.end(2) :])
        else:
            output.append(_rewrite_inline_links(line, rewrite))
    return "".join(output)


def _title(text: str, fallback: str) -> str:
    for line in text.splitlines():
        match = re.match(r"^\s*#\s+(.+?)\s*#*\s*$", line)
        if match:
            return match.group(1)
    return fallback.replace("-", " ")


def build_site(
    docs_root: Path,
    repo_root: Path,
    branch: str,
    repo_web_url: str,
    output_root: Path,
) -> set[str]:
    names = page_names(docs_root)
    page_titles: dict[str, str] = {}
    for relative, name in names.items():
        if name in {"Home", "_Sidebar"}:
            continue
        source = docs_root / Path(relative)
        text = source.read_text(encoding="utf-8")
        page_titles[name] = _title(text, Path(relative).stem)
        rendered = rewrite_markdown(text, source, repo_root, docs_root, names, repo_web_url, branch)
        (output_root / f"{name}.md").parent.mkdir(parents=True, exist_ok=True)
        (output_root / f"{name}.md").write_text(rendered, encoding="utf-8", newline="\n")

    home_source = docs_root / "wiki" / "Home.md"
    if not home_source.is_file():
        home_source = docs_root / "README.md"
    home_text = rewrite_markdown(
        home_source.read_text(encoding="utf-8"),
        home_source,
        repo_root,
        docs_root,
        names,
        repo_web_url,
        branch,
    )
    page_titles["Home"] = _title(home_text, "Home")
    (output_root / "Home.md").write_text(home_text, encoding="utf-8", newline="\n")
    home = (output_root / "Home.md").read_text(encoding="utf-8").rstrip()
    if home_source.name != "README.md":
        readme = docs_root / "README.md"
        readme_text = rewrite_markdown(
            readme.read_text(encoding="utf-8"),
            readme,
            repo_root,
            docs_root,
            names,
            repo_web_url,
            branch,
        )
        readme_body = re.sub(r"^\s*#\s+.*(?:\r?\n|$)", "", readme_text, count=1)
        home += "\n\n## Knowledge map\n\n" + readme_body.strip()
    links = [
        f"- [{page_titles[name]}]({quote(name, safe='-._~')})"
        for name in sorted(page_titles, key=str.casefold)
        if name not in {"Home", "_Footer"}
    ]
    home += "\n\n## Mirrored pages\n\n" + "\n".join(links) + "\n"
    (output_root / "Home.md").write_text(home, encoding="utf-8", newline="\n")

    sidebar = ["# Martlet documentation", "", "- [Home](Home)"]
    sidebar.extend(
        f"- [{page_titles[name]}]({quote(name, safe='-._~')})"
        for name in sorted(page_titles, key=str.casefold)
        if name not in {"Home", "_Footer"}
    )
    (output_root / "_Sidebar.md").write_text("\n".join(sidebar) + "\n", encoding="utf-8", newline="\n")

    for source in docs_root.rglob("*"):
        if not source.is_file() or source.suffix.lower() == ".md":
            continue
        relative = source.relative_to(docs_root).as_posix()
        destination = output_root / "assets" / Path(relative)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)

    return {
        path.relative_to(output_root).as_posix()
        for path in output_root.rglob("*")
        if path.is_file()
    }


def sync_plan(wiki_root: Path, output_root: Path) -> tuple[list[str], list[str], list[str]]:
    expected = {
        path.relative_to(output_root).as_posix(): path
        for path in output_root.rglob("*")
        if path.is_file()
    }
    markdown_existing = {
        path.name
        for path in wiki_root.iterdir()
        if path.is_file() and path.suffix.lower() == ".md"
    }
    expected_markdown = {name for name in expected if name.lower().endswith(".md")}
    stale = sorted(markdown_existing - expected_markdown)

    existing_assets = {
        path.relative_to(wiki_root).as_posix()
        for path in (wiki_root / "assets").rglob("*")
        if path.is_file()
    } if (wiki_root / "assets").exists() else set()
    expected_assets = {name for name in expected if name.startswith("assets/")}
    stale_assets = sorted(existing_assets - expected_assets)

    changed = sorted(
        name for name, source in expected.items()
        if not (wiki_root / Path(name)).is_file()
        or (wiki_root / Path(name)).read_bytes() != source.read_bytes()
    )
    return changed, stale, stale_assets


def apply_site(wiki_root: Path, output_root: Path) -> None:
    changed, stale_pages, stale_assets = sync_plan(wiki_root, output_root)
    del changed
    for relative in stale_pages + stale_assets:
        (wiki_root / Path(relative)).unlink()
    for source in output_root.rglob("*"):
        if not source.is_file():
            continue
        relative = source.relative_to(output_root)
        destination = wiki_root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def publish(repo_root: Path, dry_run: bool, wiki_override: str | None) -> None:
    repo_root = repo_root.resolve()
    docs_root = repo_root / "docs"
    if not docs_root.is_dir():
        raise RuntimeError(f"Documentation folder not found: {docs_root}")

    branch = get_default_branch(repo_root)
    git(
        repo_root,
        "fetch",
        "--no-tags",
        "origin",
        f"+refs/heads/{branch}:refs/remotes/origin/{branch}",
    )
    source_commit = git(repo_root, "rev-parse", f"refs/remotes/origin/{branch}^{{commit}}")
    repo_web_url, derived_wiki_url = get_remote_urls(repo_root)
    wiki_url = wiki_override or derived_wiki_url

    temp_root = Path(tempfile.mkdtemp(prefix="martlet-wiki-mirror-"))
    source_worktree = temp_root / "source"
    wiki_root = temp_root / "wiki"
    source_added = False
    try:
        git(repo_root, "worktree", "add", "--detach", str(source_worktree), source_commit)
        source_added = True
        subprocess.run(["git", "clone", "--quiet", wiki_url, str(wiki_root)], check=True)
        output_root = temp_root / "generated"
        output_root.mkdir()
        files = build_site(source_worktree / "docs", source_worktree, branch, repo_web_url, output_root)

        changed, stale_pages, stale_assets = sync_plan(wiki_root, output_root)
        print(f"Source: origin/{branch} at {source_commit[:12]} (detached worktree)")
        print(f"Wiki: {wiki_url}")
        print(f"Generated: {len(files)} files ({sum(name.endswith('.md') for name in files)} pages)")
        print(f"Pages to update: {len([name for name in changed if name.endswith('.md')])}")
        print(f"Pages to delete: {len(stale_pages)}")
        print(f"Assets to update: {len([name for name in changed if name.startswith('assets/')])}")
        print(f"Assets to delete: {len(stale_assets)}")
        for name in changed + stale_pages + stale_assets:
            action = "delete" if name in stale_pages or name in stale_assets else "write"
            print(f"  {action}: {name}")

        if dry_run:
            print("Dry run complete. The wiki repository was not changed or pushed.")
            return

        apply_site(wiki_root, output_root)
        git(wiki_root, "add", "--all")
        if not git(wiki_root, "status", "--porcelain"):
            print("Wiki is already up to date.")
            return
        git(wiki_root, "commit", "-m", f"Mirror docs from {branch} at {source_commit[:12]}")
        wiki_branch = git(wiki_root, "branch", "--show-current")
        if not wiki_branch:
            raise RuntimeError("The wiki clone has no checked-out branch; refusing to push.")
        git(wiki_root, "push", "origin", f"HEAD:refs/heads/{wiki_branch}")
        print(f"Published wiki commit {git(wiki_root, 'rev-parse', '--short', 'HEAD')}.")
    except subprocess.CalledProcessError as error:
        raise RuntimeError(f"Command failed with exit code {error.returncode}: {error.cmd}") from error
    finally:
        if source_added:
            git(repo_root, "worktree", "remove", str(source_worktree))
        shutil.rmtree(
            temp_root,
            onexc=lambda function, path, error: (
                Path(path).chmod(Path(path).stat().st_mode | stat.S_IWRITE),
                function(path),
            ),
        )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2], help="Repository worktree")
    parser.add_argument("--wiki-url", help="Override the GitHub Wiki Git URL")
    parser.add_argument("--dry-run", action="store_true", help="Show changes without committing or pushing")
    args = parser.parse_args()
    try:
        publish(args.repo, args.dry_run, args.wiki_url)
    except (OSError, RuntimeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
