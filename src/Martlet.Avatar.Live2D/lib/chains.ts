import { LIMITS } from "./assets.js";
import type { CubismModel } from "./sdk.js";

/** One physics setting of a model's physics3.json: its name (from the file's PhysicsDictionary, when it gives one), the
 *  parameters it drives and the ones it reads. */
export interface PhysicsSetting {
  readonly name?: string;
  readonly outputs: readonly string[];
  readonly inputs?: readonly string[];
}

/**
 * A part of the model that swings on its own (a tail, a ponytail, a skirt, ears), for touch zones: the drawables one physics
 * setting (or several that swing the same drawables) moves. A tail often hangs behind the body at rest, so a box around what
 * shows can't hold it; its drawables, followed as they move, can.
 */
export interface SwingingChain {
  /** The names of the physics settings that swing it (尾巴, Hair Front...), joined with " / "; absent when they have none. */
  readonly name?: string;
  /** Its drawables' IDs, from the one that moves least (its root) to the one that moves most (its tip). */
  readonly drawables: readonly string[];
  /** Where it can reach: its drawables' bounds at rest, swung and swung the other way (mirrored across its rest), as fractions
   *  of the canvas (origin top-left, +y down). */
  readonly left: number;
  readonly top: number;
  readonly right: number;
  readonly bottom: number;
}

export const CHAIN_LIMITS = Object.freeze({
  /** At most this many chains are reported, each with at most `drawables` drawables. */
  chains: 48,
  drawables: 256,
  /** A setting that moves more than this share of the visible drawables moves the whole body, not a part of it. */
  share: 0.4,
  /** A drawable moves when a vertex moves farther than this share of the model's canvas height. */
  moved: 0.01,
  /** Settings named alike (侧发 SY and 侧发 S) swing the same part when this share of the larger one's drawables is in both;
   *  settings without names, only when this many more are. */
  same: 0.6,
  sameUnnamed: 0.85,
  /** At most this many setting names make up a chain's name. */
  names: 4,
  /** Finding the chains stops after this long; the settings measured by then still count. */
  milliseconds: 4000,
});

type Bounds = [minX: number, minY: number, maxX: number, maxY: number];

/**
 * Finds the parts of `model` that swing on their own: each physics setting's parameters are put at their maximum, and the
 * visible drawables whose vertices move then (farther than CHAIN_LIMITS.moved of the canvas height) are that setting's part,
 * root first (the one that moved least). A setting that only feeds other settings (each parameter it drives is another's
 * input) is skipped, and one that moves more than CHAIN_LIMITS.share of the visible drawables moves the whole body and is left
 * out. Settings named alike that swing the same drawables are one chain. Every parameter is put back and the model updated, so
 * the pose doesn't change. `project` turns a point in model units (y up) into fractions of the canvas; measuring stops when
 * `late` says so. Empty when the model can't read its parameters or has no physics.
 */
