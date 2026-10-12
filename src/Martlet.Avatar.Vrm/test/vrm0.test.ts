import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { inspectVrm, loadLocalVrm, VRM0_PRESET_NAMES, VrmRuntime, type PlaybackIdentity, type Selection } from "../src/index.js";
import { encodeGlb, fixture, fixtureDocument, fixtureDocumentV0, fixtureV0, jpegHeader, withImage } from "./fixture.js";

const identity: PlaybackIdentity = {
  sessionId: "11111111-1111-1111-1111-111111111111", turnId: "22222222-2222-2222-2222-222222222222",
  requestId: "33333333-3333-3333-3333-333333333333", sourceId: "host-composed-a2f", epoch: 0, sampleRate: 24000,
};
const revision = { modelRevision: "fixture-v0", mappingRevision: "mapping-v1" };
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
function world(runtime: VrmRuntime, name: string): THREE.Vector3 {
  runtime.scene!.updateWorldMatrix(true, true);
  return runtime.scene!.getObjectByName(name)!.getWorldPosition(new THREE.Vector3());
}
/** Replaces globals for one test (the browser decoder), restoring them after. */
function stub(t: { after(fn: () => void): void }, values: Record<string, unknown>): void {
  for (const [key, value] of Object.entries(values)) {
    const descriptor = Object.getOwnPropertyDescriptor(globalThis, key);
    Object.defineProperty(globalThis, key, { configurable: true, writable: true, value });
    t.after(() => { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else Reflect.deleteProperty(globalThis, key); });
  }
}

test("a VRM 0.x model is inspected with VRM 1.0 expression names and its meta in the VRM 1.0 shape", () => {
  const capabilities = inspectVrm(fixtureV0());
  assert.equal(capabilities.format, "VRM0"); assert.equal(capabilities.specVersion, "0.0");
  assert.deepEqual(capabilities.expressions.map(e => [e.name, e.preset, e.usable]), [
    ["aa", true, true], ["happy", true, true], ["blink", true, true], ["lookUp", true, true], ["lookDown", true, true],
    ["lookLeft", true, true], ["lookRight", true, true], ["AuthoredLip", false, true],
  ]);
  assert.deepEqual(capabilities.expressions[0]!.morphTargets, ["17:0"], "a bind drives the morph on the node that draws the mesh");
  assert.deepEqual({ ...capabilities.meta }, {
    metaVersion: "0", name: "Synthetic VRM 0 triangle", version: "1", authors: ["Martlet tests"], licenseName: "CC0",
    avatarPermission: "everyone", allowExcessivelyViolentUsage: false, allowExcessivelySexualUsage: false, allowCommercialUsage: true,
  });
  assert.equal(capabilities.gaze, "expression"); assert.equal(capabilities.secondaryMotion, true);
  assert.equal(capabilities.facialDetail, "explicit-mapping-required"); assert.equal(capabilities.bodyPlayback, false);
  assert.ok(capabilities.humanoidBones.includes("leftEye") && capabilities.humanoidBones.includes("hips"));

  const v1 = inspectVrm(fixture());
  assert.deepEqual([v1.format, v1.specVersion, v1.meta.metaVersion, v1.meta.name, v1.meta.licenseUrl, v1.meta.avatarPermission],
    ["VRM1", "1.0", "1", "Synthetic triangle only", "https://vrm.dev/licenses/1.0/", "onlyAuthor"]);
});

