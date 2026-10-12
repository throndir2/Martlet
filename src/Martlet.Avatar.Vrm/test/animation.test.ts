import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { VRMA_LIMITS, VrmError, VrmRuntime } from "../src/index.js";
import { encodeGlb, fixture, fixtureV0, vrmaFixture } from "./fixture.js";

type Model = { humanoid: { getNormalizedBoneNode(name: string): THREE.Object3D; getRawBoneNode(name: string): THREE.Object3D };
  expressionManager: { getValue(name: string): number | null } };
const modelOf = (runtime: VrmRuntime) => (runtime as unknown as { model: Model }).model;
const worldY = (node: THREE.Object3D) => node.getWorldPosition(new THREE.Vector3()).y;
const zTurn = (angle: number) => new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 0, 1), angle);

test("a VRMA clip blends in over the idle pose, plays once and gives the body back to it", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  const vrm = modelOf(runtime), bone = (name: string) => vrm.humanoid.getNormalizedBoneNode(name);
  runtime.update(0.016);
  const hanging = bone("rightUpperArm").quaternion.clone(), hipsRest = bone("hips").position.clone();
  assert.ok(bone("rightUpperArm").rotation.z > 1, "the right arm hangs at rest");

  const info = await runtime.loadAnimation("raise", vrmaFixture({ hips: [0.1, 0.8, 0.2], eye: true }));
  assert.deepEqual([info.name, info.duration, [...info.bones], info.hips, info.inPlace], ["raise", 1, ["rightUpperArm"], true, true],
    "the eye track stays with the gaze; the arm and hips are the clip's, the hips in place by default");
  assert.deepEqual(runtime.animations.map(a => a.name), ["raise"]);
  assert.equal(runtime.animationReading, undefined, "a kept clip doesn't play by itself");
  assert.equal(runtime.playAnimation("wave"), false, "no clip has that name");
  assert.equal(runtime.playAnimation("raise"), true);

  // It blends in: part of the way at first, never a snap.
  const raised = zTurn(-1.4);
  runtime.update(0.05);
  const early = runtime.animationReading!;
  assert.ok(early.weight > 0 && early.weight < 0.5, `it blends in: ${early.weight}`);
  const partway = bone("rightUpperArm").quaternion.angleTo(raised);
  assert.ok(partway > 0.1 && partway < hanging.angleTo(raised), `the arm is on its way up: ${partway}`);
  let last = bone("rightUpperArm").quaternion.clone(), largest = 0;
  for (let i = 0; i < 27; i++) {
    runtime.update(1 / 60);
    largest = Math.max(largest, bone("rightUpperArm").quaternion.angleTo(last));
    last = bone("rightUpperArm").quaternion.clone();
  }
  // In the middle the clip has the arm and the hips' height; across the ground the hips stay at rest, so the character stays
  // in its window however the clip walks or drifts.
  const middle = runtime.animationReading!;
  assert.deepEqual([middle.name, middle.duration, middle.weight], ["raise", 1, 1]);
  assert.ok(Math.abs(middle.time - 0.5) < 0.01, `half way: ${middle.time}`);
  assert.ok(bone("rightUpperArm").quaternion.angleTo(raised) < 1e-3, "the arm is where the clip has it");
  runtime.scene!.updateWorldMatrix(true, true);
  assert.ok(worldY(vrm.humanoid.getRawBoneNode("rightHand")) > worldY(vrm.humanoid.getRawBoneNode("rightUpperArm")) + 0.2,
    "the raised hand is above the shoulder");
  assert.ok(bone("hips").position.distanceTo(new THREE.Vector3(hipsRest.x, 0.85, hipsRest.z)) < 1e-3,
    `the hips go down with the clip and stay in place: ${bone("hips").position.toArray()}`);
  assert.ok(runtime.idleReading!.idle, "the idle motion still runs under the clip");

  // Lip-sync and the look keep working while it plays.
  runtime.setLipSync(1);
  runtime.update(0.05);
  assert.ok((vrm.expressionManager.getValue("aa") ?? 0) > 0.5, "the voice still opens the mouth");

  // It gives the body back by its end, smoothly, and is gone after it.
  for (let i = 0; i < 40; i++) {
    runtime.update(1 / 60);
    largest = Math.max(largest, bone("rightUpperArm").quaternion.angleTo(last));
    last = bone("rightUpperArm").quaternion.clone();
  }
  assert.ok(largest < 0.35, `no frame snaps the arm: at most ${largest} radians a frame at 60 frames a second`);
  assert.equal(runtime.animationReading, undefined, "it played once");
  assert.ok(bone("rightUpperArm").rotation.z > 1, "the arm hangs again");
  assert.ok(bone("hips").position.distanceTo(hipsRest) < 1e-9, "the hips are back where they rest");
  runtime.update(0.05);
  assert.ok(bone("hips").position.distanceTo(hipsRest) < 1e-9, "and stay there");

  // A kept clip plays again by its name.
  assert.equal(runtime.playAnimation("raise"), true);
  for (let i = 0; i < 5; i++) runtime.update(0.1);
  assert.ok(bone("rightUpperArm").quaternion.angleTo(raised) < 1e-3, "it plays again");

  // Kept without in place, a clip moves the hips across the ground from where it starts.
  const walking = await runtime.loadAnimation("walk", vrmaFixture({ hips: [0.1, 0.8, 0.2] }), false);
  assert.equal(walking.inPlace, false);
  runtime.playAnimation("walk");
  for (let i = 0; i < 10; i++) runtime.update(0.05);
  assert.ok(bone("hips").position.distanceTo(new THREE.Vector3(hipsRest.x + 0.05, 0.85, hipsRest.z + 0.1)) < 1e-2,
    `the hips walk with the clip: ${bone("hips").position.toArray()}`);
  runtime.dispose();
});

