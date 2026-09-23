import assert from "node:assert/strict";
import test from "node:test";
import { Live2DAdapter, browserServices, checkRuntime } from "../dist/index.js";
import { bundle, environment, identity, mouthMapping } from "./fixtures.mjs";

const code = expected => error => error.code === expected;
const create = env => new Live2DAdapter(env.canvas, {
  sdk: env.sdk, services: env.services, onDiagnostic: d => env.diagnostics.push(d),
});
const frame = (adapter, id = identity(), sequence = 0, value = 1) => ({
  identity: id, sequence, configurationId: adapter.configurationId, channels: { "semantics.mouth_open": value },
});
async function loaded(env, t) {
  const adapter = create(env);
  t.after(() => adapter.dispose());
  await adapter.load(bundle());
  adapter.configure([mouthMapping]);
  adapter.resetEpoch(identity());
  return adapter;
}

test("missing/wrong SDK and Core yield actionable typed diagnostics", () => {
  const env = environment();
  assert.throws(() => checkRuntime(undefined), code("MISSING_SDK"));
  assert.throws(() => checkRuntime({ ...env.sdk, revision: "wrong" }), code("SDK_VERSION_MISMATCH"));
  delete globalThis.Live2DCubismCore;
  assert.throws(() => checkRuntime(env.sdk), code("MISSING_CORE"));
  globalThis.Live2DCubismCore = env.core;
  env.core.Version.csmGetVersion = () => 0x05020000;
  assert.throws(() => checkRuntime(env.sdk), code("CORE_VERSION_MISMATCH"));
});

test("Core static Version class is accepted as well as namespace objects", () => {
  const env = environment();
  const methods = env.core.Version;
  env.core.Version = Object.assign(function Version() {}, methods);
  assert.equal(checkRuntime(env.sdk), env.core.Version);
});

test("production PNG decoder abort closes a late bitmap and preserves premultiplied upload contract", async t => {
  const original = globalThis.createImageBitmap;
  t.after(() => {
    if (original === undefined) delete globalThis.createImageBitmap;
    else globalThis.createImageBitmap = original;
  });
  let resolve;
  let options;
  globalThis.createImageBitmap = (_blob, value) => {
    options = value;
    return new Promise(r => { resolve = r; });
  };
  const controller = new AbortController();
  const pending = browserServices.decodeTexture(new Uint8Array(33), controller.signal);
  controller.abort();
  await assert.rejects(pending, code("LOAD_CANCELLED"));
  let closed = false;
  resolve({ close: () => { closed = true; } });
  await Promise.resolve();
  assert.equal(closed, true);
  assert.equal(options.premultiplyAlpha, "premultiply");
});

test("production PNG decoder deadline rejects instead of installing a late image", async t => {
  const originalDecoder = globalThis.createImageBitmap;
  const originalTimer = globalThis.setTimeout;
  const originalClear = globalThis.clearTimeout;
  t.after(() => {
    if (originalDecoder === undefined) delete globalThis.createImageBitmap;
    else globalThis.createImageBitmap = originalDecoder;
    globalThis.setTimeout = originalTimer;
    globalThis.clearTimeout = originalClear;
  });
  let deadline;
  let resolve;
  globalThis.setTimeout = (callback, milliseconds) => {
    assert.equal(milliseconds, 10_000);
    deadline = callback;
    return 1;
  };
  globalThis.clearTimeout = () => {};
  globalThis.createImageBitmap = () => new Promise(r => { resolve = r; });
  const pending = browserServices.decodeTexture(new Uint8Array(33), new AbortController().signal);
  deadline();
  await assert.rejects(pending, code("TEXTURE_TIMEOUT"));
  let closed = false;
  resolve({ close: () => { closed = true; } });
  await Promise.resolve();
  assert.equal(closed, true);
});

