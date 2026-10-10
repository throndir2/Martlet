import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { breathing, BREATH_SECONDS, cheekFrame, inspectVrm, LIMITS, loadLocalVrm, VRM_BLUSH_LEVELS, VRM_GESTURES, VRM_HOLD_PARTS,
  VrmRuntime, type PlaybackIdentity, type Selection } from "../src/index.js";
import { encodeGlb, fixture, fixtureDocument } from "./fixture.js";

const identity: PlaybackIdentity = {
  sessionId: "11111111-1111-1111-1111-111111111111", turnId: "22222222-2222-2222-2222-222222222222",
  requestId: "33333333-3333-3333-3333-333333333333", sourceId: "host-composed-a2f", epoch: 0, sampleRate: 24000,
};
const revision = { modelRevision: "fixture-v1", mappingRevision: "mapping-v1" };
const selection: Selection = {
  faceSource: identity.sourceId, faceMode: "authored-explicit",
  mappings: [
    { channel: "jawOpen", expression: "aa", aspect: "mouth", minimum: 0, maximum: 1 },
    { channel: "happy", expression: "happy", aspect: "expression", minimum: 0, maximum: 1 },
    { channel: "blink", expression: "blink", aspect: "blink", minimum: 0, maximum: 1 },
  ], gaze: true, head: true, secondaryMotion: true,
};
function morphs(runtime: VrmRuntime): number[] {
  let found: number[] | undefined;
  runtime.scene!.traverse(node => { if (node instanceof THREE.Mesh && node.morphTargetInfluences) found = node.morphTargetInfluences; });
  assert.ok(found); return found;
}
async function active(): Promise<VrmRuntime> {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.configure(selection, revision, "coefficients"); runtime.reset(identity); return runtime;
}
function frame(sequence = 0, coefficients = { jawOpen: 0.8, happy: 0, blink: 0.6 }) {
  return { identity, ...revision, sequence, sampleOffset: 0, coefficients };
}

test("synthetic importer inspects actual authored controls, not guessed ARKit detail", async () => {
  const capabilities = inspectVrm(fixture());
  assert.equal(capabilities.format, "VRM1"); assert.equal(capabilities.expressions.length, 8);
  assert.equal(capabilities.gaze, "expression"); assert.equal(capabilities.secondaryMotion, true);
  assert.equal(capabilities.facialDetail, "explicit-mapping-required"); assert.equal(capabilities.bodyPlayback, false);
  const { vrm } = await loadLocalVrm(fixture());
  assert.ok(vrm.expressionManager?.getExpression("AuthoredLip"));
  assert.ok(vrm.springBoneManager?.joints.size);
  const { VRMUtils } = await import("@pixiv/three-vrm");
  VRMUtils.deepDispose(vrm.scene);
});

test("each spring-bone chain is read root first, a tail from the hips as hair from the head, for touch zones", async () => {
  assert.deepEqual(new VrmRuntime().springNodes, [], "nothing before a model loads");
  const { document, bin } = fixtureDocument();
  // A tail hanging behind the hips: its own spring, after the hair's.
  document.nodes.push({ name: "TailRoot", translation: [0, -0.05, -0.1], children: [22] },
    { name: "TailMiddle", translation: [0, -0.15, -0.05], children: [23] }, { name: "TailTip", translation: [0, -0.15, 0] });
  (document.nodes[0]!.children as number[]).push(21);
  document.extensions.VRMC_springBone.springs.push({ joints: [{ node: 21 }, { node: 22 }, { node: 23 }] });
  const runtime = new VrmRuntime(); await runtime.load(encodeGlb(document, bin));
  assert.deepEqual(runtime.springNodes.map(chain => chain.map(node => node.name)),
    [["HairRoot", "HairMiddle", "HairTip"], ["TailRoot", "TailMiddle", "TailTip"]]);
  runtime.dispose();
});

test("missing optional expressions stay absent and cannot be selected", async () => {
  const { document, bin } = fixtureDocument();
  Reflect.deleteProperty(document.extensions.VRMC_vrm, "expressions");
  const data = encodeGlb(document, bin);
  const caps = inspectVrm(data);
  assert.equal(caps.facialDetail, "absent"); assert.equal(caps.gaze, "absent");
  const runtime = new VrmRuntime(); await runtime.load(data);
  assert.throws(() => runtime.configure({ ...selection, gaze: false }, revision, "coefficients"), /absent/);
  runtime.configure({ ...selection, gaze: false, mappings: [] }, revision, "coefficients");
  runtime.dispose();
});

test("empty and preset-only expressions are honestly classified", () => {
  const { document, bin } = fixtureDocument();
  Reflect.deleteProperty(document.extensions.VRMC_vrm.expressions, "custom");
  assert.equal(inspectVrm(encodeGlb(document, bin)).facialDetail, "preset-only");
  for (const value of Object.values(document.extensions.VRMC_vrm.expressions.preset)) value.morphTargetBinds = [];
  assert.equal(inspectVrm(encodeGlb(document, bin)).facialDetail, "absent");
});

test("GLB header, truncation, version, size and chunk budgets", () => {
  for (const size of [0, 12, 19]) assert.throws(() => inspectVrm(new ArrayBuffer(size)));
  assert.throws(() => inspectVrm(new ArrayBuffer(LIMITS.fileBytes + 1)), /Expected local/);
  const badMagic = fixture(); new DataView(badMagic).setUint32(0, 0, true); assert.throws(() => inspectVrm(badMagic), /version 2/);
  const badVersion = fixture(); new DataView(badVersion).setUint32(4, 1, true); assert.throws(() => inspectVrm(badVersion), /version 2/);
  const badChunk = fixture(); new DataView(badChunk).setUint32(12, 0xffffffff, true); assert.throws(() => inspectVrm(badChunk), /truncated/);
  assert.throws(() => inspectVrm(fixture().slice(0, -4)), /declared size/);
  const { document, bin } = fixtureDocument(); document.extensions.VRMC_vrm.specVersion = "0.0";
  assert.throws(() => inspectVrm(encodeGlb(document, bin)), /VRM 1.0/);
});

test("external/data/file/script URLs, unknown extensions, prototype/depth budgets fail before import", () => {
  for (const uri of ["https://example.invalid/x", "file:///C:/secret", "../escape", "data:image/png;base64,AA", "javascript:alert(1)"]) {
    const { document, bin } = fixtureDocument(); Object.assign(document.buffers[0]!, { uri });
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /Forbidden field uri/);
  }
  for (const name of ["KHR_draco_mesh_compression", "EXT_meshopt_compression", "VRMC_node_constraint", "VRM"]) {
    const { document, bin } = fixtureDocument(); document.extensionsUsed.push(name);
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /Unsupported/);
  }
  const { document, bin } = fixtureDocument(); let extra: unknown = {};
  for (let i = 0; i < 50; i++) extra = { child: extra };
  Object.assign(document, { extras: extra });
  assert.throws(() => inspectVrm(encodeGlb(document, bin)), /depth budget/);
});

test("common but intentionally unsupported glTF features have explicit errors", () => {
  const cases: [string, (document: ReturnType<typeof fixtureDocument>["document"]) => void, RegExp][] = [
    ["sparse", d => Object.assign(d.accessors[0]!, { sparse: {} }), /Sparse/],
    ["matrix", d => Object.assign(d.nodes[0]!, { matrix: new Array(16).fill(0) }), /TRS/],
    ["animation", d => Object.assign(d, { animations: [{}] }), /VRMA/],
    ["JPEG", d => Object.assign(d, { images: [{ mimeType: "image/jpeg", bufferView: 0 }] }), /embedded PNG/],
  ];
  for (const [name, mutate, error] of cases) {
    const { document, bin } = fixtureDocument(); mutate(document);
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), error, name);
  }
});

