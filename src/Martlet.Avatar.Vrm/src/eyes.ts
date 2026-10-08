import * as THREE from "three";
import type { VRM } from "@pixiv/three-vrm";

/** Each eye's iris and visible opening on a VRM, for Martlet's drawings over the eyes (heart eyes, star eyes, dizzy swirls).
 *  The eye bones place each eye; the model's own iris and eye-white meshes (VRoid's EyeIris and EyeWhite materials, or names
 *  with iris, pupil, hitomi, 瞳 or 白目) give the iris's size and the opening, skinned and with the blink's morph targets. A
 *  hint from vision (`EyeHint`) gives what the meshes can't. */

/** Where the eye data came from, for both eyes together the weakest: "bones" when the eye bones and the model's meshes give
 *  every eye's iris and opening, "vision" when the hint gives what they don't, "estimate" while an eye has neither. */
export type EyesFrom = "bones" | "vision" | "estimate";

/** The most points and triangles of one eye's shape. */
export const EYE_SHAPE_POINTS = 512, EYE_SHAPE_TRIANGLES = 1024;
/** The points around a hinted eye's outline. */
export const OUTLINE_POINTS = 24;

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

interface Point { x: number; y: number }
/** An iris for drawings over it: its middle and radii across and down the face, in canvas pixels. */
export interface EyeIris { readonly x: number; readonly y: number; readonly rx: number; readonly ry: number }
/** An eye's visible opening: the union of `triangles` (three indices into `points` each) or, without them, the closed outline
 *  through `points`; no points when the eye is hidden. */
export interface EyeOpening { readonly points: readonly Point[]; readonly triangles?: readonly number[] }
/** The eye fields of a face anchor. */
export interface EyeFields {
  readonly eyesFrom: EyesFrom;
  readonly irisLeft?: EyeIris; readonly irisRight?: EyeIris;
  readonly eyeLeftShape?: EyeOpening; readonly eyeRightShape?: EyeOpening;
  readonly eyeLeft?: Point; readonly eyeRight?: Point;
}
/** The face in world space, as VrmRuntime.faceGeometry gives it. */
export interface FaceFrame {
  readonly center: THREE.Vector3; readonly side: THREE.Vector3; readonly up: THREE.Vector3; readonly forward: THREE.Vector3;
  readonly width: number;
}

const IRIS = /iris|pupil|hitomi|瞳/i, WHITE = /eye[\s_-]?white|sclera|白目/i;

/** One eye white's part in one mesh: the mesh's vertices it uses and its triangles over them. */
interface WhitePart { readonly mesh: THREE.Mesh; readonly vertices: readonly number[] }
interface White { readonly parts: readonly WhitePart[]; readonly triangles: readonly number[] }

/** An eye with its bone: the iris's middle at rest in the bone's space (so it turns with a bone look-at) and in its parent's
 *  (the eye's middle, riding the head), and from the meshes, the iris's radii at rest (world units) and the eye white. */
interface BoneEye {
  readonly bone: THREE.Object3D;
  readonly iris: THREE.Vector3;
  readonly middle: THREE.Vector3;
  readonly size?: { readonly rx: number; readonly ry: number };
  readonly white?: White;
}

/** The triangles (vertex index triples) of a mesh drawn with a material whose name matches `pattern`. */
function triangles(mesh: THREE.Mesh, pattern: RegExp): number[] {
  const geometry = mesh.geometry as THREE.BufferGeometry, index = geometry.index;
  const count = index ? index.count : geometry.getAttribute("position")?.count ?? 0;
  const vertex = (k: number) => index ? index.getX(k) : k;
  const named = (material: THREE.Material | undefined) => !!material && (pattern.test(material.name) ||
    !Array.isArray(mesh.material) && pattern.test(mesh.name));
  const ranges: [number, number][] = [];
  if (Array.isArray(mesh.material)) {
    for (const group of geometry.groups)
      if (named(mesh.material[group.materialIndex ?? 0])) ranges.push([group.start, Math.min(count, group.start + group.count)]);
  } else if (named(mesh.material)) ranges.push([0, count]);
  const found: number[] = [];
  for (const [start, end] of ranges) for (let k = start; k + 2 < end; k += 3) found.push(vertex(k), vertex(k + 1), vertex(k + 2));
  return found;
}

const scratch = new THREE.Vector3();

/** A mesh vertex where the model draws it now (skinned, with its morph targets), in world space. */
function worldVertex(mesh: THREE.Mesh, vertex: number, target: THREE.Vector3): THREE.Vector3 {
  return mesh.localToWorld(mesh.getVertexPosition(vertex, target));
}

