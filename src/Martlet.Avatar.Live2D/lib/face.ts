/** Where a Live2D model's face is, for Martlet's own drawings over it (a blush glow, manga symbols). Live2D models carry no
 *  face landmarks, so it is estimated at rest: from an authored head/face hit area's mesh, from meshes whose IDs name the
 *  face or cheeks, or from the shape of the top of the model; a hint (from vision) overrides the estimate. The estimate is
 *  then pinned to the model's own meshes (see `pinFace`): to the face's skin when one drawable holds it (see `faceSkin`),
 *  otherwise to the vertices that ride the head. So it follows everything that moves the head as Live2D draws it: idle
 *  motions, body sway, breathing, the cursor look, gestures, and a head that physics turns. */

export interface Point { readonly x: number; readonly y: number }

/** A face in model units (y up): the middle of the eye line, the face's width and its roll (radians, counterclockwise). */
export interface Face { readonly x: number; readonly y: number; readonly width: number; readonly roll: number }

/** The features Martlet draws around, in the same units as the face (y up). */
export interface FaceFeatures {
  readonly x: number; readonly y: number; readonly width: number; readonly roll: number;
  readonly cheekLeft: Point; readonly cheekRight: Point; readonly eyeLeft: Point; readonly eyeRight: Point;
  readonly mouth: Point; readonly top: Point;
}

/** A face found by vision, as fractions of the model's canvas (0,0 top left, y down; width of the canvas width). */
export interface FaceHint {
  readonly x: number; readonly y: number; readonly width: number;
  readonly cheekLeft?: Point; readonly cheekRight?: Point;
}

export interface Box { readonly minX: number; readonly maxX: number; readonly minY: number; readonly maxY: number }

export const HEAD_AREA = /head|face|頭|顔|头|脸|臉|atama|kao/i;
export const FACE_MESH = /face|顔|脸|臉|kao/i;
export const CHEEK_MESH = /cheek|頬|ほほ|腮|hoho/i;

/** How the face is found: the meshes to measure (and how to read their box), or a fixed face to move with the angles. */
export type FaceSource =
  | { readonly kind: "head" | "skin" | "cheeks"; readonly drawables: readonly number[] }
  | { readonly kind: "fixed"; readonly face: Face };

/** Picks how to find the face from the hit areas (`{ id, name }`, the id an ArtMesh's) and the drawables' IDs. */
export function faceSource(hitAreas: readonly { readonly id: string; readonly name: string }[], drawableIds: readonly string[],
  layout: () => Face | undefined): FaceSource | undefined {
  const index = new Map(drawableIds.map((id, i) => [id, i]));
  const head = hitAreas.filter(h => HEAD_AREA.test(h.name) || HEAD_AREA.test(h.id))
    .map(h => index.get(h.id)).filter((i): i is number => i !== undefined);
  if (head.length) return { kind: "head", drawables: head };
  const skin = drawableIds.flatMap((id, i) => FACE_MESH.test(id) ? [i] : []);
  if (skin.length) return { kind: "skin", drawables: skin };
  const cheeks = drawableIds.flatMap((id, i) => CHEEK_MESH.test(id) ? [i] : []);
  if (cheeks.length) return { kind: "cheeks", drawables: cheeks };
  const face = layout();
  return face ? { kind: "fixed", face } : undefined;
}

/** The box of the vertices (interleaved x, y). */
export function bounds(meshes: Iterable<ArrayLike<number>>): Box | undefined {
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const vertices of meshes)
    for (let v = 0; v + 1 < vertices.length; v += 2) {
      const x = vertices[v]!, y = vertices[v + 1]!;
      if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
  return Number.isFinite(minX) && maxX > minX && maxY > minY ? { minX, maxX, minY, maxY } : undefined;
}

/** The face from the box of the meshes a source names. */
export function faceFromBox(kind: "head" | "skin" | "cheeks", box: Box, roll = 0): Face {
  const width = box.maxX - box.minX, height = box.maxY - box.minY, x = (box.minX + box.maxX) / 2;
  switch (kind) {
    // A head area takes in the hair: the face is narrower, the eyes a little below its middle.
    case "head": return { x, y: box.minY + 0.42 * height, width: 0.72 * width, roll };
    // The face's skin runs from the chin to under the fringe: the eyes sit a little above its middle.
    case "skin": return { x, y: box.minY + 0.55 * height, width, roll };
    // Both cheeks: the eyes are above them and the face wider.
    case "cheeks": return { x, y: (box.minY + box.maxY) / 2 + 0.25 * width, width: 1.3 * width, roll };
  }
}