test("geometry, hierarchy, morph and PNG expansion bounds are checked before allocation", () => {
  {
    const { document, bin } = fixtureDocument(); document.accessors[0]!.count = 500001;
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /accessor.count/);
  }
  {
    const { document, bin } = fixtureDocument(); document.accessors[0]!.byteOffset = bin.length;
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /Accessor exceeds/);
  }
  {
    const { document, bin } = fixtureDocument(); new DataView(bin.buffer).setFloat32(0, NaN, true);
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /geometry value/);
  }
  {
    const { document, bin } = fixtureDocument(); (document.nodes[2]!.children as number[]).push(0);
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /cyclic/);
  }
  {
    const { document, bin } = fixtureDocument(); document.extensions.VRMC_vrm.expressions.custom.AuthoredLip.morphTargetBinds[0]!.index = 100;
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /morph target index/);
  }
  {
    const { document, bin } = fixtureDocument();
    const data = new Uint8Array(bin.length + 36); data.set(bin);
    const png = new DataView(data.buffer, bin.length);
    [0x89504e47, 0x0d0a1a0a, 13, 0x49484452, 100000, 100000].forEach((v, i) => png.setUint32(i * 4, v));
    document.buffers[0]!.byteLength = data.length; document.bufferViews.push({ buffer: 0, byteOffset: bin.length, byteLength: 36 });
    Object.assign(document, { images: [{ mimeType: "image/png", bufferView: 1 }] });
    assert.throws(() => inspectVrm(encodeGlb(document, data)), /PNG width/);
  }
});

test("production morph binding and official mouth/blink/lookAt overrides", async () => {
  const runtime = await active();
  runtime.setPose({ gaze: [1, 1.5, 2], head: [0, 0, 0, 1] });
  runtime.applyFrame(frame(), 0); runtime.update(1 / 60);
  assert.equal(morphs(runtime)[0], 0.8); assert.equal(morphs(runtime)[2], 0.6);
  const gazeBefore = morphs(runtime).slice(3, 7);
  assert.ok(gazeBefore.some(v => v > 0));
  runtime.applyFrame(frame(1, { jawOpen: 0.8, happy: 0.5, blink: 0.6 }), 0); runtime.update(1 / 60);
  assert.equal(morphs(runtime)[0], 0); assert.equal(morphs(runtime)[1], 0.5); assert.equal(morphs(runtime)[2], 0.3);
  morphs(runtime).slice(3, 7).forEach((v, i) => assert.ok(Math.abs(v - gazeBefore[i]! / 2) < 1e-6));
  runtime.dispose();
});

test("invalid current/old turn input is inert; explicit reset clears and binds request/source/audio rate", async () => {
  const runtime = await active(); runtime.applyFrame(frame(), 0); runtime.update(0);
  const before = [...morphs(runtime)];
  for (const changed of [{ epoch: 1 }, { requestId: "44444444-4444-4444-4444-444444444444" }, { sourceId: "different" }, { sampleRate: 48000 }]) {
    assert.throws(() => runtime.applyFrame({ ...frame(1), identity: { ...identity, ...changed } }, 0), /identity/);
    runtime.update(0); assert.deepEqual(morphs(runtime), before);
  }
  assert.throws(() => runtime.applyFrame(frame(), 0), /sequence/);
  assert.throws(() => runtime.applyFrame(frame(1, { jawOpen: NaN, happy: 0, blink: 0 }), 0), /finite/);
  runtime.update(0); assert.deepEqual(morphs(runtime), before);
  const next = { ...identity, epoch: 1 };
  runtime.reset(next); assert.equal(morphs(runtime)[0], 0);
  runtime.applyFrame({ ...frame(), identity: next }, 0); runtime.update(0);
  assert.throws(() => runtime.applyFrame(frame(2), 0), /identity/);
  runtime.update(0); assert.deepEqual(morphs(runtime), before);
  runtime.dispose();
});

test("future/late/rewinding audio frames reject; actual playback position is mandatory", async () => {
  const runtime = await active();
  assert.throws(() => runtime.applyFrame({ ...frame(), sampleOffset: 1 }, 0), /future/);
  assert.throws(() => runtime.applyFrame(frame(), 6001), /late/);
  runtime.applyFrame(frame(), 6000);
  assert.throws(() => runtime.applyFrame(frame(1), 5999), /rewind/);
  runtime.dispose();
});

test("ownership/range/unsupported selection and unknown channels are explicit, transactional failures", async () => {
  const runtime = await active(); runtime.applyFrame(frame(), 0); runtime.update(0);
  const before = [...morphs(runtime)];
  assert.throws(() => runtime.configure({ ...selection, mappings: [...selection.mappings, selection.mappings[0]!] }, revision, "coefficients"), /Duplicate/);
  assert.throws(() => runtime.configure({ ...selection, faceMode: "reduced-vowel-jaw-only" }, revision, "coefficients"), /Reduced mode/);
  assert.throws(() => runtime.configure({ ...selection, mappings: [{ ...selection.mappings[0]!, maximum: 2 }] }, revision, "coefficients"), /maximum/);
  assert.throws(() => runtime.configure({ ...selection, mappings: [{ ...selection.mappings[0]!, expression: "lookUp" }] }, revision, "coefficients"), /exclusively/);
  assert.throws(() => runtime.applyFrame({ ...frame(1), coefficients: { invented: 1 } }, 0), /selected channels/);
  assert.throws(() => runtime.setPose({ head: [0, 0, 0, 0] }), /normalized/);
  assert.throws(() => runtime.update(Infinity), /finite/);
  runtime.update(0); assert.deepEqual(morphs(runtime), before);
  runtime.dispose();
});

test("custom mouth mappings participate in VRM overrideMouth; reduced mode is explicit", async () => {
  const runtime = await active();
  runtime.configure({ ...selection, mappings: [
    { ...selection.mappings[0]!, expression: "AuthoredLip" }, selection.mappings[1]!,
  ] }, revision, "coefficients");
  runtime.reset(identity);
  runtime.applyFrame({ ...frame(), coefficients: { jawOpen: 0.9, happy: 0.5 } }, 0); runtime.update(0);
  assert.equal(morphs(runtime)[7], 0);
  runtime.configure({ ...selection, faceMode: "reduced-vowel-jaw-only", mappings: [selection.mappings[0]!] }, revision, "coefficients");
  runtime.reset(identity); runtime.applyFrame({ ...frame(), coefficients: { jawOpen: 0.4 } }, 0); runtime.update(0);
  assert.equal(morphs(runtime)[0], 0.4);
  runtime.dispose();
});

test("reload/dispose release real Three geometry; failed reload preserves current model", async () => {
  const runtime = await active(); runtime.applyFrame(frame(), 0); runtime.update(0);
  let releases = 0;
  runtime.scene!.traverse(node => { if (node instanceof THREE.Mesh) node.geometry.addEventListener("dispose", () => releases++); });
  await assert.rejects(runtime.load(new ArrayBuffer(4)));
  assert.equal(morphs(runtime)[0], 0.8); assert.equal(releases, 0);
  await runtime.load(fixture()); assert.ok(releases > 0);
  assert.throws(() => runtime.applyFrame(frame(), 0), /Configure/);
  runtime.dispose(); runtime.dispose();
  assert.equal(runtime.scene, undefined); assert.equal(runtime.capabilities, undefined);
  await assert.rejects(runtime.load(fixture()), /disposed/);
  assert.throws(() => runtime.update(0), /disposed/);
});

test("dispose during async import cancels load instead of resurrecting a model", async () => {
  const runtime = new VrmRuntime(); const load = runtime.load(fixture()); runtime.dispose();
  await assert.rejects(load, /canceled/); assert.equal(runtime.isLoaded, false);
});

