import assert from "node:assert/strict";
import test from "node:test";
import { Live2DAdapter, LocalModelBundle, faceFeatures, faceFromBox, faceFromLayout, faceSource, headRoll, pinFace, trackFace,
  turnFace } from "../dist/index.js";
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

test("the head's roll is a damped, clamped share of ParamAngleZ, not its full degrees", () => {
  assert.equal(headRoll(0), 0);
  assert.ok(headRoll(30) > 0 && headRoll(30) < 12.0001 * Math.PI / 180, String(headRoll(30)));
  assert.ok(Math.abs(headRoll(10) - 3.5 * Math.PI / 180) < 1e-9);
  assert.equal(headRoll(-90), -headRoll(90));
  assert.equal(headRoll(Number.NaN), 0);
});

test("a fixed face follows the head's angles and rolls its features", () => {
  const face = { x: 0, y: 0, width: 1, roll: 0 };
  assert.ok(turnFace(face, 30, 0, 0).x > 0.1);
  assert.ok(turnFace(face, 0, 30, 0).y > 0.05);
  const rolled = turnFace(face, 0, 0, 20);
  assert.ok(rolled.x < 0 && Math.abs(rolled.roll - headRoll(20)) < 1e-9);
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
  assert.equal(anchor.tracking, "estimate", "a model whose parameters can't be read follows the head's angles");
  assert.equal(adapter.faceTracking.carriers, 0);
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

test("every blush level moves ParamCheek fully when the model has it; without it the page draws them", async t => {
  const env = environment();
  const { adapter } = await adapterWith(env, {}, t);
  for (const level of ["blush", "blush_deep", "blush_fierce"]) {
    assert.ok(adapter.gestures.includes(level), level);
    assert.equal(adapter.gesture(level, true), false, `${level}: no ParamCheek, the page draws it`);
  }
  adapter.dispose();

  const withCheek = environment();
  withCheek.parameters.push(["ParamCheek", 0, 1, 0]);
  withCheek.values.push(0);
  const held = await adapterWith(withCheek, {}, t);
  assert.equal(held.adapter.gesture("blush_fierce", true), true);
  for (let i = 0; i < 20; i++) held.adapter.update(0.1);
  assert.equal(held.gestures.at(-1).ParamCheek, 1, "the model's own blush, fully, under Martlet's drawing");
  assert.deepEqual(held.adapter.gestureState, { held: "blush_fierce" });
  held.adapter.endGesture("blush_fierce");
  for (let i = 0; i < 10; i++) held.adapter.update(0.1);
  assert.equal(held.gestures.at(-1), undefined, "turned off, it fades out and ends");
});

const near = (a, b, tolerance = 1e-3) => Math.hypot(a.x - b.x, a.y - b.y) < tolerance;

test("a face pinned to mesh vertices follows them however they move, turn, tilt and squash", () => {
  const carriers = [];
  for (let i = 0; i < 9; i++) for (let j = 0; j < 9; j++)
    carriers.push({ drawable: carriers.length % 2, vertex: carriers.length, x: -1 + i * 0.25, y: -1 + j * 0.25 });
  const face = { x: 0, y: 0, width: 1, roll: 0 };
  const pinned = pinFace(face, carriers);
  assert.ok(pinned);
  assert.equal(pinFace(face, carriers.slice(0, 2)), undefined, "too few vertices near the face");
  // Squashed across (a turned head), tilted 10° and moved.
  const angle = 10 * Math.PI / 180, c = Math.cos(angle), s = Math.sin(angle);
  const pose = p => ({ x: 0.8 * c * p.x - s * p.y + 0.3, y: 0.8 * s * p.x + c * p.y - 0.2 });
  const meshes = [new Float32Array(2 * carriers.length), new Float32Array(2 * carriers.length)];
  for (const carrier of carriers) {
    const p = pose(carrier);
    meshes[carrier.drawable][2 * carrier.vertex] = p.x;
    meshes[carrier.drawable][2 * carrier.vertex + 1] = p.y;
  }
  const tracked = trackFace(pinned, drawable => meshes[drawable]);
  const rest = faceFeatures(face);
  for (const key of ["cheekLeft", "cheekRight", "eyeLeft", "eyeRight", "mouth", "top"])
    assert.ok(near(tracked[key], pose(rest[key]), 1e-4), `${key}: ${JSON.stringify(tracked[key])}`);
  assert.ok(near(tracked, pose(face), 1e-4));
  assert.ok(Math.abs(tracked.roll - angle) < 1e-4 && Math.abs(tracked.width - 0.8) < 1e-4, JSON.stringify(tracked));
  assert.ok(near(tracked.cheekLeftFrame.right, { x: 0.8 * c, y: 0.8 * s }, 1e-4), "one face width across the cheek, squashed");
  assert.ok(near(tracked.cheekRightFrame.down, { x: s, y: -c }, 1e-4), "one face width down the cheek, tilted");
  assert.equal(trackFace(pinned, () => new Float32Array(0)), undefined, "a missing vertex loses the face");
});

// A model deformed like a Live2D rig: the head's meshes (skin, front hair, mouth, an eyelid) turn and squash with ParamAngleX,
// nod with ParamAngleY and tilt about the neck with ParamAngleZ, and the whole upper body sways with ParamBodyAngleZ; the
// hair also swings with ParamHairFront (physics), the mouth opens with ParamMouthOpenY and the eyelid closes with
// ParamEyeLOpen. Model units, y up, on the fixture's 2 by 4 canvas.
function rig({ headShift = false } = {}) {
  const env = environment();
  for (const parameter of [["ParamAngleX", -30, 30, 0], ["ParamAngleY", -30, 30, 0], ["ParamAngleZ", -30, 30, 0],
    ["ParamBodyAngleZ", -10, 10, 0], ["ParamHairFront", -1, 1, 0], ["ParamMouthOpenY", 0, 1, 0], ["ParamEyeLOpen", 0, 1, 1],
    ...headShift ? [["ParamHeadShift", -1, 1, 0]] : []]) {
    env.parameters.push(parameter);
    env.values.push(parameter[3]);
  }
  const value = id => env.values[env.parameters.findIndex(p => p[0] === id)] ?? 0;
  const grid = (x0, x1, y0, y1, columns, rows) => {
    const points = [];
    for (let i = 0; i < columns; i++) for (let j = 0; j < rows; j++)
      points.push(x0 + (x1 - x0) * i / (columns - 1), y0 + (y1 - y0) * j / (rows - 1));
    return new Float32Array(points);
  };
  const meshes = [
    { id: "D_FACE_00", rest: grid(-0.3, 0.3, 0.85, 1.55, 7, 8), head: true },
    { id: "D_HAIR_FRONT", rest: new Float32Array([-0.32, 0.9, -0.32, 1.2, -0.3, 1.5, 0.32, 0.9, 0.32, 1.2, 0.3, 1.5, -0.2, 1.62, 0, 1.66, 0.2, 1.62]),
      head: true, local: (x, y) => [x + 0.08 * value("ParamHairFront"), y] },
    { id: "D_MOUTH", rest: grid(-0.05, 0.05, 0.97, 1.03, 3, 3), head: true, local: (x, y) => [x, y - 0.04 * value("ParamMouthOpenY")] },
    { id: "D_EYELID_L", rest: grid(-0.18, -0.06, 1.24, 1.3, 3, 2), head: true, local: (x, y) => [x, y - 0.03 * (1 - value("ParamEyeLOpen"))] },
    { id: "D_BODY", rest: grid(-0.6, 0.6, -1.8, 0.8, 7, 9), head: false },
  ];
  const turn = (x, y, angle, px, py) => [px + (x - px) * Math.cos(angle) - (y - py) * Math.sin(angle),
    py + (x - px) * Math.sin(angle) + (y - py) * Math.cos(angle)];
  // Where a rest point of the head (or the body) is in the pose the parameters set now.
  const pose = (x, y, head) => {
    if (head) {
      const ax = value("ParamAngleX") / 30;
      [x, y] = [(x - 0) * (1 - 0.15 * Math.abs(ax)) + 0.06 * ax + 0.2 * value("ParamHeadShift"), y + 0.04 * value("ParamAngleY") / 30];
      [x, y] = turn(x, y, (10 * Math.PI / 180) * value("ParamAngleZ") / 30, 0, 0.8);
    }
    return turn(x, y, (5 * Math.PI / 180) * value("ParamBodyAngleZ") / 10, 0, -1.5);
  };
  const current = meshes.map(mesh => Float32Array.from(mesh.rest));
  Object.assign(env.model, {
    getDrawableCount: () => meshes.length,
    getDrawableVertexCount: i => meshes[i].rest.length / 2,
    getDrawableVertexIndexCount: () => 3,
    getDrawableVertexIndices: () => new Uint16Array([0, 1, 2]),
    getDrawableRenderOrders: () => Int32Array.from(meshes.keys()),
    getDrawableId: i => ({ getString: () => ({ s: meshes[i].id }) }),
    getDrawableVertices: i => current[i],
    getParameterValueByIndex: i => env.values[i],
    update: () => meshes.forEach((mesh, m) => {
      for (let v = 0; v < mesh.rest.length; v += 2) {
        const [x, y] = mesh.local ? mesh.local(mesh.rest[v], mesh.rest[v + 1]) : [mesh.rest[v], mesh.rest[v + 1]];
        [current[m][v], current[m][v + 1]] = pose(x, y, mesh.head);
      }
    }),
  });
  const set = values => { for (const [id, v] of Object.entries(values)) env.values[env.parameters.findIndex(p => p[0] === id)] = v; env.model.update(); };
  return { env, set, pose: (p, head = true) => { const [x, y] = pose(p.x, p.y, head); return { x, y }; } };
}

test("a Live2D face is pinned to the meshes that ride the head, so the blush follows the head and body as drawn", async t => {
  const { env, set, pose } = rig();
  const adapter = new Live2DAdapter(env.canvas, { sdk: env.sdk, services: env.services, onDiagnostic: () => {} });
  t.after(() => adapter.dispose());
  await adapter.load(new LocalModelBundle(files(), "avatar.model3.json"));
  assert.equal(adapter.faceTracking.carriers, 56, "the face's skin, not the swinging hair, the mouth or the eyelid");
  const rest = adapter.faceAnchor();
  assert.equal(rest.tracking, "mesh");
  assert.ok(rest.cheekLeftFrame && rest.cheekRightFrame);
  assert.ok(Math.abs(Math.hypot(rest.cheekLeftFrame.right.x, rest.cheekLeftFrame.right.y) - rest.width) < 1e-3);
  // The fixture's canvas is 640 by 480 and the model fits its 4-unit height: canvas pixels from model units.
  const toModel = p => ({ x: (p.x / 320 - 1) / 0.375, y: (1 - p.y / 240) / 0.5 });
  const toCanvas = p => ({ x: (p.x * 0.375 + 1) * 320, y: (1 - 0.5 * p.y) * 240 });
  const features = ["cheekLeft", "cheekRight", "eyeLeft", "eyeRight", "mouth", "top"];

  // Hair physics, talking and blinking change the face's shape, not where it is.
  set({ ParamHairFront: 1, ParamMouthOpenY: 1, ParamEyeLOpen: 0 });
  for (const key of features) assert.ok(near(adapter.faceAnchor()[key], rest[key]), `${key} stays`);

  // Turned toward the mouse, tilted and swaying: every feature lands where the meshes took that spot.
  set({ ParamAngleX: 20, ParamAngleY: -12, ParamAngleZ: 30, ParamBodyAngleZ: 10 });
  const moved = adapter.faceAnchor();
  for (const key of features) {
    const expected = toCanvas(pose(toModel(rest[key])));
    assert.ok(near(moved[key], expected), `${key}: ${JSON.stringify(moved[key])} vs ${JSON.stringify(expected)}`);
  }
  assert.ok(near(moved, toCanvas(pose(toModel(rest)))));
  const roll = 15 * Math.PI / 180;
  assert.ok(Math.abs(moved.angle + roll) < 1e-4, `tilted 15° counterclockwise on screen: ${moved.angle}`);
  assert.ok(Math.abs(moved.width - 0.9 * rest.width) < 1e-2, "a turned head is narrower across");
  assert.ok(Math.abs(Math.hypot(moved.cheekLeftFrame.right.x, moved.cheekLeftFrame.right.y) - 0.9 * rest.width) < 1e-2);
  assert.ok(moved.cheekLeftFrame.right.y < 0, "the blush tilts with the head");
});

test("a model's own parameter that moves the whole head is left out of the probe, so the face is still pinned", async t => {
  const { env, set } = rig({ headShift: true });
  const adapter = new Live2DAdapter(env.canvas, { sdk: env.sdk, services: env.services, onDiagnostic: () => {} });
  t.after(() => adapter.dispose());
  await adapter.load(new LocalModelBundle(files(), "avatar.model3.json"));
  assert.equal(adapter.faceTracking.carriers, 56, "probed one by one, the head shift is skipped and the hair, mouth and eyelid still drop out");
  const rest = adapter.faceAnchor();
  set({ ParamHeadShift: 1 });
  const shifted = adapter.faceAnchor();
  assert.equal(shifted.tracking, "mesh");
  // 0.2 model units at 0.375 of 320 pixels each.
  assert.ok(Math.abs(shifted.cheekLeft.x - rest.cheekLeft.x - 0.2 * 0.375 * 320) < 1e-3 && Math.abs(shifted.cheekLeft.y - rest.cheekLeft.y) < 1e-3);
});

test("a face found by vision is pinned where the meshes are drawn now, then follows them", async t => {
  const { env, set } = rig();
  const adapter = new Live2DAdapter(env.canvas, { sdk: env.sdk, services: env.services, onDiagnostic: () => {} });
  t.after(() => adapter.dispose());
  await adapter.load(new LocalModelBundle(files(), "avatar.model3.json"));
  const toCanvas = p => ({ x: (p.x * 0.375 + 1) * 320, y: (1 - 0.5 * p.y) * 240 });
  // Vision looks while the head is tilted 10° about the neck (0, 0.8) and finds the face at (0, 1.2), 0.6 wide.
  set({ ParamAngleZ: 30 });
  adapter.setFaceHint({ x: 0.5, y: 0.2, width: 0.3 });
  const seen = adapter.faceAnchor();
  assert.equal(seen.tracking, "mesh");
  assert.ok(near(seen, toCanvas({ x: 0, y: 1.2 })) && Math.abs(seen.angle) < 1e-6, JSON.stringify(seen));
  // Upright again, that spot of the face turns back with the head.
  set({ ParamAngleZ: 0 });
  const upright = adapter.faceAnchor(), back = -10 * Math.PI / 180;
  assert.ok(near(upright, toCanvas({ x: -0.4 * Math.sin(back), y: 0.8 + 0.4 * Math.cos(back) })), JSON.stringify(upright));
  assert.ok(Math.abs(upright.angle - 10 * Math.PI / 180) < 1e-4, String(upright.angle));
});
