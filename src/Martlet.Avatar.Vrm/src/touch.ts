import * as THREE from "three";

export interface VrmHit {
  /** VRM humanoid bone of the dominant node, or of its nearest humanoid ancestor. */
  readonly bone: string | undefined;
  /** The dominant node's own name (a spring-bone hair joint, say). */
  readonly node: string | undefined;
  /** The dominant node is not a humanoid bone and sits under the head (hair, ears, accessories). */
  readonly hair: boolean;
  readonly mesh: string | undefined;
  readonly material: string | undefined;
  /** The hit point in the model's own space (its scene root). */
  readonly point: { readonly x: number; readonly y: number; readonly z: number };
  /** Where the hit point was in the rest pose (see restPoint), in the same space as `point`; absent without the rest pose. */
  readonly rest?: { readonly x: number; readonly y: number; readonly z: number };
}

/** Each node's world matrix in the rest pose (the pose the touch zones picture shows), by node. */
export type RestPose = ReadonlyMap<THREE.Object3D, THREE.Matrix4>;

/** Every node's world matrix under `root` as it is posed now, to trace hits back to later (see restPoint). */
export function capturePose(root: THREE.Object3D): Map<THREE.Object3D, THREE.Matrix4> {
  root.updateWorldMatrix(true, true);
  const pose = new Map<THREE.Object3D, THREE.Matrix4>();
  root.traverse(node => { pose.set(node, node.matrixWorld.clone()); });
  return pose;
}

/**
 * Where a hit point of the posed model was in the rest pose `rest` (world space): the hit triangle's corners skinned with
 * the bones' world matrices at rest (without morph targets, which the rest pose doesn't show), weighted as the point lies in
 * the triangle; for a mesh that isn't skinned, the point carried back with the mesh. So it is the same spot of the skin
 * however the head turns to the mouse, nods or tilts, or the arms move. Undefined when the hit has no triangle or a node it
 * needs has no rest matrix.
 */
export function restPoint(hit: THREE.Intersection, rest: RestPose): THREE.Vector3 | undefined {
  const mesh = hit.object as THREE.Mesh;
  const skinned = mesh as THREE.SkinnedMesh;
  const geometry = mesh.geometry as THREE.BufferGeometry | undefined;
  const position = geometry?.getAttribute("position");
  const indices = geometry?.getAttribute("skinIndex"), weights = geometry?.getAttribute("skinWeight");
  if (!(skinned.isSkinnedMesh && skinned.skeleton && indices && weights)) {
    const at = rest.get(mesh);
    return at ? hit.point.clone().applyMatrix4(mesh.matrixWorld.clone().invert()).applyMatrix4(at) : undefined;
  }
  if (!hit.face || !hit.barycoord || !position) return undefined;
  // Bound "attached" (three.js's default), a skinned mesh's own matrices cancel out: only its bones place it.
  const meshAt = rest.get(skinned);
  const outer = skinned.bindMode === THREE.AttachedBindMode ? undefined : meshAt?.clone().multiply(skinned.bindMatrixInverse);
  if (skinned.bindMode !== THREE.AttachedBindMode && !outer) return undefined;
  const share = [hit.barycoord.x, hit.barycoord.y, hit.barycoord.z];
  const point = new THREE.Vector3(), corner = new THREE.Vector3(), bound = new THREE.Vector3(), moved = new THREE.Vector3();
  const matrix = new THREE.Matrix4();
  for (const [n, vertex] of [hit.face.a, hit.face.b, hit.face.c].entries()) {
    bound.fromBufferAttribute(position, vertex).applyMatrix4(skinned.bindMatrix);
    corner.set(0, 0, 0);
    for (let k = 0; k < indices.itemSize; k++) {
      const weight = weights.getComponent(vertex, k);
      if (weight === 0) continue;
      const index = indices.getComponent(vertex, k);
      const bone = skinned.skeleton.bones[index], inverse = skinned.skeleton.boneInverses[index];
      const at = bone && rest.get(bone);
      if (!at || !inverse) return undefined;
      corner.addScaledVector(moved.copy(bound).applyMatrix4(matrix.multiplyMatrices(at, inverse)), weight);
    }
    if (outer) corner.applyMatrix4(outer);
    point.addScaledVector(corner, share[n]!);
  }
  return [point.x, point.y, point.z].every(Number.isFinite) ? point : undefined;
}

const HAIR = /hair|kami|髪|bang|ahoge|ponytail|twintail|braid/i;