/** The face estimated from the visible meshes: the width of the top of the model (hair and head) gives the head's size. */
export function faceFromLayout(meshes: readonly ArrayLike<number>[]): Face | undefined {
  const box = bounds(meshes);
  if (!box) return undefined;
  const height = box.maxY - box.minY;
  const xs: number[] = [];
  for (const vertices of meshes)
    for (let v = 0; v + 1 < vertices.length; v += 2) {
      const y = vertices[v + 1]!;
      if (y <= box.maxY - 0.04 * height && y >= box.maxY - 0.14 * height) xs.push(vertices[v]!);
    }
  if (xs.length < 4) return undefined;
  xs.sort((a, b) => a - b);
  const low = xs[Math.floor(xs.length * 0.1)]!, high = xs[Math.ceil(xs.length * 0.9) - 1]!;
  const head = Math.min(high - low, box.maxX - box.minX, 0.6 * height);
  if (!(head > 0)) return undefined;
  // Tuned on the bundled Hiyori and a VTuber-style bust: the middle 80% of the band's vertices spans about the face's
  // width, and the eyes sit about that far below the top of the hair.
  return { x: (low + high) / 2, y: box.maxY - 0.92 * head, width: head, roll: 0 };
}

/** A vision hint as a face in model units, for a model canvas `canvasWidth` by `canvasHeight` centered on the origin. */
export function faceFromHint(hint: FaceHint, canvasWidth: number, canvasHeight: number): Face | undefined {
  if (![hint.x, hint.y, hint.width].every(Number.isFinite) || !(hint.width > 0)) return undefined;
  return { x: (hint.x - 0.5) * canvasWidth, y: (0.5 - hint.y) * canvasHeight, width: hint.width * canvasWidth, roll: 0 };
}

/** How far the head really rolls for ParamAngleZ (degrees), in radians, counterclockwise (model units, y up). A positive
 *  ParamAngleZ tips the top of the head toward the viewer's right (clockwise as the viewer sees it, as on Hiyori), so its
 *  roll is negative. The parameter's ±30 is a range, not an angle: artists roll the head mesh far less (about 10° on
 *  Hiyori), so it is scaled down and clamped. */
export function headRoll(angleZ: number): number {
  const degrees = Math.max(-MAX_ROLL_DEGREES, Math.min(MAX_ROLL_DEGREES, -ROLL_SCALE * angleZ));
  return Number.isFinite(degrees) && degrees !== 0 ? degrees * Math.PI / 180 : 0;
}
const ROLL_SCALE = 0.35, MAX_ROLL_DEGREES = 12;

/** A fixed face moved by the head's angles (degrees; Cubism's ParamAngleX turns right, Y up, Z tips the top of the head
 *  toward the viewer's right). */
export function turnFace(face: Face, angleX: number, angleY: number, angleZ: number): Face {
  const roll = headRoll(angleZ);
  // The head turns about the neck, below the face.
  const pivot = { x: face.x, y: face.y - 0.9 * face.width };
  const x = face.x + 0.12 * face.width * (angleX / 30), y = face.y + 0.1 * face.width * (angleY / 30);
  const dx = x - pivot.x, dy = y - pivot.y, c = Math.cos(roll), s = Math.sin(roll);
  return { x: pivot.x + dx * c - dy * s, y: pivot.y + dx * s + dy * c, width: face.width, roll: face.roll + roll };
}

/** The eyes, cheeks, mouth and top of the head around `face` (left and right as the viewer sees them). */
export function faceFeatures(face: Face): FaceFeatures {
  const c = Math.cos(face.roll), s = Math.sin(face.roll), w = face.width;
  const at = (dx: number, dy: number): Point => ({ x: face.x + (dx * c - dy * s) * w, y: face.y + (dx * s + dy * c) * w });
  return { ...face, eyeLeft: at(-0.2, 0), eyeRight: at(0.2, 0), cheekLeft: at(-0.27, -0.2), cheekRight: at(0.27, -0.2),
    mouth: at(0, -0.36), top: at(0, 0.62) };
}

