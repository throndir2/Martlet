/** Each eye's iris and visible opening, for Martlet's drawings over the eyes (heart eyes, star eyes, dizzy swirls). A Live2D
 *  model draws its eyes from its own meshes: the irises (with their highlights) are the drawables the eyeball parameters
 *  move, and each iris is clipped by its eye white, so the eye white's triangles are the eye's opening as drawn, closing as
 *  the model blinks. For an eye the meshes can't give, a hint from vision (`EyeHint`) gives a sized iris that follows the
 *  gaze and an opening that closes with the eye's open parameter. */

import { type Carrier, type Face, type Pin, pinPoint, type Point, trackPin, type Tracked } from "./face.js";

/** Where the eye data came from: the model's meshes, a hint from vision, or nowhere (the drawings then use their own
 *  estimate). For both eyes together, the weakest of them. */
export type EyesFrom = "mesh" | "vision" | "estimate";

/** The most points and triangles of one eye's shape. */
export const EYE_SHAPE_POINTS = 512, EYE_SHAPE_TRIANGLES = 1024;

/** An iris for drawings over it: its middle and its radii across and down the face. */
export interface EyeIris { readonly x: number; readonly y: number; readonly rx: number; readonly ry: number }

/** An eye's visible opening between the eyelids: the union of `triangles` (three indices into `points` each) or, without
 *  them, the closed outline through `points`. No points: the eye is closed or hidden. */
export interface EyeOpening { readonly points: readonly Point[]; readonly triangles?: readonly number[] }

/** The eye fields of a face anchor (see the adapter's faceAnchor). */
export interface EyeFields {
  readonly eyesFrom: EyesFrom;
  readonly irisLeft?: EyeIris; readonly irisRight?: EyeIris;
  readonly eyeLeftShape?: EyeOpening; readonly eyeRightShape?: EyeOpening;
  readonly eyeLeft?: Point; readonly eyeRight?: Point;
}

/** One eye measured by vision. */
export interface EyeHintEye {
  readonly iris: { readonly x: number; readonly y: number; readonly r: number };
  readonly eye: { readonly x: number; readonly y: number; readonly rx: number; readonly ry: number };
}

/** The eyes measured by vision (`left` and `right` as the viewer sees them), in face widths from the face anchor's middle (x
 *  toward the viewer's right, y down, in the face's own frame before its roll), in the rest pose: the eyes open and looking
 *  straight ahead. The iris is its middle and radius, the eye the middle and radii of the opening between the eyelids. */
export interface EyeHint { readonly left?: EyeHintEye; readonly right?: EyeHintEye }

/** The hint's usable eyes (finite numbers, middles within 2 face widths, radii above 0 and at most 1), or undefined. */
export function readEyeHint(value: unknown): EyeHint | undefined {
  if (!value || typeof value !== "object") return undefined;
  const place = (n: unknown) => typeof n === "number" && Number.isFinite(n) && Math.abs(n) <= 2;
  const size = (n: unknown) => typeof n === "number" && Number.isFinite(n) && n > 0 && n <= 1;
  const eye = (candidate: unknown): EyeHintEye | undefined => {
    const { iris, eye } = (candidate ?? {}) as { iris?: Record<string, unknown>; eye?: Record<string, unknown> };
    if (!iris || !eye || !place(iris.x) || !place(iris.y) || !size(iris.r) || !place(eye.x) || !place(eye.y) ||
      !size(eye.rx) || !size(eye.ry)) return undefined;
    return Object.freeze({ iris: Object.freeze({ x: iris.x as number, y: iris.y as number, r: iris.r as number }),
      eye: Object.freeze({ x: eye.x as number, y: eye.y as number, rx: eye.rx as number, ry: eye.ry as number }) });
  };
  const { left, right } = value as { left?: unknown; right?: unknown };
  const l = eye(left), r = eye(right);
  return l || r ? Object.freeze({ ...(l ? { left: l } : {}), ...(r ? { right: r } : {}) }) : undefined;
}

/** A part of an eye white (one of the iris's clipping masks) that belongs to one eye: its vertices in that eye and its
 *  triangles over them. `watched` when it showed at rest, so hiding it (a closed-eye swap) empties the eye's shape. */
export interface MaskPart {
  readonly drawable: number;
  readonly watched: boolean;
  readonly vertices: readonly number[];
  readonly triangles: readonly number[];
}

/** An eye drawn by the model's meshes: its main iris drawable, its eye white and the eye opening's middle at rest (model
 *  units, y up). `triangles` is every part's triangles over their vertices in order, for the frames they all show. */
