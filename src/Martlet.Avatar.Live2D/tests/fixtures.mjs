import { CORE_VERSION, FRAMEWORK_REVISION, LocalModelBundle } from "../dist/index.js";
import assert from "node:assert/strict";

export function png(width = 2, height = 2) {
  const bytes = new Uint8Array(33);
  bytes.set([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82]);
  const view = new DataView(bytes.buffer);
  view.setUint32(16, width);
  view.setUint32(20, height);
  return bytes;
}

export function files(metadata = {}) {
  return new Map([
    ["avatar.model3.json", new TextEncoder().encode(JSON.stringify({
      Version: 3,
      FileReferences: { Moc: "avatar.moc3", Textures: ["texture.png"] },
      Groups: [
        { Target: "Parameter", Name: "LipSync", Ids: ["CustomMouth", "AbsentMouth"] },
        { Target: "Parameter", Name: "EyeBlink", Ids: ["CustomEye"] },
      ],
      ...metadata,
    }))],
    ["avatar.moc3", new Uint8Array([77, 79, 67, 51, 0, 0, 0, 0])],
    ["texture.png", png()],
  ]);
}

export const bundle = () => new LocalModelBundle(files(), "avatar.model3.json");
export const identity = (epoch = 0) => ({
  sessionId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  turnId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  requestId: "cccccccc-cccc-cccc-cccc-cccccccccccc",
  sourceId: "audio2face",
  epoch,
});
export const mouthMapping = {
  channel: "semantics.mouth_open", parameterId: "CustomMouth", aspect: "mouth",
  outputMinimum: -2, outputMaximum: 4,
};

export function environment() {
  const calls = [];
  const values = [-1, 0.6, 0];
  const parameters = [
    ["CustomMouth", -2, 4, -1],
    ["CustomEye", 0.2, 0.9, 0.6],
    ["GazeHorizontal", -30, 30, 0],
  ];
  const model = {
    getParameterCount: () => parameters.length,
    getParameterId: i => ({ getString: () => ({ s: parameters[i][0] }) }),
    getParameterMinimumValue: i => parameters[i][1],
    getParameterMaximumValue: i => parameters[i][2],
    getParameterDefaultValue: i => parameters[i][3],
    setParameterValueByIndex: (i, value, weight) => { calls.push(["write", i, value, weight]); values[i] = value; },
    getCanvasWidth: () => 2,
    getCanvasHeight: () => 4,
    getDrawableCount: () => 1,
    getDrawableId: () => ({ getString: () => ({ s: "ArtMesh0" }) }),
    getDrawableVertexCount: () => 4,
    getDrawableVertexIndexCount: () => 6,
    getDrawableTextureIndex: () => 0,
    getDrawableVertices: () => new Float32Array([-1, -2, 1, -2, 1, 1.5, -1, 1.5]),
    getDrawableOpacity: () => 1,
    getDrawableDynamicFlagIsVisible: () => true,
    getDrawableId: () => ({ getString: () => ({ s: "ArtMeshBody" }) }),
    getDrawableVertexIndices: () => new Uint16Array([0, 1, 2, 0, 2, 3]),
    getDrawableRenderOrders: () => new Int32Array([0]),
    update: () => calls.push(["model.update"]),
  };
  let started = false;
  let initialized = false;
  const coreMoc = {};
  const mocVersion = { value: 5 };
  const core = { Version: {
    csmGetVersion: () => CORE_VERSION,
    csmGetMocVersion(moc, bytes) {
      assert.equal(arguments.length, 2, "Core 05.01.0000 requires csmGetMocVersion(moc, mocBytes).");
      assert.equal(moc, coreMoc);
      assert.ok(bytes instanceof ArrayBuffer);
      calls.push(["core.mocVersion", moc, bytes]);
      return mocVersion.value;
    },
    csmGetLatestMocVersion: () => 5,
  } };
  globalThis.Live2DCubismCore = core;
  const sdk = {
    revision: FRAMEWORK_REVISION,
    CubismFramework: {
      isStarted: () => started,
      isInitialized: () => initialized,
      startUp: () => { started = true; return true; },
      initialize: () => { initialized = true; },
      dispose: () => { calls.push(["framework.dispose"]); initialized = false; },
      cleanUp: () => { calls.push(["framework.cleanUp"]); started = false; },
    },
    CubismMoc: { create: (bytes, consistency) => {
      calls.push(["moc.create", consistency]);
      assert.equal(consistency, true);
      const version = core.Version.csmGetMocVersion(coreMoc, bytes);
      return {
        getMocVersion: () => { calls.push(["moc.getMocVersion"]); return version; },
        createModel: () => { calls.push(["model.create"]); return model; },
        deleteModel: () => calls.push(["model.delete"]),
        release: () => calls.push(["moc.release"]),
      };
    } },
    CubismRenderer_WebGL: class {
      initialize() { calls.push(["renderer.initialize"]); }
      startUp() { calls.push(["renderer.startUp"]); }
      bindTexture() { calls.push(["texture.bind"]); }
      setIsPremultipliedAlpha() {}
      setMvpMatrix(matrix) { calls.push(["matrix", [...matrix.getArray()]]); }
      setRenderState() {}
      drawModel() { calls.push(["draw"]); }
      release() { calls.push(["renderer.release"]); }
    },
    CubismMatrix44: class {
      setMatrix(values) { this.values = values; }
      getArray() { return this.values; }
    },
  };
  const gl = {
    NO_ERROR: 0, MAX_TEXTURE_SIZE: 3379,
    isContextLost: () => false,
    getParameter: () => 4096,
    createTexture: () => ({}),
    deleteTexture: () => calls.push(["texture.delete"]),
    getError: () => 0,
  };
  for (const method of ["bindTexture", "pixelStorei", "texImage2D", "texParameteri", "generateMipmap", "bindFramebuffer",
    "viewport", "clearColor", "clear"]) gl[method] = () => {};
  const events = new Map();
  const canvas = {
    width: 640, height: 480,
    getContext: () => gl,
    addEventListener: (name, callback) => events.set(name, callback),
    removeEventListener: name => events.delete(name),
  };
  const frames = new Map();
  let next = 0;
  const services = {
    decodeTexture: async () => ({ width: 2, height: 2, close: () => calls.push(["bitmap.close"]) }),
    requestFrame: callback => { frames.set(++next, callback); return next; },
    cancelFrame: id => { calls.push(["cancel", id]); frames.delete(id); },
    now: () => 0,
  };
  const diagnostics = [];
  return { calls, values, parameters, model, core, mocVersion, sdk, gl, canvas, services, frames, events, diagnostics };
}
