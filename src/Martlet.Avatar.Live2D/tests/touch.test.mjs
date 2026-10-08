import assert from "node:assert/strict";
import test from "node:test";
import { Live2DAdapter, LocalModelBundle, drawableContains, hitTestModel, restPoint } from "../dist/index.js";
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

const near = (actual, expected, message) =>
  assert.ok(Math.abs(actual.x - expected.x) < 1e-6 && Math.abs(actual.y - expected.y) < 1e-6,
    `${message ?? "point"}: ${JSON.stringify(actual)} is not ${JSON.stringify(expected)}`);

test("a point on a moved mesh is traced to the same place on its triangle at rest", () => {
  // At rest the triangle is (0,0) (1,0) (0,1); now the head turned it a quarter turn and moved it right by 2.
  const rest = [0, 0, 1, 0, 0, 1], now = [2, 0, 2, 1, 1, 0];
  // A quarter of the way from the first corner to each of the others, now and at rest.
  near(restPoint(now, [0, 1, 2], rest, 1.75, 0.25), { x: 0.25, y: 0.25 });
  near(restPoint(now, [0, 1, 2], rest, 2, 0), { x: 0, y: 0 }, "a corner");
  assert.equal(restPoint(now, [0, 1, 2], rest, 3, 3), undefined, "outside every triangle");
  // A triangle squashed flat by the pose holds no point; the next one does.
  near(restPoint([0, 0, 1, 0, 2, 0, 0, 0, 1, 0, 0, 1], [0, 1, 2, 3, 4, 5], [0, 0, 1, 0, 2, 0, 5, 5, 6, 5, 5, 6], 0.25, 0.25),
    { x: 5.25, y: 5.25 }, "the next triangle");
  assert.equal(restPoint(now, [0, 1, 2], [0, 0], 1.75, 0.25), undefined, "rest vertices of another mesh");
});

test("hit test traces the point on the topmost drawable that holds it to the rest pose", () => {
  // At rest the face sat 0.2 higher: the head looks down now.
  const rest = [[-1, -1, 1, -1, 1, 1, -1, 1], [-0.5, 0.2, 0.5, 0.2, 0, 1.2], [-0.5, 0, 0.5, 0, 0, 1]];
  const hit = hitTestModel(model(), areas, 0, 0.3, i => rest[i]);
  assert.deepEqual(hit.drawables, ["D_FACE", "ArtMeshBody"]);
  assert.equal(hit.rest.drawable, "D_FACE");
  near(hit.rest, { x: 0, y: 0.5 });
  // Only the body is there: traced on the body, which hasn't moved.
  near(hitTestModel(model(), areas, 0.8, -0.8, i => rest[i]).rest, { x: 0.8, y: -0.8 });
  // Without the rest pose there is nothing to trace.
  assert.equal(hitTestModel(model(), areas, 0, 0.3).rest, undefined);
});

test("hit test says whether the topmost drawable is hair", () => {
  // The face (drawable 1) is drawn over the body (drawable 0): only the topmost one counts.
  assert.equal(hitTestModel(model(), areas, 0, 0.3, undefined, i => i === 1).hair, true);
  assert.equal(hitTestModel(model(), areas, 0, 0.3, undefined, i => i === 0).hair, undefined);
  assert.equal(hitTestModel(model(), areas, 0.8, -0.8, undefined, i => i === 0).hair, true);
  assert.equal(hitTestModel(model(), areas, 0, 0.3).hair, undefined);
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

test("adapter hit test says where the touched point was in the rest pose, in the canvas's framing now", async t => {
  const env = environment();
  const adapter = new Live2DAdapter(env.canvas, { sdk: env.sdk, services: env.services, onDiagnostic: () => {} });
  t.after(() => adapter.dispose());
  await adapter.load(new LocalModelBundle(files(), "avatar.model3.json"));
  // Not moved since it loaded: the point is where it was.
  near(adapter.hitTest(0.5, 0.5).restCanvas, { x: 0.5, y: 0.5 }, "at rest");
  // An idle motion moved the body 0.5 model units to the right: the canvas middle is now 0.5 left of the point that was there.
  env.model.getDrawableVertices = () => new Float32Array([-0.5, -2, 1.5, -2, 1.5, 1.5, -0.5, 1.5]);
  const moved = adapter.hitTest(0.5, 0.5);
  assert.equal(moved.rest.drawable, "ArtMeshBody");
  near(moved.rest, { x: -0.5, y: 0 });
  // 640x480 canvas, fitted scale 0.5: -0.5 model units is 0.1875 of clip space, so 0.09375 of the canvas, left of the middle.
  near(moved.restCanvas, { x: 0.40625, y: 0.5 });
  // Zoomed in 2x, the same spot is drawn twice as far from the middle.
  adapter.setView(2, 0, 0, 1);
  near(adapter.hitTest(0.5, 0.5).restCanvas, { x: 0.3125, y: 0.5 }, "zoomed");
});
