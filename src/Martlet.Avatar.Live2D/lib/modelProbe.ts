import { LIMITS, type LocalModelBundle } from "./assets.js";
import { boundedInteger, requireCondition } from "./diagnostics.js";
import { type EyeHint, meshEyes, type MeshEye, pinEye } from "./eyes.js";
import { type Carrier, type Face, faceFromLayout, faceSkin, faceSource, type FaceSource, HEAD_ANGLES, type Pin, POSE_PARAMETERS,
  skinCarriers } from "./face.js";
import type { Parameter } from "./mapping.js";
import type { CubismModel } from "./sdk.js";

// What the adapter reads from a loaded model by asking it: its geometry limits, where its face and the face's skin are, the
// vertices that ride the head, and the eyes its meshes draw. Each probe puts every parameter it moves back.

/** The eyes found at load (see eyes.ts) and from vision: the face at rest they are measured on, the eyes the meshes draw
 *  (`found`) and the ones in use (`mesh`: those that don't clearly miss the measured eye), the hinted ones, each eye's middle
 *  pinned to the face's carriers, and how long finding them took. */
export interface EyeState {
  readonly face?: Face;
  readonly found?: { readonly left?: MeshEye; readonly right?: MeshEye };
  readonly mesh: { readonly left?: MeshEye; readonly right?: MeshEye };
  readonly meshPins: { readonly left?: Pin; readonly right?: Pin };
  readonly hint?: EyeHint;
  readonly hintPins: { readonly left?: Pin; readonly right?: Pin };
  readonly milliseconds: number;
}
export const NO_EYES: EyeState = Object.freeze({ mesh: {}, meshPins: {}, hintPins: {}, milliseconds: 0 });

/** Highest vertex of the visible meshes in model units (y up), or undefined when nothing is visible. */
export function visibleTop(model: CubismModel): number | undefined {
  let top = Number.NEGATIVE_INFINITY;
  for (let i = 0; i < model.getDrawableCount(); i++) {
    if (!model.getDrawableDynamicFlagIsVisible(i) || model.getDrawableOpacity(i) < 0.05) continue;
    const vertices = model.getDrawableVertices(i);
    for (let v = 1; v < vertices.length; v += 2) top = Math.max(top, vertices[v]!);
  }
  return Number.isFinite(top) ? top : undefined;
}

/** How to find the face (see face.ts): from the model's resting pose, which the fixed estimate is measured in. */
export function findFace(model: CubismModel, bundle: LocalModelBundle): FaceSource | undefined {
  const count = model.getDrawableCount();
  const ids: string[] = [];
  if (model.getDrawableId) for (let i = 0; i < count; i++) ids.push(model.getDrawableId(i).getString().s);
  return faceSource(bundle.description.hitAreas, ids, () => {
    const visible: Float32Array[] = [];
    for (let i = 0; i < count; i++)
      if (model.getDrawableDynamicFlagIsVisible(i) && model.getDrawableOpacity(i) >= 0.05) visible.push(model.getDrawableVertices(i));
    return faceFromLayout(visible);
  });
}

/**
 * The face's skin around `face` (see faceSkin): its ID and every vertex of it as carriers, which the face then follows as
 * Core deforms the skin, whatever moves the head (its angles, or parameters that physics drives from them). Undefined when
 * no drawable holds the face.
 */
export function findSkin(model: CubismModel, face: Face): { id: string; carriers: Carrier[] } | undefined {
  const orders = model.getDrawableRenderOrders();
  const skin = faceSkin(face, {
    count: model.getDrawableCount(),
    shown: i => model.getDrawableDynamicFlagIsVisible(i) && model.getDrawableOpacity(i) >= 0.05,
    vertices: i => model.getDrawableVertices(i),
    indices: i => model.getDrawableVertexIndices(i),
    order: i => orders[i] ?? i,
  });
  return skin === undefined ? undefined
    : { id: model.getDrawableId(skin).getString().s, carriers: skinCarriers(skin, model.getDrawableVertices(skin)) };
}

/**
 * The vertices around `face` that ride the head rigidly. Live2D reports every mesh as it deforms it, so the model is asked:
 * each head angle is moved in turn (what moves is the head), then every other parameter at once to its maximum and to its
 * minimum (what moves then deforms on its own: hair physics, blinking, the eyes' gaze, the mouth, the brows). Every
 * parameter is put back. A parameter that moves the whole head (a model's own position or head parameter) would leave
 * nothing, so then each is tried alone and those are skipped. Empty when the model can't tell.
 */