test("overlapping underlying morph writers are rejected even with different expression names", async () => {
  const { document, bin } = fixtureDocument();
  document.extensions.VRMC_vrm.expressions.custom.AuthoredLip.morphTargetBinds[0]!.index = 0;
  const runtime = new VrmRuntime(); await runtime.load(encodeGlb(document, bin));
  assert.throws(() => runtime.configure({ ...selection, mappings: [
    selection.mappings[0]!, { ...selection.mappings[0]!, expression: "AuthoredLip", channel: "another" },
  ] }, revision, "coefficients"), /competing/);
  runtime.dispose();
});

test("direct composed targets apply affine mapping exactly once and reject obsolete revisions inertly", async () => {
  const runtime = await active();
  const revision = { modelRevision: "model-sha256-v1", mappingRevision: "mapping-v1" };
  runtime.configure({ ...selection, mappings: [{ ...selection.mappings[0]!, minimum: 0.2, maximum: 0.8 }] }, revision);
  runtime.reset(identity);
  const input = { identity, sequence: 0, sampleOffset: 0, ...revision, parameters: { aa: 0.35 } };
  runtime.applyComposedParameters(input, 0); runtime.update(0);
  assert.equal(morphs(runtime)[0], 0.35);
  for (const changed of [{ modelRevision: "old-model" }, { mappingRevision: "old-map" }])
    assert.throws(() => runtime.applyComposedParameters({ ...input, sequence: 1, ...changed }, 0), /revision/);
  assert.throws(() => runtime.applyComposedParameters({ ...input, sequence: 1, parameters: { aa: 1.1 } }, 0), /finite/);
  assert.throws(() => runtime.applyComposedParameters({ ...input, sequence: 1, parameters: { invented: 0.5 } }, 0), /selected channels/);
  assert.throws(() => runtime.applyComposedParameters({ ...input, sequence: 1, identity: { ...identity, sourceId: "foreign" } }, 0), /identity/);
  runtime.update(0); assert.equal(morphs(runtime)[0], 0.35);
  runtime.applyComposedParameters({ ...input, sequence: 1, parameters: { aa: 0.5 } }, 0);
  runtime.update(0); assert.equal(morphs(runtime)[0], 0.5);
  runtime.dispose();
});

test("stop neutralizes selected head/gaze/physics and rejects further data until trusted reset", async () => {
  const runtime = await active(); runtime.applyFrame(frame(), 0);
  runtime.setPose({ gaze: [1, 1.5, 2], head: [0, Math.sin(0.1), 0, Math.cos(0.1)] }); runtime.update(1 / 60);
  runtime.stop();
  const before = runtime.scene!.getObjectByName("HairMiddle")!.quaternion.clone();
  runtime.update(0.1);
  assert.equal(morphs(runtime)[0], 0);
  assert.ok(before.equals(runtime.scene!.getObjectByName("HairMiddle")!.quaternion));
  assert.throws(() => runtime.applyFrame(frame(1), 0), /reset/);
  runtime.dispose();
});

test("overlapping buffer views count separately against actual loader copy budget", () => {
  const { document, bin } = fixtureDocument();
  const padded = new Uint8Array(8 * 1024 * 1024); padded.set(bin);
  document.buffers[0]!.byteLength = padded.length;
  document.bufferViews = document.accessors.map(() => ({ buffer: 0, byteOffset: 0, byteLength: padded.length }));
  document.accessors.forEach((accessor, i) => { accessor.bufferView = i; });
  assert.throws(() => inspectVrm(encodeGlb(document, padded)), /Aggregate bufferView copy budget/);
});

test("tangent-only morphs rejected instead of falsely advertising usable facial controls", async () => {
  const { document, bin } = fixtureDocument();
  for (const target of document.meshes[0]!.primitives[0]!.targets) {
    Object.assign(target, { TANGENT: target.POSITION }); Reflect.deleteProperty(target, "POSITION");
  }
  const bytes = encodeGlb(document, bin);
  assert.throws(() => inspectVrm(bytes), /Unsupported morph attribute/);
  await assert.rejects(loadLocalVrm(bytes), /Unsupported morph attribute/);
});

test("bone gaze partial overrides scale saturated mapped eye rotation, not input angle", async () => {
  const { document, bin } = fixtureDocument();
  const lookAt = document.extensions.VRMC_vrm.lookAt;
  lookAt.type = "bone";
  for (const range of [lookAt.rangeMapHorizontalInner, lookAt.rangeMapHorizontalOuter, lookAt.rangeMapVerticalDown, lookAt.rangeMapVerticalUp]) {
    range.inputMaxValue = 10; range.outputScale = 30;
  }
  const runtime = new VrmRuntime(); await runtime.load(encodeGlb(document, bin)); runtime.configure(selection, revision, "coefficients"); runtime.reset(identity);
  runtime.setPose({ gaze: [2, 1.5, 2] });
  const angles: number[] = [];
  for (const [sequence, happy] of [0, 0.5, 1].entries()) {
    runtime.applyFrame(frame(sequence, { jawOpen: 0, blink: 0, happy }), 0); runtime.update(0);
    angles.push(runtime.scene!.getObjectByName("leftEye")!.quaternion.angleTo(new THREE.Quaternion()));
  }
  assert.ok(angles[0]! > 0.5);
  assert.ok(Math.abs(angles[1]! - angles[0]! / 2) < 1e-6);
  assert.ok(angles[2]! < 1e-6);
  runtime.dispose();
});

test("Martlet's gestures play on the humanoid bones and return to idle", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  assert.deepEqual(runtime.gestures, [...VRM_GESTURES]);
  assert.equal(runtime.playGesture("smile"), false);
  const vrm = (runtime as unknown as { model: { humanoid: { getNormalizedBoneNode(name: string): THREE.Object3D } } }).model;
  const bone = (name: string) => vrm.humanoid.getNormalizedBoneNode(name);
  runtime.update(0.016);
  const hips = bone("hips").position.y;
  assert.equal(runtime.playGesture("wave"), true);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  assert.ok(bone("rightUpperArm").rotation.z < 0, "the right arm is raised");
  assert.ok(bone("rightLowerArm").rotation.z < -0.9, "the forearm points up");
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  assert.ok(bone("rightUpperArm").rotation.z > 1, "the arm is back down");
  assert.equal(runtime.playGesture("bounce"), true);
  for (let i = 0; i < 4; i++) runtime.update(0.05);
  assert.ok(bone("hips").position.y > hips, "the hips rise");
  for (let i = 0; i < 30; i++) runtime.update(0.05);
  assert.ok(Math.abs(bone("hips").position.y - hips) < 1e-9, "the hips settle back");
  assert.equal(runtime.playGesture("bow"), true);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  assert.ok(bone("spine").rotation.x > 0.3, "the spine bends forward");
});

/** The fixture with a chest, neck, shoulders and index, middle and thumb bones, so the idle pose has them to move. */
function relaxedFixture(): ArrayBuffer {
  const { document, bin } = fixtureDocument();
  const nodes = document.nodes as { name: string; translation?: readonly number[]; children?: number[] }[];
  const bones = document.extensions.VRMC_vrm.humanoid.humanBones as Record<string, { node: number }>;
  const add = (name: string, parent: number, translation: readonly number[]) => {
    nodes.push({ name, translation, children: [] });
    nodes[parent]!.children!.push(nodes.length - 1);
    bones[name] = { node: nodes.length - 1 };
    return nodes.length - 1;
  };
  const move = (child: number, from: number, to: number, translation: readonly number[]) => {
    nodes[from]!.children = nodes[from]!.children!.filter(c => c !== child);
    nodes[to]!.children!.push(child);
    nodes[child]!.translation = translation;
  };
  const spine = bones.spine!.node;
  const chest = add("chest", spine, [0, 0.1, 0]);
  const neck = add("neck", chest, [0, 0.15, 0]);
  move(bones.head!.node, spine, neck, [0, 0.15, 0]);
  for (const [side, sign] of [["left", 1], ["right", -1]] as const) {
    const shoulder = add(`${side}Shoulder`, chest, [0.05 * sign, 0.15, 0]);
    move(bones[`${side}UpperArm`]!.node, spine, shoulder, [0.15 * sign, 0.05, 0]);
    for (const finger of [["IndexProximal", "IndexIntermediate", "IndexDistal"], ["MiddleProximal", "MiddleIntermediate", "MiddleDistal"],
      ["ThumbMetacarpal", "ThumbProximal", "ThumbDistal"]]) {
      let parent = bones[`${side}Hand`]!.node;
      for (const part of finger) parent = add(`${side}${part}`, parent, [0.03 * sign, 0, 0]);
    }
  }
  return encodeGlb(document, bin);
}

