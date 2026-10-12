// Loads the converter's .vrma output with the real @pixiv/three-vrm-animation loader and plays it on a
// VRM 1.0 and a VRM 0.x model that this script makes itself. It then measures where the model's raw bones
// went and checks each case in cases.json (written by make_cases.py). It also runs the Khronos glTF
// validator on each file and stops on any error or warning.
//
// Usage: node check.mjs [CASES_DIR]
// Without CASES_DIR it runs `python make_cases.py out` first (set PYTHON to choose the interpreter).

import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { VRMLoaderPlugin, VRMUtils } from "@pixiv/three-vrm";
import { VRMAnimationLoaderPlugin, createVRMAnimationClip } from "@pixiv/three-vrm-animation";
import validator from "gltf-validator";

const here = path.dirname(fileURLToPath(import.meta.url));
const MIN_COSINE = 0.95; // about 18 degrees

// The test model's T-pose, facing +Z with its left side at +X (VRM 1.0). Right side and VRM 0.x come from it.
const LEFT_AND_CENTER = [
  ["hips", null, [0, 0.8, 0]],
  ["spine", "hips", [0, 0.88, 0]],
  ["chest", "spine", [0, 0.98, 0]],
  ["upperChest", "chest", [0, 1.06, -0.01]],
  ["neck", "upperChest", [0, 1.18, -0.02]],
  ["head", "neck", [0, 1.26, -0.02]],
  ["leftShoulder", "upperChest", [0.03, 1.14, -0.02]],
  ["leftUpperArm", "leftShoulder", [0.12, 1.14, -0.02]],
  ["leftLowerArm", "leftUpperArm", [0.36, 1.14, -0.02]],
  ["leftHand", "leftLowerArm", [0.58, 1.14, -0.02]],
  ["leftThumbMetacarpal", "leftHand", [0.6, 1.13, 0.01]],
  ["leftThumbProximal", "leftThumbMetacarpal", [0.625, 1.13, 0.03]],
  ["leftThumbDistal", "leftThumbProximal", [0.645, 1.13, 0.045]],
  ...finger("Index", 0.66, 0.0),
  ...finger("Middle", 0.66, -0.02),
  ...finger("Ring", 0.655, -0.04),
  ...finger("Little", 0.645, -0.06),
  ["leftUpperLeg", "hips", [0.07, 0.74, 0]],
  ["leftLowerLeg", "leftUpperLeg", [0.07, 0.42, 0]],
  ["leftFoot", "leftLowerLeg", [0.07, 0.07, -0.02]],
  ["leftToes", "leftFoot", [0.07, 0.01, 0.09]],
];

function finger(name, x, z) {
  return [
    [`left${name}Proximal`, "leftHand", [x, 1.14, z]],
    [`left${name}Intermediate`, `left${name}Proximal`, [x + 0.035, 1.14, z]],
    [`left${name}Distal`, `left${name}Intermediate`, [x + 0.06, 1.14, z]],
  ];
}

const SKELETON = [
  ...LEFT_AND_CENTER,
  ...LEFT_AND_CENTER.filter(([name]) => name.startsWith("left")).map(([name, parent, [x, y, z]]) => [
    name.replace(/^left/, "right"),
    parent.replace(/^left/, "right"),
    [-x, y, z],
  ]),
];

// VRM 1.0 thumb names to VRM 0.x thumb names.
const VRM0_NAMES = { ThumbMetacarpal: "ThumbProximal", ThumbProximal: "ThumbIntermediate" };

function vrm0Name(name) {
  return name.replace(/(Thumb(?:Metacarpal|Proximal))$/, (m) => VRM0_NAMES[m]);
}