test("VRM 0.x presets, thumbs and custom names map as three-vrm maps them; a repeated name is skipped as it skips it", async () => {
  assert.deepEqual({ ...VRM0_PRESET_NAMES }, {
    a: "aa", i: "ih", u: "ou", e: "ee", o: "oh", blink: "blink", blink_l: "blinkLeft", blink_r: "blinkRight", joy: "happy",
    angry: "angry", sorrow: "sad", fun: "relaxed", lookup: "lookUp", lookdown: "lookDown", lookleft: "lookLeft", lookright: "lookRight",
    neutral: "neutral",
  });
  const { document, bin } = fixtureDocumentV0();
  const vrm = document.extensions.VRM;
  vrm.blendShapeMaster.blendShapeGroups.push(
    { name: "Sorrow", presetName: "sorrow", binds: [{ mesh: 0, index: 7, weight: 50 }], materialValues: [], isBinary: false },
    { name: "Blink_L", presetName: "blink_l", binds: [{ mesh: 0, index: 2, weight: 100 }], materialValues: [], isBinary: false },
    { name: "Neutral", presetName: "neutral", binds: [], materialValues: [], isBinary: false },
    { name: "A2", presetName: "a", binds: [{ mesh: 0, index: 7, weight: 100 }], materialValues: [], isBinary: false },
    { name: "Surprised", presetName: "unknown", binds: [{ mesh: 0, index: 1, weight: 0 }],
      materialValues: [{ materialName: "FixtureFace", propertyName: "_Color", targetValue: [1, 0, 0, 1] }], isBinary: false },
  );
  const nodes = document.nodes as { name: string; translation?: number[]; children?: number[] }[];
  for (const [bone, name] of [["leftThumbProximal", "LThumb1"], ["leftThumbIntermediate", "LThumb2"]] as const) {
    nodes.push({ name, translation: [-0.03, 0, -0.02], children: [] });
    const parent = bone === "leftThumbProximal" ? 11 : nodes.length - 2;
    nodes[parent]!.children!.push(nodes.length - 1);
    vrm.humanoid.humanBones.push({ bone, node: nodes.length - 1, useDefaultValues: true } as never);
  }
  const bytes = encodeGlb(document, bin);
  const capabilities = inspectVrm(bytes);
  const named = Object.fromEntries(capabilities.expressions.map(e => [e.name, e]));
  assert.deepEqual(Object.keys(named).slice(8), ["sad", "blinkLeft", "neutral", "Surprised"], "the second a preset is skipped");
  assert.deepEqual(named.aa!.morphTargets, ["17:0"], "the first aa wins");
  assert.deepEqual([named.sad!.usable, named.neutral!.usable, named.Surprised!.usable], [true, false, false]);
  assert.deepEqual(named.Surprised!.materialTargets, ["0:color"]);
  assert.ok(capabilities.diagnostics.some(d => /repeated VRM 0.x blend shape aa/.test(d)));
  assert.ok(capabilities.humanoidBones.includes("leftThumbMetacarpal") && capabilities.humanoidBones.includes("leftThumbProximal"));

  const { vrm: imported } = await loadLocalVrm(bytes);
  assert.deepEqual(imported.expressionManager!.expressions.map(e => e.expressionName).sort(),
    capabilities.expressions.map(e => e.name).sort(), "the importer registers the names the preflight reports");
  assert.equal(imported.humanoid.getRawBoneNode("leftThumbMetacarpal")?.name, "LThumb1");
  assert.equal(imported.humanoid.getRawBoneNode("leftThumbProximal")?.name, "LThumb2");
  assert.ok(imported.materials?.some(m => (m as { isMToonMaterial?: boolean }).isMToonMaterial), "the MToon properties make an MToon material");
  const { VRMUtils } = await import("@pixiv/three-vrm");
  VRMUtils.deepDispose(imported.scene);
});

