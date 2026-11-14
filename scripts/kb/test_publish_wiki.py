import tempfile
import unittest
from pathlib import Path

from publish_wiki import apply_site, build_site, page_names, sync_plan, validate_readme_wiki_links


class PublishWikiTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"
        self.docs = self.repo / "docs"
        self.output = self.root / "generated"
        self.repo.mkdir()
        self.docs.mkdir()
        (self.repo / "README.md").write_text(
            "[Guide](https://github.com/example/project/wiki/Welcome#intro)\n",
            encoding="utf-8",
        )
        (self.docs / "README.md").write_text("# Docs\n\n[Guide](guides/start.md#intro)\n", encoding="utf-8")
        (self.docs / "guides").mkdir()
        (self.docs / "guides" / "start.md").write_text(
            "# Start\n\n[Home](../README.md#docs) [Other](other)\n"
            "![Image](../images/picture.png)\n"
            "[Code](../../src/example.cs#L3)\n"
            "[Wiki](https://github.com/example/project/wiki/Welcome#intro)\n"
            "`[inline](other.md)`\n"
            "```md\n[example](other.md)\n```\n",
            encoding="utf-8",
        )
        (self.docs / "guides" / "other.md").write_text("# Other\n", encoding="utf-8")
        (self.docs / "wiki").mkdir()
        (self.docs / "wiki" / "Welcome.md").write_text("# Welcome\n\n## Intro\n", encoding="utf-8")
        (self.docs / "images").mkdir()
        (self.docs / "images" / "picture.png").write_bytes(b"image-data")
        (self.repo / "src").mkdir()
        (self.repo / "src" / "example.cs").write_text("code", encoding="utf-8")
        self.output.mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def test_build_flattens_pages_rewrites_links_and_copies_assets(self):
        files = build_site(
            self.docs,
            self.repo,
            "main",
            "https://github.com/example/project",
            self.output,
        )

        self.assertIn("Home.md", files)
        self.assertIn("guides-start.md", files)
        self.assertIn("_Sidebar.md", files)
        self.assertEqual(b"image-data", (self.output / "assets" / "images" / "picture.png").read_bytes())

        page = (self.output / "guides-start.md").read_text(encoding="utf-8")
        self.assertIn("[Home](Home#docs)", page)
        self.assertIn("[Other](guides-other)", page)
        self.assertIn("![Image](assets/images/picture.png)", page)
        self.assertIn("https://github.com/example/project/blob/main/src/example.cs#L3", page)
        self.assertIn("[Wiki](Welcome#intro)", page)
        self.assertIn("`[inline](other.md)`", page)
        self.assertIn("```md\n[example](other.md)\n```", page)
        self.assertIn("[Start](guides-start)", (self.output / "_Sidebar.md").read_text(encoding="utf-8"))

    def test_page_name_collisions_fail(self):
        (self.docs / "guides-start.md").write_text("# Collision\n", encoding="utf-8")
        with self.assertRaisesRegex(RuntimeError, "collide"):
            page_names(self.docs)

    def test_wiki_slugs_are_kept_and_root_collisions_get_flat_names(self):
        wiki_docs = self.docs / "wiki"
        wiki_docs.mkdir(exist_ok=True)
        (wiki_docs / "Home.md").write_text("# Welcome\n", encoding="utf-8")
        (wiki_docs / "Architecture.md").write_text("# Old architecture page\n", encoding="utf-8")
        (wiki_docs / "_Sidebar.md").write_text("# Old sidebar\n", encoding="utf-8")
        (self.docs / "ARCHITECTURE.md").write_text("# Architecture reference\n", encoding="utf-8")

        names = page_names(self.docs)

        self.assertEqual("Home", names["README.md"])
        self.assertEqual("Home", names["wiki/Home.md"])
        self.assertEqual("Architecture", names["wiki/Architecture.md"])
        self.assertEqual("docs-Architecture", names["ARCHITECTURE.md"])
        self.assertEqual("_Sidebar", names["wiki/_Sidebar.md"])

    def test_root_readme_links_keep_their_wiki_page_names(self):
        names = page_names(self.docs)

        self.assertEqual(
            1,
            validate_readme_wiki_links(
                self.repo,
                self.docs,
                "https://github.com/example/project",
                names,
            ),
        )

        (self.repo / "README.md").write_text(
            "[Missing](https://github.com/example/project/wiki/Removed-Page)\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(RuntimeError, "removed-page"):
            validate_readme_wiki_links(
                self.repo,
                self.docs,
                "https://github.com/example/project",
                names,
            )
        (self.repo / "README.md").write_text(
            "[Missing heading](https://github.com/example/project/wiki/Welcome#removed-heading)\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(RuntimeError, "welcome#removed-heading"):
            validate_readme_wiki_links(
                self.repo,
                self.docs,
                "https://github.com/example/project",
                names,
            )

    def test_home_keeps_wiki_welcome_and_adds_the_documentation_map(self):
        (self.docs / "wiki" / "Home.md").write_text("# Welcome\n\nStart here.\n", encoding="utf-8")

        build_site(self.docs, self.repo, "main", "https://github.com/example/project", self.output)

        home = (self.output / "Home.md").read_text(encoding="utf-8")
        self.assertIn("# Welcome\n\nStart here.", home)
        self.assertIn("## Knowledge map", home)
        self.assertIn("[Start](guides-start)", home)

    def test_sync_plan_lists_removed_markdown_and_assets(self):
        wiki = self.root / "wiki"
        (wiki / "assets" / "images").mkdir(parents=True)
        (wiki / "removed.md").write_text("old page", encoding="utf-8")
        (wiki / "assets" / "images" / "old.png").write_bytes(b"old")
        build_site(self.docs, self.repo, "main", "https://github.com/example/project", self.output)

        changed, stale_pages, stale_assets = sync_plan(wiki, self.output)

        self.assertIn("Home.md", changed)
        self.assertEqual(["removed.md"], stale_pages)
        self.assertEqual(["assets/images/old.png"], stale_assets)

        apply_site(wiki, self.output)
        self.assertFalse((wiki / "removed.md").exists())
        self.assertFalse((wiki / "assets" / "images" / "old.png").exists())

    def test_sync_plan_ignores_markdown_line_ending_differences(self):
        wiki = self.root / "wiki"
        wiki.mkdir()
        build_site(self.docs, self.repo, "main", "https://github.com/example/project", self.output)
        apply_site(wiki, self.output)
        for page in wiki.glob("*.md"):
            page.write_bytes(page.read_bytes().replace(b"\n", b"\r\n"))

        changed, stale_pages, stale_assets = sync_plan(wiki, self.output)

        self.assertEqual([], changed)
        self.assertEqual([], stale_pages)
        self.assertEqual([], stale_assets)


if __name__ == "__main__":
    unittest.main()