test("a breath goes in quickly, out more slowly, and rests before the next", () => {
  assert.equal(breathing(0), 0);
  assert.equal(breathing(0.38), 1);
  assert.ok(breathing(0.2) > 0.4 && breathing(0.2) < 0.6, "half in, halfway through breathing in");
  assert.ok(breathing(0.5) > breathing(0.7) && breathing(0.7) > breathing(0.85), "breathing out");
  assert.equal(breathing(0.9), 0, "resting");
  assert.equal(breathing(2.38), 1, "every breath the same");
  assert.ok(60 / BREATH_SECONDS > 12 && 60 / BREATH_SECONDS < 16, "a calm 12 to 16 breaths a minute");
});

test("the idle pose hangs the arms relaxed, curls the fingers and breathes", async () => {
  const runtime = new VrmRuntime(); await runtime.load(relaxedFixture()); runtime.startIdle();
  const vrm = (runtime as unknown as { model: { humanoid: { getNormalizedBoneNode(name: string): THREE.Object3D } } }).model;
  const bone = (name: string) => vrm.humanoid.getNormalizedBoneNode(name);
  runtime.update(0);
  // The first frame is the rest pose a still picture shows: breathed out, no sway.
  const rest = runtime.idleReading!;
  assert.deepEqual([rest.idle, rest.breathing.inhale, rest.sway], [true, 0, 0]);
  for (const side of ["left", "right"] as const) {
    const arm = rest.arms[side]!;
    assert.ok(arm.fromDown > 10 && arm.fromDown < 20, `the ${side} arm hangs close to the body, not out in an A-pose: ${arm.fromDown}`);
    assert.ok(arm.elbow > 10 && arm.elbow < 25, `the ${side} elbow bends softly: ${arm.elbow}`);
    assert.ok(rest.curl[side]! > 45, `the ${side} fingers curl: ${rest.curl[side]}`);
  }
  assert.ok(bone("leftUpperArm").rotation.x < 0 && bone("rightUpperArm").rotation.x < 0, "both arms hang a little forward");
  assert.ok(bone("leftIndexProximal").rotation.z < 0 && bone("rightIndexProximal").rotation.z > 0, "the fingers curl toward each palm");
  assert.ok(bone("leftThumbProximal").rotation.y > 0 && bone("rightThumbProximal").rotation.y < 0, "the thumbs lie in, mirrored");
  assert.ok(bone("leftHand").rotation.z < 0 && bone("rightHand").rotation.z > 0, "the wrists turn toward the thighs");

  // Through a few breaths: breathing in lifts the shoulders and opens the chest; the arms keep hanging.
  let fullest = { inhale: 0, shoulder: 0, chest: 0, arm: 0 }, sways = new Set<number>(), perMinute = 0;
  for (let i = 0; i < 100; i++) {
    runtime.update(0.1);
    const reading = runtime.idleReading!;
    sways.add(reading.sway);
    perMinute = reading.breathing.perMinute;
    if (reading.breathing.inhale > fullest.inhale)
      fullest = { inhale: reading.breathing.inhale, shoulder: bone("leftShoulder").rotation.z, chest: bone("chest").rotation.x,
        arm: reading.arms.left!.fromDown };
  }
  assert.ok(fullest.inhale > 0.95, `breathes in fully: ${fullest.inhale}`);
  assert.ok(fullest.shoulder > 0.06 && bone("rightShoulder").rotation.z <= 0, `the shoulders rise: ${fullest.shoulder}`);
  assert.ok(fullest.chest < -0.005, "the chest opens");
  assert.ok(Math.abs(fullest.arm - rest.arms.left!.fromDown) < 3, `the arm still hangs: ${fullest.arm}`);
  assert.ok(perMinute > 12 && perMinute < 17, `about 14 breaths a minute: ${perMinute}`);
  assert.ok(sways.size > 5 && Math.max(...sways) < 1 && Math.min(...sways) > -1, "a slow sway of less than a degree");
  assert.ok(Math.abs(bone("spine").rotation.x) < 1e-12, "breathing leaves the spine's bend to gestures");

  // A wave opens the right hand only; the left one stays relaxed.
  assert.equal(runtime.playGesture("wave"), true);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  assert.ok(Math.abs(bone("rightIndexProximal").rotation.z) < 0.01, "the waving hand is open");
  assert.ok(bone("leftIndexProximal").rotation.z < -0.2, "the other hand stays relaxed");
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  assert.ok(bone("rightIndexProximal").rotation.z > 0.2, "and the waving hand relaxes again");
  runtime.dispose();

  // Without shoulder bones a breath still swings the arms out a little, never in.
  const plain = new VrmRuntime(); await plain.load(fixture()); plain.startIdle();
  plain.update(0);
  const hanging = plain.idleReading!.arms.left!.fromDown;
  let full = { inhale: 0, fromDown: hanging };
  for (let i = 0; i < 60; i++) {
    plain.update(0.1);
    const reading = plain.idleReading!;
    if (reading.breathing.inhale > full.inhale) full = { inhale: reading.breathing.inhale, fromDown: reading.arms.left!.fromDown };
  }
  assert.ok(full.inhale > 0.95 && full.fromDown > hanging && full.fromDown < hanging + 2,
    `the arm swings out a little at a full breath: ${hanging} to ${full.fromDown}`);
  plain.dispose();
});

test("blush shows an authored cheek expression, held until released, and the face is found from the eye bones", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  assert.ok(runtime.gestures.includes("blush"));
  assert.equal(runtime.playGesture("blush"), false, "no blush of its own: the page draws one");
  const face = runtime.faceGeometry()!;
  assert.ok(face.eyeRight.x > face.eyeLeft.x, "the character's left eye is on the viewer's right");
  assert.ok(face.cheekRight.x > face.center.x && face.cheekLeft.x < face.center.x && face.cheekLeft.y < face.center.y);
  assert.ok(Math.abs(face.width - 0.08 * 2.3) < 1e-6 && face.forward.z > 0.99);
  runtime.dispose();

  const { document, bin } = fixtureDocument();
  (document.extensions.VRMC_vrm.expressions.custom as Record<string, unknown>).CheekRed = { morphTargetBinds: [{ node: 17, index: 7, weight: 1 }] };
  const own = new VrmRuntime(); await own.load(encodeGlb(document, bin)); own.startIdle();
  const vrm = (own as unknown as { model: { expressionManager: { getValue(name: string): number } } }).model;
  assert.equal(own.playGesture("blush", true), true);
  for (let i = 0; i < 100; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! > 0.99, "held, it stays");
  assert.equal(own.setAction("happy", true), true);
  for (let i = 0; i < 20; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! > 0.99, "a passing emote leaves the held blush on");
  assert.equal(own.playGesture("blush"), true);
  for (let i = 0; i < 50; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! > 0.99 && own.gestureState.held.includes("blush"),
    "played once while held, it stays held");
  assert.equal(own.endGesture("blush"), undefined);
  for (let i = 0; i < 20; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! < 0.01);
  assert.equal(own.playGesture("blush"), true);
  for (let i = 0; i < 50; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! < 0.01, "not held, it ends by itself");
  own.dispose();
});

