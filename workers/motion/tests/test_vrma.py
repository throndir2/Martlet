"""Tests for the SMPL-H to VRMA converter: file structure, motion directions and input errors."""

from __future__ import annotations

import json
import math
import struct
import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from martlet_motion.vrma import PELVIS_OFFSET, REST_HIPS_HEIGHT, SMPLH_JOINTS, smplh_to_vrma  # noqa: E402

# HY-Motion hymotion/utils/smplh2woodfbx.py SMPLH_JOINT2NUM order, and the SMPL-H kinematic tree.
UPSTREAM_NAMES = (
    "Pelvis L_Hip R_Hip Spine1 L_Knee R_Knee Spine2 L_Ankle R_Ankle Spine3 L_Foot R_Foot Neck L_Collar "
    "R_Collar Head L_Shoulder R_Shoulder L_Elbow R_Elbow L_Wrist R_Wrist "
    "L_Index1 L_Index2 L_Index3 L_Middle1 L_Middle2 L_Middle3 L_Pinky1 L_Pinky2 L_Pinky3 "
    "L_Ring1 L_Ring2 L_Ring3 L_Thumb1 L_Thumb2 L_Thumb3 "
    "R_Index1 R_Index2 R_Index3 R_Middle1 R_Middle2 R_Middle3 R_Pinky1 R_Pinky2 R_Pinky3 "
    "R_Ring1 R_Ring2 R_Ring3 R_Thumb1 R_Thumb2 R_Thumb3"
).split()
UPSTREAM_PARENTS = [
    -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19,
    20, 22, 23, 20, 25, 26, 20, 28, 29, 20, 31, 32, 20, 34, 35,
    21, 37, 38, 21, 40, 41, 21, 43, 44, 21, 46, 47, 21, 49, 50,
]
# VRM 1.0 humanoid parents (three-vrm VRMHumanBoneParentMap) for the bones the converter writes.
VRM_PARENTS = {
    "spine": "hips", "chest": "spine", "upperChest": "chest", "neck": "upperChest", "head": "neck",
    "leftUpperLeg": "hips", "leftLowerLeg": "leftUpperLeg", "leftFoot": "leftLowerLeg", "leftToes": "leftFoot",
    "leftShoulder": "upperChest", "leftUpperArm": "leftShoulder", "leftLowerArm": "leftUpperArm",
    "leftHand": "leftLowerArm", "leftThumbMetacarpal": "leftHand", "leftThumbProximal": "leftThumbMetacarpal",
    "leftThumbDistal": "leftThumbProximal",
}
for _finger in ("Index", "Middle", "Ring", "Little"):
    VRM_PARENTS[f"left{_finger}Proximal"] = "leftHand"
    VRM_PARENTS[f"left{_finger}Intermediate"] = f"left{_finger}Proximal"
    VRM_PARENTS[f"left{_finger}Distal"] = f"left{_finger}Intermediate"
for _bone, _parent in list(VRM_PARENTS.items()):
    if _bone.startswith("left"):
        VRM_PARENTS["right" + _bone[4:]] = "right" + _parent[4:] if _parent.startswith("left") else _parent

STANDING = 1.16
FPS = 30


def read_glb(data: bytes):
    magic, version, length = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67 and version == 2 and length == len(data), (magic, version, length, len(data))
    json_length, json_type = struct.unpack_from("<II", data, 12)
    assert json_type == 0x4E4F534A
    document = json.loads(data[20 : 20 + json_length])
    bin_offset = 20 + json_length
    bin_length, bin_type = struct.unpack_from("<II", data, bin_offset)
    assert bin_type == 0x004E4942
    return document, data[bin_offset + 8 : bin_offset + 8 + bin_length], json_length, bin_length


def accessor(document, binary, index):
    item = document["accessors"][index]
    view = document["bufferViews"][item["bufferView"]]
    width = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}[item["type"]]
    values = np.frombuffer(binary, dtype="<f4", count=item["count"] * width, offset=view["byteOffset"])
    return values.reshape(item["count"], width) if width > 1 else values


def track(document, binary, bone, path):
    node = document["extensions"]["VRMC_vrm_animation"]["humanoid"]["humanBones"][bone]["node"]
    animation = document["animations"][0]
    for channel in animation["channels"]:
        if channel["target"] == {"node": node, "path": path}:
            return accessor(document, binary, animation["samplers"][channel["sampler"]]["output"])
    raise AssertionError(f"no {path} channel for {bone}")