test("a VRM 0.x model loads turned to the camera and idles, nods, waves, blinks and lip-syncs like a VRM 1.0 one", async () => {
  const runtime = new VrmRuntime();
  const capabilities = await runtime.load(fixtureV0());
  assert.equal(capabilities.format, "VRM0");
  assert.ok(Math.abs(runtime.scene!.rotation.y - Math.PI) < 1e-9, "rotateVRM0 turned its root half a turn");
  assert.ok(world(runtime, "leftUpperArm").x > 0.1 && world(runtime, "leftEye").x > 0, "the character's left is at +X");
  assert.ok(world(runtime, "leftEye").z > world(runtime, "head").z, "its eyes look toward +Z, the camera");
  const face = runtime.faceGeometry()!;
  assert.ok(face.forward.z > 0.99 && face.side.x > 0.99 && face.up.y > 0.99, "the face looks at the camera");
  assert.ok(face.eyeRight.x > face.eyeLeft.x, "the character's left eye is on the viewer's right");
  assert.deepEqual(runtime.springNodes.map(chain => chain.map(node => node.name)), [["HairRoot", "HairMiddle", "HairTip"]]);

  runtime.startIdle();
  const rest = runtime.idleReading!;
  for (const side of ["left", "right"] as const) {
    const arm = rest.arms[side]!;
    assert.ok(arm.fromDown > 10 && arm.fromDown < 20, `the ${side} arm hangs close to the body, not up: ${arm.fromDown}`);
    assert.ok(arm.elbow > 10 && arm.elbow < 25, `the ${side} elbow bends softly: ${arm.elbow}`);
  }
  assert.ok(world(runtime, "leftHand").z > world(runtime, "leftUpperArm").z, "the arms hang a little forward, toward the camera");
  assert.ok(world(runtime, "leftHand").x > world(runtime, "hips").x && world(runtime, "rightHand").x < world(runtime, "hips").x,
    "each hand stays on its own side");

  assert.equal(runtime.playGesture("nod"), true);
  let lowest = 1;
  for (let i = 0; i < 12; i++) { runtime.update(0.05); lowest = Math.min(lowest, runtime.faceGeometry()!.forward.y); }
  assert.ok(lowest < -0.15, `a nod looks down, not up: ${lowest}`);
  for (let i = 0; i < 20; i++) runtime.update(0.05);

  assert.equal(runtime.playGesture("wave"), true);
  for (let i = 0; i < 20; i++) runtime.update(0.05);
  assert.ok(world(runtime, "rightHand").y > world(runtime, "rightUpperArm").y, "the waving hand is up");
  for (let i = 0; i < 40; i++) runtime.update(0.05);
  assert.ok(world(runtime, "rightHand").y < world(runtime, "rightUpperArm").y, "and back down");

  let blinked = 0;
  for (let i = 0; i < 140; i++) { runtime.update(0.05); blinked = Math.max(blinked, morphs(runtime)[2]!); }
  assert.ok(blinked > 0.5, `the idle body blinks through the blink preset: ${blinked}`);
  for (let i = 0; i < 6; i++) { runtime.setLipSync(0.8); runtime.update(0.05); }
  assert.ok(morphs(runtime)[0]! > 0.5, "lip-sync opens the mouth through the aa preset (the VRM 0.x a)");
  runtime.dispose();
});

test("a VRM 0.x model takes composed frames by its VRM 1.0 names and turns its head the same way as VRM 1.0", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixtureV0());
  runtime.configure(selection, revision, "coefficients"); runtime.reset(identity);
  // A turn about +X looks down on a VRM 1.0 model's normalized head.
  runtime.setPose({ gaze: [0, 1.5, 2], head: [Math.sin(0.2), 0, 0, Math.cos(0.2)] });
  runtime.applyFrame({ identity, ...revision, sequence: 0, sampleOffset: 0, coefficients: { jawOpen: 0.8, happy: 0.4, blink: 0.6 } }, 0);
  runtime.update(1 / 60);
  assert.deepEqual(morphs(runtime).slice(0, 3).map(v => Math.round(v * 100) / 100), [0.8, 0.4, 0.6]);
  assert.ok(runtime.faceGeometry()!.forward.y < -0.3, "the head looks down");
  runtime.dispose();
});

