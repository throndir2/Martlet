/** The parts of a Cubism model a hit test reads (Framework 5 CubismModel). */
export interface TouchModel {
  getDrawableCount(): number;
  getDrawableId(index: number): { getString(): { s: string } };
  /** Interleaved x, y vertex positions in model units (y up). */
  getDrawableVertices(index: number): Float32Array;
  getDrawableVertexIndices(index: number): Uint16Array;
  getDrawableRenderOrders(): Int32Array;
  getDrawableOpacity(index: number): number;
  getDrawableDynamicFlagIsVisible(index: number): boolean;
}

export interface HitArea {
  /** The drawable (ArtMesh) ID the area is authored on. */
  readonly id: string;
  readonly name: string;
}

export interface Live2DHit {
  /** Authored HitAreas (their Name, else Id) whose mesh contains the point; their meshes are often invisible. */
  readonly hitAreas: readonly string[];
  /** IDs of visible drawables under the point, topmost (highest render order) first, at most 8. */
  readonly drawables: readonly string[];
  /** The point in model units (y up). */
  readonly point: { readonly x: number; readonly y: number };
  /** Where the touched point of the character was in the rest pose (model units, y up), traced on the topmost drawable under
   *  the point (`drawable`, its ID; see restPoint). Absent without the rest pose's vertices. */
  readonly rest?: { readonly x: number; readonly y: number; readonly drawable: string };
  /** The topmost drawable under the point is hair: the model names one of the parts it sits in as hair (see HAIR_PART). */
  readonly hair?: boolean;
}

export const MAXIMUM_TOUCH_DRAWABLES = 8;

/** A part the model names as hair, by its ID or its DisplayInfo name, in any language Martlet knows (like a VRM's hair joints). */
export const HAIR_PART = /hair|kami|髪|bang|ahoge|ponytail|twintail|braid/i;

function inTriangle(px: number, py: number, ax: number, ay: number, bx: number, by: number, cx: number, cy: number): boolean {
  const d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
  const d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
  const d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
  return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
}

/** Whether a drawable's triangles (as posed now) contain the point; checks its bounding box first. */
export function drawableContains(model: TouchModel, index: number, x: number, y: number): boolean {
  const vertices = model.getDrawableVertices(index);
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (let v = 0; v + 1 < vertices.length; v += 2) {
    const vx = vertices[v]!, vy = vertices[v + 1]!;
    if (vx < minX) minX = vx; if (vx > maxX) maxX = vx;
    if (vy < minY) minY = vy; if (vy > maxY) maxY = vy;
  }
  if (!(x >= minX && x <= maxX && y >= minY && y <= maxY)) return false;
  const indices = model.getDrawableVertexIndices(index);
  for (let i = 0; i + 2 < indices.length; i += 3) {
    const a = indices[i]! * 2, b = indices[i + 1]! * 2, c = indices[i + 2]! * 2;
    if (inTriangle(x, y, vertices[a]!, vertices[a + 1]!, vertices[b]!, vertices[b + 1]!, vertices[c]!, vertices[c + 1]!)) return true;
  }
  return false;
}

/**
 * Where a point of a mesh as posed now was in the rest pose: the point's place in the triangle that holds it (`vertices` and
 * `rest` are the mesh's interleaved x, y vertex positions now and at rest, `indices` its triangles, three each), taken on
 * the same triangle at rest. Live2D moves a mesh by moving its vertices, so the point stays on the same spot of the skin
 * however the head turns, tilts or nods. Undefined when no triangle holds the point.
 */
export function restPoint(vertices: ArrayLike<number>, indices: ArrayLike<number>, rest: ArrayLike<number>, x: number, y: number):
  { x: number; y: number } | undefined {
  if (rest.length !== vertices.length) return undefined;
  for (let t = 0; t + 2 < indices.length; t += 3) {
    const a = 2 * indices[t]!, b = 2 * indices[t + 1]!, c = 2 * indices[t + 2]!;
    const ax = vertices[a]!, ay = vertices[a + 1]!, bx = vertices[b]!, by = vertices[b + 1]!, cx = vertices[c]!, cy = vertices[c + 1]!;
    const d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
    if (!(Math.abs(d) > 1e-12)) continue;
    const u = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d, v = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d, w = 1 - u - v;
    if (u < -1e-9 || v < -1e-9 || w < -1e-9) continue;
    const rx = u * rest[a]! + v * rest[b]! + w * rest[c]!, ry = u * rest[a + 1]! + v * rest[b + 1]! + w * rest[c + 1]!;
    if (Number.isFinite(rx) && Number.isFinite(ry)) return { x: rx, y: ry };
  }
  return undefined;
}

/** What of the model is at a point in model units: hit areas by their triangles (visible or not) and the visible drawables,
 *  topmost first. With `rest` (a drawable's vertex positions in the rest pose), also where the touched point was at rest,
 *  traced on the topmost drawable that holds it (see restPoint); with `hair` (whether a drawable is hair), whether the topmost
 *  drawable is. Undefined when neither is there. */
export function hitTestModel(model: TouchModel, hitAreas: readonly HitArea[], x: number, y: number,
  rest?: (drawable: number) => ArrayLike<number> | undefined, hair?: (drawable: number) => boolean): Live2DHit | undefined {
  const count = model.getDrawableCount();
  const orders = model.getDrawableRenderOrders();
  const ids: string[] = [];
  const visible: number[] = [];
  for (let i = 0; i < count; i++) {
    ids.push(model.getDrawableId(i).getString().s);
    if (model.getDrawableDynamicFlagIsVisible(i) && model.getDrawableOpacity(i) >= 0.05 && drawableContains(model, i, x, y)) visible.push(i);
  }
  visible.sort((a, b) => (orders[b] ?? 0) - (orders[a] ?? 0));
  const areas: string[] = [];
  for (const area of hitAreas) {
    const index = ids.indexOf(area.id);
    if (index >= 0 && drawableContains(model, index, x, y)) areas.push(area.name || area.id);
  }
  if (visible.length === 0 && areas.length === 0) return undefined;
  let traced: { x: number; y: number; drawable: string } | undefined;
  for (const i of rest ? visible : []) {
    const before = rest!(i);
    const point = before && restPoint(model.getDrawableVertices(i), model.getDrawableVertexIndices(i), before, x, y);
    if (point) { traced = { ...point, drawable: ids[i]! }; break; }
  }
  return Object.freeze({
    hitAreas: Object.freeze(areas),
    drawables: Object.freeze(visible.slice(0, MAXIMUM_TOUCH_DRAWABLES).map(i => ids[i]!)),
    point: Object.freeze({ x, y }),
    ...(traced ? { rest: Object.freeze(traced) } : {}),
    ...(visible.length > 0 && hair?.(visible[0]!) ? { hair: true } : {}),
  });
}