def rotate(q, v):
    """Rotate vector v by quaternion q = (x, y, z, w)."""
    u, w = np.asarray(q[:3], dtype=float), float(q[3])
    v = np.asarray(v, dtype=float)
    return v + 2.0 * np.cross(u, np.cross(u, v) + w * v)


def standing(frames=4, joints=52):
    return np.zeros((frames, joints, 3)), np.tile([0.0, STANDING, 0.0], (frames, 1))


def rest_world(bone):
    return np.array(next(position for _, _, name, position in SMPLH_JOINTS if name == bone))


class SkeletonTests(unittest.TestCase):
    def test_joint_order_and_tree_match_smplh(self):
        self.assertEqual([row[0] for row in SMPLH_JOINTS], UPSTREAM_NAMES)
        self.assertEqual([row[1] for row in SMPLH_JOINTS], UPSTREAM_PARENTS)

    def test_bone_tree_matches_vrm_humanoid(self):
        bones = [row[2] for row in SMPLH_JOINTS]
        self.assertEqual(len(set(bones)), 52)
        self.assertEqual(set(bones) - {"hips"}, set(VRM_PARENTS))
        for name, parent, bone, _ in SMPLH_JOINTS[1:]:
            self.assertEqual(SMPLH_JOINTS[parent][2], VRM_PARENTS[bone], name)

    def test_rest_pose_is_a_t_pose_facing_plus_z(self):
        self.assertAlmostEqual(REST_HIPS_HEIGHT, 0.97)
        self.assertAlmostEqual(REST_HIPS_HEIGHT, STANDING + PELVIS_OFFSET[1])
        for side, sign in (("left", 1), ("right", -1)):
            upper, lower, hand = (rest_world(f"{side}{b}") for b in ("UpperArm", "LowerArm", "Hand"))
            self.assertGreater(sign * (lower[0] - upper[0]), 0.2)  # arms out to the side
            self.assertLess(abs(lower[1] - upper[1]) + abs(hand[1] - lower[1]), 0.03)  # and level
            knee, ankle, toes = (rest_world(f"{side}{b}") for b in ("LowerLeg", "Foot", "Toes"))
            self.assertGreater(knee[1] - ankle[1], 0.3)  # legs straight down
            self.assertGreater(toes[2], ankle[2])  # feet point to +Z
        for name, _, bone, position in SMPLH_JOINTS:
            if name.startswith(("L_", "R_")):
                mirror = bone.replace("left", "right") if name.startswith("L_") else bone.replace("right", "left")
                np.testing.assert_allclose(rest_world(mirror), np.array(position) * [-1, 1, 1], err_msg=bone)