test("the head keeps following the look on top of a clip that turns it, and a stopped clip blends out", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  const bone = (name: string) => modelOf(runtime).humanoid.getNormalizedBoneNode(name);
  await runtime.loadAnimation("tilt", vrmaFixture({ seconds: 4, head: 0.3 }));
  assert.equal(runtime.playAnimation("tilt"), true);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  const tilted = zTurn(0.3);
  assert.ok(bone("head").quaternion.angleTo(tilted) < 0.02, "the clip tilts the head");
  runtime.setLook(1, 0);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  const turned = bone("head").quaternion.angleTo(tilted);
  assert.ok(turned > 0.2, `the head turns toward the look on top of the tilt: ${turned}`);
  assert.ok(new THREE.Euler().setFromQuaternion(bone("head").quaternion.clone().premultiply(tilted.clone().invert())).y > 0.2,
    "it turns the way the look goes");

  // Stopped, it blends back to the idle pose in about 0.3 seconds.
  assert.equal(runtime.stopAnimation(), true);
  runtime.update(0.1);
  assert.ok(runtime.animationReading!.weight > 0 && runtime.animationReading!.weight < 1, "it blends out");
  for (let i = 0; i < 5; i++) runtime.update(0.05);
  assert.equal(runtime.animationReading, undefined, "it is gone");
  assert.equal(runtime.stopAnimation(), false, "nothing plays");
  runtime.dispose();
});

test("a new clip crossfades from the one playing", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  const bone = (name: string) => modelOf(runtime).humanoid.getNormalizedBoneNode(name);
  await runtime.loadAnimation("up", vrmaFixture({ seconds: 3, armZ: -1.4 }));
  await runtime.loadAnimation("out", vrmaFixture({ seconds: 3, armZ: 0 }));
  runtime.playAnimation("up");
  for (let i = 0; i < 30; i++) runtime.update(1 / 60);
  let last = bone("rightUpperArm").quaternion.clone(), largest = 0;
  runtime.playAnimation("out");
  for (let i = 0; i < 60; i++) {
    runtime.update(1 / 60);
    largest = Math.max(largest, bone("rightUpperArm").quaternion.angleTo(last));
    last = bone("rightUpperArm").quaternion.clone();
  }
  assert.equal(runtime.animationReading!.name, "out");
  assert.ok(bone("rightUpperArm").quaternion.angleTo(zTurn(0)) < 1e-3, "the new clip has the arm");
  assert.ok(largest < 0.2, `the switch never snaps: at most ${largest} radians a frame`);
  runtime.dispose();
});

test("a VRMA clip plays on a VRM 0.x model too", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixtureV0()); runtime.startIdle();
  const vrm = modelOf(runtime);
  runtime.update(0.016);
  runtime.scene!.updateWorldMatrix(true, true);
  const shoulder = () => worldY(vrm.humanoid.getRawBoneNode("rightUpperArm")), hand = () => worldY(vrm.humanoid.getRawBoneNode("rightHand"));
  assert.ok(hand() < shoulder(), "the right hand hangs below the shoulder");
  const info = await runtime.loadAnimation("raise", vrmaFixture());
  assert.deepEqual([...info.bones], ["rightUpperArm"]);
  runtime.playAnimation("raise");
  for (let i = 0; i < 10; i++) runtime.update(0.05);
  runtime.scene!.updateWorldMatrix(true, true);
  assert.ok(hand() > shoulder() + 0.2, "the clip raises the right arm, mirrored for the model's -Z bones");
  for (let i = 0; i < 12; i++) runtime.update(0.05);
  runtime.scene!.updateWorldMatrix(true, true);
  assert.ok(hand() < shoulder(), "and gives it back");
  runtime.dispose();
});

test("only real VRMA clips are kept, at most 16 for one model, and a new model forgets them", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  const rejects = (name: string, bytes: ArrayBuffer, pattern: RegExp) =>
    assert.rejects(runtime.loadAnimation(name, bytes), (error: unknown) => error instanceof VrmError && pattern.test(error.message));
  await rejects("model", fixture(), /not a VRM Animation/);
  await rejects("noise", new Uint8Array(64).fill(7).buffer, /binary glTF/);
  await rejects("big", new ArrayBuffer(VRMA_LIMITS.bytes + 4), /VRMA clip is 20 bytes/);
  await rejects("", vrmaFixture(), /clip name/);
  await rejects(" padded", vrmaFixture(), /clip name/);
  const remote = JSON.parse(new TextDecoder().decode(new Uint8Array(vrmaFixture(), 20, new DataView(vrmaFixture()).getUint32(12, true))));
  remote.buffers[0].uri = "https://example.invalid/clip.bin";
  await rejects("remote", encodeGlb(remote), /inside the file/);
  assert.equal(runtime.animations.length, 0);

  for (let i = 0; i < VRMA_LIMITS.clips + 2; i++) await runtime.loadAnimation(`clip ${i}`, vrmaFixture());
  assert.equal(runtime.animations.length, VRMA_LIMITS.clips);
  assert.equal(runtime.animations[0]!.name, "clip 2", "the oldest went first");
  assert.equal(runtime.playAnimation("clip 0"), false);
  assert.equal(runtime.playAnimation(`clip ${VRMA_LIMITS.clips + 1}`), true);
  await runtime.load(fixture());
  assert.equal(runtime.animations.length, 0, "a clip belongs to the model it was read for");
  assert.equal(runtime.animationReading, undefined);
  runtime.dispose();
});