test("real integration path orders consistency check, model/renderer creation, writes, update and draw", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  assert.ok(env.calls.some(call => call[0] === "moc.create" && call[1] === true));
  assert.ok(env.calls.findIndex(c => c[0] === "renderer.initialize") < env.calls.findIndex(c => c[0] === "renderer.startUp"));
  env.calls.length = 0;
  assert.equal(adapter.applyFrame(frame(adapter)).accepted, true);
  adapter.update(0.01);
  assert.equal(env.values[0], 4);
  assert.ok(env.calls.findIndex(c => c[0] === "write") < env.calls.findIndex(c => c[0] === "model.update"));
  assert.ok(env.calls.findIndex(c => c[0] === "model.update") < env.calls.findIndex(c => c[0] === "draw"));
  assert.ok(env.calls.find(c => c[0] === "matrix")[1].every(Number.isFinite));
});

test("A -> B reset, delayed A and old request/sequence never mutate B state or clock", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  adapter.applyFrame(frame(adapter, identity(), 0, 0));
  adapter.update(0);
  const b = { ...identity(1), requestId: "dddddddd-dddd-dddd-dddd-dddddddddddd" };
  adapter.resetEpoch(b);
  adapter.applyFrame(frame(adapter, b, 4, 1));
  adapter.startClock();
  adapter.update(0);
  const before = [...env.values];
  const scheduled = [...env.frames.keys()];
  assert.equal(adapter.applyFrame(frame(adapter, identity(), 999, 0)).accepted, false);
  assert.equal(adapter.applyFrame(frame(adapter, { ...b, requestId: identity().requestId }, 5, 0)).accepted, false);
  assert.equal(adapter.applyFrame(frame(adapter, { ...b, sourceId: "wrong-provider" }, 5, 0)).accepted, false);
  assert.equal(adapter.applyFrame(frame(adapter, b, 3, 0)).accepted, false);
  assert.deepEqual([...env.frames.keys()], scheduled);
  assert.deepEqual(env.values, before);
  assert.equal(adapter.applyFrame(frame(adapter, b, 5, 0.5)).accepted, true);
  adapter.update(0);
  assert.equal(env.values[0], 1);
});

test("malformed frame is atomic and cannot consume a valid sequence", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  adapter.applyFrame(frame(adapter, identity(), 1, 1));
  adapter.update(0);
  assert.throws(() => adapter.applyFrame(frame(adapter, identity(), 2, NaN)), code("INVALID_CHANNEL_VALUE"));
  adapter.update(0);
  assert.equal(env.values[0], 4);
  assert.equal(adapter.applyFrame(frame(adapter, identity(), 2, 0)).accepted, true);
  adapter.update(0);
  assert.equal(env.values[0], -2);
});

test("normalized frames cannot cross a reconfigured profile or omit its configuration ID", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  const oldFrame = frame(adapter, identity(), 99, 1);
  adapter.configure([{ ...mouthMapping, outputMinimum: 4, outputMaximum: -2 }]);
  adapter.resetEpoch(identity());
  const currentFrame = frame(adapter, identity(), 0, 0);
  assert.equal(adapter.applyFrame(currentFrame).accepted, true);
  adapter.startClock();
  adapter.update(0.2);
  assert.equal(env.values[0], 4);
  const scheduled = [...env.frames.keys()];
  const result = adapter.applyFrame(oldFrame);
  adapter.update(0);
  assert.equal(env.values[0], 4, "Old profile output must not overwrite the current profile.");
  assert.equal(result.accepted, false);
  assert.equal(result.diagnostics[0].code, "STALE_CONFIGURATION");
  assert.deepEqual([...env.frames.keys()], scheduled);
  assert.equal(adapter.applyFrame({ ...currentFrame, sequence: 1 }).accepted, true);
  adapter.update(0.2);
  const { configurationId, ...missingConfiguration } = { ...currentFrame, sequence: 100 };
  assert.equal(adapter.applyFrame(missingConfiguration).accepted, false);
  adapter.update(0.051);
  assert.equal(env.values[0], -1, "Rejected output must not refresh the active frame age.");
  assert.equal(env.diagnostics.at(-1).code, "FRAME_EXPIRED");
});

test("sparse frame neutralizes omitted owned controls; no arbitrary parameter channel accepted", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  adapter.applyFrame(frame(adapter));
  adapter.update(0);
  const result = adapter.applyFrame({ ...frame(adapter, identity(), 1), channels: { CustomMouth: 0.5 } });
  assert.equal(result.diagnostics[0].code, "UNMAPPED_CHANNEL");
  adapter.update(0);
  assert.equal(env.values[0], -1);
});