// Makes a VRM (GLB) with only a skeleton. Every bone has a different, odd rest rotation like an exported rig,
// so the check also covers three-vrm's normalization. A VRM 0.x model faces -Z, as VRM 0.x requires.
function makeVrm({ version, withUpperChest, withToes }) {
  let bones = SKELETON;
  if (!withUpperChest) {
    bones = bones
      .filter(([name]) => name !== "upperChest")
      .map(([name, parent, pos]) => [name, parent === "upperChest" ? "chest" : parent, pos]);
  }
  if (!withToes) bones = bones.filter(([name]) => !name.endsWith("Toes"));
  const turn = version === "0" ? -1 : 1;

  const nodes = [{ name: "Root", children: [] }];
  const index = new Map();
  const world = new Map([[null, { pos: new THREE.Vector3(), rot: new THREE.Quaternion() }]]);
  bones.forEach(([name, parent, [x, y, z]], i) => {
    const pos = new THREE.Vector3(turn * x, y, turn * z);
    const rot = new THREE.Quaternion().setFromEuler(
      new THREE.Euler(0.7 * Math.sin(1.3 * i), 0.9 * Math.sin(2.1 * i + 1), 0.5 * Math.sin(0.7 * i + 2)),
    );
    const parentWorld = world.get(parent);
    const inverse = parentWorld.rot.clone().invert();
    const node = {
      name: `Raw_${name}`,
      translation: pos.clone().sub(parentWorld.pos).applyQuaternion(inverse).toArray(),
      rotation: inverse.multiply(rot).toArray(),
    };
    world.set(name, { pos, rot });
    index.set(name, i + 1);
    nodes.push(node);
    const parentNode = parent == null ? nodes[0] : nodes[index.get(parent)];
    (parentNode.children ??= []).push(i + 1);
  });

  const extension =
    version === "1"
      ? {
          extensionsUsed: ["VRMC_vrm"],
          extensions: {
            VRMC_vrm: {
              specVersion: "1.0",
              meta: {
                name: "Martlet check model",
                version: "1",
                authors: ["Martlet"],
                licenseUrl: "https://vrm.dev/licenses/1.0/",
                avatarPermission: "onlyAuthor",
                commercialUsage: "personalNonProfit",
                creditNotation: "required",
                modification: "prohibited",
              },
              humanoid: { humanBones: Object.fromEntries(bones.map(([name]) => [name, { node: index.get(name) }])) },
            },
          },
        }
      : {
          extensionsUsed: ["VRM"],
          extensions: {
            VRM: {
              exporterVersion: "Martlet check",
              specVersion: "0.0",
              meta: {
                title: "Martlet check model",
                version: "1",
                author: "Martlet",
                allowedUserName: "OnlyAuthor",
                violentUssageName: "Disallow",
                sexualUssageName: "Disallow",
                commercialUssageName: "Disallow",
                licenseName: "Redistribution_Prohibited",
              },
              humanoid: {
                humanBones: bones.map(([name]) => ({ bone: vrm0Name(name), node: index.get(name), useDefaultValues: true })),
              },
            },
          },
        };

  return glb({ asset: { version: "2.0", generator: "Martlet vrma-check" }, scene: 0, scenes: [{ nodes: [0] }], nodes, ...extension });
}

function glb(json) {
  let text = Buffer.from(JSON.stringify(json), "utf8");
  text = Buffer.concat([text, Buffer.alloc((4 - (text.length % 4)) % 4, 0x20)]);
  const header = Buffer.alloc(20);
  header.writeUInt32LE(0x46546c67, 0);
  header.writeUInt32LE(2, 4);
  header.writeUInt32LE(20 + text.length, 8);
  header.writeUInt32LE(text.length, 12);
  header.writeUInt32LE(0x4e4f534a, 16);
  return Buffer.concat([header, text]);
}

async function parse(bytes, Plugin) {
  const loader = new GLTFLoader();
  loader.register((parser) => new Plugin(parser));
  const buffer = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
  return loader.parseAsync(buffer, "");
}

function pose(vrm) {
  vrm.update(0);
  vrm.scene.updateMatrixWorld(true);
  const positions = {};
  for (const [name] of SKELETON) {
    const node = vrm.humanoid.getRawBoneNode(name);
    if (node) positions[name] = node.getWorldPosition(new THREE.Vector3());
  }
  return positions;
}

function directions(rest) {
  const up = new THREE.Vector3(0, 1, 0);
  const right = rest.rightUpperArm.clone().sub(rest.leftUpperArm).setY(0).normalize();
  const forward = new THREE.Vector3().crossVectors(up, right).normalize();
  const all = { up, right, forward };
  all.down = up.clone().negate();
  all.left = right.clone().negate();
  all.back = forward.clone().negate();
  return all;
}

const fmt = (v) => `(${v.toArray().map((n) => n.toFixed(3)).join(", ")})`;

