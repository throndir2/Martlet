/** Self-authored synthetic triangle rig. Importer evidence only, not an artist/model quality fixture. */
export function fixtureDocument() {
  const roles = [
    ["hips", -1, [0, 0.9, 0]], ["spine", 0, [0, 0.2, 0]], ["head", 1, [0, 0.4, 0]],
    ["leftUpperLeg", 0, [0.1, -0.1, 0]], ["leftLowerLeg", 3, [0, -0.3, 0]], ["leftFoot", 4, [0, -0.3, 0.1]],
    ["rightUpperLeg", 0, [-0.1, -0.1, 0]], ["rightLowerLeg", 6, [0, -0.3, 0]], ["rightFoot", 7, [0, -0.3, 0.1]],
    ["leftUpperArm", 1, [0.2, 0.3, 0]], ["leftLowerArm", 9, [0.2, 0, 0]], ["leftHand", 10, [0.2, 0, 0]],
    ["rightUpperArm", 1, [-0.2, 0.3, 0]], ["rightLowerArm", 12, [-0.2, 0, 0]], ["rightHand", 13, [-0.2, 0, 0]],
    ["leftEye", 2, [0.04, 0.05, 0.05]], ["rightEye", 2, [-0.04, 0.05, 0.05]],
  ] as const;
  const nodes: Record<string, unknown>[] = roles.map(([name, , translation]) => ({ name, translation, children: [] }));
  roles.forEach(([, parent], i) => { if (parent >= 0) (nodes[parent]!.children as number[]).push(i); });
  nodes.push({ name: "FixtureFace", mesh: 0 });
  nodes.push({ name: "HairRoot", translation: [0, 0.1, 0], children: [19] });
  nodes.push({ name: "HairMiddle", translation: [0, -0.1, 0], children: [20] });
  nodes.push({ name: "HairTip", translation: [0, -0.1, 0] });
  (nodes[2]!.children as number[]).push(17, 18);
  const names = ["aa", "happy", "blink", "lookUp", "lookDown", "lookLeft", "lookRight", "AuthoredLip"];
  const values = [
    -0.1, 0, 0, 0.1, 0, 0, 0, 0.2, 0,
    ...names.flatMap((_, i) => [0, 0, 0, 0, 0, 0, 0, 0.01 * (i + 1), 0]),
  ];
  const bin = new Uint8Array(new Float32Array(values).buffer);
  const document = {
    asset: { version: "2.0", generator: "Martlet self-authored synthetic importer fixture" },
    extensionsUsed: ["VRMC_vrm", "VRMC_springBone"],
    extensions: {
      VRMC_vrm: {
        specVersion: "1.0",
        meta: { name: "Synthetic triangle only", authors: ["Martlet tests"], licenseUrl: "https://vrm.dev/licenses/1.0/" },
        humanoid: { humanBones: Object.fromEntries(roles.map(([name], node) => [name, { node }])) },
        expressions: {
          preset: Object.fromEntries(names.slice(0, 7).map((name, index) => [name, {
            morphTargetBinds: [{ node: 17, index, weight: 1 }],
            ...(name === "happy" ? { overrideMouth: "block", overrideBlink: "blend", overrideLookAt: "blend" } : {}),
          }])),
          custom: { AuthoredLip: { morphTargetBinds: [{ node: 17, index: 7, weight: 1 }] } },
        },
        lookAt: {
          type: "expression",
          rangeMapHorizontalInner: { inputMaxValue: 90, outputScale: 1 },
          rangeMapHorizontalOuter: { inputMaxValue: 90, outputScale: 1 },
          rangeMapVerticalDown: { inputMaxValue: 90, outputScale: 1 },
          rangeMapVerticalUp: { inputMaxValue: 90, outputScale: 1 },
        },
      },
      VRMC_springBone: { specVersion: "1.0", springs: [{ joints: [{ node: 18 }, { node: 19 }, { node: 20 }] }] },
    },
    scene: 0, scenes: [{ nodes: [0] }], nodes,
    meshes: [{ name: "FixtureFace", primitives: [{
      attributes: { POSITION: 0 }, targets: names.map((_, i) => ({ POSITION: i + 1 })),
    }] }],
    buffers: [{ byteLength: bin.length }],
    bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: bin.length }],
    accessors: values.length ? Array.from({ length: 9 }, (_, i) => ({
      bufferView: 0, byteOffset: i * 36, componentType: 5126, count: 3, type: "VEC3",
      min: i === 0 ? [-0.1, 0, 0] : [0, 0, 0], max: i === 0 ? [0.1, 0.2, 0] : [0, 0.08, 0],
    })) : [],
  };
  return { document, bin };
}