test("250ms boundary, expiration and queued callback cancellation use actual render time", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  adapter.applyFrame(frame(adapter));
  adapter.update(0.25);
  assert.equal(env.values[0], 4);
  adapter.startClock();
  const callback = [...env.frames.values()][0];
  adapter.update(0.001);
  assert.equal(env.values[0], -1);
  assert.equal(env.frames.size, 0);
  assert.equal(env.diagnostics.at(-1).code, "FRAME_EXPIRED");
  const count = env.calls.length;
  callback(1000);
  assert.equal(env.calls.length, count);
});

test("delayed first RAF expires frame and reset cancels previously queued callback", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  adapter.applyFrame(frame(adapter));
  adapter.startClock();
  const first = [...env.frames.entries()][0];
  env.frames.delete(first[0]);
  first[1](300);
  assert.equal(env.values[0], -1);
  assert.equal(env.diagnostics.at(-1).code, "FRAME_EXPIRED");
  adapter.resetEpoch(identity(2));
  adapter.applyFrame(frame(adapter, identity(2)));
  adapter.startClock();
  const queued = [...env.frames.values()][0];
  adapter.resetEpoch(identity(3));
  const count = env.calls.length;
  queued(320);
  assert.equal(env.calls.length, count);
});

test("pinned Core MOC version is read through the consistency-checked Framework object", async t => {
  const env = environment();
  await loaded(env, t);
  const names = env.calls.map(c => c[0]);
  assert.ok(names.indexOf("moc.create") < names.indexOf("core.mocVersion"));
  assert.ok(names.indexOf("core.mocVersion") < names.indexOf("moc.getMocVersion"));
  assert.ok(names.indexOf("moc.getMocVersion") < names.indexOf("model.create"));
  assert.equal(names.filter(name => name === "core.mocVersion").length, 1);
});

test("unsupported MOC rejects before model creation and releases the checked MOC and Framework", async t => {
  const env = environment();
  env.mocVersion.value = 6;
  const adapter = create(env);
  t.after(() => adapter.dispose());
  await assert.rejects(adapter.load(bundle()), code("UNSUPPORTED_MOC"));
  assert.ok(!env.calls.some(c => c[0] === "model.create"));
  assert.ok(env.calls.some(c => c[0] === "moc.release"));
  assert.ok(env.calls.some(c => c[0] === "framework.dispose"));
});

test("failed consistency, geometry and WebGL initialization clean up all acquired ownership", async () => {
  for (const fail of ["moc", "geometry", "gl"]) {
    const env = environment();
    if (fail === "moc") env.sdk.CubismMoc.create = () => null;
    if (fail === "geometry") env.model.getDrawableVertexCount = () => 999_999;
    if (fail === "gl") env.gl.getError = () => 1282;
    const adapter = create(env);
    await assert.rejects(adapter.load(bundle()));
    assert.ok(env.calls.some(c => c[0] === "framework.dispose"));
    if (fail !== "moc") assert.ok(env.calls.some(c => c[0] === "model.delete"));
    if (fail === "gl") assert.ok(env.calls.some(c => c[0] === "texture.delete"));
    adapter.dispose();
  }
});

test("dispose during async texture decode closes late image and never draws disposed model", async () => {
  const env = environment();
  let resolve;
  env.services.decodeTexture = () => new Promise(r => { resolve = r; });
  const adapter = create(env);
  const pending = adapter.load(bundle());
  adapter.dispose();
  const count = env.calls.length;
  resolve({ width: 2, height: 2, close: () => env.calls.push(["late.close"]) });
  await assert.rejects(pending, code("LOAD_CANCELLED"));
  assert.deepEqual(env.calls.slice(count), [["late.close"]]);
  assert.equal(env.calls.filter(c => c[0] === "model.delete").length, 1);
  assert.throws(() => adapter.applyFrame(frame(adapter)), code("DISPOSED"));
});