function shown(object: THREE.Object3D): boolean {
  for (let node: THREE.Object3D | null = object; node; node = node.parent) if (!node.visible) return false;
  return true;
}

/** The node a hit triangle moves with: the bone with the largest skin weight over its corners (weighted by where the point
 *  lies), or for a mesh that isn't skinned the mesh's own parent. */
function dominantNode(hit: THREE.Intersection): THREE.Object3D {
  const mesh = hit.object as THREE.Mesh;
  const skinned = mesh as THREE.SkinnedMesh;
  const geometry = mesh.geometry as THREE.BufferGeometry | undefined;
  const indices = geometry?.getAttribute("skinIndex"), weights = geometry?.getAttribute("skinWeight");
  if (!(skinned.isSkinnedMesh && skinned.skeleton && hit.face && indices && weights)) return mesh.parent ?? mesh;
  const corners = [hit.face.a, hit.face.b, hit.face.c];
  const bary = hit.barycoord;
  const share = bary ? [bary.x, bary.y, bary.z] : [1 / 3, 1 / 3, 1 / 3];
  const totals = new Map<number, number>();
  corners.forEach((vertex, corner) => {
    for (let k = 0; k < indices.itemSize; k++) {
      const weight = weights.getComponent(vertex, k) * share[corner]!;
      if (weight > 0) totals.set(indices.getComponent(vertex, k), (totals.get(indices.getComponent(vertex, k)) ?? 0) + weight);
    }
  });
  let best = -1, most = 0;
  for (const [index, total] of totals) if (total > most) { best = index; most = total; }
  return skinned.skeleton.bones[best] ?? mesh.parent ?? mesh;
}

/**
 * Measures each skinned mesh's bounds again in the pose it has now. three.js keeps a skinned mesh's bounds from when they were
 * first measured (the framing measures them at load, in the model's T-pose) and its raycast skips a mesh whose bounds the ray
 * misses: a sleeve or a glove drawn as a mesh of its own could never be hit once the arms were lowered.
 */
export function measurePosedBounds(root: THREE.Object3D): void {
  root.updateWorldMatrix(true, true);
  root.traverse(object => {
    const mesh = object as THREE.SkinnedMesh;
    if (!mesh.isSkinnedMesh) return;
    mesh.computeBoundingBox();
    mesh.computeBoundingSphere();
  });
}

/**
 * What of a posed VRM a ray hits first: the mesh and material, the node the hit triangle moves with and its humanoid bone.
 * `humanoid` maps the raw humanoid bone nodes to their VRM names; `springs` are the spring-bone joints (hair is a spring-bone
 * joint, or named as hair, under the head). `measure` measures the skinned meshes' bounds in this pose first
 * (measurePosedBounds); a caller that hit-tests one pose many times needs it only once. With `rest` (see capturePose), also
 * where the hit point was in the rest pose. Undefined when the ray misses every visible mesh.
 */
export function hitTestVrm(root: THREE.Object3D, humanoid: ReadonlyMap<THREE.Object3D, string>, raycaster: THREE.Raycaster,
  springs: ReadonlySet<THREE.Object3D> = new Set(), measure = true, rest?: RestPose): VrmHit | undefined {
  if (measure) measurePosedBounds(root);
  else root.updateWorldMatrix(true, true);
  const hit = raycaster.intersectObject(root, true).find(candidate =>
    (candidate.object as THREE.Mesh).isMesh && shown(candidate.object));
  if (!hit) return undefined;
  const node = dominantNode(hit);
  let bone: string | undefined;
  let ancestor: THREE.Object3D | null = node;
  while (ancestor && !(bone = humanoid.get(ancestor))) ancestor = ancestor === root ? null : ancestor.parent;
  const mesh = hit.object as THREE.Mesh;
  const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
  const material = materials[hit.face?.materialIndex ?? 0] ?? materials[0];
  const local = root.worldToLocal(hit.point.clone());
  const was = rest && restPoint(hit, rest);
  const restLocal = was && root.worldToLocal(was);
  const round = (value: number) => Math.round(value * 1000) / 1000;
  return Object.freeze({
    bone, node: node.name || undefined,
    hair: !humanoid.has(node) && bone === "head" && (springs.has(node) || HAIR.test(node.name)),
    mesh: mesh.name || undefined, material: material?.name || undefined,
    point: Object.freeze({ x: round(local.x), y: round(local.y), z: round(local.z) }),
    ...(restLocal ? { rest: Object.freeze({ x: round(restLocal.x), y: round(restLocal.y), z: round(restLocal.z) }) } : {}),
  });
}
