"""Write synthetic SMPL-H motions as .vrma files for the three-vrm check (check.mjs).

Usage: python make_cases.py OUTPUT_DIR

Each case moves one joint (or the pelvis) from the rest pose at frame 0 to a target
at frame 15 and holds it to frame 59 (30 fps). ``cases.json`` lists what the VRM must
do: at a time, the direction from one bone to another in the model's own frame
("forward", "left", "up", ...), or how far the hips moved in source meters.
"""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from martlet_motion.vrma import REST_HIPS_HEIGHT, smplh_to_vrma  # noqa: E402

FPS = 30
FRAMES = 60
RAMP = 15
STAND = 1.16  # HY-Motion transl y for a standing body (pelvis 0.97 m above the floor)
HALF_PI = math.pi / 2

# SMPL-H joint indexes
PELVIS, L_KNEE, SPINE1, L_SHOULDER, R_SHOULDER, L_ELBOW, L_INDEX1, R_INDEX1 = 0, 4, 3, 16, 17, 18, 22, 37


def ramp():
    return np.minimum(np.arange(FRAMES) / RAMP, 1.0)[:, None]


def motion(joints=52, rotations=None, moves=None):
    poses = np.zeros((FRAMES, joints, 3))
    for joint, axis_angle in (rotations or {}).items():
        poses[:, joint] = ramp() * np.asarray(axis_angle, dtype=float)
    trans = np.tile([0.0, STAND, 0.0], (FRAMES, 1))
    if moves is not None:
        trans += ramp() * np.asarray(moves, dtype=float)
    return poses, trans


def bone(time, start, end, direction):
    return {"time": time, "from": start, "to": end, "direction": direction}


def hips(time, forward=0.0, up=0.0, left=0.0):
    return {"time": time, "hips": {"forward": forward, "up": up, "left": left}}


CASES = [
    (
        "right_arm_forward",
        motion(rotations={R_SHOULDER: (0, HALF_PI, 0)}),
        [
            bone(0.0, "rightUpperArm", "rightHand", "right"),
            bone(1.0, "rightUpperArm", "rightHand", "forward"),
            bone(1.0, "leftUpperArm", "leftHand", "left"),
            hips(1.0),
        ],
    ),
    (
        "left_arm_forward",
        motion(rotations={L_SHOULDER: (0, -HALF_PI, 0)}),
        [bone(1.0, "leftUpperArm", "leftHand", "forward"), bone(1.0, "rightUpperArm", "rightHand", "right")],
    ),
    ("right_arm_up", motion(rotations={R_SHOULDER: (0, 0, -HALF_PI)}), [bone(1.0, "rightUpperArm", "rightHand", "up")]),
    (
        "left_elbow_bend",
        motion(rotations={L_ELBOW: (0, -HALF_PI, 0)}),
        [bone(1.0, "leftUpperArm", "leftLowerArm", "left"), bone(1.0, "leftLowerArm", "leftHand", "forward")],
    ),
    (
        "left_knee_bend",
        motion(rotations={L_KNEE: (HALF_PI, 0, 0)}),
        [bone(1.0, "leftUpperLeg", "leftLowerLeg", "down"), bone(1.0, "leftLowerLeg", "leftFoot", "back")],
    ),
    ("lean_forward", motion(rotations={SPINE1: (HALF_PI, 0, 0)}), [bone(1.0, "spine", "head", "forward")]),
    (
        "turn_left",
        motion(rotations={PELVIS: (0, HALF_PI, 0)}),
        [bone(1.0, "leftUpperArm", "rightUpperArm", "forward"), bone(1.0, "leftUpperLeg", "rightUpperLeg", "forward")],
    ),
    ("walk_forward", motion(moves=(0, 0, 1.0)), [hips(0.0), hips(1.0, forward=1.0)]),
    ("step_left", motion(moves=(0.5, 0, 0)), [hips(1.0, left=0.5)]),
    ("crouch", motion(moves=(0, -0.3, 0)), [hips(1.0, up=-0.3)]),
    (
        "index_curl",
        motion(rotations={L_INDEX1: (0, 0, -HALF_PI), R_INDEX1: (0, 0, HALF_PI)}),
        [
            bone(1.0, "leftIndexProximal", "leftIndexDistal", "down"),
            bone(1.0, "rightIndexProximal", "rightIndexDistal", "down"),
        ],
    ),
    (
        "body_only_right_arm_forward",
        motion(joints=22, rotations={R_SHOULDER: (0, HALF_PI, 0)}),
        [bone(1.0, "rightUpperArm", "rightHand", "forward"), bone(1.0, "leftIndexProximal", "leftIndexDistal", "left")],
    ),
]


def main(argv):
    if len(argv) != 2:
        print(__doc__.strip().splitlines()[2], file=sys.stderr)
        return 2
    out = Path(argv[1])
    out.mkdir(parents=True, exist_ok=True)
    listed = []
    for name, (poses, trans), checks in CASES:
        (out / f"{name}.vrma").write_bytes(smplh_to_vrma(poses, trans, FPS))
        listed.append({"name": name, "file": f"{name}.vrma", "checks": checks})
    (out / "cases.json").write_text(json.dumps({"restHipsHeight": REST_HIPS_HEIGHT, "cases": listed}, indent=2))
    print(f"Wrote {len(listed)} cases to {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