class StructureTests(unittest.TestCase):
    def setUp(self):
        poses, trans = standing(frames=7)
        self.data = smplh_to_vrma(poses, trans, FPS)
        self.document, self.binary, self.json_length, self.bin_length = read_glb(self.data)

    def test_glb_chunks_are_aligned_and_sized(self):
        self.assertEqual(self.json_length % 4, 0)
        self.assertEqual(self.bin_length % 4, 0)
        self.assertEqual(self.document["asset"]["version"], "2.0")
        self.assertLessEqual(self.document["buffers"][0]["byteLength"], self.bin_length)
        for view in self.document["bufferViews"]:
            self.assertEqual(view["byteOffset"] % 4, 0)
            self.assertLessEqual(view["byteOffset"] + view["byteLength"], self.document["buffers"][0]["byteLength"])

    def test_vrm_animation_extension(self):
        self.assertIn("VRMC_vrm_animation", self.document["extensionsUsed"])
        self.assertNotIn("extensionsRequired", self.document)
        extension = self.document["extensions"]["VRMC_vrm_animation"]
        self.assertEqual(extension["specVersion"], "1.0")
        bones = extension["humanoid"]["humanBones"]
        self.assertEqual(len(bones), 52)
        self.assertEqual({bone["node"] for bone in bones.values()}, set(range(52)))
        for name, bone in bones.items():
            self.assertEqual(self.document["nodes"][bone["node"]]["name"], name)

    def test_rest_nodes_have_no_rotation_and_hips_is_the_root(self):
        nodes = self.document["nodes"]
        self.assertEqual(self.document["scenes"][self.document["scene"]]["nodes"], [0])
        for node in nodes:
            self.assertNotIn("rotation", node)
            self.assertNotIn("scale", node)
            self.assertNotIn("matrix", node)
        np.testing.assert_allclose(nodes[0]["translation"], [0.0, REST_HIPS_HEIGHT, 0.0])
        children = {child: index for index, node in enumerate(nodes) for child in node.get("children", [])}
        self.assertEqual([children.get(i, -1) for i in range(52)], UPSTREAM_PARENTS)

    def test_one_sampler_per_bone_on_a_shared_time_axis(self):
        animation = self.document["animations"][0]
        paths = sorted((c["target"]["node"], c["target"]["path"]) for c in animation["channels"])
        self.assertEqual(paths, sorted([(0, "translation")] + [(i, "rotation") for i in range(52)]))
        inputs = {sampler["input"] for sampler in animation["samplers"]}
        self.assertEqual(len(inputs), 1)
        time_index = inputs.pop()
        time = self.document["accessors"][time_index]
        self.assertEqual((time["type"], time["componentType"], time["count"]), ("SCALAR", 5126, 7))
        self.assertEqual(time["min"], [0.0])
        self.assertAlmostEqual(time["max"][0], 6 / FPS, places=6)
        for channel in animation["channels"]:
            sampler = animation["samplers"][channel["sampler"]]
            self.assertEqual(sampler["interpolation"], "LINEAR")
            output = self.document["accessors"][sampler["output"]]
            expected = "VEC3" if channel["target"]["path"] == "translation" else "VEC4"
            self.assertEqual((output["type"], output["componentType"], output["count"]), (expected, 5126, 7))
        np.testing.assert_allclose(accessor(self.document, self.binary, time_index), np.arange(7) / FPS, rtol=1e-6)

    def test_flat_and_per_joint_pose_shapes_give_the_same_file(self):
        rng = np.random.default_rng(1)
        poses = rng.normal(scale=0.4, size=(5, 52, 3))
        trans = rng.normal(size=(5, 3))
        self.assertEqual(smplh_to_vrma(poses, trans, FPS), smplh_to_vrma(poses.reshape(5, 156), trans, FPS))

    def test_body_only_poses_write_no_finger_tracks(self):
        poses, trans = standing(frames=3, joints=22)
        document, _, _, _ = read_glb(smplh_to_vrma(poses.reshape(3, 66), trans, FPS))
        nodes = {c["target"]["node"] for c in document["animations"][0]["channels"]}
        self.assertEqual(nodes, set(range(22)))
        self.assertEqual(len(document["extensions"]["VRMC_vrm_animation"]["humanoid"]["humanBones"]), 52)

    def test_single_frame(self):
        poses, trans = standing(frames=1)
        document, _, _, _ = read_glb(smplh_to_vrma(poses, trans, 30))
        time = document["accessors"][document["animations"][0]["samplers"][0]["input"]]
        self.assertEqual((time["count"], time["min"], time["max"]), (1, [0.0], [0.0]))