export function encodeGlb(document: unknown, bin: Uint8Array = new Uint8Array(0)): ArrayBuffer {
  const json = new TextEncoder().encode(JSON.stringify(document));
  const jsonSize = Math.ceil(json.length / 4) * 4;
  const binSize = Math.ceil(bin.length / 4) * 4;
  const buffer = new ArrayBuffer(12 + 8 + jsonSize + (binSize ? 8 + binSize : 0));
  const view = new DataView(buffer);
  view.setUint32(0, 0x46546c67, true); view.setUint32(4, 2, true); view.setUint32(8, buffer.byteLength, true);
  view.setUint32(12, jsonSize, true); view.setUint32(16, 0x4e4f534a, true);
  new Uint8Array(buffer, 20, jsonSize).fill(32); new Uint8Array(buffer, 20, json.length).set(json);
  if (binSize) {
    view.setUint32(20 + jsonSize, binSize, true); view.setUint32(24 + jsonSize, 0x004e4942, true);
    new Uint8Array(buffer, 28 + jsonSize, bin.length).set(bin);
  }
  return buffer;
}
export function fixture(): ArrayBuffer { const { document, bin } = fixtureDocument(); return encodeGlb(document, bin); }

/** A self-authored VRMA clip (VRMC_vrm_animation 1.0) on the fixture's skeleton in its T-pose, `seconds` long and the same at
 *  both ends: the right upper arm turned `armZ` radians about Z (negative raises it, for VRM 1.0's +Z-facing bones), with
 *  `head` the head tilted that many radians about Z, with `eye` the left eye turned, and with `hips` the hips moved from
 *  (0, 0.9, 0) at the start to there at the end. Importer evidence only, not motion quality. */
export function vrmaFixture({ seconds = 1, armZ = -1.4, head, eye, hips, specVersion = "1.0" }: { seconds?: number; armZ?: number;
  head?: number; eye?: boolean; hips?: readonly [number, number, number]; specVersion?: string } = {}): ArrayBuffer {
  const roles = [
    ["hips", -1, [0, 0.9, 0]], ["spine", 0, [0, 0.2, 0]], ["head", 1, [0, 0.4, 0]],
    ["leftUpperLeg", 0, [0.1, -0.1, 0]], ["leftLowerLeg", 3, [0, -0.3, 0]], ["leftFoot", 4, [0, -0.3, 0.1]],
    ["rightUpperLeg", 0, [-0.1, -0.1, 0]], ["rightLowerLeg", 6, [0, -0.3, 0]], ["rightFoot", 7, [0, -0.3, 0.1]],
    ["leftUpperArm", 1, [0.2, 0.3, 0]], ["leftLowerArm", 9, [0.2, 0, 0]], ["leftHand", 10, [0.2, 0, 0]],
    ["rightUpperArm", 1, [-0.2, 0.3, 0]], ["rightLowerArm", 12, [-0.2, 0, 0]], ["rightHand", 13, [-0.2, 0, 0]],
    ["leftEye", 2, [0.04, 0.05, 0.05]],
  ] as const;
  const nodes = roles.map(([name, , translation]) => ({ name, translation: [...translation], children: [] as number[] }));
  roles.forEach(([, parent], i) => { if (parent >= 0) nodes[parent]!.children.push(i); });
  const index = (name: string) => roles.findIndex(([role]) => role === name);
  const turn = (angle: number) => [0, 0, Math.sin(angle / 2), Math.cos(angle / 2)];
  const floats: number[] = [0, seconds];
  const accessors: Record<string, unknown>[] = [{ bufferView: 0, byteOffset: 0, componentType: 5126, count: 2, type: "SCALAR", min: [0], max: [seconds] }];
  const channels: Record<string, unknown>[] = [], samplers: Record<string, unknown>[] = [];
  const track = (node: number, path: string, values: number[], type: string) => {
    accessors.push({ bufferView: 0, byteOffset: floats.length * 4, componentType: 5126, count: 2, type });
    floats.push(...values);
    samplers.push({ input: 0, output: accessors.length - 1, interpolation: "LINEAR" });
    channels.push({ sampler: samplers.length - 1, target: { node, path } });
  };
  track(index("rightUpperArm"), "rotation", [...turn(armZ), ...turn(armZ)], "VEC4");
  if (head !== undefined) track(index("head"), "rotation", [...turn(head), ...turn(head)], "VEC4");
  if (eye) track(index("leftEye"), "rotation", [...turn(0.5), ...turn(0.5)], "VEC4");
  if (hips) track(index("hips"), "translation", [0, 0.9, 0, ...hips], "VEC3");
  const bin = new Uint8Array(new Float32Array(floats).buffer);
  const document = {
    asset: { version: "2.0", generator: "Martlet self-authored synthetic VRMA fixture" },
    extensionsUsed: ["VRMC_vrm_animation"],
    extensions: { VRMC_vrm_animation: { specVersion, humanoid: { humanBones: Object.fromEntries(roles.map(([name], node) => [name, { node }])) } } },
    scene: 0, scenes: [{ nodes: [0] }], nodes,
    buffers: [{ byteLength: bin.length }], bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: bin.length }], accessors,
    animations: [{ channels, samplers }],
  };
  return encodeGlb(document, bin);
}