export function findSwingingChains(model: CubismModel, settings: readonly PhysicsSetting[],
  project: (x: number, y: number) => readonly [number, number], late: () => boolean = () => false): SwingingChain[] {
  const read = model.getParameterValueByIndex?.bind(model);
  if (!read || settings.length === 0) return [];
  const ids = new Map<string, number>();
  for (let i = 0; i < Math.min(model.getParameterCount(), LIMITS.parameters); i++) ids.set(model.getParameterId(i).getString().s, i);
  const shown: number[] = [];
  for (let d = 0; d < Math.min(model.getDrawableCount(), LIMITS.drawables); d++)
    if (model.getDrawableDynamicFlagIsVisible(d) && model.getDrawableOpacity(d) >= 0.05) shown.push(d);
  if (shown.length === 0) return [];
  const rest = shown.map(d => Float32Array.from(model.getDrawableVertices(d)));
  const threshold = CHAIN_LIMITS.moved * Math.max(1e-6, Math.abs(model.getCanvasHeight()));
  const fed = new Set(settings.flatMap(setting => setting.inputs ?? []));
  const base = new Map<number, number>();
  // Per setting: how far each moved drawable (its position in `shown`) moved, and the bounds it reached swung both ways.
  const found: { names: string[]; moved: Map<number, number>; reach: Map<number, Bounds> }[] = [];
  try {
    for (const setting of settings.slice(0, LIMITS.physicsSettings)) {
      if (late()) break;
      if (setting.outputs.every(id => fed.has(id))) continue;
      const outputs = [...new Set(setting.outputs.flatMap(id => {
        const index = ids.get(id);
        return index === undefined ? [] : [index];
      }))].filter(i => model.getParameterMaximumValue(i) > model.getParameterMinimumValue(i));
      if (outputs.length === 0) continue;
      for (const i of outputs) if (!base.has(i)) base.set(i, read(i));
      for (const i of outputs) model.setParameterValueByIndex(i, model.getParameterMaximumValue(i));
      model.update();
      const moved = new Map<number, number>(), reach = new Map<number, Bounds>();
      shown.forEach((d, n) => {
        const now = model.getDrawableVertices(d), was = rest[n]!;
        let farthest = 0;
        for (let v = 0; v + 1 < now.length && v + 1 < was.length; v += 2)
          farthest = Math.max(farthest, (now[v]! - was[v]!) ** 2 + (now[v + 1]! - was[v + 1]!) ** 2);
        const distance = Math.sqrt(farthest);
        if (!(distance > threshold)) return;
        moved.set(n, distance);
        // Swung the other way it goes about as far to the other side: the bounds at rest, swung, and swung mirrored across
        // the middle of the rest.
        const at = bounds(was), swung = bounds(now), middle = at[0] + at[2];
        reach.set(n, union(union(at, swung), [middle - swung[2], swung[1], middle - swung[0], swung[3]]));
      });
      for (const i of outputs) model.setParameterValueByIndex(i, base.get(i)!);
      if (moved.size === 0 || moved.size > CHAIN_LIMITS.share * shown.length) continue;
      found.push({ names: setting.name ? [setting.name] : [], moved, reach });
    }
  } finally {
    for (const [i, value] of base) model.setParameterValueByIndex(i, value);
    model.update();
  }
  // Settings named alike that swing the same drawables (front hair's two directions, a tail's sway and its angle) are one chain.
  for (let merged = true; merged;) {
    merged = false;
    for (let a = 0; a < found.length && !merged; a++)
      for (let b = a + 1; b < found.length && !merged; b++) {
        const first = found[a]!, second = found[b]!;
        let shared = 0;
        for (const n of second.moved.keys()) if (first.moved.has(n)) shared++;
        const unnamed = first.names.length === 0 && second.names.length === 0;
        if (!unnamed && !alike(first.names, second.names)) continue;
        if (shared < (unnamed ? CHAIN_LIMITS.sameUnnamed : CHAIN_LIMITS.same) * Math.max(first.moved.size, second.moved.size)) continue;
        for (const [n, distance] of second.moved) first.moved.set(n, Math.max(first.moved.get(n) ?? 0, distance));
        for (const [n, box] of second.reach) first.reach.set(n, union(first.reach.get(n) ?? box, box));
        for (const name of second.names) if (!first.names.includes(name)) first.names.push(name);
        found.splice(b, 1);
        merged = true;
      }
  }
  return found.slice(0, CHAIN_LIMITS.chains).map(chain => {
    const members = [...chain.moved.entries()].sort((a, b) => a[1] - b[1] || a[0] - b[0]).slice(0, CHAIN_LIMITS.drawables);
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
    for (const [n] of members) {
      const [minX, minY, maxX, maxY] = chain.reach.get(n)!;
      for (const [x, y] of [project(minX, minY), project(maxX, maxY)]) {
        left = Math.min(left, x); right = Math.max(right, x); top = Math.min(top, y); bottom = Math.max(bottom, y);
      }
    }
    const name = chain.names.slice(0, CHAIN_LIMITS.names).join(" / ").slice(0, 2 * LIMITS.partName);
    return Object.freeze({
      ...(name ? { name } : {}),
      drawables: Object.freeze(members.map(([n]) => model.getDrawableId(shown[n]!).getString().s)),
      left, top, right, bottom,
    });
  }).filter(chain => [chain.left, chain.top, chain.right, chain.bottom].every(Number.isFinite));
}

// Whether two settings' names start with the same word (侧发 SY and 侧发 S, 尾巴 and 尾巴(2), Hair Front L and Hair Front R).
function alike(first: readonly string[], second: readonly string[]): boolean {
  const lead = (name: string) => name.trim().split(/[\s_\-()（）[\]{}.,:;/|0-9]+/u).find(word => word.length > 0)?.toLowerCase();
  const words = new Set(first.map(lead).filter(word => word !== undefined));
  return second.some(name => { const word = lead(name); return word !== undefined && words.has(word); });
}

function bounds(vertices: Float32Array): Bounds {
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (let v = 0; v + 1 < vertices.length; v += 2) {
    const x = vertices[v]!, y = vertices[v + 1]!;
    if (x < minX) minX = x;
    if (x > maxX) maxX = x;
    if (y < minY) minY = y;
    if (y > maxY) maxY = y;
  }
  return [minX, minY, maxX, maxY];
}

function union(a: Bounds, b: Bounds): Bounds {
  return [Math.min(a[0], b[0]), Math.min(a[1], b[1]), Math.max(a[2], b[2]), Math.max(a[3], b[3])];
}
