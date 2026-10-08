import assert from "node:assert/strict";
import test from "node:test";
import { access, readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import { Live2DAdapter, LocalModelBundle } from "../dist/index.js";
import { environment, png } from "./fixtures.mjs";

// The bundled Hiyori through the actual Cubism Core, from the SDK `npm run build:dev` (or the renderer's build) downloads into
// vendor/; skipped when it isn't there. Core runs unmodified in a browser-like context; only the Framework is stood in for.
const sdk = fileURLToPath(new URL("../vendor/CubismSdkForWeb-5-r.4/", import.meta.url));
const present = await access(sdk + "Samples/Resources/Hiyori/Hiyori.moc3").then(() => true, () => false);

async function hiyori(t) {
  const context = vm.createContext({ console, WebAssembly, atob, btoa, TextDecoder, TextEncoder, setTimeout, clearTimeout, Uint8Array,
    Int32Array, Float32Array, Uint16Array, ArrayBuffer, Promise, Math, Date, performance,
    document: { currentScript: { src: "http://localhost/core.js" } }, location: { href: "http://localhost/" } });
  context.window = context; context.self = context;
  vm.runInContext(await readFile(sdk + "Core/live2dcubismcore.min.js", "utf8") + "\nglobalThis.Live2DCubismCore = Live2DCubismCore;", context);
  const core = context.Live2DCubismCore;
  await new Promise(resolve => setTimeout(resolve, 50));
  const bytes = await readFile(sdk + "Samples/Resources/Hiyori/Hiyori.moc3");
  const moc = core.Moc.fromArrayBuffer(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
  const model = core.Model.fromMoc(moc);
  t.after(() => { model.release(); moc._release?.(); });
  const P = model.parameters, D = model.drawables, info = model.canvasinfo;
  for (let i = 0; i < P.count; i++) P.values[i] = P.defaultValues[i];
  const env = environment();
  Object.assign(env.model, {
    getParameterCount: () => P.count,
    getParameterId: i => ({ getString: () => ({ s: P.ids[i] }) }),
    getParameterMinimumValue: i => P.minimumValues[i],
    getParameterMaximumValue: i => P.maximumValues[i],
    getParameterDefaultValue: i => P.defaultValues[i],
    getParameterValueByIndex: i => P.values[i],
    setParameterValueByIndex: (i, value) => { P.values[i] = Math.max(P.minimumValues[i], Math.min(P.maximumValues[i], value)); },
    getCanvasWidth: () => info.CanvasWidth / info.PixelsPerUnit,
    getCanvasHeight: () => info.CanvasHeight / info.PixelsPerUnit,
    getDrawableCount: () => D.count,
    getDrawableVertexCount: i => D.vertexCounts[i],
    getDrawableVertexIndexCount: i => D.indexCounts[i],
    getDrawableTextureIndex: i => D.textureIndices[i],
    getDrawableVertices: i => D.vertexPositions[i],
    getDrawableOpacity: i => D.opacities[i],
    getDrawableDynamicFlagIsVisible: i => (D.dynamicFlags[i] & 1) !== 0,
    getDrawableId: i => ({ getString: () => ({ s: D.ids[i] }) }),
    getDrawableVertexIndices: i => D.indices[i],
    getDrawableRenderOrders: () => D.renderOrders,
    getDrawableMasks: () => D.masks,
    getDrawableMaskCounts: () => D.maskCounts,
    update: () => { model.update(); D.resetDynamicFlags(); },
  });
  const files = new Map([
    ["avatar.model3.json", new TextEncoder().encode(JSON.stringify({ Version: 3,
      FileReferences: { Moc: "avatar.moc3", Textures: ["texture_00.png", "texture_01.png"] } }))],
    ["avatar.moc3", new Uint8Array([77, 79, 67, 51, 0, 0, 0, 0])], ["texture_00.png", png()], ["texture_01.png", png()],
  ]);
  const adapter = new Live2DAdapter(env.canvas, { sdk: env.sdk, services: { ...env.services, now: () => performance.now() },
    onDiagnostic: () => {} });
  t.after(() => adapter.dispose());
  await adapter.load(new LocalModelBundle(files, "avatar.model3.json"));
  const set = values => {
    for (const [id, value] of Object.entries(values)) P.values[P.ids.indexOf(id)] = value;
    env.model.update();
  };
  return { adapter, set, canvas: env.canvas };
}

// Whether `point` is inside the union of a shape's triangles.
function inside(shape, point) {
  const t = shape.triangles;
  for (let k = 0; k + 2 < t.length; k += 3) {
    const [a, b, c] = [shape.points[t[k]], shape.points[t[k + 1]], shape.points[t[k + 2]]];
    const side = (p, q) => (q.x - p.x) * (point.y - p.y) - (q.y - p.y) * (point.x - p.x);
    const s1 = side(a, b), s2 = side(b, c), s3 = side(c, a);
    if ((s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0)) return true;
  }
  return false;
}
const height = shape => Math.max(...shape.points.map(p => p.y)) - Math.min(...shape.points.map(p => p.y));

test("the bundled Hiyori's eyes come from her own meshes: irises inside the eye whites, following the gaze and the blink",
  { skip: !present && "the Live2D SDK isn't downloaded" }, async t => {
    const { adapter, set } = await hiyori(t);
    assert.equal(adapter.eyesFrom, "mesh");
    const tracking = adapter.faceTracking;
    assert.ok(tracking.carriers > 0 && tracking.eyeMilliseconds < 100, JSON.stringify(tracking));
    t.diagnostic(`face pinned to ${tracking.carriers} vertices in ${tracking.milliseconds} ms; eyes found in ${tracking.eyeMilliseconds} ms`);
    const rest = adapter.faceAnchor();
    assert.equal(rest.eyesFrom, "mesh");
    for (const side of ["Left", "Right"]) {
      const iris = rest[`iris${side}`], shape = rest[`eye${side}Shape`];
      assert.ok(iris.rx > 0.05 * rest.width && iris.rx < 0.2 * rest.width, `${side} iris ${iris.rx} for a face ${rest.width} wide`);
      assert.ok(inside(shape, iris), `${side} iris inside its eye white`);
      assert.ok(shape.points.length >= 3 && shape.triangles.length >= 3);
      assert.ok(Math.hypot(rest[`eye${side}`].x - iris.x, rest[`eye${side}`].y - iris.y) < iris.rx, "the eye's middle is at its iris");
    }
    assert.ok(rest.irisLeft.x < rest.x && rest.irisRight.x > rest.x, "the viewer's left iris is left of the face's middle");

    set({ ParamEyeBallX: 1 });
    const looking = adapter.faceAnchor();
    for (const side of ["Left", "Right"]) {
      assert.ok(looking[`iris${side}`].x > rest[`iris${side}`].x + 0.01 * rest.width, `${side} iris follows the gaze to the right`);
      assert.ok(inside(looking[`eye${side}Shape`], looking[`iris${side}`]));
    }
    set({ ParamEyeBallX: 0, ParamEyeLOpen: 0, ParamEyeROpen: 0 });
    const closed = adapter.faceAnchor();
    for (const side of ["Left", "Right"])
      assert.ok(height(closed[`eye${side}Shape`]) < 0.3 * height(rest[`eye${side}Shape`]), `${side} eye white closes on a blink`);
    assert.ok(Math.hypot(closed.eyeLeft.x - rest.eyeLeft.x, closed.eyeLeft.y - rest.eyeLeft.y) < 0.01 * rest.width,
      "the eye's middle stays put while it blinks");
  });

test("the bundled Hiyori's face is pinned to her skin, so Martlet's blush moves with her own as she looks down or tips her head",
  { skip: !present && "the Live2D SDK isn't downloaded" }, async t => {
    const { adapter, set, canvas } = await hiyori(t);
    const tracking = adapter.faceTracking;
    assert.ok(typeof tracking.skin === "string" && tracking.carriers > 0, JSON.stringify(tracking));
    t.diagnostic(`face pinned to ${tracking.skin}'s ${tracking.carriers} vertices in ${tracking.milliseconds} ms`);
    // Her own blush (ParamCheek): a drawable on each cheek, the viewer's left one first.
    const shown = () => new Map(adapter.drawableBounds().map(b => [b.id, b]));
    const without = shown();
    set({ ParamCheek: 1 });
    const blush = () => [...shown().values()].filter(b => !without.has(b.id))
      .map(b => ({ x: (b.left + b.right) / 2 * canvas.width, y: (b.top + b.bottom) / 2 * canvas.height })).sort((a, b) => a.x - b.x);
    const rest = adapter.faceAnchor(), restBlush = blush();
    assert.equal(rest.tracking, "mesh");
    assert.equal(restBlush.length, 2);
    for (const pose of [{ ParamAngleY: -30 }, { ParamAngleY: 30 }, { ParamAngleZ: 30 }, { ParamAngleZ: -30 }]) {
      set({ ParamAngleX: 0, ParamAngleY: 0, ParamAngleZ: 0, ...pose });
      const now = adapter.faceAnchor(), nowBlush = blush();
      ["cheekLeft", "cheekRight"].forEach((key, side) => {
        const drawn = { x: now[key].x - rest[key].x, y: now[key].y - rest[key].y };
        const own = { x: nowBlush[side].x - restBlush[side].x, y: nowBlush[side].y - restBlush[side].y };
        assert.ok(Math.hypot(drawn.x - own.x, drawn.y - own.y) < 0.02 * rest.width,
          `${JSON.stringify(pose)} ${key} moved ${JSON.stringify(drawn)}, her blush ${JSON.stringify(own)}`);
      });
    }
  });