/** A VRM's eyes (see the file's comment). Built at rest, right after the model loads. */
export class VrmEyes {
  readonly #vrm: VRM;
  readonly #left: BoneEye | undefined;
  readonly #right: BoneEye | undefined;
  #hint: EyeHint | undefined;

  constructor(vrm: VRM) {
    this.#vrm = vrm;
    // The character's right eye is on the viewer's left.
    const left = vrm.humanoid.getRawBoneNode("rightEye"), right = vrm.humanoid.getRawBoneNode("leftEye");
    if (!left || !right) return;
    vrm.scene.updateWorldMatrix(true, true);
    const l = left.getWorldPosition(new THREE.Vector3()), r = right.getWorldPosition(new THREE.Vector3());
    const apart = l.distanceTo(r);
    if (!(apart > 1e-4) || !(r.x > l.x)) return;
    // At rest a VRM 1.0 faces +Z with the viewer's right at +X.
    const split = (l.x + r.x) / 2;
    const irises: [THREE.Vector3[], THREE.Vector3[]] = [[], []];
    const whites: [WhitePart[], number[]][] = [[[], []], [[], []]];
    const counts = [0, 0];
    vrm.scene.traverse(node => {
      if (!(node instanceof THREE.Mesh) || !node.geometry?.getAttribute("position")) return;
      const iris = triangles(node, IRIS), seen = new Set<number>();
      for (const k of iris) {
        if (seen.has(k)) continue;
        seen.add(k);
        const p = worldVertex(node, k, new THREE.Vector3());
        if ([p.x, p.y, p.z].every(Number.isFinite)) irises[p.x < split ? 0 : 1].push(p);
      }
      const white = triangles(node, WHITE);
      if (!white.length) return;
      const locals = [new Map<number, number>(), new Map<number, number>()], kept: number[][] = [[], []];
      for (let t = 0; t + 2 < white.length; t += 3) {
        let x = 0;
        for (let n = 0; n < 3; n++) x += worldVertex(node, white[t + n]!, scratch).x;
        const side = x / 3 < split ? 0 : 1, local = locals[side]!;
        for (let n = 0; n < 3; n++) {
          const k = white[t + n]!;
          if (!local.has(k)) local.set(k, local.size);
          kept[side]!.push(counts[side]! + local.get(k)!);
        }
      }
      for (const side of [0, 1]) {
        if (!locals[side]!.size) continue;
        whites[side]![0].push(Object.freeze({ mesh: node, vertices: Object.freeze([...locals[side]!.keys()]) }));
        whites[side]![1].push(...kept[side]!);
        counts[side]! += locals[side]!.size;
      }
    });
    const eye = (bone: THREE.Object3D, at: THREE.Vector3, side: 0 | 1): BoneEye => {
      // The iris's middle and radii at rest from its mesh, when it is plausible: 4% to 40% of the eyes' distance across and
      // down, its middle within half that distance of the bone across the face.
      let middle = at.clone().add(new THREE.Vector3(0, 0, 0.2 * apart)), size: { rx: number; ry: number } | undefined;
      const points = irises[side];
      if (points.length >= 3) {
        const box = new THREE.Box3().setFromPoints(points), centre = box.getCenter(new THREE.Vector3());
        const rx = (box.max.x - box.min.x) / 2, ry = (box.max.y - box.min.y) / 2;
        if (rx >= 0.04 * apart && rx <= 0.4 * apart && ry >= 0.04 * apart && ry <= 0.4 * apart &&
          Math.hypot(centre.x - at.x, centre.y - at.y) <= 0.5 * apart) {
          centre.z = points.reduce((sum, p) => sum + p.z, 0) / points.length;
          middle = centre;
          size = { rx, ry };
        }
      }
      const [parts, list] = whites[side]!;
      const white = parts.length && counts[side]! <= EYE_SHAPE_POINTS && list.length <= 3 * EYE_SHAPE_TRIANGLES
        ? Object.freeze({ parts: Object.freeze(parts), triangles: Object.freeze(list) }) : undefined;
      const parent = bone.parent ?? bone;
      return { bone, iris: bone.worldToLocal(middle.clone()), middle: parent.worldToLocal(middle.clone()),
        ...(size ? { size } : {}), ...(white ? { white } : {}) };
    };
    this.#left = eye(left, l, 0);
    this.#right = eye(right, r, 1);
  }

  /** Uses eyes measured by vision for what the model can't give; undefined clears them. Returns where the eyes come from. */
  setHint(hint: EyeHint | undefined): EyesFrom {
    this.#hint = readEyeHint(hint);
    return this.from;
  }