test("VRM 0.x preflight rejects mixed versions, unknown extensions and a broken humanoid, springs or blend shapes", () => {
  {
    const { document, bin } = fixtureDocumentV0();
    Object.assign(document.extensions, { VRMC_vrm: fixtureDocument().document.extensions.VRMC_vrm });
    document.extensionsUsed.push("VRMC_vrm");
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /both VRMC_vrm \(VRM 1.0\) and VRM \(VRM 0.x\)/);
  }
  {
    const { document, bin } = fixtureDocument();
    Reflect.deleteProperty(document.extensions, "VRMC_vrm");
    document.extensionsUsed = ["VRMC_springBone"];
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /not a VRM model/);
  }
  for (const name of ["KHR_draco_mesh_compression", "EXT_meshopt_compression", "KHR_texture_basisu", "VRMC_vrm", "VRMC_node_constraint"]) {
    const { document, bin } = fixtureDocumentV0(); document.extensionsUsed.push(name);
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /Unsupported/, name);
  }
  {
    const { document, bin } = fixtureDocumentV0();
    Object.assign(document.materials[0]!, { extensions: { VRMC_materials_mtoon: { specVersion: "1.0" } } });
    document.extensionsUsed.push("VRMC_materials_mtoon");
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), /Unsupported extension VRMC_materials_mtoon/, "VRM 1.0 MToon in a VRM 0.x model");
  }
  type V0 = ReturnType<typeof fixtureDocumentV0>["document"];
  const bones = (d: V0) => d.extensions.VRM.humanoid.humanBones as { bone: string; node: number }[];
  const cases: [string, (d: V0) => void, RegExp][] = [
    ["specVersion", d => { d.extensions.VRM.specVersion = "1.0"; }, /Unknown VRM 0.x specVersion/],
    ["missing hips", d => { d.extensions.VRM.humanoid.humanBones = bones(d).filter(b => b.bone !== "hips") as never; }, /Missing required humanoid bone hips/],
    ["unknown role", d => { bones(d).push({ bone: "tail", node: 18 }); }, /Unknown humanoid role tail/],
    ["listed twice", d => { bones(d).push({ bone: "head", node: 18 }); }, /listed twice/],
    ["shared node", d => { bones(d).push({ bone: "neck", node: 2 }); }, /shares a node/],
    ["ancestry", d => { bones(d).find(b => b.bone === "leftHand")!.node = 18; d.extensions.VRM.secondaryAnimation.boneGroups = []; },
      /leftHand must descend from standardized parent leftLowerArm/],
    ["not a list", d => { (d.extensions.VRM.humanoid as Record<string, unknown>).humanBones = { hips: { node: 0 } }; }, /must be an array/],
    ["spring on a humanoid bone", d => { d.extensions.VRM.secondaryAnimation.boneGroups[0]!.bones = [2]; }, /separate from humanoid/],
    ["spring twice", d => { d.extensions.VRM.secondaryAnimation.boneGroups[0]!.bones = [18, 19]; }, /must be unique/],
    ["collider group", d => { d.extensions.VRM.secondaryAnimation.boneGroups[0]!.colliderGroups = [3]; }, /colliderGroup/],
    ["drag force", d => { d.extensions.VRM.secondaryAnimation.boneGroups[0]!.dragForce = 2; }, /dragForce/],
    ["bind mesh", d => { d.extensions.VRM.blendShapeMaster.blendShapeGroups[0]!.binds[0]!.mesh = 4; }, /blendShape bind mesh/],
    ["bind index", d => { d.extensions.VRM.blendShapeMaster.blendShapeGroups[0]!.binds[0]!.index = 99; }, /morph target index/],
    ["bind weight", d => { d.extensions.VRM.blendShapeMaster.blendShapeGroups[0]!.binds[0]!.weight = 150; }, /blendShape bind weight/],
    ["bind twice", d => { const b = d.extensions.VRM.blendShapeMaster.blendShapeGroups[0]!.binds; b.push({ ...b[0]! }); }, /Duplicate morph target/],
    ["prototype preset", d => { d.extensions.VRM.blendShapeMaster.blendShapeGroups[7]!.presetName = "constructor"; }, /Invalid VRM 0.x presetName/],
    ["prototype name", d => { d.extensions.VRM.blendShapeMaster.blendShapeGroups[7]!.name = "__proto__"; }, /Invalid VRM 0.x blend shape name/],
    ["lookAt type", d => { d.extensions.VRM.firstPerson.lookAtTypeName = "Camera"; }, /lookAtTypeName/],
    ["MToon texture", d => { d.extensions.VRM.materialProperties[0]!.textureProperties = { _MainTex: 3 }; }, /textureProperties._MainTex/],
    ["MToon keyword", d => { (d.extensions.VRM.materialProperties[0]!.keywordMap as Record<string, unknown>)._ALPHABLEND_ON = "yes"; }, /keywordMap/],
    ["uri", d => { Object.assign(d.buffers[0]!, { uri: "https://example.invalid/x.bin" }); }, /Forbidden field uri/],
  ];
  for (const [name, mutate, error] of cases) {
    const { document, bin } = fixtureDocumentV0(); mutate(document);
    assert.throws(() => inspectVrm(encodeGlb(document, bin)), error, name);
  }
});

