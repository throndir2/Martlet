import assert from "node:assert/strict";
import test from "node:test";
import { Live2DAdapter, LocalModelBundle, drawableContains, hitTestModel } from "../dist/index.js";
import { environment, files } from "./fixtures.mjs";

// Three meshes: a square body (render order 1), a triangle face drawn over it (order 2) and an invisible head hit-area
// triangle (order 0), all in model units (y up).
function model({ faceVisible = true } = {}) {
  const meshes = [
    { id: "ArtMeshBody", vertices: [-1, -1, 1, -1, 1, 1, -1, 1], indices: [0, 1, 2, 0, 2, 3], order: 1, visible: true, opacity: 1 },
    { id: "D_FACE", vertices: [-0.5, 0, 0.5, 0, 0, 1], indices: [0, 1, 2], order: 2, visible: faceVisible, opacity: 1 },
    { id: "HitAreaHead", vertices: [-0.5, 0, 0.5, 0, 0, 1], indices: [0, 1, 2], order: 0, visible: true, opacity: 0 },
  ];
  return {
    getDrawableCount: () => meshes.length,
    getDrawableId: i => ({ getString: () => ({ s: meshes[i].id }) }),
    getDrawableVertices: i => new Float32Array(meshes[i].vertices),
    getDrawableVertexIndices: i => new Uint16Array(meshes[i].indices),
    getDrawableRenderOrders: () => new Int32Array(meshes.map(m => m.order)),
    getDrawableOpacity: i => meshes[i].opacity,
    getDrawableDynamicFlagIsVisible: i => meshes[i].visible,
  };
}
const areas = [{ id: "HitAreaHead", name: "Head" }, { id: "Missing", name: "Body" }];

test("hit test reports hit areas (even invisible) and visible drawables topmost first", () => {
  const hit = hitTestModel(model(), areas, 0, 0.3);
  assert.deepEqual(hit.hitAreas, ["Head"]);
  assert.deepEqual(hit.drawables, ["D_FACE", "ArtMeshBody"]);
  assert.deepEqual(hit.point, { x: 0, y: 0.3 });
});

test("hit test uses the triangles, not the bounding box", () => {
  // Inside the face triangle's bounding box but outside the triangle itself.
  assert.equal(drawableContains(model(), 1, 0.45, 0.9), false);
  const hit = hitTestModel(model(), areas, 0.45, 0.9);
  assert.deepEqual(hit.hitAreas, []);
  assert.deepEqual(hit.drawables, ["ArtMeshBody"]);
});

test("hidden drawables are skipped and a miss is undefined", () => {
  assert.deepEqual(hitTestModel(model({ faceVisible: false }), areas, 0, 0.3).drawables, ["ArtMeshBody"]);
  assert.equal(hitTestModel(model(), areas, 3, 3), undefined);
});

test("model3.json HitAreas are kept and no longer reported inactive", () => {
  const bundle = new LocalModelBundle(files({ HitAreas: [{ Id: "HitAreaHead", Name: "Head" }] }), "avatar.model3.json");
  assert.deepEqual(bundle.description.hitAreas, [{ id: "HitAreaHead", name: "Head" }]);
  assert.ok(!bundle.description.diagnostics.some(d => d.code === "INACTIVE_HIT_AREAS"));
});

test("adapter hit test maps canvas fractions through the fitted view and zoom", async t => {
  const env = environment();
  const adapter = new Live2DAdapter(env.canvas, { sdk: env.sdk, services: env.services, onDiagnostic: () => {} });
  t.after(() => adapter.dispose());
  await adapter.load(new LocalModelBundle(files({ HitAreas: [{ Id: "ArtMeshBody", Name: "Body" }] }), "avatar.model3.json"));
  // 640x480 canvas, model canvas 2x4: fitted scale 0.5, so the middle of the canvas is the model's origin.
  const middle = adapter.hitTest(0.5, 0.5);
  assert.deepEqual(middle.point, { x: 0, y: 0 });
  assert.deepEqual(middle.hitAreas, ["Body"]);
  assert.deepEqual(middle.drawables, ["ArtMeshBody"]);
  // The top of the canvas is y 2 in model units, above the mesh (which ends at 1.5).
  assert.equal(adapter.hitTest(0.5, 0.01), undefined);
  // Zoomed in 2x and panned up by 0.5 of clip space: the canvas middle is now model y -0.5.
  adapter.setView(2, 0, 0.5, 1);
  const panned = adapter.hitTest(0.5, 0.5);
  assert.ok(Math.abs(panned.point.y + 0.5) < 1e-9);
  assert.equal(panned.point.x, 0);
});
