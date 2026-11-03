#!/usr/bin/env python3
"""Focused source/archive documentation integrity fixtures (no network or build)."""
from __future__ import annotations

import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest


def load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


checker = load("documentation_checker_test", "check-documentation.py")
packager = load("documentation_packager_test", "prepare-bundle-documentation.py")
SHA = "a" * 40


class DocumentationIntegrityTests(unittest.TestCase):
    def test_heading_anchors_follow_rendered_text_and_ignore_code(self):
        anchors = checker.markdown_anchors('# Hello World\n## Hello World\n## `config validate`\nTitle\n=====\n<a id="custom"></a>\n```md\n# Hidden\n```\n')
        self.assertTrue({"hello-world", "hello-world-1", "config-validate", "title", "custom"} <= anchors)
        self.assertNotIn("hidden", anchors)

    def test_root_case_and_missing_anchor_are_real_failures(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "docs").mkdir()
            (root / "docs" / "README.md").write_text('# Index\n[wrong case](../readme.md)\n[bad anchor](../README.md#missing)\n')
            (root / "README.md").write_text('# Present\n')
            (root / "docs" / "ad-hoc.md").write_text('# Misplaced\n')
            errors = checker.check_repository(root, packaged=True)
            self.assertTrue(any("categorized subdirectory" in error for error in errors))
            self.assertTrue(any("case-sensitive" in error for error in errors))
            self.assertTrue(any("missing Markdown anchor" in error for error in errors))

    def test_html_images_validate_local_files_and_case_but_ignore_code_and_remote_sources(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, _ = self.fixture(temporary)
            image = root / "docs" / "assets" / "mark&icon.png"
            image.write_bytes(b'fixture')
            (root / "README.md").write_text('''# Images
<img src="docs/assets/mark&amp;icon.png" alt="a > b" />
<IMG\n SRC='docs/assets/mark.png?size=1&amp;theme=light'>
<img data-src="missing.png" src=docs/assets/mark.png>
<img alt="Example src='not-an-attribute.png'" src="docs/assets/mark.png" />
<img
    src="docs/assets/mark.png"
    alt="Indented attributes are HTML, not code">
<img src="https://example.test/remote.png">
<img src="data:image/png;base64,AA==">
<!-- <img src="comment.png"> -->
`<img src="inline.png">`
```html
<img src="example.png">
```
''')
            self.assertEqual([], checker.check_repository(root, packaged=True))
            (root / "README.md").write_text('<img src="docs/assets/Mark.png">\n<img\n    src="missing.png">\n<img src="docs">\n')
            errors = checker.check_repository(root, packaged=True)
            self.assertEqual(3, len(errors), errors)
            self.assertEqual(2, sum("case-sensitive" in error for error in errors))
            self.assertTrue(any("HTML image target must be a file" in error for error in errors))

    def fixture(self, temporary: str):
        root = Path(temporary) / "repository"
        bundle = Path(temporary) / "bundle"
        (root / "docs" / "assets").mkdir(parents=True)
        (root / "release").mkdir()
        (root / "docs" / "guides").mkdir()
        bundle.mkdir()
        (root / "source.cs").write_text('// Source only\n')
        (root / "NOTICE").write_text('Legal notice\n')
        (root / "docs" / "assets" / "mark.png").write_bytes(b'fixture')
        (root / "docs" / "README.md").write_text('# Index\n[Install](../release/INSTALL.md)\n[Guide](guides/guide.md#guide)\n')
        (root / "docs" / "guides" / "guide.md").write_text('# Guide\n![Asset](../assets/mark.png)\n[Source](../../source.cs)\n[Notice](../../NOTICE)\n[Self](guide.md#guide)\n[Reference][install]\n\n[install]: ../../release/INSTALL.md\n\n```md\n[example](missing.md)\n```\n')
        (root / "release" / "INSTALL.md").write_text('# Install\n[Guide](docs/guides/guide.md#guide)\n')
        return root, bundle

    def test_compose_bundle_preserves_installed_links_and_pins_source_fallback(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, bundle = self.fixture(temporary)
            shutil.copytree(root / "docs", bundle / "docs")
            shutil.copy(root / "release" / "INSTALL.md", bundle / "INSTALL.md")
            shutil.copy(root / "NOTICE", bundle / "NOTICE")
            packager.prepare_bundle(root, bundle, SHA)
            index = (bundle / "docs" / "README.md").read_text()
            guide = (bundle / "docs" / "guides" / "guide.md").read_text()
            self.assertIn('[Install](../INSTALL.md)', index)
            self.assertIn('![Asset](../assets/mark.png)', guide)
            self.assertIn('[Notice](../../NOTICE)', guide)
            self.assertIn('[install]: ../../INSTALL.md', guide)
            self.assertIn(f'/blob/{SHA}/source.cs', guide)
            self.assertIn('[example](missing.md)', guide)
            self.assertEqual([], checker.check_repository(bundle, packaged=True))

    def test_curated_compose_keeps_offline_guides_media_and_notices_without_history(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, bundle = self.fixture(temporary)
            for category in ("deployment", "client", "mcp-http", "integrations", "operations", "contracts", "adr"):
                directory = root / "docs" / category
                directory.mkdir()
                (directory / "README.md").write_text('# Operations\n[History](../evidence/history.md#history)\n')
            (root / "docs" / "evidence").mkdir()
            (root / "docs" / "evidence" / "history.md").write_text('# History\n')
            (root / "docs" / "evidence" / "screenshot.png").write_bytes(b'historical screenshot')
            hero = root / "docs" / "assets" / "netratel" / "netratel-readme-hero.webp"
            hero.parent.mkdir()
            hero.write_bytes(b'required README hero')
            (root / "README.md").write_text('<img src="docs/assets/netratel/netratel-readme-hero.webp">\n[Deploy](docs/deployment/README.md)\n')
            (root / "docs" / "THIRD-PARTY-NOTICES.txt").write_bytes(b'upstream attribution\n')
            for name in ("README.md", "NOTICE"):
                shutil.copy2(root / name, bundle / name)
            shutil.copy2(root / "release" / "INSTALL.md", bundle / "INSTALL.md")
            runtime = bundle / "compose.images.yaml"
            runtime.write_bytes(b'unchanged deployment bytes\n')
            packager.copy_compose_documentation(root, bundle)
            packager.prepare_bundle(root, bundle, SHA)
            self.assertEqual([], checker.check_repository(bundle, packaged=True))
            for category in ("deployment", "client", "mcp-http", "integrations", "operations", "contracts", "adr"):
                guide = (bundle / "docs" / category / "README.md").read_text()
                self.assertIn(f'/blob/{SHA}/docs/evidence/history.md#history', guide)
            self.assertIn('[Deploy](docs/deployment/README.md)', (bundle / "README.md").read_text())
            self.assertEqual(hero.read_bytes(), (bundle / hero.relative_to(root)).read_bytes())
            self.assertEqual(b'upstream attribution\n', (bundle / "docs" / "THIRD-PARTY-NOTICES.txt").read_bytes())
            self.assertEqual(b'Legal notice\n', (bundle / "NOTICE").read_bytes())
            self.assertEqual(b'unchanged deployment bytes\n', runtime.read_bytes())
            self.assertFalse((bundle / "docs" / "evidence").exists())
            self.assertFalse((bundle / "docs" / "guides").exists())
            self.assertTrue((root / "docs" / "evidence" / "screenshot.png").is_file())
            with self.assertRaisesRegex(ValueError, "clean bundle docs/"):
                packager.copy_compose_documentation(root, bundle)

    def test_standalone_tool_guide_adapts_self_and_retains_bundled_notice(self):
        with tempfile.TemporaryDirectory() as temporary:
            root, bundle = self.fixture(temporary)
            shutil.copy(root / "docs" / "guides" / "guide.md", bundle / "CLI_AND_MCP.md")
            shutil.copy(root / "NOTICE", bundle / "NOTICE")
            packager.prepare_bundle(root, bundle, SHA, {"CLI_AND_MCP.md": "docs/guides/guide.md"})
            guide = (bundle / "CLI_AND_MCP.md").read_text()
            self.assertIn('[Self](CLI_AND_MCP.md#guide)', guide)
            self.assertIn('[Notice](NOTICE)', guide)
            self.assertIn(f'/blob/{SHA}/docs/assets/mark.png', guide)
            self.assertEqual([], checker.check_repository(bundle, packaged=True, require_docs=False))
            with self.assertRaisesRegex(ValueError, "complete evaluated release commit SHA"):
                packager.prepare_bundle(root, bundle, "main", {"CLI_AND_MCP.md": "docs/guides/guide.md"})


if __name__ == '__main__':
    unittest.main()