/** The same synthetic triangle rig as a VRM 0.x model (the VRM extension), as VRoid Studio and UniVRM export it: facing -Z
 *  with the character's left at -X, bones listed as {bone, node}, blend shape groups with VRM 0.x preset names and 0..100
 *  weights, a first-person BlendShape look-at, a secondary-animation hair group and one MToon material property set.
 *  Importer evidence only, not an artist/model quality fixture. */
export function fixtureDocumentV0() {
  const roles = [
    ["hips", -1, [0, 0.9, 0]], ["spine", 0, [0, 0.2, 0]], ["head", 1, [0, 0.4, 0]],
    ["leftUpperLeg", 0, [-0.1, -0.1, 0]], ["leftLowerLeg", 3, [0, -0.3, 0]], ["leftFoot", 4, [0, -0.3, -0.1]],
    ["rightUpperLeg", 0, [0.1, -0.1, 0]], ["rightLowerLeg", 6, [0, -0.3, 0]], ["rightFoot", 7, [0, -0.3, -0.1]],
    ["leftUpperArm", 1, [-0.2, 0.3, 0]], ["leftLowerArm", 9, [-0.2, 0, 0]], ["leftHand", 10, [-0.2, 0, 0]],
    ["rightUpperArm", 1, [0.2, 0.3, 0]], ["rightLowerArm", 12, [0.2, 0, 0]], ["rightHand", 13, [0.2, 0, 0]],
    ["leftEye", 2, [-0.04, 0.05, -0.05]], ["rightEye", 2, [0.04, 0.05, -0.05]],
  ] as const;
  const nodes: Record<string, unknown>[] = roles.map(([name, , translation]) => ({ name, translation, children: [] }));
  roles.forEach(([, parent], i) => { if (parent >= 0) (nodes[parent]!.children as number[]).push(i); });
  nodes.push({ name: "FixtureFace", mesh: 0 });
  nodes.push({ name: "HairRoot", translation: [0, 0.1, 0], children: [19] });
  nodes.push({ name: "HairMiddle", translation: [0, -0.1, 0], children: [20] });
  nodes.push({ name: "HairTip", translation: [0, -0.1, 0] });
  (nodes[2]!.children as number[]).push(17, 18);
  const groups: [string, string][] = [["A", "a"], ["Joy", "joy"], ["Blink", "blink"], ["LookUp", "lookup"], ["LookDown", "lookdown"],
    ["LookLeft", "lookleft"], ["LookRight", "lookright"], ["AuthoredLip", "unknown"]];
  const values = [
    0.1, 0, 0, -0.1, 0, 0, 0, 0.2, 0,
    ...groups.flatMap((_, i) => [0, 0, 0, 0, 0, 0, 0, 0.01 * (i + 1), 0]),
  ];
  const bin = new Uint8Array(new Float32Array(values).buffer);
  const degreeMap = { curve: [0, 0, 0, 1, 1, 1, 1, 0], xRange: 90, yRange: 1 };
  const document = {
    asset: { version: "2.0", generator: "Martlet self-authored synthetic VRM 0.x importer fixture" },
    extensionsUsed: ["VRM"],
    extensions: {
      VRM: {
        exporterVersion: "Martlet tests", specVersion: "0.0",
        meta: {
          title: "Synthetic VRM 0 triangle", version: "1", author: "Martlet tests", contactInformation: "", reference: "",
          allowedUserName: "Everyone", violentUssageName: "Disallow", sexualUssageName: "Disallow", commercialUssageName: "Allow",
          otherPermissionUrl: "", licenseName: "CC0", otherLicenseUrl: "", texture: -1,
        },
        humanoid: {
          humanBones: roles.map(([bone], node) => ({ bone, node, useDefaultValues: true })),
          armStretch: 0.05, legStretch: 0.05, upperArmTwist: 0.5, lowerArmTwist: 0.5, upperLegTwist: 0.5, lowerLegTwist: 0.5,
          feetSpacing: 0, hasTranslationDoF: false,
        },
        blendShapeMaster: {
          blendShapeGroups: groups.map(([name, presetName], index) => ({
            name, presetName, binds: [{ mesh: 0, index, weight: 100 }], materialValues: [] as unknown[], isBinary: false,
          })),
        },
        firstPerson: {
          firstPersonBone: 2, firstPersonBoneOffset: { x: 0, y: 0.06, z: 0 }, meshAnnotations: [{ mesh: 0, firstPersonFlag: "Auto" }],
          lookAtTypeName: "BlendShape", lookAtHorizontalInner: degreeMap, lookAtHorizontalOuter: degreeMap,
          lookAtVerticalDown: degreeMap, lookAtVerticalUp: degreeMap,
        },
        secondaryAnimation: {
          boneGroups: [{
            comment: "Hair", stiffiness: 1, gravityPower: 0, gravityDir: { x: 0, y: -1, z: 0 }, dragForce: 0.4, center: -1,
            hitRadius: 0.02, bones: [18], colliderGroups: [0],
          }],
          colliderGroups: [{ node: 2, colliders: [{ offset: { x: 0, y: 0.05, z: 0 }, radius: 0.1 }] }],
        },
        materialProperties: [{
          name: "FixtureFace", shader: "VRM/MToon", renderQueue: 2000,
          floatProperties: { _Cutoff: 0.5, _ShadeShift: 0, _ShadeToony: 0.9, _OutlineWidthMode: 0, _CullMode: 2 },
          vectorProperties: { _Color: [1, 1, 1, 1], _ShadeColor: [0.9, 0.8, 0.8, 1], _MainTex: [0, 0, 1, 1] },
          textureProperties: {} as Record<string, number>, keywordMap: { _ALPHABLEND_ON: false }, tagMap: { RenderType: "Opaque" },
        }],
      },
    },
    scene: 0, scenes: [{ nodes: [0] }], nodes,
    materials: [{ name: "FixtureFace", pbrMetallicRoughness: { baseColorFactor: [1, 1, 1, 1] } }],
    meshes: [{ name: "FixtureFace", primitives: [{
      attributes: { POSITION: 0 }, material: 0, targets: groups.map((_, i) => ({ POSITION: i + 1 })),
    }] }],
    buffers: [{ byteLength: bin.length }],
    bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: bin.length }],
    accessors: Array.from({ length: 9 }, (_, i) => ({
      bufferView: 0, byteOffset: i * 36, componentType: 5126, count: 3, type: "VEC3",
      min: i === 0 ? [-0.1, 0, 0] : [0, 0, 0], max: i === 0 ? [0.1, 0.2, 0] : [0, 0.08, 0],
    })),
  };
  return { document, bin };
}
export function fixtureV0(): ArrayBuffer { const { document, bin } = fixtureDocumentV0(); return encodeGlb(document, bin); }

