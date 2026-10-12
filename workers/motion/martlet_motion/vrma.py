"""Convert SMPL-H body motion (HY-Motion output) to a VRM Animation clip (``.vrma``).

A ``.vrma`` file is binary glTF (GLB) with the ``VRMC_vrm_animation`` 1.0 extension
(https://github.com/vrm-c/vrm-specification/blob/master/specification/VRMC_vrm_animation-1.0/README.md).
This module writes it with the Python standard library and numpy only. It needs no
torch, no FBX SDK and no SMPL/SMPL-H body model files.

How the conversion works:

- The clip has a fixed rest skeleton: one glTF node for each SMPL-H joint, named and
  mapped to its VRM 1.0 humanoid bone. Every node has only a translation (no
  rotation), so each bone's rest world rotation is the identity, like the SMPL T-pose.
- SMPL-H and VRM 1.0 use the same frame: Y up, the body faces +Z and its left side is
  at +X. With identity rest rotations, the VRM normalized rotation of a bone is
  exactly the SMPL-H local joint rotation. The converter writes the rotations
  unchanged as quaternions. ``@pixiv/three-vrm-animation`` turns them to face -Z for
  VRM 0.x models.
- The hips translation track is the pelvis position above the floor:
  ``trans + PELVIS_OFFSET``. The rest hips height is ``REST_HIPS_HEIGHT``. The loader
  scales the track by ``model hips height / REST_HIPS_HEIGHT``, so the motion fits
  models of all sizes.
"""

from __future__ import annotations

import json
import math
import numbers
import struct

import numpy as np

__all__ = [
    "PELVIS_OFFSET",
    "REST_HIPS_HEIGHT",
    "SMPLH_JOINTS",
    "smplh_to_vrma",
]