test("VRM 0.x exporters' unused extension declarations are accepted; VRM 1.0 stays strict", () => {
  const { document, bin } = fixtureDocumentV0();
  // The Blender VRM add-on lists VRMC_materials_mtoon without using it.
  document.extensionsUsed.push("VRMC_materials_mtoon", "KHR_texture_transform", "KHR_materials_unlit");
  assert.equal(inspectVrm(encodeGlb(document, bin)).format, "VRM0");
  Object.assign(document, { extensionsRequired: ["KHR_materials_unlit"] });
  assert.throws(() => inspectVrm(encodeGlb(document, bin)), /extensionsRequired contains undeclared or absent extension/);
  const v1 = fixtureDocument(); v1.document.extensionsUsed.push("KHR_texture_transform");
  assert.throws(() => inspectVrm(encodeGlb(v1.document, v1.bin)), /absent extension KHR_texture_transform/);
});

test("embedded JPEG textures are accepted within the PNG pixel limits; other JPEG kinds, broken JPEGs and WebP are not", () => {
  for (const [make, marker] of [[fixtureDocumentV0, 0xc0], [fixtureDocument, 0xc2], [fixtureDocumentV0, 0xc1]] as const) {
    const { document, bin } = make();
    assert.doesNotThrow(() => inspectVrm(withImage(document, bin, jpegHeader(512, 256, marker), "image/jpeg")), `marker ${marker}`);
  }
  {
    const { document, bin } = fixtureDocumentV0();
    document.extensions.VRM.materialProperties[0]!.textureProperties = { _MainTex: 0, _ShadeTexture: 0 };
    document.extensions.VRM.meta.texture = 0;
    const capabilities = inspectVrm(withImage(document, bin, jpegHeader(4096, 4096), "image/jpeg"));
    assert.equal(capabilities.meta.thumbnailImage, 0, "the meta texture's source image is the thumbnail");
  }
  const cases: [Uint8Array, string, RegExp][] = [
    [jpegHeader(4097, 16), "image/jpeg", /JPEG width/],
    [jpegHeader(16, 4097), "image/jpeg", /JPEG height/],
    [jpegHeader(16, 0), "image/jpeg", /JPEG height/],
    [jpegHeader(16, 16, 0xc3), "image/jpeg", /baseline, extended or progressive/],
    [jpegHeader(16, 16, 0xc9), "image/jpeg", /baseline, extended or progressive/],
    [new Uint8Array([0xff, 0xd8, 0xff, 0xd9]), "image/jpeg", /frame header/],
    [jpegHeader(16, 16).slice(0, 25), "image/jpeg", /Truncated JPEG/],
    [new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), "image/jpeg", /JPEG signature/],
    [jpegHeader(16, 16), "image/png", /PNG/],
    [jpegHeader(16, 16), "image/webp", /embedded PNG or JPEG/],
    [jpegHeader(16, 16), "image/ktx2", /embedded PNG or JPEG/],
  ];
  for (const [image, mimeType, error] of cases) {
    const { document, bin } = fixtureDocumentV0();
    assert.throws(() => inspectVrm(withImage(document, bin, image, mimeType)), error, `${mimeType} ${error}`);
  }
  {
    // Two 4096 x 4096 JPEGs are over the decoded texture pixel budget, as two PNGs would be.
    const { document, bin } = fixtureDocumentV0();
    const big = jpegHeader(4096, 4096);
    withImage(document, bin, big, "image/jpeg");
    (document as unknown as { images: unknown[] }).images.push({ mimeType: "image/jpeg", bufferView: 1 });
    const data = new Uint8Array(document.buffers[0]!.byteLength);
    data.set(bin); data.set(big, Math.ceil(bin.length / 4) * 4);
    assert.throws(() => inspectVrm(encodeGlb(document, data)), /pixel budget/);
  }
});