// ---------- following the face on the model's own meshes ----------

/** The head and body angles: what turns, tilts and sways the whole face. Every other parameter only changes its shape. */
export const HEAD_ANGLES: readonly string[] = ["ParamAngleX", "ParamAngleY", "ParamAngleZ"];
export const POSE_PARAMETERS: ReadonlySet<string> = new Set([...HEAD_ANGLES, "ParamBodyAngleX", "ParamBodyAngleY",
  "ParamBodyAngleZ", "ParamBreath"]);

/** A mesh vertex the face is pinned to (a vertex of the face's skin, or one that rides the head rigidly: the skin, outline,
 *  ears; not hair physics, eyelids, irises or the mouth), with where it was at rest in model units (y up). */
export interface Carrier { readonly drawable: number; readonly vertex: number; readonly x: number; readonly y: number }

/** The drawables at rest, as the skin search reads them. */
export interface SkinDrawables {
  readonly count: number;
  /** Whether it shows (visible and not nearly transparent). */
  shown(drawable: number): boolean;
  /** Interleaved x, y vertex positions (model units, y up). */
  vertices(drawable: number): ArrayLike<number>;
  indices(drawable: number): ArrayLike<number>;
  /** Its place in the drawing order: a higher one is drawn over a lower one. */
  order(drawable: number): number;
}

/** Whether `point` lies in one of the triangles (`indices`, three each) over `vertices` (interleaved x, y). */
function holds(vertices: ArrayLike<number>, indices: ArrayLike<number>, point: Point): boolean {
  for (let t = 0; t + 2 < indices.length; t += 3) {
    const a = 2 * indices[t]!, b = 2 * indices[t + 1]!, c = 2 * indices[t + 2]!;
    const ax = vertices[a]!, ay = vertices[a + 1]!, bx = vertices[b]!, by = vertices[b + 1]!, cx = vertices[c]!, cy = vertices[c + 1]!;
    const d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
    if (!(Math.abs(d) > 1e-12)) continue;
    const u = ((by - cy) * (point.x - cx) + (cx - bx) * (point.y - cy)) / d;
    const v = ((cy - ay) * (point.x - cx) + (ax - cx) * (point.y - cy)) / d;
    if (u >= -1e-9 && v >= -1e-9 && u + v <= 1 + 1e-9) return true;
  }
  return false;
}

/**
 * The face's skin around `face` (at rest): the drawable a blush painted on the cheeks moves with. Live2D draws a face in
 * layers that move apart as the head turns and nods (the back hair, the skin, the eyes, nose and mouth over it, the front
 * hair), so the face's points follow one layer, the skin: of the drawables showing at rest whose triangles hold the face's
 * middle and both cheeks, the one drawn highest. The back hair is drawn under the skin, and the eyes, nose, mouth and front
 * hair don't hold all three points. A drawable over three face widths across or high is an overlay over the whole picture,
 * not skin. Undefined when no drawable holds the three points.
 */
export function faceSkin(face: Face, drawables: SkinDrawables): number | undefined {
  if (![face.x, face.y, face.width, face.roll].every(Number.isFinite) || !(face.width > 0)) return undefined;
  const { cheekLeft, cheekRight } = faceFeatures(face), points = [{ x: face.x, y: face.y }, cheekLeft, cheekRight];
  const largest = 3 * face.width;
  let skin: number | undefined, highest = -Infinity;
  for (let d = 0; d < drawables.count; d++) {
    const order = drawables.order(d);
    if (!(order > highest) || !drawables.shown(d)) continue;
    const vertices = drawables.vertices(d), box = bounds([vertices]);
    if (!box || box.maxX - box.minX > largest || box.maxY - box.minY > largest) continue;
    const indices = drawables.indices(d);
    if (points.every(p => p.x >= box.minX && p.x <= box.maxX && p.y >= box.minY && p.y <= box.maxY && holds(vertices, indices, p))) {
      skin = d;
      highest = order;
    }
  }
  return skin;
}

/** Every vertex of drawable `skin` as it is now, as carriers. */
export function skinCarriers(skin: number, vertices: ArrayLike<number>): Carrier[] {
  const carriers: Carrier[] = [];
  for (let v = 0; 2 * v + 1 < vertices.length; v++) carriers.push({ drawable: skin, vertex: v, x: vertices[2 * v]!, y: vertices[2 * v + 1]! });
  return carriers;
}