# One row per SMPL-H joint, in SMPL-H order (HY-Motion's hymotion/utils/smplh2woodfbx.py
# SMPLH_JOINT2NUM): (SMPL-H name, parent index, VRM 1.0 humanoid bone, rest position).
# The rest position is the joint's world position in meters in the T-pose, with the
# floor at y = 0. The values are rounded adult proportions that match the HY-Motion
# skeleton (pelvis 0.97 m above the floor). Only the hips height changes the output;
# the other offsets only make the clip's skeleton look like a body in glTF viewers.
SMPLH_JOINTS = (
    ("Pelvis", -1, "hips", (0.0, 0.97, 0.0)),
    ("L_Hip", 0, "leftUpperLeg", (0.08, 0.88, 0.0)),
    ("R_Hip", 0, "rightUpperLeg", (-0.08, 0.88, 0.0)),
    ("Spine1", 0, "spine", (0.0, 1.06, 0.0)),
    ("L_Knee", 1, "leftLowerLeg", (0.10, 0.50, -0.02)),
    ("R_Knee", 2, "rightLowerLeg", (-0.10, 0.50, -0.02)),
    ("Spine2", 3, "chest", (0.0, 1.19, 0.0)),
    ("L_Ankle", 4, "leftFoot", (0.10, 0.10, -0.07)),
    ("R_Ankle", 5, "rightFoot", (-0.10, 0.10, -0.07)),
    ("Spine3", 6, "upperChest", (0.0, 1.27, -0.03)),
    ("L_Foot", 7, "leftToes", (0.11, 0.03, 0.08)),
    ("R_Foot", 8, "rightToes", (-0.11, 0.03, 0.08)),
    ("Neck", 9, "neck", (0.0, 1.46, -0.05)),
    ("L_Collar", 9, "leftShoulder", (0.05, 1.39, -0.05)),
    ("R_Collar", 9, "rightShoulder", (-0.05, 1.39, -0.05)),
    ("Head", 12, "head", (0.0, 1.56, -0.05)),
    ("L_Shoulder", 13, "leftUpperArm", (0.17, 1.40, -0.05)),
    ("R_Shoulder", 14, "rightUpperArm", (-0.17, 1.40, -0.05)),
    ("L_Elbow", 16, "leftLowerArm", (0.43, 1.41, -0.06)),
    ("R_Elbow", 17, "rightLowerArm", (-0.43, 1.41, -0.06)),
    ("L_Wrist", 18, "leftHand", (0.70, 1.41, -0.07)),
    ("R_Wrist", 19, "rightHand", (-0.70, 1.41, -0.07)),
    ("L_Index1", 20, "leftIndexProximal", (0.79, 1.41, -0.05)),
    ("L_Index2", 22, "leftIndexIntermediate", (0.83, 1.41, -0.05)),
    ("L_Index3", 23, "leftIndexDistal", (0.855, 1.41, -0.045)),
    ("L_Middle1", 20, "leftMiddleProximal", (0.79, 1.41, -0.075)),
    ("L_Middle2", 25, "leftMiddleIntermediate", (0.84, 1.41, -0.08)),
    ("L_Middle3", 26, "leftMiddleDistal", (0.865, 1.41, -0.08)),
    ("L_Pinky1", 20, "leftLittleProximal", (0.78, 1.40, -0.115)),
    ("L_Pinky2", 28, "leftLittleIntermediate", (0.805, 1.40, -0.13)),
    ("L_Pinky3", 29, "leftLittleDistal", (0.82, 1.40, -0.14)),
    ("L_Ring1", 20, "leftRingProximal", (0.80, 1.405, -0.095)),
    ("L_Ring2", 31, "leftRingIntermediate", (0.83, 1.405, -0.10)),
    ("L_Ring3", 32, "leftRingDistal", (0.85, 1.405, -0.11)),
    ("L_Thumb1", 20, "leftThumbMetacarpal", (0.72, 1.40, -0.05)),
    ("L_Thumb2", 34, "leftThumbProximal", (0.75, 1.40, -0.025)),
    ("L_Thumb3", 35, "leftThumbDistal", (0.78, 1.395, -0.005)),
    ("R_Index1", 21, "rightIndexProximal", (-0.79, 1.41, -0.05)),
    ("R_Index2", 37, "rightIndexIntermediate", (-0.83, 1.41, -0.05)),
    ("R_Index3", 38, "rightIndexDistal", (-0.855, 1.41, -0.045)),
    ("R_Middle1", 21, "rightMiddleProximal", (-0.79, 1.41, -0.075)),
    ("R_Middle2", 40, "rightMiddleIntermediate", (-0.84, 1.41, -0.08)),
    ("R_Middle3", 41, "rightMiddleDistal", (-0.865, 1.41, -0.08)),
    ("R_Pinky1", 21, "rightLittleProximal", (-0.78, 1.40, -0.115)),
    ("R_Pinky2", 43, "rightLittleIntermediate", (-0.805, 1.40, -0.13)),
    ("R_Pinky3", 44, "rightLittleDistal", (-0.82, 1.40, -0.14)),
    ("R_Ring1", 21, "rightRingProximal", (-0.80, 1.405, -0.095)),
    ("R_Ring2", 46, "rightRingIntermediate", (-0.83, 1.405, -0.10)),
    ("R_Ring3", 47, "rightRingDistal", (-0.85, 1.405, -0.11)),
    ("R_Thumb1", 21, "rightThumbMetacarpal", (-0.72, 1.40, -0.05)),
    ("R_Thumb2", 49, "rightThumbProximal", (-0.75, 1.40, -0.025)),
    ("R_Thumb3", 50, "rightThumbDistal", (-0.78, 1.395, -0.005)),
)

BODY_JOINT_COUNT = 22
JOINT_COUNT = len(SMPLH_JOINTS)
REST_HIPS_HEIGHT = SMPLH_JOINTS[0][3][1]

# ``trans`` has SMPL ``transl`` meaning: the pelvis joint is at ``trans + PELVIS_OFFSET``.
# The SMPL-H (and HY-Motion "wooden") rest pelvis is 0.19 m below the model origin, and
# HY-Motion moves ``transl`` so that the lowest vertex of the clip is at y = 0. A standing
# frame therefore has trans y of about 1.16 and puts the hips at REST_HIPS_HEIGHT. The
# horizontal part of the offset is left out: it only moves the whole clip by a few cm.
PELVIS_OFFSET = (0.0, -0.19, 0.0)

