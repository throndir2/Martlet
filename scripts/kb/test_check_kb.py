"""Small disposable fixtures for the local KB checker."""

from pathlib import Path
import subprocess
import tempfile
import unittest

from check_kb import check, changed_paths, diff_warnings, normalize_path


class KbChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.write("docs/README.md", "# Map\n\n[Feature](feature.md)\n")
        self.write("docs/feature.md", "# Feature\n\n**Code:** `src/feature.py`\n")
        self.write("src/feature.py", "enabled = True\n")

    def write(self, path, text):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def git(self, *args):
        return subprocess.run(
            ["git", *args], cwd=self.root, check=True, capture_output=True, text=True,
        ).stdout

    def test_missing_code_path_is_an_error(self):
        (self.root / "src/feature.py").unlink()
        errors, _ = check(self.root)
        self.assertTrue(any("Code path does not exist: src/feature.py" in e for e in errors))

    def test_fenced_examples_are_not_source_claims(self):
        self.write("docs/feature.md", "# Feature\n**Code:** `src/feature.py`\n"
                   "```markdown\n**Code:** `missing/example.cs`\n```\n"
                   "~~~~markdown\n**Code:** `missing/other.cs`\n~~~~\n")
        self.assertEqual(([], []), check(self.root))

    def test_index_requires_page_and_subfolder(self):
        self.write("docs/extra/README.md", "# Extra\n")
        self.write("docs/other.md", "# Other\n")
        errors, _ = check(self.root)
        self.assertEqual(2, len(errors))
        self.assertTrue(any("does not list extra" in e for e in errors))
        self.assertTrue(any("does not list other.md" in e for e in errors))

    def test_missing_readme_is_an_error(self):
        (self.root / "docs/README.md").unlink()
        errors, _ = check(self.root)
        self.assertTrue(any("missing README.md" in e for e in errors))

    def test_readme_link_covers_folder_and_generated_folders_are_skipped(self):
        self.write("docs/extra/README.md", "# Extra\n")
        self.write("docs/images/generated.md", "# Generated\n")
        self.write("docs/README.md", "# Map\n[Feature](feature.md)\n[Extra](extra/README.md)\n")
        self.assertEqual(([], []), check(self.root))

    def test_wiki_extensionless_and_encoded_links(self):
        self.write("docs/README.md", "# Map\n[Feature](feature)\n[Space](space%20page.md)\n")
        self.write("docs/space page.md", "# Page\n")
        self.assertEqual(([], []), check(self.root))

    def test_unsafe_and_wildcard_code_paths_are_errors(self):
        for path in ("../outside", "C:\\outside", "/outside", "src/*.py"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                normalize_path(path)

    def test_folder_coverage_has_path_boundary_and_deletion_warning(self):
        warnings = diff_warnings(
            {"src/feature": ["docs/feature.md"], "src/old.py": ["docs/old.md"]},
            [("M", "src/feature/a.py"), ("M", "src/feature-other/a.py"),
             ("D", "src/old.py"), ("M", "docs/feature.md"), ("M", "src/bin/a.py")],
        )
        self.assertEqual(2, len(warnings))
        self.assertIn("src/feature-other/a.py", warnings[0])
        self.assertIn("deleted or renamed Code path src/old.py", warnings[1])

    def test_git_rename_reports_old_and_new_paths(self):
        self.git("init", "--quiet")
        self.git("add", ".")
        self.git("-c", "user.name=KB Test", "-c", "user.email=kb@example.invalid",
                 "commit", "--quiet", "-m", "base")
        base = self.git("rev-parse", "HEAD").strip()
        self.git("mv", "src/feature.py", "src/renamed.py")
        self.git("-c", "user.name=KB Test", "-c", "user.email=kb@example.invalid",
                 "commit", "--quiet", "-m", "rename")
        changes = changed_paths(self.root, base)
        self.assertEqual([("D", "src/feature.py"), ("A", "src/renamed.py")], changes)
        _, warnings = check(self.root, base)
        self.assertEqual(2, len(warnings))


if __name__ == "__main__":
    unittest.main()