export interface MeshEye {
  readonly iris: number;
  readonly masks: readonly MaskPart[];
  readonly points: number;
  readonly triangles: readonly number[];
  readonly middle: Point;
}

/** The drawables at rest, as the eye finder reads them. */
export interface EyeDrawables {
  /** Interleaved x, y vertex positions (model units, y up). */
  vertices(drawable: number): ArrayLike<number>;
  indices(drawable: number): ArrayLike<number>;
  /** The drawable's clipping masks. */
  masks(drawable: number): readonly number[];
  /** Whether it shows (visible and not nearly transparent). */
  shown(drawable: number): boolean;
}

interface Extent { readonly u0: number; readonly u1: number; readonly v0: number; readonly v1: number }

/** The extent of vertices across (u) and down (v) a face, in its widths from its middle. */
function extent(vertices: ArrayLike<number>, face: Face, only?: readonly number[]): Extent | undefined {
  const c = Math.cos(face.roll), s = Math.sin(face.roll);
  let u0 = Infinity, u1 = -Infinity, v0 = Infinity, v1 = -Infinity;
  const count = only ? only.length : vertices.length / 2;
  for (let n = 0; n < count; n++) {
    const k = only ? only[n]! : n, x = vertices[2 * k]! - face.x, y = vertices[2 * k + 1]! - face.y;
    const u = (x * c + y * s) / face.width, v = (x * s - y * c) / face.width;
    if (u < u0) u0 = u; if (u > u1) u1 = u; if (v < v0) v0 = v; if (v > v1) v1 = v;
  }
  return [u0, u1, v0, v1].every(Number.isFinite) ? { u0, u1, v0, v1 } : undefined;
}

/**
 * The model's eyes around `face` (at rest) from the drawables the eyeball parameters move (`moving`): split into the viewer's
 * left and right eye by the face's middle, each eye's largest is its iris (smaller ones are highlights), and the iris's
 * clipping masks are its eye white. An eye is left out when its iris has no masks or isn't plausible: an iris 3% to 50% of
 * the face wide (3% to 60% high), its middle 4% to 60% of the face's width from the face's middle and inside its eye white,
 * an eye white at most 70% of the face wide and 60% high, the two eyes at least 12% of the face's width apart, and a shape
 * within EYE_SHAPE_POINTS and EYE_SHAPE_TRIANGLES. A mask both eyes share is split between them by each triangle's middle.
 */
