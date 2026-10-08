import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { capturePose, hitTestVrm, VrmRuntime } from "../src/index.js";
import { fixture } from "./fixture.js";

const ray = (x: number, y: number) => new THREE.Raycaster(new THREE.Vector3(x, y, 5), new THREE.Vector3(0, 0, -1));

// A head bone with a hair joint under it, and a skinned quad whose left half follows the head and right half the hair.
function rig() {
  const root = new THREE.Group();
  const head = new THREE.Bone(); head.name = "J_Head"; head.position.set(0, 1, 0);
  const hair = new THREE.Bone(); hair.name = "HairRoot"; hair.position.set(0.5, 0, 0);
  head.add(hair);
  root.add(head);
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.Float32BufferAttribute([
    -1, 0, 0, 0, 0, 0, 0, 2, 0, -1, 2, 0, // left square
    0, 0, 0, 1, 0, 0, 1, 2, 0, 0, 2, 0, // right square
  ], 3));
  geometry.setIndex([0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
  geometry.setAttribute("skinIndex", new THREE.Uint16BufferAttribute(Array.from({ length: 8 }, (_, v) => [v < 4 ? 0 : 1, 0, 0, 0]).flat(), 4));
  geometry.setAttribute("skinWeight", new THREE.Float32BufferAttribute(Array.from({ length: 8 }, () => [1, 0, 0, 0]).flat(), 4));
  const material = new THREE.MeshBasicMaterial(); material.name = "HairMaterial";
  const mesh = new THREE.SkinnedMesh(geometry, material); mesh.name = "Body";
  root.add(mesh);
  root.updateMatrixWorld(true);
  mesh.bind(new THREE.Skeleton([head, hair]));
  return { root, head, hair, mesh, humanoid: new Map<THREE.Object3D, string>([[head, "head"]]) };
}

test("a skinned hit reports the dominant bone, its humanoid bone and whether it is hair", () => {
  const { root, hair, humanoid } = rig();
  const left = hitTestVrm(root, humanoid, ray(-0.5, 1.5), new Set([hair]));
  assert.deepEqual({ ...left, point: undefined }, { bone: "head", node: "J_Head", hair: false, mesh: "Body", material: "HairMaterial", point: undefined });
  const right = hitTestVrm(root, humanoid, ray(0.5, 1.5), new Set([hair]))!;
  assert.equal(right.bone, "head");
  assert.equal(right.node, "HairRoot");
  assert.equal(right.hair, true);
  assert.deepEqual(right.point, { x: 0.5, y: 1.5, z: 0 });
  // Not a spring joint and not named as hair: still under the head, but not hair (an eye adjuster, say).
  hair.name = "J_Adj_FaceEye";
  assert.equal(hitTestVrm(root, humanoid, ray(0.5, 1.5))!.hair, false);
});

test("misses and hidden meshes return undefined", () => {
  const { root, mesh, humanoid } = rig();
  assert.equal(hitTestVrm(root, humanoid, ray(3, 1.5)), undefined);
  mesh.visible = false;
  assert.equal(hitTestVrm(root, humanoid, ray(-0.5, 1.5)), undefined);
});

test("a skinned part posed away from where its bounds were first measured is still hit", () => {
  const { root, head, humanoid } = rig();
  // The framing measures a skinned mesh's bounds once, at load, in the rest pose (a T-pose); then the part moves (an arm
  // lowered into the idle pose).
  new THREE.Box3().setFromObject(root);
  head.position.y += 3;
  assert.equal(hitTestVrm(root, humanoid, ray(-0.5, 4.5), new Set(), false), undefined,
    "three.js's own raycast skips the part by the bounds it measured at rest");
  assert.equal(hitTestVrm(root, humanoid, ray(-0.5, 4.5))?.bone, "head");
  assert.equal(hitTestVrm(root, humanoid, ray(-0.5, 1.5)), undefined, "and nothing is left where it rested");
});

test("the runtime hit-tests a loaded VRM's posed meshes against its humanoid bones", async () => {
  const runtime = new VrmRuntime();
  await runtime.load(fixture());
  runtime.update(0);
  // The fixture's face triangle hangs off the head bone (world y 1.5 to 1.7).
  const hit = runtime.hitTestRay(ray(0, 1.55))!;
  assert.equal(hit.bone, "head");
  assert.equal(hit.hair, false);
  assert.equal(hit.mesh, "FixtureFace");
  assert.equal(runtime.hitTestRay(ray(2, 1.55)), undefined);
  runtime.dispose();
});

const distance = (a: { x: number; y: number; z: number }, b: { x: number; y: number; z: number }) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);