_EXTENSION = "VRMC_vrm_animation"
_GLB_MAGIC = 0x46546C67  # "glTF"
_CHUNK_JSON = 0x4E4F534A  # "JSON"
_CHUNK_BIN = 0x004E4942  # "BIN\0"
_FLOAT = 5126


def smplh_to_vrma(poses, trans, fps) -> bytes:
    """Return the bytes of a ``.vrma`` clip for SMPL-H motion.

    Args:
        poses: SMPL-H local joint rotations as axis-angle vectors in radians, in SMPL-H
            joint order. Shape (F, 52, 3) or (F, 156). The 22 body joints alone,
            (F, 22, 3) or (F, 66), are also accepted; the clip then has no finger tracks.
        trans: SMPL ``transl`` in meters, shape (F, 3). The pelvis is at
            ``trans + PELVIS_OFFSET`` and the floor is at y = 0 (HY-Motion's ``transl``).
        fps: frames per second, a finite number above 0 (HY-Motion makes 30).

    Both arrays use the SMPL frame: Y up, the body faces +Z, the rest pose is the T-pose.

    Raises:
        ValueError: when a shape is wrong, a value is not finite or fps is bad.
    """
    rotations, translation, rate = _validate(poses, trans, fps)
    frames, joints = rotations.shape[:2]

    quaternions = _continuous(_axis_angle_to_quaternions(rotations))
    hips = translation + np.asarray(PELVIS_OFFSET, dtype=np.float64)
    times = np.arange(frames, dtype=np.float64) / rate

    binary = _BinaryBuilder()
    time_accessor = binary.accessor(times, "SCALAR", bounds=True)
    samplers = []
    channels = []

    def add_channel(node, path, values, kind):
        samplers.append({"input": time_accessor, "output": binary.accessor(values, kind), "interpolation": "LINEAR"})
        channels.append({"sampler": len(samplers) - 1, "target": {"node": node, "path": path}})

    add_channel(0, "translation", hips, "VEC3")
    for joint in range(joints):
        add_channel(joint, "rotation", quaternions[:, joint], "VEC4")

    document = {
        "asset": {"version": "2.0", "generator": "Martlet motion worker (SMPL-H to VRMA)"},
        "extensionsUsed": [_EXTENSION],
        "extensions": {
            _EXTENSION: {
                "specVersion": "1.0",
                "humanoid": {"humanBones": {bone: {"node": index} for index, (_, _, bone, _) in enumerate(SMPLH_JOINTS)}},
            }
        },
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": _rest_nodes(),
        "animations": [{"name": "motion", "channels": channels, "samplers": samplers}],
        "buffers": [{"byteLength": binary.length}],
        "bufferViews": binary.buffer_views,
        "accessors": binary.accessors,
    }
    return _glb(document, binary.data())


