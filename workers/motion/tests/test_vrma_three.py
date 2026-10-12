"""Plays converter output with the real @pixiv/three-vrm-animation 3.5.5 loader (vrma-check/check.mjs).

The test runs when Node is on PATH and `npm ci` has run in workers/motion/vrma-check; otherwise it skips.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HARNESS = Path(__file__).resolve().parents[1] / "vrma-check"
NODE = shutil.which("node")
INSTALLED = (HARNESS / "node_modules" / "@pixiv" / "three-vrm-animation").is_dir()


@unittest.skipUnless(NODE and INSTALLED, "needs Node and `npm ci` in workers/motion/vrma-check")
class ThreeVrmAnimationTests(unittest.TestCase):
    def test_vrm_bones_move_as_the_smplh_joints_on_vrm_1_and_vrm_0(self):
        with tempfile.TemporaryDirectory() as cases:
            made = subprocess.run(
                [sys.executable, str(HARNESS / "make_cases.py"), cases], capture_output=True, text=True, timeout=120
            )
            self.assertEqual(made.returncode, 0, made.stdout + made.stderr)
            checked = subprocess.run(
                [NODE, "check.mjs", cases], cwd=HARNESS, capture_output=True, text=True, timeout=300
            )
            self.assertEqual(checked.returncode, 0, checked.stdout + checked.stderr)
            self.assertIn("VRM 1.0", checked.stdout)
            self.assertIn("VRM 0.x", checked.stdout)
            self.assertRegex(checked.stdout, r"\n\d+ checks passed, 0 failed\.")


if __name__ == "__main__":
    unittest.main()