test("every blush level shows the model's own cheek expression fully and says which level is held", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  assert.deepEqual([...VRM_BLUSH_LEVELS], ["blush", "blush_deep", "blush_fierce"]);
  assert.deepEqual(VRM_GESTURES.slice(-2), ["blush_deep", "blush_fierce"]);
  for (const level of VRM_BLUSH_LEVELS) {
    assert.ok(runtime.gestures.includes(level), level);
    assert.equal(runtime.playGesture(level, true), false, `${level}: no blush of its own, the page draws it`);
  }
  runtime.dispose();

  const { document, bin } = fixtureDocument();
  (document.extensions.VRMC_vrm.expressions.custom as Record<string, unknown>).CheekRed = { morphTargetBinds: [{ node: 17, index: 7, weight: 1 }] };
  const own = new VrmRuntime(); await own.load(encodeGlb(document, bin)); own.startIdle();
  const vrm = (own as unknown as { model: { expressionManager: { getValue(name: string): number } } }).model;
  assert.deepEqual(VRM_BLUSH_LEVELS.map(level => VRM_HOLD_PARTS[level]), [["cheeks"], ["cheeks"], ["cheeks"]]);
  assert.equal(own.playGesture("eyes_up", true), true);
  assert.equal(own.playGesture("blush_deep", true), true);
  for (let i = 0; i < 30; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! > 0.99, "the model's own blush, fully, under Martlet's drawing");
  assert.deepEqual(own.gestureState, { held: ["eyes_up", "blush_deep"] }, "a blush level layers with the eyes");
  // The page lets the level before go as it holds the next: the cheeks stay flushed.
  own.endGesture("blush_deep");
  assert.equal(own.playGesture("blush_fierce", true), true);
  for (let i = 0; i < 5; i++) {
    own.update(0.1);
    assert.ok(vrm.expressionManager.getValue("CheekRed")! > 0.99);
  }
  own.endGesture("blush_deep");
  assert.deepEqual(own.gestureState, { held: ["eyes_up", "blush_fierce"] }, "ending another level changes nothing");
  own.endGesture("blush_fierce");
  own.endGesture("eyes_up");
  assert.deepEqual(own.gestureState, { held: [] });
  for (let i = 0; i < 20; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! < 0.01);
  assert.equal(own.playGesture("blush_fierce"), true);
  for (let i = 0; i < 50; i++) own.update(0.1);
  assert.ok(vrm.expressionManager.getValue("CheekRed")! < 0.01, "not held, it ends by itself");
  own.dispose();
});

test("a turned head shows the near cheek wider and the far one narrower, then out of sight", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture());
  const vrm = (runtime as unknown as { model: { humanoid: { getNormalizedBoneNode(name: string): THREE.Object3D } } }).model;
  const camera = new THREE.PerspectiveCamera(30, 1, 0.01, 100);
  const cheeks = (yaw: number) => {
    vrm.humanoid.getNormalizedBoneNode("head").rotation.set(0, yaw, 0);
    const face = runtime.faceGeometry()!;
    camera.position.set(face.center.x, face.center.y, face.center.z + 1);
    camera.lookAt(face.center);
    camera.updateMatrixWorld();
    const project = (v: THREE.Vector3) => { const n = v.clone().project(camera); return { x: (n.x + 1) * 250, y: (1 - n.y) * 250 }; };
    const down = face.up.clone().negate();
    return { left: cheekFrame(face.cheekLeft, face.cheekLeftAcross, down, face.cheekLeftNormal, face.width, camera.position, project),
      right: cheekFrame(face.cheekRight, face.cheekRightAcross, down, face.cheekRightNormal, face.width, camera.position, project) };
  };
  const across = (frame: { right: { x: number; y: number } }) => Math.hypot(frame.right.x, frame.right.y);
  const front = cheeks(0);
  assert.ok(Math.abs(across(front.left) / across(front.right) - 1) < 1e-3 && front.left.visible === 1 && front.right.visible === 1);
  assert.ok(front.left.down.y > 0 && Math.abs(front.left.down.x) < 1e-6, "down the cheek is down the canvas");
  // Turned toward the viewer's right: the character's right cheek (the viewer's left) comes round to face the camera.
  const turned = cheeks(0.6);
  assert.ok(across(turned.left) > 1.15 * across(front.left), `the near cheek widens: ${across(turned.left)}`);
  assert.ok(across(turned.right) < 0.5 * across(front.right), `the far cheek narrows: ${across(turned.right)}`);
  const away = cheeks(1);
  assert.ok(away.left.visible === 1 && away.right.visible < 0.05, `the far cheek turns out of sight: ${away.right.visible}`);
  runtime.dispose();
});

test("voice emotes move the head, spine, shoulders and arms and return to idle", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  const vrm = (runtime as unknown as { model: { humanoid: { getNormalizedBoneNode(name: string): THREE.Object3D } } }).model;
  const bone = (name: string) => vrm.humanoid.getNormalizedBoneNode(name);
  runtime.update(0.016);
  const rest = bone("head").rotation.x;
  assert.equal(runtime.playGesture("gasp"), true);
  for (let i = 0; i < 10; i++) runtime.update(0.05);
  assert.ok(bone("head").rotation.x < rest - 0.08, "the head goes back");
  assert.ok(bone("spine").rotation.x < -0.1, "the body leans back");
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  assert.ok(Math.abs(bone("spine").rotation.x) < 1e-9, "the spine settles");
  assert.equal(runtime.playGesture("crying"), true);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  assert.ok(bone("head").rotation.x > rest + 0.1, "the head bows");
  assert.ok(bone("spine").rotation.x > 0.1, "the body curls forward");
  assert.equal(runtime.playGesture("dramatic"), true);
  for (let i = 0; i < 24; i++) runtime.update(0.05);
  assert.ok(bone("leftUpperArm").rotation.z > -0.3 && bone("rightUpperArm").rotation.z < 0.3, "the arms open out");
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  assert.ok(bone("rightUpperArm").rotation.z > 1, "the arms come back down");
  assert.equal(runtime.playGesture("clear_throat"), true);
  runtime.dispose();
});

test("touch and mood gestures use the face presets and holdable ones stay until ended", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  const vrm = (runtime as unknown as { model: { humanoid: { getNormalizedBoneNode(name: string): THREE.Object3D };
    expressionManager: { getValue(name: string): number | null } } }).model;
  const bone = (name: string) => vrm.humanoid.getNormalizedBoneNode(name);
  const face = (name: string) => vrm.expressionManager.getValue(name) ?? 0;
  runtime.update(0.016);
  const rest = bone("head").rotation.x;
  assert.equal(runtime.playGesture("giggle"), true);
  assert.deepEqual(runtime.gestureState, { playing: "giggle", held: [] });
  for (let i = 0; i < 10; i++) runtime.update(0.05);
  assert.ok(face("happy") > 0.7, "giggles with a happy face");
  for (let i = 0; i < 30; i++) runtime.update(0.05);
  assert.equal(face("happy"), 0, "the face settles back");
  assert.deepEqual(runtime.gestureState, { held: [] });
  assert.equal(runtime.playGesture("flinch"), true);
  runtime.update(0.05); runtime.update(0.05);
  assert.ok(bone("head").rotation.x < rest - 0.05 && bone("spine").rotation.x < -0.1, "jerks back");
  for (let i = 0; i < 30; i++) runtime.update(0.05);
  assert.ok(Math.abs(bone("spine").rotation.x) < 1e-6, "and settles");

  assert.equal(runtime.playGesture("drowsy", true), true);
  assert.deepEqual(runtime.gestureState, { held: ["drowsy"] });
  for (let i = 0; i < 300; i++) runtime.update(0.05);
  assert.ok(face("blink") >= 0.5 && bone("head").rotation.x > rest + 0.05, "still drowsy after 15 seconds");
  assert.equal(runtime.playGesture("nod"), true);
  assert.deepEqual(runtime.gestureState, { playing: "nod", held: ["drowsy"] });
  for (let i = 0; i < 30; i++) runtime.update(0.05);
  assert.deepEqual(runtime.gestureState, { held: ["drowsy"] }, "the nod played on top and the held pose resumes");
  assert.ok(face("blink") >= 0.5);
  assert.equal(runtime.playGesture("shy", true), true);
  for (let i = 0; i < 30; i++) runtime.update(0.05);
  assert.deepEqual(runtime.gestureState, { held: ["shy"] }, "shy moves the eyes and head too, so drowsy lets go");
  assert.ok(Math.abs(bone("spine").rotation.y) > 0.1, "shy turns the body away");
  assert.ok(face("blink") < 0.05, "crossfaded from drowsy");
  runtime.endGesture("shy");
  assert.deepEqual(runtime.gestureState, { held: [] });
  for (let i = 0; i < 30; i++) runtime.update(0.05);
  assert.ok(Math.abs(bone("spine").rotation.y) < 1e-9, "the body comes back once let go");
  assert.equal(runtime.playGesture("wink", true), true);
  assert.deepEqual(runtime.gestureState, { playing: "wink", held: [] }, "a gesture that can't be held plays once");
  runtime.dispose();
});