/** A point pinned to the carriers around it, by moving least squares: each carrier's weight and rest offset from their
 *  weighted middle, and the inverse of their weighted spread (row-major), absent when they lie along a line. */
export interface Pin {
  readonly x: number; readonly y: number;
  readonly carriers: readonly Carrier[];
  readonly weights: readonly number[];
  readonly middle: Point;
  readonly offsets: readonly Point[];
  readonly spread: number;
  readonly inverse?: readonly [number, number, number, number];
}

/** A pinned point now, with the linear map (row-major) that takes a small step from it at rest to the same step now: how the
 *  face there is turned, tilted, scaled and squashed. */
export interface Tracked { readonly x: number; readonly y: number; readonly map: readonly [number, number, number, number] }

const PIN_CARRIERS = 24;

/** Pins (`x`, `y`) to its nearest carriers; undefined with fewer than three. `softness` (model units) keeps a carrier right
 *  on the point from taking all the weight. */
export function pinPoint(x: number, y: number, carriers: readonly Carrier[], softness: number): Pin | undefined {
  if (carriers.length < 3 || ![x, y, softness].every(Number.isFinite)) return undefined;
  const nearest = carriers.map(carrier => ({ carrier, distance: (carrier.x - x) ** 2 + (carrier.y - y) ** 2 }))
    .sort((a, b) => a.distance - b.distance).slice(0, PIN_CARRIERS);
  const weights = nearest.map(n => 1 / (n.distance + softness * softness + 1e-12));
  const total = weights.reduce((sum, w) => sum + w, 0);
  const middle = { x: nearest.reduce((sum, n, i) => sum + weights[i]! * n.carrier.x, 0) / total,
    y: nearest.reduce((sum, n, i) => sum + weights[i]! * n.carrier.y, 0) / total };
  const offsets = nearest.map(n => ({ x: n.carrier.x - middle.x, y: n.carrier.y - middle.y }));
  let xx = 0, xy = 0, yy = 0;
  offsets.forEach((p, i) => { xx += weights[i]! * p.x * p.x; xy += weights[i]! * p.x * p.y; yy += weights[i]! * p.y * p.y; });
  const determinant = xx * yy - xy * xy;
  // Carriers along one line can't say how the face squashes across it; they still turn and scale it (see trackPin).
  const inverse = determinant > 0.005 * (xx + yy) ** 2
    ? [yy / determinant, -xy / determinant, -xy / determinant, xx / determinant] as const : undefined;
  return { x, y, carriers: nearest.map(n => n.carrier), weights, middle, offsets, spread: xx + yy, ...(inverse ? { inverse } : {}) };
}

/** Where a pinned point is now, from its carriers as the model last posed them (`at(drawable)` gives a drawable's
 *  interleaved vertex positions); undefined when one is missing. */
export function trackPin(pin: Pin, at: (drawable: number) => ArrayLike<number>): Tracked | undefined {
  const now = new Float64Array(2 * pin.carriers.length);
  let total = 0, mx = 0, my = 0, last = -1, vertices: ArrayLike<number> | undefined;
  for (let i = 0; i < pin.carriers.length; i++) {
    const carrier = pin.carriers[i]!;
    if (carrier.drawable !== last) { vertices = at(carrier.drawable); last = carrier.drawable; }
    const x = vertices?.[2 * carrier.vertex], y = vertices?.[2 * carrier.vertex + 1];
    if (typeof x !== "number" || typeof y !== "number" || !Number.isFinite(x) || !Number.isFinite(y)) return undefined;
    const w = pin.weights[i]!;
    now[2 * i] = x; now[2 * i + 1] = y;
    total += w; mx += w * x; my += w * y;
  }
  mx /= total; my /= total;
  let b11 = 0, b12 = 0, b21 = 0, b22 = 0, dot = 0, cross = 0;
  for (let i = 0; i < pin.carriers.length; i++) {
    const w = pin.weights[i]!, p = pin.offsets[i]!, qx = now[2 * i]! - mx, qy = now[2 * i + 1]! - my;
    b11 += w * qx * p.x; b12 += w * qx * p.y; b21 += w * qy * p.x; b22 += w * qy * p.y;
    dot += w * (p.x * qx + p.y * qy); cross += w * (p.x * qy - p.y * qx);
  }
  let map: [number, number, number, number];
  if (pin.inverse) {
    const [i11, i12, i21, i22] = pin.inverse;
    map = [b11 * i11 + b12 * i21, b11 * i12 + b12 * i22, b21 * i11 + b22 * i21, b21 * i12 + b22 * i22];
  } else if (pin.spread > 0) {
    const a = dot / pin.spread, b = cross / pin.spread;
    map = [a, -b, b, a];
  } else map = [1, 0, 0, 1];
  const dx = pin.x - pin.middle.x, dy = pin.y - pin.middle.y;
  const x = mx + map[0] * dx + map[1] * dy, y = my + map[2] * dx + map[3] * dy;
  return [x, y, ...map].every(Number.isFinite) ? { x, y, map } : undefined;
}