  /** Where the eyes come from now (see EyesFrom). */
  get from(): EyesFrom {
    const from = (eye: BoneEye | undefined, hint: EyeHintEye | undefined): EyesFrom =>
      eye?.size && eye.white ? "bones" : hint ? "vision" : "estimate";
    const l = from(this.#left, this.#hint?.left), r = from(this.#right, this.#hint?.right);
    return l === "estimate" || r === "estimate" ? "estimate" : l === "vision" || r === "vision" ? "vision" : "bones";
  }

  /** The eye fields of the face anchor for the face now (`face`) as `project` (world to canvas pixels) shows it. */
  fields(face: FaceFrame, project: (world: THREE.Vector3) => Point): EyeFields {
    const left = this.#eye(this.#left, this.#hint?.left, "blinkRight", face, project);
    const right = this.#eye(this.#right, this.#hint?.right, "blinkLeft", face, project);
    return { eyesFrom: this.from,
      ...(left?.iris ? { irisLeft: left.iris, eyeLeft: left.middle } : {}), ...(left?.shape ? { eyeLeftShape: left.shape } : {}),
      ...(right?.iris ? { irisRight: right.iris, eyeRight: right.middle } : {}), ...(right?.shape ? { eyeRightShape: right.shape } : {}) };
  }

  #eye(eye: BoneEye | undefined, hint: EyeHintEye | undefined, blink: "blinkLeft" | "blinkRight", face: FaceFrame,
    project: (world: THREE.Vector3) => Point): { middle: Point; iris?: EyeIris; shape?: EyeOpening } | undefined {
    if (!eye && !hint) return undefined;
    const width = face.width;
    // A world point `u` face widths across and `v` down from `from`.
    const at = (from: THREE.Vector3, u: number, v: number) =>
      from.clone().addScaledVector(face.side, u * width).addScaledVector(face.up, -v * width);
    let middle: THREE.Vector3, iris: THREE.Vector3;
    if (eye) {
      eye.bone.updateWorldMatrix(true, false);
      middle = (eye.bone.parent ?? eye.bone).localToWorld(eye.middle.clone());
      iris = eye.bone.localToWorld(eye.iris.clone());
    } else {
      middle = at(face.center, hint!.eye.x, hint!.eye.y);
      iris = middle;
    }
    if (hint && !eye?.size) {
      // The iris sits where vision saw it in its eye and moves with the gaze (an expression look-at's weights) through the
      // room the eye leaves around it.
      const expressions = this.#vrm.expressionManager, weight = (name: string) => expressions?.getValue(name) ?? 0;
      const roomX = Math.max(0, hint.eye.rx - hint.iris.r), roomY = Math.max(0, hint.eye.ry - hint.iris.r);
      const gazeX = Math.max(-1, Math.min(1, weight("lookLeft") - weight("lookRight")));
      const gazeY = Math.max(-1, Math.min(1, weight("lookUp") - weight("lookDown")));
      iris = at(iris, hint.iris.x - hint.eye.x + gazeX * roomX, hint.iris.y - hint.eye.y - gazeY * roomY);
    }
    const radius = (direction: THREE.Vector3, length: number) => {
      const a = project(iris.clone().addScaledVector(direction, length)), b = project(iris.clone().addScaledVector(direction, -length));
      return Math.hypot(a.x - b.x, a.y - b.y) / 2;
    };
    const size = eye?.size ?? (hint ? { rx: hint.iris.r * width, ry: hint.iris.r * width } : undefined);
    const centre = project(iris), shown = project(middle);
    const result: { middle: Point; iris?: EyeIris; shape?: EyeOpening } = { middle: shown };
    if (size) result.iris = { x: centre.x, y: centre.y, rx: radius(face.side, size.rx), ry: radius(face.up, size.ry) };
    if (eye?.white) {
      const hidden = eye.white.parts.some(part => !part.mesh.visible);
      const points: Point[] = [];
      if (!hidden) for (const part of eye.white.parts) for (const k of part.vertices) points.push(project(worldVertex(part.mesh, k, scratch)));
      result.shape = { points, triangles: hidden ? [] : eye.white.triangles };
    } else if (hint) {
      const expressions = this.#vrm.expressionManager, weight = (name: string) => expressions?.getValue(name) ?? 0;
      const open = 1 - Math.max(0, Math.min(1, Math.max(weight("blink"), weight(blink))));
      const outline: Point[] = [];
      for (let k = 0; k < OUTLINE_POINTS; k++) {
        const t = 2 * Math.PI * k / OUTLINE_POINTS;
        // The upper lid comes down most: closed, the slit lies a little below the middle.
        outline.push(project(at(middle, hint.eye.rx * Math.cos(t), hint.eye.ry * (0.4 * (1 - open) + open * Math.sin(t)))));
      }
      result.shape = { points: outline };
    }
    return result;
  }
}