export function findCarriers(model: CubismModel, face: Face, parameters: readonly Parameter[]): Carrier[] {
  const read = model.getParameterValueByIndex?.bind(model);
  if (!read) return [];
  const reach = (1.5 * face.width) ** 2, candidates: Carrier[] = [];
  for (let i = 0; i < model.getDrawableCount(); i++) {
    if (!model.getDrawableDynamicFlagIsVisible(i) || model.getDrawableOpacity(i) < 0.05) continue;
    const vertices = model.getDrawableVertices(i);
    for (let v = 0; 2 * v + 1 < vertices.length; v++) {
      const x = vertices[2 * v]!, y = vertices[2 * v + 1]!;
      if ((x - face.x) ** 2 + (y - face.y) ** 2 <= reach) candidates.push({ drawable: i, vertex: v, x, y });
    }
  }
  if (candidates.length < 3 || candidates.length > 60_000) return [];
  const base = parameters.map(p => read(p.index));
  // How far each candidate moves with these parameters (positions in `parameters`) set, from the pose now.
  const probe = (values: ReadonlyMap<number, number>): Float64Array => {
    for (const [k, value] of values) model.setParameterValueByIndex(parameters[k]!.index, value);
    model.update();
    const moved = new Float64Array(candidates.length);
    let last = -1, vertices: Float32Array | undefined;
    candidates.forEach((c, n) => {
      if (c.drawable !== last) { vertices = model.getDrawableVertices(c.drawable); last = c.drawable; }
      moved[n] = Math.hypot(vertices![2 * c.vertex]! - c.x, vertices![2 * c.vertex + 1]! - c.y);
    });
    for (const k of values.keys()) model.setParameterValueByIndex(parameters[k]!.index, base[k]!);
    return moved;
  };
  const turns = 0.02 * face.width, deforms = 0.01 * face.width;
  const head = new Float64Array(candidates.length);
  try {
    for (const id of HEAD_ANGLES) {
      const k = parameters.findIndex(p => p.id === id);
      if (k < 0) continue;
      const p = parameters[k]!, far = p.maximum - base[k]! >= base[k]! - p.minimum ? p.maximum : p.minimum;
      probe(new Map([[k, far]])).forEach((d, n) => { head[n] = Math.max(head[n]!, d); });
    }
    const riding = head.filter(d => d > turns).length;
    if (riding < 3) return [];
    const others = parameters.flatMap((p, k) => POSE_PARAMETERS.has(p.id) || !(p.maximum > p.minimum) ? [] : [k]);
    const rigid = new Uint8Array(candidates.length).fill(1);
    const mark = (moved: Float64Array) => moved.forEach((d, n) => { if (d > deforms) rigid[n] = 0; });
    mark(probe(new Map(others.map(k => [k, parameters[k]!.maximum]))));
    mark(probe(new Map(others.map(k => [k, parameters[k]!.minimum]))));
    if (candidates.filter((_, n) => head[n]! > turns && rigid[n]).length < 12 && others.length <= 256) {
      rigid.fill(1);
      for (const k of others)
        for (const value of [parameters[k]!.maximum, parameters[k]!.minimum]) {
          if (value === base[k]) continue;
          const moved = probe(new Map([[k, value]]));
          if (moved.filter((d, n) => d > deforms && head[n]! > turns).length >= 0.5 * riding) continue;
          mark(moved);
        }
    }
    return candidates.filter((_, n) => head[n]! > turns && rigid[n]);
  } finally {
    parameters.forEach((p, k) => model.setParameterValueByIndex(p.index, base[k]!));
    model.update();
  }
}

/** findEyes, where a model that breaks it only gets no eyes from its meshes (the eye hint still works). */
export function findEyesSafely(model: CubismModel, face: Face, parameters: readonly Parameter[], carriers: readonly Carrier[],
  now: () => number): EyeState {
  try { return findEyes(model, face, parameters, carriers, now); }
  catch {
    try { model.update(); } catch { }
    return Object.freeze({ face, mesh: {}, meshPins: {}, hintPins: {}, milliseconds: 0 });
  }
}

/**
 * The eyes the model's meshes draw (see eyes.ts): ParamEyeBallX and ParamEyeBallY are each moved to their far end in turn
 * and put back, and a visible drawable near the face whose vertices all move together by at least 0.5% of the face's width
 * is an iris or one of its highlights (an eyelid or eye white that only bends is left out). Each eye found has its middle
 * pinned to the face's carriers. No eyes when the model has no eyeball parameters or no clipping masks.
 */