const FACE_POINTS = ["middle", "eyeLeft", "eyeRight", "cheekLeft", "cheekRight", "mouth", "top"] as const;
type FacePoint = typeof FACE_POINTS[number];

/** A face at rest pinned to the model's meshes (see `pinFace`). */
export interface PinnedFace { readonly face: Face; readonly pins: Readonly<Record<FacePoint, Pin>> }

/** How a cheek's surface shows now: the steps, in model units, for one face width across it (toward the viewer's right) and
 *  down it (toward the chin). A turned head squashes the far cheek; a tilted one turns both. */
export interface CheekFrame { readonly right: Point; readonly down: Point }

export interface TrackedFace extends FaceFeatures { readonly cheekLeftFrame: CheekFrame; readonly cheekRightFrame: CheekFrame }

/** Pins `face` (at rest) to the carriers within reach of it; undefined when too few are near. */
export function pinFace(face: Face, carriers: readonly Carrier[]): PinnedFace | undefined {
  if (![face.x, face.y, face.width, face.roll].every(Number.isFinite) || !(face.width > 0)) return undefined;
  const reach = (1.5 * face.width) ** 2;
  const near = carriers.filter(c => (c.x - face.x) ** 2 + (c.y - face.y) ** 2 <= reach);
  const features = faceFeatures(face);
  const points: Record<FacePoint, Point> = { middle: features, eyeLeft: features.eyeLeft, eyeRight: features.eyeRight,
    cheekLeft: features.cheekLeft, cheekRight: features.cheekRight, mouth: features.mouth, top: features.top };
  const pins = {} as Record<FacePoint, Pin>;
  for (const name of FACE_POINTS) {
    const pin = pinPoint(points[name].x, points[name].y, near, 0.05 * face.width);
    if (!pin) return undefined;
    pins[name] = pin;
  }
  return { face, pins };
}

/** The pinned face as the model posed it last (`at` as for `trackPin`): its middle, width and roll from the meshes around
 *  the eyes, the features where their carriers took them, and each cheek's surface. Undefined when a carrier is missing. */
export function trackFace(pinned: PinnedFace, at: (drawable: number) => ArrayLike<number>): TrackedFace | undefined {
  const tracked = {} as Record<FacePoint, Tracked>;
  for (const name of FACE_POINTS) {
    const point = trackPin(pinned.pins[name], at);
    if (!point) return undefined;
    tracked[name] = point;
  }
  const { width, roll } = pinned.face, c = Math.cos(roll), s = Math.sin(roll);
  const right = { x: width * c, y: width * s }, down = { x: width * s, y: -width * c };
  const apply = (map: Tracked["map"], v: Point): Point => ({ x: map[0] * v.x + map[1] * v.y, y: map[2] * v.x + map[3] * v.y });
  const across = apply(tracked.middle.map, right);
  const point = (t: Tracked): Point => ({ x: t.x, y: t.y });
  const frame = (t: Tracked): CheekFrame => ({ right: apply(t.map, right), down: apply(t.map, down) });
  return { x: tracked.middle.x, y: tracked.middle.y, width: Math.hypot(across.x, across.y), roll: Math.atan2(across.y, across.x),
    eyeLeft: point(tracked.eyeLeft), eyeRight: point(tracked.eyeRight), cheekLeft: point(tracked.cheekLeft),
    cheekRight: point(tracked.cheekRight), mouth: point(tracked.mouth), top: point(tracked.top),
    cheekLeftFrame: frame(tracked.cheekLeft), cheekRightFrame: frame(tracked.cheekRight) };
}
