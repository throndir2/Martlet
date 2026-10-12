# Martlet motion worker

The `motion` worker makes humanoid body motion from an English prompt with
[HY-Motion 1.0](https://github.com/Tencent-Hunyuan/HY-Motion-1.0) and gives it to Martlet as a VRM
Animation clip (`.vrma`) for VRM models.

## SMPL-H to VRMA converter

`martlet_motion/vrma.py` changes HY-Motion's SMPL-H joint rotations to a `.vrma` clip. HY-Motion's own
FBX export needs the Autodesk FBX SDK, so Martlet does not use it. The converter needs only the Python
standard library and numpy. It needs no torch, no FBX SDK and no SMPL or SMPL-H body model files.

```python
from martlet_motion.vrma import smplh_to_vrma

clip = smplh_to_vrma(poses, trans, fps=30)  # bytes of a .vrma file
```

- `poses`: SMPL-H local joint rotations as axis-angle vectors in radians, in SMPL-H joint order
  (HY-Motion's `SMPLH_JOINT2NUM`). The shape is (F, 52, 3) or (F, 156). The 22 body joints alone,
  (F, 22, 3) or (F, 66), are also accepted; the clip then has no finger tracks.
- `trans`: HY-Motion's `transl` in meters, shape (F, 3). It has the SMPL `transl` meaning: the pelvis is
  at `trans + (0, -0.19, 0)`, and the floor is at y = 0. HY-Motion moves `transl` so that the lowest
  vertex of the clip touches y = 0, so a standing body has a trans y of about 1.16.
- Both arrays use the SMPL frame: Y up, the body faces +Z, its left side is at +X, and the rest pose
  is the T-pose. HY-Motion's output already uses this frame.
- `fps`: frames per second, a finite number above 0. HY-Motion makes 30.
- A wrong shape, a NaN or infinite value or a bad `fps` causes a `ValueError` with the reason.

How the clip is made:

1. The clip has a fixed rest skeleton: one glTF node for each SMPL-H joint, mapped to a VRM 1.0
   humanoid bone. The table is `SMPLH_JOINTS` in `vrma.py`. Pelvis, spine 1-3, neck and head become
   `hips`, `spine`, `chest`, `upperChest`, `neck` and `head`. Collars, shoulders, elbows and wrists
   become `*Shoulder`, `*UpperArm`, `*LowerArm` and `*Hand`. Hips, knees, ankles and feet become
   `*UpperLeg`, `*LowerLeg`, `*Foot` and `*Toes`. The finger joints become the VRM finger bones
   (pinky is `Little`; thumb 1-3 is `ThumbMetacarpal`, `ThumbProximal` and `ThumbDistal`).
2. The rest nodes have no rotation, so every rest world rotation is the identity, as in the SMPL
   T-pose. SMPL and VRM 1.0 use the same frame, so each SMPL-H local rotation is also the VRM
   normalized bone rotation. The converter writes them unchanged as quaternions, and keeps the
   quaternion signs continuous from frame to frame.
3. The hips translation track is the pelvis position above the floor. The rest hips are 0.97 m high
   (`REST_HIPS_HEIGHT`). `@pixiv/three-vrm-animation` multiplies the track by
   `model hips height / 0.97`, so the motion fits the model's size.
4. The loader turns the clip to face -Z for VRM 0.x models.

Limits:

- A VRM model without `upperChest` or toe bones does not get the Spine3 or foot rotation from the clip.
- The hips track keeps HY-Motion's horizontal travel. To play a walk in place, remove the x and z
  movement before playback.

### Check the converter with the real loader

`vrma-check/` loads converter output with `@pixiv/three-vrm-animation` 3.5.5, `@pixiv/three-vrm` 3.5.5
and `three` 0.180.0 (the versions that Martlet's VRM renderer uses) in Node. It makes its own two test
models with only a skeleton: a VRM 1.0 model, and a VRM 0.x model without `upperChest` and toes. Every
bone of the test models has a different rest rotation, like an exported rig, and the VRM 0.x model is
turned with `VRMUtils.rotateVRM0` as in Martlet. For each synthetic motion in
`vrma-check/make_cases.py` (arms forward and up, an elbow, a knee, a forward lean, a turn, walking,
stepping, crouching, finger curls and a body-only clip), the check plays the clip, measures the raw
bones and checks where they point in the model's own frame. It also runs the Khronos glTF validator
on each file.

1. Install the pinned packages:

   ```powershell
   cd workers\motion\vrma-check
   npm ci
   ```

2. Run the check (it runs `python make_cases.py out` first; set `PYTHON` to use another interpreter):

   ```powershell
   node check.mjs
   ```

3. Make sure that the last line is `44 checks passed, 0 failed.` The exit code is 0 when all checks
   pass, 1 when a check fails and 2 when the check cannot run.

When `npm ci` has run, the unit test suite also runs this check
(`tests/test_vrma_three.py`); otherwise that test skips.

### Tests

```powershell
.\scripts\Test-Martlet.ps1 -Project python:motion
```

Or, in `workers\motion`: `python -m unittest discover -s tests`.
