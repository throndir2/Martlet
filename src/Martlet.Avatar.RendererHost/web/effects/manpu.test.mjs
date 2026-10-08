import test from "node:test";
import assert from "node:assert/strict";
import { MANPU, MANPU_NAMES, drawManpu, easeOutBack, headTop, localPoint, particleTime, registerManpu } from "./manpu.mjs";

// A 2D context that records the transform it is given and every drawing call, without a browser. Each fill, stroke and clip
// also notes its path (page points: moveTo, lineTo and curve ends, and the middle and radii of arcs and ellipses), and each
// fill and stroke where its piece's origin is on the page and how many clips are on.
function recorder() {
  const calls = [], stack = [];
  let state = { alpha: 1, matrix: [1, 0, 0, 1, 0, 0], clips: [] }, path = [];
  const multiply = ([a, b, c, d, e, f], [g, h, i, j, k, l]) =>
    [a * g + c * h, b * g + d * h, a * i + c * j, b * i + d * j, a * k + c * l + e, b * k + d * l + f];
  const point = (x, y) => { const [a, b, c, d, e, f] = state.matrix; return { x: a * x + c * y + e, y: b * x + d * y + f }; };
  const size = () => { const [a, b, c, d] = state.matrix; return Math.sqrt(Math.abs(a * d - b * c)); };
  const ctx = {
    calls,
    get globalAlpha() { return state.alpha; }, set globalAlpha(value) { state.alpha = value; },
    save() { stack.push({ ...state }); }, restore() { state = stack.pop(); },
    translate(x, y) { state.matrix = multiply(state.matrix, [1, 0, 0, 1, x, y]); },
    scale(x, y) { state.matrix = multiply(state.matrix, [x, 0, 0, y, 0, 0]); },
    rotate(a) { state.matrix = multiply(state.matrix, [Math.cos(a), Math.sin(a), -Math.sin(a), Math.cos(a), 0, 0]); },
    createLinearGradient: () => ({ addColorStop() {} }),
    createRadialGradient: () => ({ addColorStop() {} }),
    beginPath() { path = []; },
    closePath() {},
    bezierCurveTo(...args) { path.push(point(args[4], args[5])); },
    quadraticCurveTo(...args) { path.push(point(args[2], args[3])); },
    arc(x, y, r) { path.push({ ...point(x, y), rx: r * size(), ry: r * size() }); },
    ellipse(x, y, rx, ry) { path.push({ ...point(x, y), rx: rx * size(), ry: ry * size() }); },
    clip(rule) { state.clips = [...state.clips, path]; calls.push({ name: "clip", path, rule }); },
  };
  for (const name of ["moveTo", "lineTo"])
    ctx[name] = (x, y) => { const at = point(x, y); path.push(at); calls.push({ name, at }); };
  for (const name of ["fill", "stroke", "fillText", "strokeText"])
    ctx[name] = (...args) => calls.push({ name, alpha: state.alpha, at: name.endsWith("Text") ? point(args[1], args[2]) : undefined,
      origin: point(0, 0), clips: state.clips.length, path });
  return ctx;
}

const anchor = (width = 200, angle = 0) => ({ x: 400, y: 300, width, angle, cheekLeft: { x: 340, y: 340 }, cheekRight: { x: 460, y: 340 },
  eyeLeft: { x: 358, y: 305 }, eyeRight: { x: 442, y: 305 }, mouth: { x: 400, y: 370 }, top: { x: 400, y: 300 - 0.75 * width } });

test("every overlay emote registers with its timing and draws on any face", () => {
  const registered = new Map();
  registerManpu((name, overlay) => registered.set(name, overlay));
  assert.deepEqual([...registered.keys()], [...MANPU_NAMES]);
  for (const name of MANPU_NAMES) {
    const overlay = registered.get(name);
    assert.ok(overlay.duration >= 1.5 && overlay.duration <= 4, name);
    assert.ok(overlay.fadeIn > 0 && overlay.fadeOut > 0, name);
    for (const t of [0.1, 0.6, 1.5, 3, 30]) {
      const ctx = recorder();
      overlay.draw(ctx, anchor(), t, 1);
      assert.ok(ctx.calls.some(c => c.name === "fill" || c.name === "stroke" || c.name === "fillText"), `${name} at ${t}s`);
      assert.ok(ctx.calls.every(c => c.alpha === undefined || (c.alpha >= 0 && c.alpha <= 1)), name);
    }
  }
});