export function findEyes(model: CubismModel, face: Face, parameters: readonly Parameter[], carriers: readonly Carrier[],
  now: () => number): EyeState {
  const started = now();
  const done = (state: Omit<EyeState, "milliseconds">): EyeState =>
    Object.freeze({ ...state, milliseconds: Math.max(0, now() - started) });
  const read = model.getParameterValueByIndex?.bind(model);
  const masks = model.getDrawableMasks?.(), counts = model.getDrawableMaskCounts?.();
  const balls = parameters.filter(p => (p.id === "ParamEyeBallX" || p.id === "ParamEyeBallY") && p.maximum > p.minimum);
  if (!read || !masks || !counts || !balls.length) return done({ face, mesh: {}, meshPins: {}, hintPins: {} });
  const count = model.getDrawableCount();
  const shown = (i: number) => model.getDrawableDynamicFlagIsVisible(i) && model.getDrawableOpacity(i) >= 0.05;
  const reach = (1.5 * face.width) ** 2, near: number[] = [], rest: Float32Array[] = [];
  for (let i = 0; i < count; i++) {
    if (!shown(i)) continue;
    const vertices = model.getDrawableVertices(i), n = Math.floor(vertices.length / 2);
    let x = 0, y = 0;
    for (let v = 0; v < n; v++) { x += vertices[2 * v]!; y += vertices[2 * v + 1]!; }
    if (n > 0 && (x / n - face.x) ** 2 + (y / n - face.y) ** 2 <= reach) { near.push(i); rest.push(Float32Array.from(vertices)); }
  }
  // The least any vertex of each drawable moved, the most over both parameters.
  const moved = new Float64Array(near.length);
  try {
    for (const p of balls) {
      const base = read(p.index), far = p.maximum - base >= base - p.minimum ? p.maximum : p.minimum;
      try {
        model.setParameterValueByIndex(p.index, far);
        model.update();
        near.forEach((d, n) => {
          const now = model.getDrawableVertices(d), before = rest[n]!;
          let least = Infinity;
          for (let v = 0; v + 1 < now.length && v + 1 < before.length; v += 2)
            least = Math.min(least, Math.hypot(now[v]! - before[v]!, now[v + 1]! - before[v + 1]!));
          if (Number.isFinite(least)) moved[n] = Math.max(moved[n]!, least);
        });
      } finally { model.setParameterValueByIndex(p.index, base); }
    }
  } finally { model.update(); }
  const mesh = meshEyes(face, near.filter((_, n) => moved[n]! >= 0.005 * face.width), {
    vertices: d => model.getDrawableVertices(d),
    indices: d => model.getDrawableVertexIndices(d),
    masks: d => Array.from(masks[d]?.subarray(0, Math.max(0, counts[d] ?? 0)) ?? []).filter(m => m >= 0 && m < count && m !== d),
    shown,
  });
  const pin = (side: "left" | "right") => {
    const eye = mesh[side], pinned = eye && pinEye(eye.middle, carriers, face);
    return pinned ? { [side]: pinned } : {};
  };
  return done({ face, found: mesh, mesh, meshPins: { ...pin("left"), ...pin("right") }, hintPins: {} });
}

export function validateGeometry(model: CubismModel, textureCount: number): void {
  for (const value of [model.getCanvasWidth(), model.getCanvasHeight()]) {
    requireCondition(Number.isFinite(value) && value >= 0.001 && value <= 10_000,
      "INVALID_MODEL_GEOMETRY", "Core returned invalid canvas dimensions.");
  }
  const count = model.getDrawableCount();
  boundedInteger(count, LIMITS.drawables, "drawable count");
  requireCondition(count > 0, "INVALID_MODEL_GEOMETRY", "The model has no drawable meshes.");
  let vertices = 0;
  let indices = 0;
  for (let i = 0; i < count; i++) {
    const vertexCount = model.getDrawableVertexCount(i);
    const indexCount = model.getDrawableVertexIndexCount(i);
    boundedInteger(vertexCount, LIMITS.vertices, "mesh vertices");
    boundedInteger(indexCount, LIMITS.indices, "mesh indices");
    vertices += vertexCount;
    indices += indexCount;
    boundedInteger(vertices, LIMITS.vertices, "model vertices");
    boundedInteger(indices, LIMITS.indices, "model indices");
    const texture = model.getDrawableTextureIndex(i);
    boundedInteger(texture, textureCount - 1, "drawable texture index");
  }
}

export function checkGl(gl: WebGLRenderingContext): void {
  const error = gl.getError();
  requireCondition(error === gl.NO_ERROR, "WEBGL_ERROR", `Live2D WebGL operation failed (0x${error.toString(16)}).`);
}
