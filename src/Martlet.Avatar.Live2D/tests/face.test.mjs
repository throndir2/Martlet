import assert from "node:assert/strict";
import test from "node:test";
import { Live2DAdapter, LocalModelBundle, faceFeatures, faceFromBox, faceFromLayout, faceSource, turnFace } from "../dist/index.js";
import { environment, files } from "./fixtures.mjs";

test("the face is found from a head hit area, then face or cheek meshes, then the top of the model", () => {
  const layout = () => ({ x: 0, y: 0, width: 1, roll: 0 });
  assert.deepEqual(faceSource([{ id: "ArtMesh2", name: "Head" }, { id: "ArtMesh0", name: "Body" }], ["ArtMesh0", "ArtMesh1", "ArtMesh2"], layout),
    { kind: "head", drawables: [2] });
  assert.deepEqual(faceSource([], ["D_FACE_00", "D_HAIR", "D_CHEEK_L"], layout), { kind: "skin", drawables: [0] });
  assert.deepEqual(faceSource([{ id: "HitArea", name: "Body" }], ["Hair", "頬L", "頬R"], layout), { kind: "cheeks", drawables: [1, 2] });
  assert.deepEqual(faceSource([], ["ArtMesh1"], layout), { kind: "fixed", face: layout() });
  assert.equal(faceSource([], [], () => undefined), undefined);
});

test("the estimate from the top of the model sits in the head, a little below the hair", () => {
  // A 10-unit tall figure whose head (x -1..1) is the top 2 units, on a wider body.
  const head = new Float32Array([-1, 8, 1, 8, 1, 10, -1, 10, -1, 9.2, 1, 9.2, -0.9, 8.8, 0.9, 8.8, -1, 9.5, 1, 9.5]);
  const body = new Float32Array([-3, 0, 3, 0, 3, 8, -3, 8]);
  const face = faceFromLayout([head, body]);
  assert.ok(face && Math.abs(face.x) < 0.01, JSON.stringify(face));
  assert.ok(face.width > 1 && face.width <= 2, JSON.stringify(face));
  assert.ok(face.y > 8 && face.y < 9.5, JSON.stringify(face));
  const features = faceFeatures(face);
  assert.ok(features.cheekLeft.x < face.x && features.cheekRight.x > face.x && features.cheekLeft.y < face.y);
  assert.ok(features.top.y > face.y && features.mouth.y < features.cheekLeft.y);
});

test("a fixed face follows the head's angles and rolls its features", () => {
  const face = { x: 0, y: 0, width: 1, roll: 0 };
  assert.ok(turnFace(face, 30, 0, 0).x > 0.1);
  assert.ok(turnFace(face, 0, 30, 0).y > 0.05);
  const rolled = turnFace(face, 0, 0, 20);
  assert.ok(rolled.x < 0 && Math.abs(rolled.roll - 20 * Math.PI / 180) < 1e-9);
  const features = faceFeatures(rolled);
  assert.ok(features.cheekRight.y > features.cheekLeft.y, "a counterclockwise roll lifts the viewer's right cheek");
  const box = faceFromBox("head", { minX: -1, maxX: 1, minY: 0, maxY: 2 });
  assert.equal(box.x, 0);
  assert.ok(box.width < 2 && box.y < 1);
});

async function adapterWith(env, metadata, t) {
  const gestures = [];
  env.model.getDrawableId = () => ({ getString: () => ({ s: "ArtMesh0" }) });
  env.sdk.createAnimator = () => ({
    update: (_dt, input) => gestures.push(input.gesture), playMotion: () => false, setExpression: () => false,
    motionGroups: [], expressions: [], release() {},
  });
  const adapter = new Live2DAdapter(env.canvas, { sdk: env.sdk, services: env.services, onDiagnostic: () => {} });
  t.after(() => adapter.dispose());
  await adapter.load(new LocalModelBundle(files(metadata), "avatar.model3.json"));
  return { adapter, gestures };
}

test("the adapter reports the face in canvas pixels, following the view", async t => {
  const env = environment();
  const { adapter } = await adapterWith(env, { HitAreas: [{ Id: "ArtMesh0", Name: "Head" }] }, t);
  const anchor = adapter.faceAnchor();
  assert.ok(anchor, "a head hit area gives a face");
  assert.ok(anchor.x > 0 && anchor.x < env.canvas.width && anchor.y > 0 && anchor.y < env.canvas.height, JSON.stringify(anchor));
  assert.ok(anchor.cheekLeft.x < anchor.cheekRight.x && anchor.cheekLeft.y > anchor.eyeLeft.y);
  assert.ok(anchor.width > 0 && anchor.angle === 0);
  adapter.setView(2, 0, 0);
  assert.ok(Math.abs(adapter.faceAnchor().width - 2 * anchor.width) < 1e-6);
  adapter.setFaceHint({ x: 0.5, y: 0.25, width: 0.2 });
  const hinted = adapter.faceAnchor();
  assert.ok(Math.abs(hinted.x - env.canvas.width / 2) < 1e-6 && hinted.y < env.canvas.height / 2);
  adapter.setFaceHint(undefined);
  assert.ok(Math.abs(adapter.faceAnchor().width - 2 * anchor.width) < 1e-6);
});

test("blush uses ParamCheek when the model has it and can be held until released; without it the page draws one", async t => {
  const env = environment();
  const { adapter, gestures } = await adapterWith(env, {}, t);
  assert.ok(adapter.gestures.includes("blush"));
  assert.equal(adapter.gesture("blush"), false, "no ParamCheek: the page's overlay draws the blush");
  adapter.dispose();
  void gestures;

  const withCheek = environment();
  withCheek.parameters.push(["ParamCheek", 0, 1, 0]);
  withCheek.values.push(0);
  const held = await adapterWith(withCheek, {}, t);
  assert.equal(held.adapter.gesture("blush", true), true);
  for (let i = 0; i < 60; i++) held.adapter.update(0.1);
  assert.equal(held.gestures.at(-1).ParamCheek, 1, "held, the blush stays");
  held.adapter.endGesture("blush");
  held.adapter.update(0.3);
  assert.ok(held.gestures.at(-1).ParamCheek < 1 && held.gestures.at(-1).ParamCheek > 0);
  held.adapter.update(0.6);
  held.adapter.update(0.1);
  assert.equal(held.gestures.at(-1), undefined, "released, it fades out and ends");
});