test("emotes stay with the face: they scale with its width and turn with the head", () => {
  for (const name of ["question", "exclaim", "sleepy"]) {
    const small = recorder(), large = recorder();
    drawManpu(name, small, anchor(100), 1, 1);
    drawManpu(name, large, anchor(200), 1, 1);
    const at = ctx => ctx.calls.find(c => c.name === "fillText").at;
    // Offsets from the face centre double with the face.
    assert.ok(Math.abs((at(large).x - 400) - 2 * (at(small).x - 400)) < 1e-6, name);
    assert.ok(Math.abs((at(large).y - 300) - 2 * (at(small).y - 300)) < 1e-6, name);
    assert.ok(at(large).x > 400 && at(large).y < 300, `${name} sits above and beside the head`);
  }
  const turned = recorder(), straight = recorder();
  drawManpu("question", straight, { ...anchor(200, 0), top: undefined }, 1, 1);
  drawManpu("question", turned, { ...anchor(200, Math.PI / 2), top: undefined }, 1, 1);
  const a = straight.calls.find(c => c.name === "fillText").at, b = turned.calls.find(c => c.name === "fillText").at;
  // A quarter turn clockwise maps (dx, dy) to (-dy, dx).
  assert.ok(Math.abs((b.x - 400) + (a.y - 300)) < 1e-6 && Math.abs((b.y - 300) - (a.x - 400)) < 1e-6);
});

test("tears fall from the eyes and need them", () => {
  const ctx = recorder();
  drawManpu("tears", ctx, anchor(), 1.2, 1);
  const starts = ctx.calls.filter(c => c.name === "moveTo").map(c => c.at);
  assert.ok(starts.some(p => Math.abs(p.x - 358) < 20 && p.y > 305 && p.y < 330), "left eye");
  assert.ok(starts.some(p => Math.abs(p.x - 442) < 20 && p.y > 305 && p.y < 330), "right eye");
  const blind = recorder();
  drawManpu("tears", blind, { ...anchor(), eyeLeft: undefined }, 1.2, 1);
  assert.equal(blind.calls.length, 0);
});

// A face `width` pixels wide centred on 400, 300 and turned `angle` clockwise, with its points where an adapter puts them.
function face(width = 200, angle = 0) {
  const scale = width / 100, cos = Math.cos(angle), sin = Math.sin(angle);
  const at = (x, y) => ({ x: 400 + (x * cos - y * sin) * scale, y: 300 + (x * sin + y * cos) * scale });
  return { x: 400, y: 300, width, angle, eyeLeft: at(-21, 2), eyeRight: at(21, 2), mouth: at(0, 38), top: at(0, -70),
    cheekLeft: at(-28, 20), cheekRight: at(28, 20) };
}
const ADDED = ["heart_eyes", "star_eyes", "tongue_out", "drool", "steam", "dizzy", "idea", "ellipsis"];
const near = (p, q, within) => Math.hypot(p.x - q.x, p.y - q.y) < within;
const drawn = ctx => ctx.calls.filter(c => c.name === "fill" || c.name === "stroke");
// Fills and strokes below the top of the head, where the eye emotes draw (dizzy's stars circle above it).
const below = anchor => call => call.origin.y > anchor.y - 0.3 * anchor.width;
const TAU = Math.PI * 2;

// What an adapter may add about the eyes, in page pixels: each iris (its middle and its radii across and down the face) and
// each eye's visible opening, the viewer's left one as an outline and the right one as a mesh's triangles.
function withEyes(anchor, radii = [8, 10]) {
  const scale = anchor.width / 100;
  const ring = p => Array.from({ length: 12 }, (_, i) => ({ x: p.x + 14 * scale * Math.cos(TAU * i / 12), y: p.y + 10 * scale * Math.sin(TAU * i / 12) }));
  const iris = p => ({ x: p.x + 2 * scale, y: p.y + scale, rx: radii[0] * scale, ry: radii[1] * scale });
  // A fan of triangles around the right eye's middle, the first one wound the other way.
  const triangles = Array.from({ length: 12 }, (_, i) => i ? [0, 1 + i, 1 + (i + 1) % 12] : [0, 2, 1]).flat();
  return { ...anchor, irisLeft: iris(anchor.eyeLeft), irisRight: iris(anchor.eyeRight), eyeLeftShape: { points: ring(anchor.eyeLeft) },
    eyeRightShape: { points: [{ ...anchor.eyeRight }, ...ring(anchor.eyeRight)], triangles }, eyesFrom: "mesh" };
}