test("many materials may share textures; the textures' own pixels are bounded", () => {
  const { document, bin } = fixtureDocumentV0();
  withImage(document, bin, jpegHeader(2048, 2048), "image/jpeg");
  const doc = document as unknown as { materials: unknown[]; textures: unknown[]; buffers: { byteLength: number }[] };
  // A VRoid-like model: 15 materials sampling one 2048 x 2048 image.
  doc.materials = Array.from({ length: 15 }, (_, i) => ({ name: `M${i}`, pbrMetallicRoughness: { baseColorTexture: { index: 0 } } }));
  const data = new Uint8Array(doc.buffers[0]!.byteLength);
  data.set(bin); data.set(jpegHeader(2048, 2048), Math.ceil(bin.length / 4) * 4);
  assert.equal(inspectVrm(encodeGlb(document, data)).format, "VRM0");
  // Nine textures of one 2048 x 2048 image would each be their own GPU texture: 36Mi pixels.
  doc.textures = Array.from({ length: 9 }, () => ({ source: 0 }));
  assert.throws(() => inspectVrm(encodeGlb(document, data)), /Texture instance pixel budget/);
});

test("the importer hands embedded JPEG bytes to the browser decoder as image/jpeg, and a reload closes the bitmap", async t => {
  class TestBitmap { width = 2; height = 2; closes = 0; close(): void { this.closes++; } }
  const types: string[] = [], bitmaps: TestBitmap[] = [];
  stub(t, {
    self: globalThis, ImageBitmap: TestBitmap,
    createImageBitmap: async (blob: Blob) => {
      types.push(blob.type);
      const bitmap = Object.assign(new TestBitmap(), { [Symbol.toStringTag]: "ImageBitmap" });
      bitmaps.push(bitmap); return bitmap;
    },
  });
  t.mock.method(URL, "createObjectURL", () => { throw new Error("Object URLs must not be created by the importer."); });
  const { document, bin } = fixtureDocumentV0();
  document.extensions.VRM.materialProperties[0]!.textureProperties = { _MainTex: 0 };
  const runtime = new VrmRuntime();
  await runtime.load(withImage(document, bin, jpegHeader(2, 2), "image/jpeg"));
  assert.deepEqual(types, ["image/jpeg"]);
  let textured = false;
  runtime.scene!.traverse(node => {
    if (node instanceof THREE.Mesh) for (const material of [node.material].flat()) if ((material as { map?: unknown }).map) textured = true;
  });
  assert.ok(textured, "the VRM 0.x MToon material samples the JPEG");
  await runtime.load(fixtureV0());
  assert.deepEqual(bitmaps.map(b => b.closes), [1], "replacing the model closes its decoded JPEG");
  runtime.dispose();
});