test("a hit on a moved part is traced to the same spot of its skin in the rest pose", () => {
  const { root, head, humanoid } = rig();
  // A hat: a mesh of its own (not skinned) that the head carries, above the quad.
  const hat = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), new THREE.MeshBasicMaterial());
  hat.name = "Hat"; hat.position.set(0, 2, 0);
  head.add(hat);
  const rest = capturePose(root);
  const atRest = hitTestVrm(root, humanoid, ray(-0.5, 1.5), new Set(), true, rest)!;
  assert.ok(distance(atRest.rest!, { x: -0.5, y: 1.5, z: 0 }) < 1e-3, `at rest: ${JSON.stringify(atRest.rest)}`);
  // The head moves 2 to the right: the quad's left half is now at x 1 to 2, and the point that was at -0.5 is at 1.5.
  head.position.x += 2;
  const moved = hitTestVrm(root, humanoid, ray(1.5, 1.5), new Set(), true, rest)!;
  assert.ok(distance(moved.rest!, { x: -0.5, y: 1.5, z: 0 }) < 1e-3, `moved: ${JSON.stringify(moved.rest)}`);
  // Then it turns a quarter turn about its joint (at 2, 1): that point (0.5 above and 0.5 left of the joint) is below and left.
  head.rotation.z = Math.PI / 2;
  const turned = hitTestVrm(root, humanoid, ray(1.5, 0.5), new Set(), true, rest)!;
  assert.equal(turned.bone, "head");
  assert.ok(distance(turned.rest!, { x: -0.5, y: 1.5, z: 0 }) < 1e-3, JSON.stringify(turned.rest));
  // The hat (at the head's joint + (0, 2), so 2 to the left of it now) is carried back with the head too.
  const worn = hitTestVrm(root, humanoid, ray(-0.2, 1.1), new Set(), true, rest)!;
  assert.equal(worn.mesh, "Hat");
  assert.ok(distance(worn.rest!, { x: 0.1, y: 3.2, z: 0 }) < 1e-3, JSON.stringify(worn.rest));
  // Without the rest pose there is nothing to trace.
  assert.equal(hitTestVrm(root, humanoid, ray(1.5, 0.5))!.rest, undefined);
});

test("the runtime keeps its rest pose when idle starts and traces hits on a head turned to the mouse back to it", async () => {
  const runtime = new VrmRuntime();
  await runtime.load(fixture());
  runtime.startIdle();
  const face = runtime.scene!.getObjectByName("FixtureFace")!;
  // A point of the face at rest, and where it is on the face's own triangle.
  const was = new THREE.Vector3(0.02, 1.62, 0);
  const atRest = runtime.hitTestRay(ray(was.x, was.y))!;
  assert.ok(distance(atRest.rest!, was) < 2e-3, `at rest: ${JSON.stringify(atRest.rest)}`);
  face.updateWorldMatrix(true, false);
  const onFace = face.worldToLocal(was.clone());
  // The head follows the mouse up and to the side.
  runtime.setLook(1, 1);
  for (let i = 0; i < 30; i++) runtime.update(0.1);
  face.updateWorldMatrix(true, false);
  const now = face.localToWorld(onFace.clone());
  const hit = runtime.hitTestRay(ray(now.x, now.y))!;
  assert.equal(hit.mesh, "FixtureFace");
  assert.ok(distance(now, was) > 0.004, `the head moved that point ${distance(now, was)}`);
  assert.ok(distance(hit.rest!, was) < 2e-3, `traced to ${JSON.stringify(hit.rest)}, was ${JSON.stringify(was)}`);
  runtime.dispose();
});
