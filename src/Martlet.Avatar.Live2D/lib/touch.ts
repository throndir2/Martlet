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
}

export const MAXIMUM_TOUCH_DRAWABLES = 8;

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

/** What of the model is at a point in model units: hit areas by their triangles (visible or not) and the visible drawables,
 *  topmost first. Undefined when neither is there. */
export function hitTestModel(model: TouchModel, hitAreas: readonly HitArea[], x: number, y: number): Live2DHit | undefined {
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
  return Object.freeze({
    hitAreas: Object.freeze(areas),
    drawables: Object.freeze(visible.slice(0, MAXIMUM_TOUCH_DRAWABLES).map(i => ids[i]!)),
    point: Object.freeze({ x, y }),
  });
}