// The fixture with VRoid-style eyes: an iris quad (material EyeIris) in front of each eye bone, riding it, and both eye
// whites (material EyeWhite) on the head, which the blink expression closes toward their lower part.
function eyeFixture({ iris = true, white = true } = {}): ArrayBuffer {
  const { document, bin } = fixtureDocument();
  const doc = document as unknown as { nodes: Record<string, unknown>[]; meshes: unknown[]; accessors: unknown[]; bufferViews: unknown[];
    buffers: { byteLength: number }[]; materials?: unknown[]; extensions: { VRMC_vrm: { expressions: { preset: Record<string, { morphTargetBinds: unknown[] }> } } } };
  const quad = (x: number, y: number, z: number, rx: number, ry: number) => [x - rx, y - ry, z, x + rx, y - ry, z, x + rx, y + ry, z, x - rx, y + ry, z];
  const irisQuad = quad(0, 0, 0, 0.012, 0.014);
  const whites = [...quad(-0.04, 0, 0, 0.022, 0.016), ...quad(0.04, 0, 0, 0.022, 0.016)];
  const closing = whites.map((value, i) => i % 3 === 1 ? -0.006 - value : 0);
  const floats = new Float32Array([...irisQuad, ...whites, ...closing]);
  const indices = new Uint16Array([0, 1, 2, 0, 2, 3, 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
  const bytes = new Uint8Array(bin.length + floats.byteLength + indices.byteLength);
  bytes.set(bin); bytes.set(new Uint8Array(floats.buffer), bin.length);
  bytes.set(new Uint8Array(indices.buffer), bin.length + floats.byteLength);
  doc.buffers[0]!.byteLength = bytes.length;
  doc.bufferViews.push({ buffer: 0, byteOffset: bin.length, byteLength: floats.byteLength },
    { buffer: 0, byteOffset: bin.length + floats.byteLength, byteLength: indices.byteLength });
  const vec3 = (offset: number, count: number, values: number[]) => ({ bufferView: 1, byteOffset: offset, componentType: 5126, count, type: "VEC3",
    min: [0, 1, 2].map(c => Math.min(...values.filter((_, i) => i % 3 === c))), max: [0, 1, 2].map(c => Math.max(...values.filter((_, i) => i % 3 === c))) });
  const first = doc.accessors.length;
  doc.accessors.push(vec3(0, 4, irisQuad), vec3(48, 8, whites), vec3(144, 8, closing),
    { bufferView: 2, byteOffset: 0, componentType: 5123, count: 6, type: "SCALAR" },
    { bufferView: 2, byteOffset: 12, componentType: 5123, count: 12, type: "SCALAR" });
  // A wink of the character's right eye, for the hinted opening.
  doc.extensions.VRMC_vrm.expressions.preset.blinkRight = { morphTargetBinds: [{ node: 17, index: 2, weight: 1 }] };
  doc.materials = [{ name: "N00_000_00_EyeIris_00_EYE (Instance)" }, { name: "N00_000_00_EyeWhite_00_EYE (Instance)" }];
  doc.meshes.push({ name: "Iris", primitives: [{ attributes: { POSITION: first }, indices: first + 3, material: 0 }] },
    { name: "Whites", primitives: [{ attributes: { POSITION: first + 1 }, indices: first + 4, material: 1, targets: [{ POSITION: first + 2 }] }] });
  // The character's right eye (node 16) is on the viewer's left.
  if (iris) {
    doc.nodes.push({ name: "IrisL", mesh: 1, translation: [0, 0, 0.012] }, { name: "IrisR", mesh: 1, translation: [0, 0, 0.012] });
    ((doc.nodes[16]!.children ??= []) as number[]).push(doc.nodes.length - 2);
    ((doc.nodes[15]!.children ??= []) as number[]).push(doc.nodes.length - 1);
  }
  if (white) {
    doc.nodes.push({ name: "EyeWhites", mesh: 2, translation: [0, 0.05, 0.058] });
    (doc.nodes[2]!.children as number[]).push(doc.nodes.length - 1);
    doc.extensions.VRMC_vrm.expressions.preset.blink!.morphTargetBinds.push({ node: doc.nodes.length - 1, index: 0, weight: 1 });
  }
  return encodeGlb(document, bytes);
}

// A camera a metre in front of the face, drawing 500 by 500 pixels.
function viewer(runtime: VrmRuntime) {
  runtime.scene!.updateWorldMatrix(true, true);
  const face = runtime.faceGeometry()!;
  const camera = new THREE.PerspectiveCamera(30, 1, 0.01, 100);
  camera.position.set(face.center.x, face.center.y, face.center.z + 1);
  camera.lookAt(face.center);
  camera.updateMatrixWorld();
  const project = (v: THREE.Vector3) => { const n = v.clone().project(camera); return { x: (n.x + 1) * 250, y: (1 - n.y) * 250 }; };
  return { face, project, fields: () => { runtime.scene!.updateWorldMatrix(true, true); return runtime.eyeFields(runtime.faceGeometry()!, project); } };
}
const insideShape = (shape: { points: readonly { x: number; y: number }[]; triangles?: readonly number[] }, p: { x: number; y: number }) => {
  const t = shape.triangles!;
  for (let k = 0; k + 2 < t.length; k += 3) {
    const [a, b, c] = [shape.points[t[k]!]!, shape.points[t[k + 1]!]!, shape.points[t[k + 2]!]!];
    const side = (u: { x: number; y: number }, v: { x: number; y: number }) => (v.x - u.x) * (p.y - u.y) - (v.y - u.y) * (p.x - u.x);
    const s = [side(a, b), side(b, c), side(c, a)];
    if (s.every(v => v >= 0) || s.every(v => v <= 0)) return true;
  }
  return false;
};
const tall = (points: readonly { y: number }[]) => Math.max(...points.map(p => p.y)) - Math.min(...points.map(p => p.y));

test("a VRM's eyes: the bones place each iris, the EyeIris mesh sizes it and the EyeWhite mesh is its opening, closing on a blink", async () => {
  const runtime = new VrmRuntime(); await runtime.load(eyeFixture());
  assert.equal(runtime.eyesFrom, "bones");
  const { project, fields } = viewer(runtime);
  const rest = fields();
  assert.equal(rest.eyesFrom, "bones");
  const leftIris = project(new THREE.Vector3(-0.04, 1.55, 0.062)), across = project(new THREE.Vector3(-0.028, 1.55, 0.062));
  assert.ok(Math.hypot(rest.irisLeft!.x - leftIris.x, rest.irisLeft!.y - leftIris.y) < 1e-6, JSON.stringify(rest.irisLeft));
  assert.ok(Math.abs(rest.irisLeft!.rx - (across.x - leftIris.x)) < 1e-3 && rest.irisLeft!.ry > rest.irisLeft!.rx, "sized by its mesh");
  assert.ok(rest.irisRight!.x > rest.irisLeft!.x, "the character's left eye is on the viewer's right");
  for (const [iris, shape] of [[rest.irisLeft!, rest.eyeLeftShape!], [rest.irisRight!, rest.eyeRightShape!]] as const) {
    assert.deepEqual([shape.points.length, shape.triangles!.length], [4, 6]);
    assert.ok(insideShape(shape, iris));
  }
  assert.ok(Math.hypot(rest.eyeLeft!.x - leftIris.x, rest.eyeLeft!.y - leftIris.y) < 1e-6, "the eye's middle is its iris at rest");

  // A bone look-at turns the eye: the iris goes with it, the eye's middle doesn't.
  const vrm = (runtime as unknown as { model: { humanoid: { getRawBoneNode(name: string): THREE.Object3D };
    expressionManager: { setValue(name: string, value: number): void } } }).model;
  vrm.humanoid.getRawBoneNode("rightEye").rotation.set(0, 0.3, 0);
  const looking = fields();
  assert.ok(looking.irisLeft!.x > rest.irisLeft!.x + 1, `turned toward the viewer's right: ${looking.irisLeft!.x} vs ${rest.irisLeft!.x}`);
  assert.ok(Math.hypot(looking.eyeLeft!.x - rest.eyeLeft!.x, looking.eyeLeft!.y - rest.eyeLeft!.y) < 1e-6);
  vrm.humanoid.getRawBoneNode("rightEye").rotation.set(0, 0, 0);

  vrm.expressionManager.setValue("blink", 1);
  runtime.update(0);
  const closed = fields();
  assert.ok(tall(closed.eyeLeftShape!.points) < 0.01 && tall(rest.eyeLeftShape!.points) > 10, "the blink closes the eye white");
  vrm.expressionManager.setValue("blink", 0);

  // Martlet's own eyes_up turns the eye bones: the irises look up with them, inside their openings.
  runtime.startIdle();
  assert.equal(runtime.playGesture("eyes_up", true), true);
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  const up = fields();
  assert.ok(up.irisLeft!.y < rest.irisLeft!.y - 1 && up.irisRight!.y < rest.irisRight!.y - 1, `${up.irisLeft!.y} vs ${rest.irisLeft!.y}`);
  runtime.dispose();
});

test("a VRM's eyes without iris or eye-white meshes come from vision, or are left to the drawings' own estimate", async () => {
  const runtime = new VrmRuntime(); await runtime.load(eyeFixture({ iris: true, white: false }));
  assert.equal(runtime.eyesFrom, "estimate", "the iris is known but not the opening");
  const { fields } = viewer(runtime);
  const partial = fields();
  assert.ok(partial.irisLeft && partial.eyeLeftShape === undefined);
  const hint = { left: { iris: { x: -0.22, y: 0.02, r: 0.07 }, eye: { x: -0.22, y: 0, rx: 0.12, ry: 0.09 } },
    right: { iris: { x: 0.22, y: 0.02, r: 0.07 }, eye: { x: 0.22, y: 0, rx: 0.12, ry: 0.09 } } };
  assert.equal(runtime.setEyeHint(hint), "vision");
  const hinted = fields();
  assert.equal(hinted.eyeLeftShape!.points.length, 24);
  assert.ok(Math.abs(hinted.irisLeft!.rx - partial.irisLeft!.rx) < 1e-9, "the mesh's iris wins over the hint's");
  const blinkRight = (runtime as unknown as { model: { expressionManager: { setValue(name: string, value: number): void } } }).model;
  blinkRight.expressionManager.setValue("blinkRight", 1);
  runtime.update(0);
  const winking = fields();
  assert.ok(tall(winking.eyeLeftShape!.points) < 0.01 && tall(winking.eyeRightShape!.points) > 5,
    "blinkRight closes the character's right eye: the one on the viewer's left");
  runtime.dispose();

  const plain = new VrmRuntime(); await plain.load(fixture());
  assert.equal(plain.eyesFrom, "estimate");
  const bare = viewer(plain);
  const none = bare.fields();
  for (const key of ["irisLeft", "irisRight", "eyeLeftShape", "eyeRightShape", "eyeLeft"] as const) assert.equal(none[key], undefined, key);
  assert.equal(plain.setEyeHint({ left: { iris: { x: 9, y: 0, r: 0.1 }, eye: { x: 0, y: 0, rx: 0.1, ry: 0.1 } } }), "estimate");
  assert.equal(plain.setEyeHint(hint), "vision");
  const seen = bare.fields();
  // The iris sits 0.02 face widths below its eye's middle, the eye in front of its bone.
  const width = bare.face.width, bone = bare.project(new THREE.Vector3(-0.04, 1.55, 0.05 + 0.2 * 0.08));
  assert.ok(Math.hypot(seen.eyeLeft!.x - bone.x, seen.eyeLeft!.y - bone.y) < 1e-6, JSON.stringify(seen.eyeLeft));
  const below = bare.project(new THREE.Vector3(-0.04, 1.55 - 0.02 * width, 0.05 + 0.2 * 0.08));
  assert.ok(Math.hypot(seen.irisLeft!.x - below.x, seen.irisLeft!.y - below.y) < 1e-6, JSON.stringify(seen.irisLeft));
  assert.equal(plain.setEyeHint(undefined), "estimate");
  plain.dispose();

  // Without eye bones the hint places the eyes on the face found from the head bone.
  const { document, bin } = fixtureDocument();
  const bones = document.extensions.VRMC_vrm.humanoid.humanBones as Record<string, unknown>;
  delete bones.leftEye; delete bones.rightEye;
  const boneless = new VrmRuntime(); await boneless.load(encodeGlb(document, bin));
  assert.equal(boneless.eyesFrom, "estimate");
  assert.equal(boneless.setEyeHint(hint), "vision");
  const view = viewer(boneless), placed = view.fields();
  const expected = view.project(view.face.center.clone().addScaledVector(view.face.side, -0.22 * view.face.width));
  assert.ok(Math.hypot(placed.eyeLeft!.x - expected.x, placed.eyeLeft!.y - expected.y) < 1e-6, JSON.stringify(placed.eyeLeft));
  assert.equal(placed.eyeRightShape!.points.length, 24);
  boneless.dispose();
});

test("cheeks and mouth measured by vision move a VRM's blush and mouth; without eye bones the eye line moves the middle", async () => {
  const hint = { left: { iris: { x: -0.22, y: 0.12, r: 0.07 }, eye: { x: -0.22, y: 0.1, rx: 0.12, ry: 0.09 } },
    right: { iris: { x: 0.22, y: 0.12, r: 0.07 }, eye: { x: 0.22, y: 0.1, rx: 0.12, ry: 0.09 } },
    cheekLeft: { x: -0.3, y: 0.35, r: 0.1 }, cheekRight: { x: 0.3, y: 0.35, r: 0.12 }, mouth: { x: 0, y: 0.5, r: 0.05 } };
  const close = (a: THREE.Vector3, b: THREE.Vector3) => a.distanceTo(b) < 1e-9;
  const runtime = new VrmRuntime(); await runtime.load(fixture());
  const before = runtime.faceGeometry()!;
  assert.equal(before.cheekSize, undefined);
  runtime.setEyeHint(hint);
  const face = runtime.faceGeometry()!;
  const at = (x: number, y: number, z: number) => face.center.clone().addScaledVector(face.side, x * face.width)
    .addScaledVector(face.up, y * face.width).addScaledVector(face.forward, z * face.width);
  assert.ok(close(face.cheekLeft, at(-0.3, -0.35, 0.08)) && close(face.cheekRight, at(0.3, -0.35, 0.08)), "the cheeks are where vision saw them");
  assert.ok(close(face.mouth, at(0, -0.5, 0.1)));
  assert.ok(Math.abs(face.cheekSize! - 0.11) < 1e-9);
  assert.ok(close(face.middle, face.center), "the eye bones keep the middle");
  runtime.setEyeHint(undefined);
  assert.ok(close(runtime.faceGeometry()!.cheekLeft, before.cheekLeft), "clearing the hint puts the estimate back");
  runtime.dispose();

  const { document, bin } = fixtureDocument();
  const bones = document.extensions.VRMC_vrm.humanoid.humanBones as Record<string, unknown>;
  delete bones.leftEye; delete bones.rightEye;
  const boneless = new VrmRuntime(); await boneless.load(encodeGlb(document, bin));
  const estimate = boneless.faceGeometry()!;
  boneless.setEyeHint(hint);
  const moved = boneless.faceGeometry()!;
  const shift = moved.middle.clone().sub(moved.center);
  assert.ok(Math.abs(shift.length() - 0.1 * moved.width) < 1e-9 && shift.dot(moved.up) < 0, "the middle moves down to the eye line");
  assert.ok(close(moved.top, estimate.top.clone().add(shift)), "the top of the head moves with it");
  const { fields } = viewer(boneless);
  assert.ok(fields().eyeLeft, "the hinted eyes still show");
  boneless.dispose();
});

test("held gestures that move different parts layer, and one that shares a part lets the other go", async () => {
  const { document, bin } = fixtureDocument();
  (document.extensions.VRMC_vrm.expressions.custom as Record<string, unknown>).CheekRed = { morphTargetBinds: [{ node: 17, index: 7, weight: 1 }] };
  const runtime = new VrmRuntime(); await runtime.load(encodeGlb(document, bin)); runtime.startIdle();
  const vrm = (runtime as unknown as { model: { humanoid: { getNormalizedBoneNode(name: string): THREE.Object3D };
    expressionManager: { getValue(name: string): number | null } } }).model;
  const bone = (name: string) => vrm.humanoid.getNormalizedBoneNode(name);
  const face = (name: string) => vrm.expressionManager.getValue(name) ?? 0;
  assert.deepEqual([VRM_HOLD_PARTS.eyes_up, VRM_HOLD_PARTS.mouth_open, VRM_HOLD_PARTS.blush], [["eyes"], ["mouth"], ["cheeks"]]);
  assert.ok(runtime.gestures.includes("eyes_up") && runtime.gestures.includes("mouth_open"));
  runtime.update(0.016);
  const head = bone("head").rotation.x;
  for (const name of ["eyes_up", "mouth_open", "blush"]) assert.equal(runtime.playGesture(name, true), true, name);
  assert.deepEqual(runtime.gestureState, { held: ["eyes_up", "mouth_open", "blush"] }, "the eyes, the mouth and the cheeks stay on together");
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  assert.ok(bone("leftEye").rotation.x < -0.15 && bone("rightEye").rotation.x < -0.15, "the eyes turn up");
  assert.ok(Math.abs(bone("head").rotation.x - head) < 1e-6, "the head stays where it looks");
  assert.ok(face("aa") > 0.6, "the mouth opens (with aa: this model has no oh)");
  assert.ok(face("CheekRed") > 0.99, "the cheeks blush");
  for (let i = 0; i < 20; i++) { runtime.setLipSync(0); runtime.update(0.05); }
  assert.ok(face("aa") < 0.01, "the held mouth gives way to the voice entirely: closed between its sounds");
  for (let i = 0; i < 5; i++) { runtime.setLipSync(1); runtime.update(0.05); }
  assert.ok(face("aa") > 0.9, "and the voice opens it");
  for (let i = 0; i < 18; i++) runtime.update(0.05);
  assert.ok(face("aa") < 0.01, "a pause in the speech keeps the mouth the voice's");
  for (let i = 0; i < 24; i++) runtime.update(0.05);
  assert.ok(face("aa") > 0.6, "a second after the voice stops, the held mouth opens again");
  assert.equal(runtime.playGesture("pout", true), true);
  assert.deepEqual(runtime.gestureState, { held: ["eyes_up", "pout", "blush"] }, "pout moves the mouth too, so mouth_open lets go");
  assert.equal(runtime.playGesture("look_away", true), true);
  assert.deepEqual(runtime.gestureState, { held: ["look_away", "blush"] }, "look_away moves the eyes and head, so eyes_up and pout let go");
  runtime.endGesture("look_away");
  assert.deepEqual(runtime.gestureState, { held: ["blush"] }, "ending one leaves the others on");
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  assert.ok(Math.abs(bone("leftEye").rotation.x) < 1e-6, "the eyes come back once let go");
  assert.ok(face("CheekRed") > 0.99, "the blush stays");
  runtime.dispose();

  // A model with an oh mouth holds it open with oh and leaves aa to the voice.
  (document.extensions.VRMC_vrm.expressions.preset as Record<string, unknown>).oh = { morphTargetBinds: [{ node: 17, index: 7, weight: 1 }] };
  const round = new VrmRuntime(); await round.load(encodeGlb(document, bin)); round.startIdle();
  const roundFace = (name: string) => (round as unknown as { model: { expressionManager: { getValue(name: string): number | null } } })
    .model.expressionManager.getValue(name) ?? 0;
  assert.equal(round.playGesture("mouth_open", true), true);
  for (let i = 0; i < 40; i++) round.update(0.05);
  assert.ok(roundFace("oh") > 0.6 && roundFace("aa") === 0, "oh holds the mouth open");
  round.endGesture("mouth_open");
  for (let i = 0; i < 40; i++) round.update(0.05);
  assert.ok(roundFace("oh") < 1e-6, "and closes it when let go");
  round.dispose();
});

test("an emote that blocks the mouth never holds it still while the voice speaks, and has it again a second after", async () => {
  // The fixture's happy is authored with overrideMouth "block", as VRoid's emotions often are.
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.startIdle();
  assert.equal(runtime.setAction("happy", true), true);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  let mouth = runtime.mouthReading!;
  assert.deepEqual([mouth.speaking, mouth.voice, mouth.blocked, mouth.open], [false, 0, 1, 0], "quiet, happy has the mouth");
  for (let i = 0; i < 6; i++) { runtime.setLipSync(1); runtime.update(0.05); }
  mouth = runtime.mouthReading!;
  assert.ok(mouth.speaking && mouth.blocked === 0 && mouth.open > 0.99 && mouth.level > 0.99, JSON.stringify(mouth));
  assert.ok(morphs(runtime)[0]! > 0.99 && morphs(runtime)[1]! > 0.99, "the voice opens aa fully, and happy stays on");
  for (let i = 0; i < 18; i++) runtime.update(0.05);
  mouth = runtime.mouthReading!;
  assert.ok(mouth.speaking && mouth.blocked === 0, `a pause in the speech keeps it the voice's: ${JSON.stringify(mouth)}`);
  for (let i = 0; i < 10; i++) runtime.update(0.05);
  mouth = runtime.mouthReading!;
  assert.ok(!mouth.speaking && mouth.blocked === 1 && morphs(runtime)[1]! > 0.99, `then happy has the mouth again: ${JSON.stringify(mouth)}`);
  runtime.setLipSync(1); runtime.update(0.05);
  assert.equal(runtime.mouthReading!.blocked, 0, "and the voice takes it at its first sound");
  const happy = (runtime as unknown as { model: { expressionManager: { getExpression(name: string): { overrideMouth: string } } } })
    .model.expressionManager.getExpression("happy");
  assert.equal(happy.overrideMouth, "none", "while the voice has the mouth");
  runtime.stop();
  assert.equal(happy.overrideMouth, "block", "stopping gives the model its own setting back");
  runtime.dispose();
});
