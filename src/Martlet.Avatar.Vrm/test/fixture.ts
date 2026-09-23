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