export function meshEyes(face: Face, moving: readonly number[], drawables: EyeDrawables): { left?: MeshEye; right?: MeshEye } {
  if (![face.x, face.y, face.width, face.roll].every(Number.isFinite) || !(face.width > 0)) return {};
  const irises: { drawable: number; box: Extent; area: number }[] = [];
  for (const drawable of moving) {
    const box = extent(drawables.vertices(drawable), face);
    if (box) irises.push({ drawable, box, area: (box.u1 - box.u0) * (box.v1 - box.v0) });
  }
  const main = (side: -1 | 1) => irises.filter(i => Math.sign((i.box.u0 + i.box.u1) / 2) === side)
    .sort((a, b) => b.area - a.area)[0];
  const plausible = (iris: { drawable: number; box: Extent } | undefined) => {
    if (!iris) return undefined;
    const { u0, u1, v0, v1 } = iris.box, u = (u0 + u1) / 2, v = (v0 + v1) / 2;
    const masks = drawables.masks(iris.drawable);
    if (!masks.length || u1 - u0 < 0.03 || u1 - u0 > 0.5 || v1 - v0 < 0.03 || v1 - v0 > 0.6 || Math.abs(u) < 0.04 ||
      Math.abs(u) > 0.6 || Math.abs(v) > 0.5) return undefined;
    return { ...iris, u, v, masks };
  };
  let left = plausible(main(-1)), right = plausible(main(1));
  if (left && right && right.u - left.u < 0.12) left = right = undefined;
  // A mask in both eyes' lists is split between them halfway between the irises.
  const split = left && right ? (left.u + right.u) / 2 : undefined;
  const shared = new Set(left && right ? left.masks.filter(m => right!.masks.includes(m)) : []);
  const c = Math.cos(face.roll), s = Math.sin(face.roll);
  const build = (iris: NonNullable<ReturnType<typeof plausible>>, side: -1 | 1): MeshEye | undefined => {
    const masks: MaskPart[] = [], triangles: number[] = [];
    let points = 0;
    for (const drawable of iris.masks) {
      const vertices = drawables.vertices(drawable), indices = drawables.indices(drawable);
      const local = new Map<number, number>(), kept: number[] = [];
      for (let t = 0; t + 2 < indices.length; t += 3) {
        const a = indices[t]!, b = indices[t + 1]!, d = indices[t + 2]!;
        if (shared.has(drawable) && split !== undefined) {
          const x = (vertices[2 * a]! + vertices[2 * b]! + vertices[2 * d]!) / 3 - face.x;
          const y = (vertices[2 * a + 1]! + vertices[2 * b + 1]! + vertices[2 * d + 1]!) / 3 - face.y;
          if (Math.sign((x * c + y * s) / face.width - split) !== side) continue;
        }
        for (const k of [a, b, d]) {
          if (2 * k + 1 >= vertices.length) return undefined;
          if (!local.has(k)) local.set(k, local.size);
          kept.push(local.get(k)!);
        }
      }
      if (!kept.length) continue;
      masks.push(Object.freeze({ drawable, watched: drawables.shown(drawable), vertices: Object.freeze([...local.keys()]),
        triangles: Object.freeze(kept) }));
      for (const k of kept) triangles.push(points + k);
      points += local.size;
    }
    if (!masks.length || points > EYE_SHAPE_POINTS || triangles.length > 3 * EYE_SHAPE_TRIANGLES) return undefined;
    // The opening's middle at rest, which the iris must sit in.
    let u0 = Infinity, u1 = -Infinity, v0 = Infinity, v1 = -Infinity;
    for (const part of masks) {
      const box = extent(drawables.vertices(part.drawable), face, part.vertices);
      if (!box) return undefined;
      u0 = Math.min(u0, box.u0); u1 = Math.max(u1, box.u1); v0 = Math.min(v0, box.v0); v1 = Math.max(v1, box.v1);
    }
    // The opening must hold its iris and be about an eye's size: at most 70% of the face wide and 60% high.
    if (iris.u < u0 || iris.u > u1 || iris.v < v0 || iris.v > v1 || u1 - u0 > 0.7 || v1 - v0 > 0.6) return undefined;
    const u = (u0 + u1) / 2 * face.width, v = (v0 + v1) / 2 * face.width;
    return Object.freeze({ iris: iris.drawable, masks: Object.freeze(masks), points, triangles: Object.freeze(triangles),
      middle: Object.freeze({ x: face.x + u * c + v * s, y: face.y + u * s - v * c }) });
  };
  const l = left && build(left, -1), r = right && build(right, 1);
  return { ...(l ? { left: l } : {}), ...(r ? { right: r } : {}) };
}

/** An iris now from its drawable's vertices: its middle and its half sizes across and down a face rolled `roll` (radians,
 *  counterclockwise; model units, y up). */
export function irisBox(vertices: ArrayLike<number>, roll: number): { x: number; y: number; rx: number; ry: number } | undefined {
  const c = Math.cos(roll), s = Math.sin(roll);
  let u0 = Infinity, u1 = -Infinity, v0 = Infinity, v1 = -Infinity;
  for (let k = 0; k + 1 < vertices.length; k += 2) {
    const x = vertices[k]!, y = vertices[k + 1]!, u = x * c + y * s, v = x * s - y * c;
    if (u < u0) u0 = u; if (u > u1) u1 = u; if (v < v0) v0 = v; if (v > v1) v1 = v;
  }
  if (![u0, u1, v0, v1].every(Number.isFinite)) return undefined;
  const u = (u0 + u1) / 2, v = (v0 + v1) / 2;
  return { x: u * c + v * s, y: u * s - v * c, rx: (u1 - u0) / 2, ry: (v1 - v0) / 2 };
}

/** An eye's opening now from its eye white (`at` a drawable's vertices now, `shown` whether it shows), each point made by
 *  `point`: the union of its triangles, empty when a part that showed at rest is hidden or nearly transparent. */
export function eyeShape<P>(eye: MeshEye, at: (drawable: number) => ArrayLike<number>, shown: (drawable: number) => boolean,
  point: (x: number, y: number) => P): { points: P[]; triangles: readonly number[] } {
  const showing = eye.masks.map(part => !part.watched || shown(part.drawable));
  const all = showing.every(Boolean);
  const points: P[] = [], triangles: number[] = [];
  eye.masks.forEach((part, m) => {
    if (!showing[m]) return;
    const vertices = at(part.drawable), offset = points.length;
    for (const k of part.vertices) points.push(point(vertices[2 * k]!, vertices[2 * k + 1]!));
    if (!all) for (const k of part.triangles) triangles.push(offset + k);
  });
  return { points, triangles: all ? eye.triangles : triangles };
}

