import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { inspectVrm, loadLocalVrm, VrmRuntime, type Selection } from "../src/index.js";
import { encodeGlb, fixture, fixtureDocument } from "./fixture.js";

const identity = {
  sessionId: "11111111-1111-1111-1111-111111111111", turnId: "22222222-2222-2222-2222-222222222222",
  requestId: "33333333-3333-3333-3333-333333333333", sourceId: "audio2face", epoch: 0, sampleRate: 24000,
};
const revision = { modelRevision: "model-v2", mappingRevision: "map-v2" };
const selection: Selection = {
  faceSource: "audio2face", faceMode: "reduced-vowel-jaw-only",
  mappings: [{ channel: "jawOpen", expression: "aa", aspect: "mouth", minimum: 0, maximum: 1 }],
  gaze: false, head: false, secondaryMotion: false,
};
function mouth(runtime: VrmRuntime): number | undefined {
  let result: number | undefined;
  runtime.scene!.traverse(node => { if (node instanceof THREE.Mesh && node.morphTargetInfluences) result = node.morphTargetInfluences[0]; });
  return result;
}

test("coefficient input cannot overwrite a revision-bound direct session", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture()); runtime.configure(selection, revision); runtime.reset(identity);
  runtime.applyComposedParameters({ identity, ...revision, sequence: 0, sampleOffset: 0, parameters: { aa: 0.3 } }, 0);
  runtime.update(0);
  assert.throws(() => runtime.applyFrame({
    identity, ...revision, sequence: 99, sampleOffset: 0, coefficients: { jawOpen: 0.8 },
  }, 0), /mode/);
  runtime.update(0); assert.equal(mouth(runtime), 0.3);
  runtime.applyComposedParameters({ identity, ...revision, sequence: 1, sampleOffset: 0, parameters: { aa: 0.4 } }, 0);
  runtime.dispose();
});

test("coefficient mode requires current revisions and cannot accept direct targets", async () => {
  const runtime = new VrmRuntime(); await runtime.load(fixture());
  runtime.configure(selection, revision, "coefficients"); runtime.reset(identity);
  const input = { identity, ...revision, sequence: 0, sampleOffset: 0, coefficients: { jawOpen: 0.3 } };
  runtime.applyFrame(input, 0); runtime.update(0);
  for (const wrong of [{ modelRevision: "model-v1" }, { mappingRevision: "map-v1" }])
    assert.throws(() => runtime.applyFrame({ ...input, ...wrong, sequence: 99 }, 0), /revision/);
  const missing = { ...input, sequence: 99 };
  Reflect.deleteProperty(missing, "modelRevision");
  assert.throws(() => runtime.applyFrame(missing, 0), /revision/);
  assert.throws(() => runtime.applyComposedParameters({ identity, ...revision, sequence: 99, sampleOffset: 0, parameters: { aa: 0.8 } }, 0), /mode/);
  runtime.update(0); assert.equal(mouth(runtime), 0.3);
  runtime.applyFrame({ ...input, sequence: 1 }, 0);
  runtime.configure(selection, { ...revision, mappingRevision: "map-v3" }, "coefficients");
  runtime.reset(identity);
  assert.throws(() => runtime.applyFrame({ ...input, sequence: 99 }, 0), /revision/);
  runtime.dispose();
});

test("duplicate intra-expression morph target rejects before importer can add weights twice", async () => {
  const { document, bin } = fixtureDocument();
  const binds = document.extensions.VRMC_vrm.expressions.preset.aa!.morphTargetBinds;
  binds.push({ ...binds[0]! });
  const bytes = encodeGlb(document, bin);
  assert.throws(() => inspectVrm(bytes), /Duplicate morph/);
  await assert.rejects(loadLocalVrm(bytes), /Duplicate morph/);
});

test("spring extension without extensionsUsed declaration rejects before capability advertisement", async () => {
  const { document, bin } = fixtureDocument();
  document.extensionsUsed = ["VRMC_vrm"];
  const bytes = encodeGlb(document, bin);
  assert.throws(() => inspectVrm(bytes), /extensionsUsed/);
  await assert.rejects(loadLocalVrm(bytes), /extensionsUsed/);
  document.extensionsUsed = ["VRMC_vrm", "VRMC_springBone"];
  Reflect.deleteProperty(document, "extensionsUsed");
  assert.throws(() => inspectVrm(encodeGlb(document, bin)), /extensionsUsed/);
});

test("zero-length imported spring joints cannot become a usable runtime capability", async () => {
  const { document, bin } = fixtureDocument();
  document.nodes[19]!.translation = [0, 0, 0];
  document.nodes[20]!.translation = [0, 0, 0];
  const runtime = new VrmRuntime();
  await assert.rejects(runtime.load(encodeGlb(document, bin)), /usable spring joints/);
  assert.equal(runtime.capabilities, undefined);
  runtime.dispose();
});