def _validate(poses, trans, fps):
    rotations = _float_array("poses", poses)
    if rotations.ndim == 2 and rotations.shape[1] in (JOINT_COUNT * 3, BODY_JOINT_COUNT * 3):
        rotations = rotations.reshape(rotations.shape[0], rotations.shape[1] // 3, 3)
    if rotations.ndim != 3 or rotations.shape[1] not in (JOINT_COUNT, BODY_JOINT_COUNT) or rotations.shape[2] != 3:
        raise ValueError(
            f"poses must have shape (F, 52, 3), (F, 156), (F, 22, 3) or (F, 66); got {rotations.shape}."
        )
    frames = rotations.shape[0]
    if frames < 1:
        raise ValueError("poses must have at least one frame.")

    translation = _float_array("trans", trans)
    if translation.shape != (frames, 3):
        raise ValueError(f"trans must have shape ({frames}, 3) to match poses; got {translation.shape}.")

    if not np.isfinite(rotations).all():
        raise ValueError("poses contains NaN or infinite values.")
    if not np.isfinite(translation).all():
        raise ValueError("trans contains NaN or infinite values.")

    if isinstance(fps, bool) or not isinstance(fps, numbers.Real):
        raise ValueError(f"fps must be a number; got {type(fps).__name__}.")
    rate = float(fps)
    if not math.isfinite(rate) or rate <= 0:
        raise ValueError(f"fps must be a finite number above 0; got {fps!r}.")
    return rotations, translation, rate


def _float_array(name, value):
    try:
        array = np.asarray(value, dtype=np.float64)
    except (TypeError, ValueError) as error:
        raise ValueError(f"{name} must be a numeric array: {error}") from None
    return array


def _axis_angle_to_quaternions(rotations):
    """Axis-angle (..., 3) to unit quaternions (..., 4) in glTF order (x, y, z, w)."""
    angle = np.linalg.norm(rotations, axis=-1, keepdims=True)
    half = 0.5 * angle
    small = angle < 1e-8
    # sin(a/2)/a, with its Taylor series near 0 so that a zero rotation stays exact.
    scale = np.where(small, 0.5 - angle * angle / 48.0, np.sin(half) / np.where(small, 1.0, angle))
    quaternions = np.concatenate([rotations * scale, np.cos(half)], axis=-1)
    return quaternions / np.linalg.norm(quaternions, axis=-1, keepdims=True)


def _continuous(quaternions):
    """Flip signs so that each key is in the same hemisphere as the key before it.

    q and -q are the same rotation, but linear keyframe interpolation between keys in
    opposite hemispheres turns the long way round.
    """
    if quaternions.shape[0] < 2:
        return quaternions
    dots = np.sum(quaternions[1:] * quaternions[:-1], axis=-1)
    flips = np.cumsum(dots < 0, axis=0) % 2
    signs = np.concatenate([np.ones((1,) + flips.shape[1:]), 1.0 - 2.0 * flips], axis=0)
    return quaternions * signs[..., None]


def _rest_nodes():
    children = [[] for _ in SMPLH_JOINTS]
    for index, (_, parent, _, _) in enumerate(SMPLH_JOINTS):
        if parent >= 0:
            children[parent].append(index)
    nodes = []
    for index, (_, parent, bone, position) in enumerate(SMPLH_JOINTS):
        origin = SMPLH_JOINTS[parent][3] if parent >= 0 else (0.0, 0.0, 0.0)
        node = {"name": bone, "translation": [round(position[i] - origin[i], 6) for i in range(3)]}
        if children[index]:
            node["children"] = children[index]
        nodes.append(node)
    return nodes


class _BinaryBuilder:
    """Collects float32 accessors in one glTF buffer."""

    _COMPONENTS = {"SCALAR": 1, "VEC3": 3, "VEC4": 4}

    def __init__(self):
        self._parts = []
        self.length = 0
        self.buffer_views = []
        self.accessors = []

    def accessor(self, values, kind, bounds=False):
        data = np.ascontiguousarray(values, dtype="<f4").reshape(-1, self._COMPONENTS[kind])
        raw = data.tobytes()
        self.buffer_views.append({"buffer": 0, "byteOffset": self.length, "byteLength": len(raw)})
        self._parts.append(raw)
        self.length += len(raw)  # float32 data keeps every view 4-byte aligned
        accessor = {
            "bufferView": len(self.buffer_views) - 1,
            "componentType": _FLOAT,
            "count": data.shape[0],
            "type": kind,
        }
        if bounds:
            accessor["min"] = [float(v) for v in data.min(axis=0)]
            accessor["max"] = [float(v) for v in data.max(axis=0)]
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def data(self):
        return b"".join(self._parts)


def _glb(document, binary):
    text = json.dumps(document, separators=(",", ":")).encode("utf-8")
    text += b" " * (-len(text) % 4)
    binary += b"\x00" * (-len(binary) % 4)
    length = 12 + 8 + len(text) + 8 + len(binary)
    return b"".join(
        (
            struct.pack("<III", _GLB_MAGIC, 2, length),
            struct.pack("<II", len(text), _CHUNK_JSON),
            text,
            struct.pack("<II", len(binary), _CHUNK_BIN),
            binary,
        )
    )