/** The smallest JPEG header the preflight reads: SOI, a JFIF APP0 segment and a frame header (`marker` 0xC0 baseline by
 *  default) of `width` x `height`. Not decodable; the browser decoder is simulated in tests. */
export function jpegHeader(width: number, height: number, marker = 0xc0): Uint8Array {
  return new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0, 16, 0x4a, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
    0xff, marker, 0, 17, 8, height >> 8, height & 255, width >> 8, width & 255, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
    0xff, 0xd9]);
}

/** A document (VRM 0.x or 1.0 fixture) with `image` appended to its BIN as its only image, texture and material. */
export function withImage(document: { buffers: { byteLength: number }[]; bufferViews: Record<string, unknown>[];
  meshes: { primitives: Record<string, unknown>[] }[] }, bin: Uint8Array, image: Uint8Array, mimeType: string): ArrayBuffer {
  const padded = Math.ceil(bin.length / 4) * 4;
  const data = new Uint8Array(padded + image.length); data.set(bin); data.set(image, padded);
  document.buffers[0]!.byteLength = data.length;
  document.bufferViews.push({ buffer: 0, byteOffset: padded, byteLength: image.length });
  Object.assign(document, {
    images: [{ mimeType, bufferView: document.bufferViews.length - 1 }], textures: [{ source: 0 }],
    materials: [{ name: "FixtureFace", pbrMetallicRoughness: { baseColorTexture: { index: 0 } } }],
  });
  Object.assign(document.meshes[0]!.primitives[0]!, { material: 0 });
  return encodeGlb(document, data);
}