function texturedFixture(): ArrayBuffer {
  const { document, bin } = fixtureDocument();
  const data = new Uint8Array(bin.length + 36); data.set(bin);
  const png = new DataView(data.buffer, bin.length);
  [0x89504e47, 0x0d0a1a0a, 13, 0x49484452, 1, 1].forEach((v, i) => png.setUint32(i * 4, v));
  document.buffers[0]!.byteLength = data.length;
  document.bufferViews.push({ buffer: 0, byteOffset: bin.length, byteLength: 36 });
  Object.assign(document, {
    images: [{ mimeType: "image/png", bufferView: 1 }], textures: [{ source: 0 }],
    materials: [{ pbrMetallicRoughness: { baseColorTexture: { index: 0 } } }],
  });
  Object.assign(document.meshes[0]!.primitives[0]!, { material: 0 });
  return encodeGlb(document, data);
}

test("real importer rejecting PNG decode does not retain blob URLs", async t => {
  for (const [key, value] of Object.entries({
    self: globalThis, createImageBitmap: async () => { throw new Error("Test browser decoder rejected malformed PNG"); },
  })) {
    const descriptor = Object.getOwnPropertyDescriptor(globalThis, key);
    Object.defineProperty(globalThis, key, { configurable: true, writable: true, value });
    t.after(() => { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else Reflect.deleteProperty(globalThis, key); });
  }
  const created = new Set<string>();
  const originalCreate = URL.createObjectURL.bind(URL);
  const originalRevoke = URL.revokeObjectURL.bind(URL);
  t.mock.method(URL, "createObjectURL", (blob: Blob) => { const url = originalCreate(blob); created.add(url); return url; });
  t.mock.method(URL, "revokeObjectURL", (url: string) => { created.delete(url); originalRevoke(url); });
  t.after(() => { created.forEach(url => originalRevoke(url)); });
  await assert.rejects(loadLocalVrm(texturedFixture()), /decode/);
  assert.equal(created.size, 0, "No created object URL may outlive failed import");
});

test("PNG success, concurrent failure, reload and late disposal own bitmap resources without URLs", async t => {
  // Only the browser decoder is simulated; the real importer/material/runtime lifecycle runs.
  class TestBitmap {
    width = 1;
    height = 1;
    closes = 0;
    close(): void { this.closes++; }
  }
  const pending: { resolve: (bitmap: ImageBitmap) => void; reject: (reason: Error) => void }[] = [];
  for (const [key, value] of Object.entries({
    self: globalThis, ImageBitmap: TestBitmap,
    createImageBitmap: () => new Promise<ImageBitmap>((resolve, reject) => pending.push({ resolve, reject })),
  })) {
    const descriptor = Object.getOwnPropertyDescriptor(globalThis, key);
    Object.defineProperty(globalThis, key, { configurable: true, writable: true, value });
    t.after(() => { if (descriptor) Object.defineProperty(globalThis, key, descriptor); else Reflect.deleteProperty(globalThis, key); });
  }
  t.mock.method(URL, "createObjectURL", () => { throw new Error("Object URLs must not be created by the importer."); });
  async function waitForDecodes(count: number): Promise<void> {
    for (let i = 0; i < 50 && pending.length < count; i++) await new Promise(resolve => setImmediate(resolve));
    assert.equal(pending.length, count);
  }
  // Structural compatibility supplies the simulated browser return without production dependency injection.
  function bitmap(): ImageBitmap & TestBitmap {
    return Object.assign(new TestBitmap(), { [Symbol.toStringTag]: "ImageBitmap" });
  }
  const first = new VrmRuntime(); const second = new VrmRuntime();
  const firstLoad = first.load(texturedFixture());
  const secondLoad = second.load(texturedFixture());
  const rejected = assert.rejects(secondLoad, /decode/);
  await waitForDecodes(2);
  const image = bitmap();
  pending[1]!.reject(new Error("Concurrent decode rejection"));
  pending[0]!.resolve(image);
  await firstLoad; await rejected;
  assert.equal(image.closes, 0);
  await first.load(fixture());
  assert.equal(image.closes, 1);
  first.dispose(); second.dispose();
  const late = new VrmRuntime(); const lateLoad = late.load(texturedFixture());
  const canceled = assert.rejects(lateLoad, /canceled/);
  await waitForDecodes(3);
  late.dispose();
  const lateImage = bitmap(); pending[2]!.resolve(lateImage);
  await canceled;
  assert.equal(lateImage.closes, 1);
  assert.equal(late.capabilities, undefined);
});