test("eye emotes sit on both eyes and mouth emotes at the mouth", () => {
  const anchor = face(200);
  for (const name of ["heart_eyes", "star_eyes", "dizzy"]) {
    const ctx = recorder();
    drawManpu(name, ctx, anchor, 1.2, 1);
    for (const eye of [anchor.eyeLeft, anchor.eyeRight])
      assert.ok(drawn(ctx).some(c => near(c.origin, eye, 0.06 * anchor.width)), `${name} on each eye`);
  }
  for (const name of ["tongue_out", "drool"]) {
    const ctx = recorder();
    drawManpu(name, ctx, anchor, 1.2, 1);
    const points = [...drawn(ctx).map(c => c.origin), ...ctx.calls.filter(c => c.name === "moveTo").map(c => c.at)];
    assert.ok(points.some(p => near(p, anchor.mouth, 0.08 * anchor.width)), `${name} at the mouth`);
    assert.ok(points.every(p => p.y > anchor.mouth.y - 0.05 * anchor.width && near(p, anchor.mouth, 0.35 * anchor.width)),
      `${name} hangs from the mouth`);
  }
});

test("the added emotes scale with the face and turn with the head", () => {
  const points = ctx => ctx.calls.map(c => c.at ?? c.origin ?? c.path[0]).filter(Boolean);
  for (const name of ADDED) for (const t of [0.4, 1.2, 30]) {
    const [small, large, turned] = [face(100), face(200), face(200, Math.PI / 2)].map(anchor => {
      const ctx = recorder();
      drawManpu(name, ctx, anchor, t, 1);
      return points(ctx);
    });
    assert.ok(small.length > 0 && small.length === large.length && large.length === turned.length, `${name} at ${t}s`);
    small.forEach((p, i) => {
      // Offsets from the face centre double with the face, and a quarter turn clockwise maps (dx, dy) to (-dy, dx).
      const [q, r] = [large[i], turned[i]];
      assert.ok(Math.abs((q.x - 400) - 2 * (p.x - 400)) < 1e-6 && Math.abs((q.y - 300) - 2 * (p.y - 300)) < 1e-6, `${name} scales`);
      assert.ok(Math.abs((r.x - 400) + (q.y - 300)) < 1e-6 && Math.abs((r.y - 300) - (q.x - 400)) < 1e-6, `${name} turns`);
    });
  }
});

test("the eye and mouth emotes need their anchor points", () => {
  for (const [name, point] of [["heart_eyes", "eyeLeft"], ["star_eyes", "eyeRight"], ["dizzy", "eyeLeft"], ["tongue_out", "mouth"],
    ["drool", "mouth"]]) {
    const ctx = recorder();
    drawManpu(name, ctx, { ...withEyes(face()), [point]: undefined }, 1.2, 1);
    assert.equal(ctx.calls.length, 0, name);
  }
  // Those around the head use a typical head without its top.
  for (const name of ["steam", "idea", "ellipsis"]) {
    const ctx = recorder();
    drawManpu(name, ctx, { ...face(), top: undefined, eyeLeft: undefined, mouth: undefined }, 1.2, 1);
    assert.ok(drawn(ctx).length > 0, name);
  }
});