/** Pins an eye's middle at rest to the carriers around it (see face.ts), or undefined with too few near. */
export function pinEye(middle: Point, carriers: readonly Carrier[], face: Face): Pin | undefined {
  const reach = (1.5 * face.width) ** 2;
  return pinPoint(middle.x, middle.y, carriers.filter(c => (c.x - face.x) ** 2 + (c.y - face.y) ** 2 <= reach), 0.05 * face.width);
}

/** Where a point of the face at rest is now and how a small step from it maps (model units): from its pin when it has one,
 *  otherwise moved with the whole face from `rest` to `now` (turned by the roll, scaled by the width). */
export function eyeFrame(point: Point, pin: Pin | undefined, at: (drawable: number) => ArrayLike<number>, rest: Face,
  now: Face): Tracked | undefined {
  const pinned = pin && trackPin(pin, at);
  if (pinned) return pinned;
  const k = now.width / rest.width, turn = now.roll - rest.roll, c = k * Math.cos(turn), s = k * Math.sin(turn);
  const dx = point.x - rest.x, dy = point.y - rest.y;
  const tracked = { x: now.x + c * dx - s * dy, y: now.y + s * dx + c * dy, map: [c, -s, s, c] as const };
  return [tracked.x, tracked.y, c, s].every(Number.isFinite) ? tracked : undefined;
}

/** A hinted eye's middle at rest (model units, y up) on the face anchor at rest, `face`. */
export function hintMiddle(eye: EyeHintEye, face: Face): Point {
  const c = Math.cos(face.roll), s = Math.sin(face.roll), x = eye.eye.x * face.width, y = eye.eye.y * face.width;
  return { x: face.x + x * c + y * s, y: face.y + x * s - y * c };
}

/** The outline's points around an eye's opening: 24 around an ellipse, closing toward its lower part as `open` goes to 0. */
export const OUTLINE_POINTS = 24;

/**
 * A hinted eye now, from its frame (`frame`, see eyeFrame, at the eye's middle), the face's width and roll at rest, the gaze
 * (`gazeX` toward the viewer's right, `gazeY` up, -1 to 1) and how open it is (0 closed, 1 as at rest): the iris moved
 * within the room the eye leaves around it, and the opening as an ellipse. Model units, y up; `point` makes each output point.
 */
export function hintEye<P>(hint: EyeHintEye, frame: Tracked, rest: Face, gazeX: number, gazeY: number, open: number,
  point: (x: number, y: number) => P): { middle: P; iris: { x: number; y: number; rx: number; ry: number }; outline: P[] } {
  const c = Math.cos(rest.roll), s = Math.sin(rest.roll), w = rest.width, [m11, m12, m21, m22] = frame.map;
  // A step in face widths across (u) and down (v) the face at rest, as it shows now.
  const step = (u: number, v: number) => {
    const x = (u * c + v * s) * w, y = (u * s - v * c) * w;
    return { x: m11 * x + m12 * y, y: m21 * x + m22 * y };
  };
  const roomX = Math.max(0, hint.eye.rx - hint.iris.r), roomY = Math.max(0, hint.eye.ry - hint.iris.r);
  const clamp = (value: number, room: number, rest: number) => {
    const most = Math.max(room, Math.abs(rest));
    return Math.max(-most, Math.min(most, value));
  };
  const restX = hint.iris.x - hint.eye.x, restY = hint.iris.y - hint.eye.y;
  const g = (value: number) => Number.isFinite(value) ? Math.max(-1, Math.min(1, value)) : 0;
  const offset = step(clamp(restX + g(gazeX) * roomX, roomX, restX), clamp(restY - g(gazeY) * roomY, roomY, restY));
  const across = step(hint.iris.r, 0), down = step(0, hint.iris.r);
  const iris = { x: frame.x + offset.x, y: frame.y + offset.y, rx: Math.hypot(across.x, across.y), ry: Math.hypot(down.x, down.y) };
  const opening = Number.isFinite(open) ? Math.max(0, Math.min(1.2, open)) : 1;
  const outline: P[] = [];
  for (let k = 0; k < OUTLINE_POINTS; k++) {
    const t = 2 * Math.PI * k / OUTLINE_POINTS;
    // The upper lid comes down most: closed, the slit lies a little below the middle.
    const p = step(hint.eye.rx * Math.cos(t), hint.eye.ry * (0.4 * (1 - Math.min(1, opening)) + opening * Math.sin(t)));
    outline.push(point(frame.x + p.x, frame.y + p.y));
  }
  return { middle: point(frame.x, frame.y), iris, outline };
}