class MotionTests(unittest.TestCase):
    def convert(self, poses, trans):
        document, binary, _, _ = read_glb(smplh_to_vrma(poses, trans, FPS))
        return document, binary

    def arm_direction(self, document, binary, side, frame=-1):
        rest = rest_world(f"{side}LowerArm") - rest_world(f"{side}UpperArm")
        q = track(document, binary, f"{side}UpperArm", "rotation")[frame]
        moved = rotate(q, rest)
        return moved / np.linalg.norm(moved)

    def test_raising_the_right_arm_forward_points_it_to_plus_z(self):
        poses, trans = standing()
        poses[-1, 17] = (0.0, math.pi / 2, 0.0)  # R_Shoulder
        document, binary = self.convert(poses, trans)
        np.testing.assert_allclose(
            track(document, binary, "rightUpperArm", "rotation")[-1], [0, math.sqrt(0.5), 0, math.sqrt(0.5)], atol=1e-6
        )
        forward = self.arm_direction(document, binary, "right")
        self.assertGreater(forward @ [0, 0, 1], 0.99)  # forward, not backward or to the side
        self.assertGreater(self.arm_direction(document, binary, "right", frame=0) @ [-1, 0, 0], 0.99)
        self.assertGreater(self.arm_direction(document, binary, "left") @ [1, 0, 0], 0.99)

    def test_other_bones_stay_at_rest(self):
        poses, trans = standing()
        poses[:, 17] = (0.0, 1.0, 0.0)
        document, binary = self.convert(poses, trans)
        for _, _, bone, _ in SMPLH_JOINTS:
            if bone != "rightUpperArm":
                np.testing.assert_allclose(track(document, binary, bone, "rotation"), [[0, 0, 0, 1]] * 4, atol=1e-7)

    def test_rotations_round_trip_as_quaternions(self):
        rng = np.random.default_rng(7)
        axis = rng.normal(size=(6, 52, 3))
        axis /= np.linalg.norm(axis, axis=-1, keepdims=True)
        angle = rng.uniform(0.0, math.pi, size=(6, 52, 1))
        trans = np.tile([0.0, STANDING, 0.0], (6, 1))
        document, binary = self.convert(axis * angle, trans)
        for joint, (_, _, bone, _) in enumerate(SMPLH_JOINTS):
            q = track(document, binary, bone, "rotation")
            np.testing.assert_allclose(np.linalg.norm(q, axis=-1), 1.0, atol=1e-6)
            for frame in range(6):
                v = rng.normal(size=3)
                expected = (
                    v * math.cos(angle[frame, joint, 0])
                    + np.cross(axis[frame, joint], v) * math.sin(angle[frame, joint, 0])
                    + axis[frame, joint] * (axis[frame, joint] @ v) * (1 - math.cos(angle[frame, joint, 0]))
                )
                np.testing.assert_allclose(rotate(q[frame], v), expected, atol=1e-5)

    def test_quaternions_stay_in_one_hemisphere_across_a_wrap(self):
        angles = np.linspace(2.8, 3.6, 9)
        poses, trans = standing(frames=9)
        for frame, theta in enumerate(angles):
            # A converter from rotation matrices keeps the angle within pi, so the axis flips.
            poses[frame, 15] = (0, 0, theta) if theta <= math.pi else (0, 0, theta - 2 * math.pi)
        document, binary = self.convert(poses, trans)
        q = track(document, binary, "head", "rotation")
        self.assertTrue(np.all(np.sum(q[1:] * q[:-1], axis=-1) > 0.9))
        for frame, theta in enumerate(angles):
            np.testing.assert_allclose(rotate(q[frame], [1, 0, 0]), [math.cos(theta), math.sin(theta), 0], atol=1e-5)

    def test_hips_translation_is_the_pelvis_above_the_floor(self):
        poses, _ = standing(frames=3)
        trans = np.array([[0.0, STANDING, 0.0], [0.2, STANDING - 0.3, 1.5], [-1.0, STANDING + 0.1, -0.5]])
        document, binary = self.convert(poses, trans)
        hips = track(document, binary, "hips", "translation")
        np.testing.assert_allclose(hips, trans + PELVIS_OFFSET, atol=1e-6)
        np.testing.assert_allclose(hips[0], [0.0, REST_HIPS_HEIGHT, 0.0], atol=1e-6)


class InputErrorTests(unittest.TestCase):
    def test_bad_inputs_raise_value_error(self):
        poses, trans = standing()
        nan_poses = poses.copy()
        nan_poses[1, 3, 0] = np.nan
        inf_trans = trans.copy()
        inf_trans[2, 1] = np.inf
        cases = {
            "51 joints": (np.zeros((4, 51, 3)), trans, 30),
            "flat 155": (np.zeros((4, 155)), trans, 30),
            "4 components": (np.zeros((4, 52, 4)), trans, 30),
            "no frame axis": (np.zeros((52, 3)), trans, 30),
            "zero frames": (np.zeros((0, 52, 3)), np.zeros((0, 3)), 30),
            "zero frames flat": (np.zeros((0, 156)), np.zeros((0, 3)), 30),
            "text poses": ("not a pose", trans, 30),
            "ragged poses": ([[1, 2, 3], [4, 5]], trans, 30),
            "trans frame count": (poses, trans[:3], 30),
            "trans width": (poses, np.zeros((4, 2)), 30),
            "NaN pose": (nan_poses, trans, 30),
            "infinite trans": (poses, inf_trans, 30),
            "zero fps": (poses, trans, 0),
            "negative fps": (poses, trans, -30),
            "NaN fps": (poses, trans, float("nan")),
            "infinite fps": (poses, trans, float("inf")),
            "bool fps": (poses, trans, True),
            "text fps": (poses, trans, "30"),
            "None fps": (poses, trans, None),
        }
        for name, (p, t, fps) in cases.items():
            with self.subTest(name), self.assertRaises(ValueError) as caught:
                smplh_to_vrma(p, t, fps)
            self.assertTrue(str(caught.exception))

    def test_numpy_and_int_fps_are_accepted(self):
        poses, trans = standing(frames=2)
        for fps in (np.float32(24), np.int64(30), 60, 29.97):
            with self.subTest(fps=fps):
                document, _, _, _ = read_glb(smplh_to_vrma(poses, trans, fps))
                time = document["accessors"][document["animations"][0]["samplers"][0]["input"]]
                self.assertAlmostEqual(time["max"][0], 1 / float(fps), places=5)


if __name__ == "__main__":
    unittest.main()