test("on an iris and an eye's opening, eye emotes sit on the iris, scale with it and draw only inside the eye", () => {
  const anchor = withEyes(face(200)), bigger = withEyes(face(200), [16, 20]);
  const on = (ctx, iris) => drawn(ctx).filter(c => near(c.origin, iris, 1e-6));
  const reach = (ctx, iris) => Math.max(...on(ctx, iris).flatMap(c => c.path).map(p => Math.hypot(p.x - iris.x, p.y - iris.y)));
  const turn = (a, b, c) => Math.sign((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
  for (const name of ["heart_eyes", "star_eyes", "dizzy"]) {
    const ctx = recorder(), large = recorder();
    drawManpu(name, ctx, anchor, 1.2, 1);
    drawManpu(name, large, bigger, 1.2, 1);
    for (const iris of [anchor.irisLeft, anchor.irisRight]) {
      assert.ok(on(ctx, iris).length > 0, `${name} sits on the iris`);
      assert.ok(Math.abs(reach(large, iris) - 2 * reach(ctx, iris)) < 1e-6, `${name} scales with the iris`);
    }
    // Each eye clips to its opening: the left eye's outline, the right eye's triangles all wound one way (nonzero: their union).
    const clips = ctx.calls.filter(c => c.name === "clip");
    assert.ok(clips.length === 2 && clips.every(c => c.rule === "nonzero"), name);
    assert.ok(clips[0].path.length === 12 && clips[0].path.every((p, i) => near(p, anchor.eyeLeftShape.points[i], 1e-6)), name);
    const mesh = clips[1].path;
    assert.ok(mesh.length === 36 && mesh.every(p => anchor.eyeRightShape.points.some(q => near(p, q, 1e-6))), name);
    assert.equal(new Set(Array.from({ length: 12 }, (_, i) => turn(mesh[3 * i], mesh[3 * i + 1], mesh[3 * i + 2]))).size, 1, name);
    assert.ok(drawn(ctx).filter(below(anchor)).every(c => c.clips === 1), `${name} draws only inside the eyes`);
  }
});

test("without them, eye emotes are iris-sized at the eye points and clipped to an eye-sized ellipse", () => {
  const anchor = face(200), scale = anchor.width / 100;
  for (const name of ["heart_eyes", "star_eyes", "dizzy"]) {
    const ctx = recorder();
    drawManpu(name, ctx, anchor, 1.2, 1);
    const clips = ctx.calls.filter(c => c.name === "clip");
    assert.deepEqual(clips.map(c => c.path.length), [1, 1], name);
    [anchor.eyeLeft, anchor.eyeRight].forEach((eye, i) => {
      const [ellipse] = clips[i].path;
      assert.ok(near(ellipse, eye, 1e-6) && Math.abs(ellipse.rx - 13 * scale) < 1e-6 && Math.abs(ellipse.ry - 9 * scale) < 1e-6, name);
      const symbol = drawn(ctx).filter(c => near(c.origin, eye, 1e-6));
      const reach = Math.max(...symbol.flatMap(c => c.path).map(p => Math.hypot(p.x - eye.x, p.y - eye.y))) / scale;
      assert.ok(symbol.length > 0 && reach > 5 && reach < 9.5, `${name} is about as big as an iris (${reach})`);
    });
    assert.ok(drawn(ctx).filter(below(anchor)).every(c => c.clips === 1), `${name} is clipped`);
  }
});

test("the ellipsis's dots appear one by one and the light bulb lights up", () => {
  const strokes = (name, t) => { const ctx = recorder(); drawManpu(name, ctx, face(), t, 1); return ctx.calls.filter(c => c.name === "stroke").length; };
  assert.deepEqual([0.2, 0.55, 0.95, 2, 2.6 + 0.2].map(t => strokes("ellipsis", t)), [1, 2, 3, 3, 1], "and again while held");
  assert.ok(strokes("idea", 1) >= strokes("idea", 0.2) + 7, "rays once it is lit");
});

test("weight fades the whole emote and nothing draws without a face", () => {
  const ctx = recorder();
  drawManpu("anger", ctx, anchor(), 1, 0.25);
  assert.ok(ctx.calls.length > 0 && ctx.calls.every(c => c.alpha === undefined || c.alpha <= 0.25 + 1e-9));
  for (const [face, weight] of [[undefined, 1], [{ ...anchor(), width: 0 }, 1], [anchor(), 0]]) {
    const none = recorder();
    drawManpu("hearts", none, face, 1, weight);
    assert.equal(none.calls.length, 0);
  }
  const unknown = recorder();
  drawManpu("nod", unknown, anchor(), 1, 1);
  assert.equal(unknown.calls.length, 0);
});

test("frame math", () => {
  assert.ok(Math.abs(easeOutBack(0)) < 1e-9);
  assert.equal(easeOutBack(1), 1);
  assert.ok(easeOutBack(0.7) > 1, "overshoots before settling");
  assert.equal(particleTime(0.2, 1, 0.5, 2), undefined);
  assert.equal(particleTime(0.7, 1, 0.5, 2), 0.7 - 0.5);
  assert.ok(Math.abs(particleTime(4.7, 1, 0.5, 2) - 0.2) < 1e-9, "repeats while held");
  const p = localPoint({ x: 100, y: 100, width: 50, angle: Math.PI / 2 }, { x: 100, y: 150 });
  assert.ok(Math.abs(p.x - 100) < 1e-9 && Math.abs(p.y) < 1e-9);
  assert.equal(headTop(anchor()), -75);
  assert.equal(headTop({ ...anchor(), top: undefined }), -75);
  assert.equal(headTop({ ...anchor(), top: { x: 400, y: 0 } }), -130);
  assert.ok(Object.values(MANPU).every(e => typeof e.draw === "function"));
});