test("decoded dimension mismatch and decoder failure cannot leave an active runtime", async () => {
  for (const decode of [
    async () => ({ width: 9999, height: 2, close() {} }),
    async () => { throw new Error("decode rejected"); },
  ]) {
    const env = environment();
    env.services.decodeTexture = decode;
    const adapter = create(env);
    await assert.rejects(adapter.load(bundle()));
    assert.ok(env.calls.some(c => c[0] === "moc.release"));
    adapter.dispose();
  }
});

test("reload and idempotent disposal release GPU, model, MOC then Framework without voice APIs", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  adapter.applyFrame(frame(adapter));
  await adapter.load(bundle());
  assert.equal(adapter.applyFrame(frame(adapter)).accepted, false);
  assert.equal(env.calls.filter(c => c[0] === "model.delete").length, 1);
  env.calls.length = 0;
  adapter.dispose();
  adapter.dispose();
  assert.deepEqual(env.calls.map(c => c[0]), [
    "renderer.release", "texture.delete", "model.delete", "moc.release", "framework.dispose", "framework.cleanUp",
  ]);
  assert.equal(env.events.size, 0);
});

test("runtime excludes concurrent adapters and externally owned Framework state", async t => {
  const env = environment();
  await loaded(env, t);
  const second = create(env);
  t.after(() => second.dispose());
  await assert.rejects(second.load(bundle()), code("RUNTIME_BUSY"));
});

test("context loss stops clock and disposes, while invalid clock/canvas are explicit errors", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  assert.throws(() => adapter.update(NaN), code("INVALID_NUMBER"));
  assert.throws(() => adapter.update(-1), code("INVALID_CLOCK"));
  env.canvas.width = 4096;
  assert.throws(() => adapter.update(0), code("RESOURCE_LIMIT"));
  env.canvas.width = 640;
  adapter.startClock();
  env.events.get("webglcontextlost")({ preventDefault() {} });
  assert.equal(env.frames.size, 0);
  assert.equal(env.diagnostics.at(-1).code, "CONTEXT_LOST");
  assert.throws(() => adapter.update(0), code("DISPOSED"));
});

test("cleanup failures are reported after all independent resources have been released", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  env.gl.deleteTexture = () => { throw new Error("GPU cleanup failure"); };
  assert.throws(() => adapter.dispose(), AggregateError);
  assert.ok(env.calls.some(c => c[0] === "moc.release"));
  assert.ok(env.calls.some(c => c[0] === "framework.cleanUp"));
});

test("final host parameters are approved, model-bounded and never mapped a second time", async t => {
  const env = environment();
  const adapter = await loaded(env, t);
  adapter.configureTargets(["CustomMouth"]);
  adapter.resetEpoch(identity());
  assert.throws(() => adapter.applyFrame(frame(adapter)), code("INPUT_MODE_MISMATCH"));
  const final = value => ({
    identity: identity(), sequence: 0, configurationId: adapter.configurationId, parameters: { CustomMouth: value },
  });
  assert.throws(() => adapter.applyComposedParameters(final(4.1)), code("INVALID_PARAMETER_VALUE"));
  assert.throws(() => adapter.applyComposedParameters({ ...final(1), parameters: { CustomEye: 0.6 } }),
    code("UNAPPROVED_PARAMETER"));
  assert.equal(adapter.applyComposedParameters({ ...final(3.5), configurationId: "old" }).accepted, false);
  assert.equal(adapter.applyComposedParameters(final(3.5)).accepted, true);
  adapter.update(0);
  assert.equal(env.values[0], 3.5);
  assert.equal(adapter.applyComposedParameters(final(0)).accepted, false);
  adapter.update(0);
  assert.equal(env.values[0], 3.5);
  const oldConfiguration = adapter.configurationId;
  adapter.configureTargets(["CustomMouth"]);
  adapter.resetEpoch(identity());
  assert.equal(adapter.applyComposedParameters({
    ...final(0), configurationId: oldConfiguration, sequence: 99,
  }).accepted, false);
  assert.equal(adapter.applyComposedParameters(final(2.5)).accepted, true);
  adapter.update(0);
  assert.equal(env.values[0], 2.5);
  adapter.configure([mouthMapping]);
  assert.throws(() => adapter.applyComposedParameters(final(0)), code("INPUT_MODE_MISMATCH"));
});
