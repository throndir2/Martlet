import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { inspectVrm, LIMITS, loadLocalVrm, VrmRuntime, type PlaybackIdentity, type Selection } from "../src/index.js";
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
  assert.deepEqual(runtime.gestures, ["nod", "shake", "tilt", "bow", "sway", "wave", "shrug", "bounce"]);
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