async function main() {
  let casesDir = process.argv[2];
  if (!casesDir) {
    casesDir = path.join(here, "out");
    const python = process.env.PYTHON ?? "python";
    const made = spawnSync(python, [path.join(here, "make_cases.py"), casesDir], { stdio: "inherit" });
    if (made.status !== 0) throw new Error(`make_cases.py failed (${made.error ?? `exit ${made.status}`})`);
  }
  const { restHipsHeight, cases } = JSON.parse(readFileSync(path.join(casesDir, "cases.json"), "utf8"));

  const warnings = [];
  const warn = console.warn;
  console.warn = (...args) => warnings.push(args.join(" "));

  const animations = [];
  const validatorNotes = new Set();
  for (const item of cases) {
    const bytes = readFileSync(path.join(casesDir, item.file));
    const report = await validator.validateBytes(new Uint8Array(bytes), { uri: item.file });
    const problems = report.issues.messages.filter((m) => m.severity <= 1);
    if (problems.length) {
      throw new Error(`${item.file}: glTF validator: ${problems.map((m) => `${m.code} ${m.pointer ?? ""} ${m.message}`).join(" | ")}`);
    }
    report.issues.messages.forEach((m) => validatorNotes.add(m.code));

    warnings.length = 0;
    const gltf = await parse(bytes, VRMAnimationLoaderPlugin);
    const animation = gltf.userData.vrmAnimations?.[0];
    if (!animation) throw new Error(`${item.file}: the loader found no VRM animation`);
    if (warnings.length) throw new Error(`${item.file}: the loader warned: ${warnings.join(" | ")}`);
    animations.push({ item, animation });
  }
  console.log(
    `glTF validator ${validator.version()}: ${cases.length} files, 0 errors, 0 warnings` +
      (validatorNotes.size ? ` (notes: ${[...validatorNotes].join(", ")})` : ""),
  );

  const models = [
    { label: "VRM 1.0", version: "1", withUpperChest: true, withToes: true },
    { label: "VRM 0.x (no upperChest, no toes)", version: "0", withUpperChest: false, withToes: false },
  ];

  let failures = 0;
  let passes = 0;
  for (const model of models) {
    const gltf = await parse(makeVrm(model), VRMLoaderPlugin);
    const vrm = gltf.userData.vrm;
    if (!vrm) throw new Error(`${model.label}: three-vrm did not load the test model`);
    if (vrm.meta.metaVersion !== model.version) throw new Error(`${model.label}: metaVersion is ${vrm.meta.metaVersion}`);
    if (model.version === "0") VRMUtils.rotateVRM0(vrm);

    vrm.humanoid.resetNormalizedPose();
    const rest = pose(vrm);
    const axes = directions(rest);
    const scale = vrm.humanoid.normalizedRestPose.hips.position[1] / restHipsHeight;
    console.log(`\n${model.label}: forward ${fmt(axes.forward)}, left ${fmt(axes.left)}, hips scale ${scale.toFixed(3)}`);

    for (const { item, animation } of animations) {
      const clip = createVRMAnimationClip(animation, vrm);
      const mixer = new THREE.AnimationMixer(vrm.scene);
      mixer.clipAction(clip).play();
      for (const check of item.checks) {
        mixer.setTime(check.time);
        const now = pose(vrm);
        let ok;
        let detail;
        if (check.hips) {
          const { forward, up, left } = check.hips;
          const offset = axes.forward.clone().multiplyScalar(forward)
            .addScaledVector(axes.up, up)
            .addScaledVector(axes.left, left)
            .multiplyScalar(scale);
          const expected = rest.hips.clone().add(offset);
          const error = now.hips.distanceTo(expected);
          ok = error <= 0.005 + 0.01 * offset.length();
          detail = `hips at ${fmt(now.hips)}, expected ${fmt(expected)}, error ${error.toFixed(4)} m`;
        } else {
          const actual = now[check.to].clone().sub(now[check.from]).normalize();
          const cosine = actual.dot(axes[check.direction]);
          ok = cosine >= MIN_COSINE;
          detail = `${check.from} -> ${check.to} points ${fmt(actual)}, ${check.direction} cosine ${cosine.toFixed(3)}`;
        }
        ok ? passes++ : failures++;
        console.log(`  ${ok ? "PASS" : "FAIL"} ${item.name} t=${check.time}s: ${detail}`);
      }
      mixer.stopAllAction();
      mixer.uncacheRoot(vrm.scene);
      vrm.humanoid.resetNormalizedPose();
    }
  }

  console.warn = warn;
  console.log(`\n${passes} checks passed, ${failures} failed.`);
  process.exitCode = failures ? 1 : 0;
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 2;
});
