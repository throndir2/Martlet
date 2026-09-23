import { VRMHumanBoneName, VRMHumanBoneParentMap, VRMRequiredHumanBoneName } from "@pixiv/three-vrm";

export class VrmError extends Error {
  constructor(public readonly code: string, message: string) {
    super(message);
    this.name = "VrmError";
  }
}

export function requireValid(condition: unknown, message: string): asserts condition {
  if (!condition) throw new VrmError("invalid-input", message);
}

type JsonObject = Record<string, unknown>;
export function object(value: unknown, label: string): JsonObject {
  requireValid(value !== null && typeof value === "object" && !Array.isArray(value), `${label} must be an object.`);
  return value as JsonObject;
}
function array(value: unknown, label: string, max = 4096): unknown[] {
  requireValid(Array.isArray(value) && value.length <= max, `${label} must be an array of at most ${max} entries.`);
  return value;
}
function optionalArray(value: unknown, label: string, max = 4096): unknown[] {
  return value === undefined ? [] : array(value, label, max);
}
export function integer(value: unknown, min: number, max: number, label: string): number {
  requireValid(typeof value === "number" && Number.isSafeInteger(value) && value >= min && value <= max,
    `${label} must be an integer in ${min}..${max}.`);
  return value;
}
export function finite(value: unknown, min: number, max: number, label: string): number {
  requireValid(typeof value === "number" && Number.isFinite(value) && value >= min && value <= max,
    `${label} must be finite in ${min}..${max}.`);
  return value;
}
function at(values: unknown[], index: unknown, label: string): JsonObject {
  return object(values[integer(index, 0, values.length - 1, label)], label);
}
function vector(value: unknown, length: number, label: string, max = 10000): number[] {
  const values = array(value, label, length);
  requireValid(values.length === length, `${label} requires ${length} values.`);
  return values.map(v => finite(v, -max, max, label));
}

export const LIMITS = Object.freeze({
  fileBytes: 32 * 1024 * 1024,
  jsonBytes: 2 * 1024 * 1024,
  nodes: 512,
  accessors: 2048,
  decodedGeometryBytes: 64 * 1024 * 1024,
  texturePixels: 16 * 1024 * 1024,
  expressions: 128,
});
const allowedExtensions = new Set([
  "VRMC_vrm", "VRMC_materials_mtoon", "VRMC_springBone",
  "KHR_materials_unlit", "KHR_texture_transform", "KHR_materials_emissive_strength",
]);
export const mouthPresets = ["aa", "ih", "ou", "ee", "oh"] as const;
export const blinkPresets = ["blink", "blinkLeft", "blinkRight"] as const;
export const gazePresets = ["lookUp", "lookDown", "lookLeft", "lookRight"] as const;
const presets = new Set<string>([...mouthPresets, ...blinkPresets, ...gazePresets,
  "happy", "angry", "sad", "relaxed", "surprised", "neutral"]);
export interface ExpressionCapability {
  readonly name: string;
  readonly preset: boolean;
  readonly usable: boolean;
  readonly morphTargets: readonly string[];
  readonly materialTargets: readonly string[];
  readonly overrideMouth: string;
  readonly overrideBlink: string;
  readonly overrideLookAt: string;
}
export interface VrmCapabilities {
  readonly format: "VRM1";
  readonly expressions: readonly ExpressionCapability[];
  readonly humanoidBones: readonly string[];
  readonly gaze: "bone" | "expression" | "absent";
  readonly secondaryMotion: boolean;
  readonly bodyPlayback: false;
  readonly facialDetail: "explicit-mapping-required" | "preset-only" | "absent";
  readonly diagnostics: readonly string[];
}
interface Parsed {
  json: JsonObject;
  bin: Uint8Array;
}

function parse(buffer: ArrayBuffer): Parsed {
  requireValid(buffer instanceof ArrayBuffer && buffer.byteLength >= 20 && buffer.byteLength <= LIMITS.fileBytes,
    `Expected local GLB buffer of 20..${LIMITS.fileBytes} bytes.`);
  const data = new DataView(buffer);
  requireValid(data.getUint32(0, true) === 0x46546c67 && data.getUint32(4, true) === 2,
    "Only GLB version 2 containers are supported.");
  requireValid(data.getUint32(8, true) === buffer.byteLength, "GLB declared size differs from supplied bytes.");
  let offset = 12;
  let json: JsonObject | undefined;
  let bin = new Uint8Array(0);
  let chunks = 0;
  while (offset < buffer.byteLength) {
    requireValid(offset + 8 <= buffer.byteLength, "Truncated GLB chunk header.");
    const size = data.getUint32(offset, true);
    const type = data.getUint32(offset + 4, true);
    offset += 8;
    requireValid(size % 4 === 0 && offset + size <= buffer.byteLength, "Unaligned or truncated GLB chunk.");
    if (chunks === 0) {
      requireValid(type === 0x4e4f534a && size <= LIMITS.jsonBytes, "First chunk must be bounded JSON.");
      try {
        json = object(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(new Uint8Array(buffer, offset, size))), "glTF");
      } catch (error) {
        if (error instanceof VrmError) throw error;
        throw new VrmError("invalid-json", "GLB JSON is not valid UTF-8 JSON.");
      }
    } else {
      requireValid(chunks === 1 && type === 0x004e4942, "Only JSON followed by one embedded BIN chunk is supported.");
      bin = new Uint8Array(buffer, offset, size);
    }
    offset += size;
    chunks++;
  }
  requireValid(json, "Missing JSON chunk.");
  return { json, bin };
}

function checkTree(json: JsonObject): void {
  let entries = 0;
  const declared = new Set(optionalArray(json.extensionsUsed, "extensionsUsed", 16));
  const present = new Set<string>();
  const visit = (value: unknown, depth: number): void => {
    requireValid(++entries <= 120000 && depth <= 48, "JSON resource/depth budget exceeded.");
    if (typeof value === "number") finite(value, -1e12, 1e12, "JSON number");
    if (typeof value === "string") requireValid(value.length <= 8192, "JSON string too long.");
    if (Array.isArray(value)) value.forEach(v => visit(v, depth + 1));
    else if (value !== null && typeof value === "object") {
      for (const [key, child] of Object.entries(value)) {
        requireValid(!["uri", "url", "__proto__", "prototype", "constructor"].includes(key),
          `Forbidden field ${key}: external/data/file resources and prototype keys are not accepted.`);
        if (key === "extensions") {
          for (const name of Object.keys(object(child, "extensions"))) {
            requireValid(allowedExtensions.has(name), `Unsupported extension ${name}; re-export without it.`);
            requireValid(declared.has(name), `Extension ${name} is missing from extensionsUsed; re-export consistent declarations.`);
            present.add(name);
          }
        }
        visit(child, depth + 1);
      }
    }
  };
  visit(json, 0);
  for (const key of ["extensionsUsed", "extensionsRequired"]) {
    for (const name of optionalArray(json[key], key, 16)) {
      requireValid(typeof name === "string" && allowedExtensions.has(name), `Unsupported ${key} entry ${String(name)}.`);
      requireValid(declared.has(name) && present.has(name), `${key} contains undeclared or absent extension ${name}.`);
    }
  }
}

/** Conservative preflight, not a complete glTF conformance validator. No GPU or URI loading. */
export function inspectVrm(buffer: ArrayBuffer): VrmCapabilities {
  const { json, bin } = parse(buffer);
  checkTree(json);
  requireValid(object(json.asset, "asset").version === "2.0", "glTF asset.version must be 2.0.");
  const extension = object(object(json.extensions, "extensions").VRMC_vrm, "VRMC_vrm");
  requireValid(extension.specVersion === "1.0", "Only VRM 1.0 is supported; VRM0 migration is not implemented.");
  const meta = object(extension.meta, "VRM meta");
  requireValid(typeof meta.name === "string" && meta.name.length > 0, "VRM meta.name is required.");
  requireValid(array(meta.authors, "meta.authors", 32).every(v => typeof v === "string" && v.length > 0)
    && (meta.authors as unknown[]).length > 0, "VRM authors are required.");
  requireValid(meta.licenseUrl === "https://vrm.dev/licenses/1.0/", "Expected VRM 1.0 licenseUrl; inspect usage terms before use.");
  requireValid(optionalArray(json.animations, "animations").length === 0, "Embedded animation playback is outside this adapter; use a separate VRMA integration.");
  const nodes = array(json.nodes, "nodes", LIMITS.nodes);
  const meshes = optionalArray(json.meshes, "meshes", 128);
  const accessors = optionalArray(json.accessors, "accessors", LIMITS.accessors);
  const views = optionalArray(json.bufferViews, "bufferViews", 2048);
  const materials = optionalArray(json.materials, "materials", 128);
  const textures = optionalArray(json.textures, "textures", 64);
  const images = optionalArray(json.images, "images", 32);
  const buffers = optionalArray(json.buffers, "buffers", 1);
  const byteLength = buffers.length ? integer(object(buffers[0], "buffer").byteLength, 0, bin.length, "buffer.byteLength") : 0;
  requireValid(bin.length - byteLength <= 3, "BIN bytes do not match embedded buffer.");
  let viewBytes = 0;
  for (const v of views) {
    const view = object(v, "bufferView");
    requireValid(view.buffer === 0 && buffers.length === 1, "bufferView must reference embedded buffer 0.");
    const start = integer(view.byteOffset ?? 0, 0, byteLength, "bufferView.byteOffset");
    const size = integer(view.byteLength, 1, byteLength, "bufferView.byteLength");
    viewBytes += size;
    requireValid(viewBytes <= LIMITS.decodedGeometryBytes,
      "Aggregate bufferView copy budget exceeded; overlapping views count separately. Re-export with shared compact views.");
    requireValid(start + size <= byteLength, "bufferView exceeds BIN.");
    if (view.byteStride !== undefined) integer(view.byteStride, 4, 252, "byteStride");
  }
  let decodedBytes = 0;
  for (const a of accessors) {
    const accessor = object(a, "accessor");
    requireValid(accessor.sparse === undefined, "Sparse accessors are not supported by the bounded importer.");
    const count = integer(accessor.count, 1, 500000, "accessor.count");
    const components = ({ SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4, MAT4: 16 } as Record<string, number>)[String(accessor.type)];
    const width = ({ 5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4 } as Record<string, number>)[String(accessor.componentType)];
    requireValid(components && width, "Unsupported accessor type/componentType.");
    const view = at(views, accessor.bufferView, "accessor.bufferView");
    const stride = integer(view.byteStride ?? components * width, components * width, 252, "accessor stride");
    const start = integer(accessor.byteOffset ?? 0, 0, byteLength, "accessor offset");
    requireValid(start % width === 0 && stride % width === 0
      && start + (count - 1) * stride + components * width <= Number(view.byteLength), "Accessor exceeds/alignment differs from its bufferView.");
    decodedBytes += count * components * 4;
    requireValid(decodedBytes <= LIMITS.decodedGeometryBytes, "Decoded geometry budget exceeded.");
    if (accessor.min !== undefined) vector(accessor.min, components, "accessor.min", 1e9);
    if (accessor.max !== undefined) vector(accessor.max, components, "accessor.max", 1e9);
    if (accessor.componentType === 5126) {
      const values = new DataView(bin.buffer, bin.byteOffset + Number(view.byteOffset ?? 0) + start);
      for (let i = 0; i < count; i++)
        for (let c = 0; c < components; c++) finite(values.getFloat32(i * stride + c * width, true), -1e9, 1e9, "geometry value");
    }
  }
  let renderBytes = 0;
  for (const m of meshes) {
    const mesh = object(m, "mesh");
    const primitives = array(mesh.primitives, "primitives", 16);
    requireValid(primitives.length > 0, "A mesh requires primitives.");
    if (mesh.weights !== undefined) {
      const count = optionalArray(object(primitives[0], "primitive").targets, "targets", 64).length;
      requireValid(array(mesh.weights, "mesh.weights", 64).length === count, "Mesh weights must match morph targets.");
      array(mesh.weights, "weights").forEach(v => finite(v, 0, 1, "mesh weight"));
    }
    for (const p of primitives) {
      const primitive = object(p, "primitive");
      requireValid(primitive.mode === undefined || primitive.mode === 4, "Only triangle primitives are supported.");
      const attributes = object(primitive.attributes, "attributes");
      const position = at(accessors, attributes.POSITION, "POSITION");
      requireValid(position.type === "VEC3" && position.componentType === 5126, "POSITION must be float VEC3.");
      let components = 0;
      for (const [name, index] of Object.entries(attributes)) {
        requireValid(["POSITION", "NORMAL", "TANGENT", "TEXCOORD_0", "TEXCOORD_1", "COLOR_0", "JOINTS_0", "WEIGHTS_0"].includes(name), `Unsupported attribute ${name}.`);
        const a = at(accessors, index, name);
        requireValid(a.count === position.count, "Vertex attribute counts differ.");
        const expected = ({ POSITION: ["VEC3"], NORMAL: ["VEC3"], TANGENT: ["VEC4"], TEXCOORD_0: ["VEC2"],
          TEXCOORD_1: ["VEC2"], COLOR_0: ["VEC3", "VEC4"], JOINTS_0: ["VEC4"], WEIGHTS_0: ["VEC4"] } as Record<string, string[]>)[name]!;
        requireValid(expected.includes(String(a.type)), `Invalid ${name} attribute shape.`);
        components += 4;
      }
      if (primitive.indices !== undefined) {
        const indices = at(accessors, primitive.indices, "indices");
        requireValid(indices.type === "SCALAR" && [5121, 5123, 5125].includes(Number(indices.componentType)), "Invalid index accessor.");
        const v = at(views, indices.bufferView, "indices view");
        requireValid(v.byteStride === undefined, "Interleaved indices are unsupported.");
        const width = indices.componentType === 5121 ? 1 : indices.componentType === 5123 ? 2 : 4;
        const bytes = new DataView(bin.buffer, bin.byteOffset + Number(v.byteOffset ?? 0) + Number(indices.byteOffset ?? 0));
        for (let i = 0; i < Number(indices.count); i++) {
          const n = width === 1 ? bytes.getUint8(i) : width === 2 ? bytes.getUint16(i * 2, true) : bytes.getUint32(i * 4, true);
          requireValid(n < Number(position.count), "Triangle index exceeds vertex count.");
        }
      }
      if (primitive.material !== undefined) at(materials, primitive.material, "material");
      for (const t of optionalArray(primitive.targets, "morph targets", 64)) {
        for (const [name, index] of Object.entries(object(t, "morph target"))) {
          requireValid(["POSITION", "NORMAL"].includes(name), "Unsupported morph attribute; re-export tangent-only morphs with POSITION/NORMAL targets.");
          const a = at(accessors, index, "morph accessor");
          requireValid(a.type === "VEC3" && a.componentType === 5126 && a.count === position.count, "Invalid morph accessor.");
          components += 4;
        }
      }
      renderBytes += Number(position.count) * components * 4;
      requireValid(renderBytes <= LIMITS.decodedGeometryBytes, "Expanded primitive/morph budget exceeded.");
    }
  }
  let pixels = 0;
  const imagePixels: number[] = [];
  for (const i of images) {
    const image = object(i, "image");
    requireValid(image.mimeType === "image/png", "Only embedded PNG textures are supported; re-export JPEG/WebP/KTX as PNG.");
    const view = at(views, image.bufferView, "image.bufferView");
    requireValid(Number(view.byteLength) >= 33, "Truncated PNG.");
    const bytes = new DataView(bin.buffer, bin.byteOffset + Number(view.byteOffset ?? 0), Number(view.byteLength));
    requireValid(bytes.getUint32(0) === 0x89504e47 && bytes.getUint32(4) === 0x0d0a1a0a
      && bytes.getUint32(8) === 13 && bytes.getUint32(12) === 0x49484452, "Invalid PNG signature/IHDR.");
    const width = integer(bytes.getUint32(16), 1, 4096, "PNG width");
    const height = integer(bytes.getUint32(20), 1, 4096, "PNG height");
    pixels += width * height;
    imagePixels.push(width * height);
    requireValid(pixels <= LIMITS.texturePixels, "Decoded texture pixel budget exceeded.");
  }
  for (const t of textures) at(images, object(t, "texture").source, "texture source");
  const sampledPixels = textures.reduce<number>((sum, t) => sum + imagePixels[Number(object(t, "texture").source)]!, 0);
  requireValid(sampledPixels * Math.max(1, materials.length) <= LIMITS.texturePixels,
    "Conservative material/texture instance pixel budget exceeded; reduce texture resolution or materials.");
  const checkMaterial = (value: unknown): void => {
    if (Array.isArray(value)) return;
    if (value !== null && typeof value === "object") {
      for (const [key, child] of Object.entries(value)) {
        if (key.endsWith("Texture")) {
          const info = object(child, key);
          at(textures, info.index, "texture index");
          if (info.texCoord !== undefined) integer(info.texCoord, 0, 1, "texture texCoord");
        }
        checkMaterial(child);
      }
    }
  };
  materials.forEach(m => checkMaterial(object(m, "material")));
  const parents = new Map<number, number>();
  let instances = 0;
  nodes.forEach((n, index) => {
    const node = object(n, "node");
    if (node.mesh !== undefined) { at(meshes, node.mesh, "node.mesh"); instances++; }
    if (node.weights !== undefined) {
      const mesh = at(meshes, node.mesh, "weighted mesh");
      const first = object(array(mesh.primitives, "primitives")[0], "primitive");
      const weights = array(node.weights, "node.weights", 64);
      requireValid(weights.length === optionalArray(first.targets, "targets").length, "Node weights must match morph targets.");
      weights.forEach(v => finite(v, 0, 1, "node weight"));
    }
    requireValid(node.matrix === undefined, "Matrix nodes must be exported as TRS for this humanoid adapter.");
    if (node.translation !== undefined) vector(node.translation, 3, "translation");
    if (node.rotation !== undefined) {
      const q = vector(node.rotation, 4, "rotation", 1);
      requireValid(Math.abs(Math.hypot(...q) - 1) < 0.001, "Node rotation must be normalized.");
    }
    if (node.scale !== undefined) requireValid(vector(node.scale, 3, "scale", 100).every(v => v > 0), "Only positive scales supported.");
    for (const child of optionalArray(node.children, "children", LIMITS.nodes)) {
      const id = integer(child, 0, nodes.length - 1, "child node");
      requireValid(id !== index && !parents.has(id), "Node graph contains self-reference or multiple parents.");
      parents.set(id, index);
    }
  });
  requireValid(renderBytes * Math.max(1, instances) <= LIMITS.decodedGeometryBytes, "Instanced geometry budget exceeded.");
  for (let i = 0; i < nodes.length; i++) {
    let current: number | undefined = i;
    const seen = new Set<number>();
    while (current !== undefined) {
      requireValid(!seen.has(current) && seen.size < 128, "Node hierarchy is cyclic or too deep.");
      seen.add(current); current = parents.get(current);
    }
  }
  const scenes = array(json.scenes, "scenes", 1);
  const scene = at(scenes, json.scene ?? 0, "scene");
  const reachable = new Set<number>();
  const walk = (id: number): void => {
    requireValid(!reachable.has(id), "Scene repeats a node.");
    reachable.add(id);
    for (const c of optionalArray(object(nodes[id], "node").children, "children")) walk(Number(c));
  };
  for (const root of array(scene.nodes, "scene.nodes", LIMITS.nodes)) {
    const id = integer(root, 0, nodes.length - 1, "scene root");
    requireValid(!parents.has(id), "Scene root has a parent.");
    walk(id);
  }
  const skins = optionalArray(json.skins, "skins", 32);
  for (const s of skins) {
    const skin = object(s, "skin");
    const joints = array(skin.joints, "skin.joints", 128);
    requireValid(joints.length > 0 && new Set(joints).size === joints.length, "Skin joints missing or repeated.");
    joints.forEach(j => at(nodes, j, "skin joint"));
    if (skin.inverseBindMatrices !== undefined) {
      const a = at(accessors, skin.inverseBindMatrices, "inverseBindMatrices");
      requireValid(a.type === "MAT4" && a.componentType === 5126 && a.count === joints.length, "Invalid inverse bind matrices.");
    }
    if (skin.skeleton !== undefined) at(nodes, skin.skeleton, "skin skeleton");
  }
  nodes.forEach(n => { const node = object(n, "node"); if (node.skin !== undefined) at(skins, node.skin, "node.skin"); });
  const bones = object(object(extension.humanoid, "humanoid").humanBones, "humanBones");
  const assigned = new Set<number>();
  for (const [name, bone] of Object.entries(bones)) {
    requireValid(Object.values(VRMHumanBoneName).includes(name as VRMHumanBoneName), `Unknown humanoid role ${name}.`);
    const id = integer(object(bone, name).node, 0, nodes.length - 1, `${name}.node`);
    requireValid(reachable.has(id) && !assigned.has(id), `Humanoid ${name} is outside scene or shares a node.`);
    assigned.add(id);
  }
  for (const required of Object.values(VRMRequiredHumanBoneName)) requireValid(bones[required], `Missing required humanoid bone ${required}.`);
  for (const [name, bone] of Object.entries(bones)) {
    let role = VRMHumanBoneParentMap[name as VRMHumanBoneName];
    while (role && !bones[role]) role = VRMHumanBoneParentMap[role];
    if (role) {
      const ancestor = Number(object(bones[role], role).node);
      let current = parents.get(Number(object(bone, name).node));
      while (current !== undefined && current !== ancestor) current = parents.get(current);
      requireValid(current === ancestor, `Humanoid ${name} must descend from standardized parent ${role}.`);
    }
  }
  const expressions: ExpressionCapability[] = [];
  const expressionRoot = extension.expressions === undefined ? {} : object(extension.expressions, "expressions");
  for (const group of ["preset", "custom"]) {
    for (const [name, e] of Object.entries(expressionRoot[group] === undefined ? {} : object(expressionRoot[group], group))) {
      requireValid(name.length > 0 && name.length <= 128 && !expressions.some(e => e.name === name), "Invalid/repeated expression name.");
      requireValid(group !== "preset" || presets.has(name), `Unknown preset ${name}.`);
      requireValid(group !== "custom" || !presets.has(name), `Custom expression must not shadow preset ${name}.`);
      const expression = object(e, "expression");
      const targets: string[] = [];
      const authoredTargets = new Set<string>();
      for (const b of optionalArray(expression.morphTargetBinds, "morphTargetBinds", 128)) {
        const bind = object(b, "morph bind");
        const node = at(nodes, bind.node, "morph node");
        requireValid(reachable.has(Number(bind.node)), "Expression binds node outside active scene.");
        const mesh = at(meshes, node.mesh, "morph mesh");
        for (const p of array(mesh.primitives, "primitives")) {
          const primitive = object(p, "primitive");
          integer(bind.index, 0, optionalArray(primitive.targets, "targets").length - 1, "morph target index");
        }
        const weight = finite(bind.weight, 0, 1, "morph bind weight");
        const target = `${Number(bind.node)}:${Number(bind.index)}`;
        requireValid(!authoredTargets.has(target), `Duplicate morph target ${target} within expression ${name}; merge or remove the duplicate binding.`);
        authoredTargets.add(target);
        if (weight > 0) targets.push(target);
      }
      const materialBinds = optionalArray(expression.materialColorBinds, "materialColorBinds", 32);
      const textureBinds = optionalArray(expression.textureTransformBinds, "textureTransformBinds", 32);
      // Non-morph expressions still render, but this slice does not qualify them as facial controls.
      for (const b of [...materialBinds, ...textureBinds]) at(materials, object(b, "material bind").material, "bound material");
      const materialTargets: string[] = [];
      for (const b of materialBinds) {
        const bind = object(b, "materialColorBind");
        requireValid(["color", "emissionColor", "shadeColor", "matcapColor", "rimColor", "outlineColor"].includes(String(bind.type)),
          "Unsupported material color binding.");
        vector(bind.targetValue, 4, "material targetValue", 100);
        materialTargets.push(`${Number(bind.material)}:${String(bind.type)}`);
      }
      for (const b of textureBinds) {
        const bind = object(b, "textureTransformBind");
        if (bind.offset !== undefined) vector(bind.offset, 2, "texture offset", 100);
        if (bind.scale !== undefined) vector(bind.scale, 2, "texture scale", 100);
        materialTargets.push(`${Number(bind.material)}:texture-transform`);
      }
      for (const key of ["overrideMouth", "overrideBlink", "overrideLookAt"])
        requireValid(expression[key] === undefined || ["none", "block", "blend"].includes(String(expression[key])), `Invalid ${key}.`);
      requireValid(expression.isBinary === undefined || typeof expression.isBinary === "boolean", "Invalid isBinary.");
      expressions.push(Object.freeze({
        name, preset: group === "preset", usable: targets.length > 0, morphTargets: Object.freeze(targets),
        materialTargets: Object.freeze(materialTargets),
        overrideMouth: String(expression.overrideMouth ?? "none"),
        overrideBlink: String(expression.overrideBlink ?? "none"),
        overrideLookAt: String(expression.overrideLookAt ?? "none"),
      }));
      requireValid(expressions.length <= LIMITS.expressions, "Expression budget exceeded.");
    }
  }
  let gaze: VrmCapabilities["gaze"] = "absent";
  const diagnostics: string[] = [];
  if (extension.lookAt !== undefined) {
    const lookAt = object(extension.lookAt, "lookAt");
    requireValid(lookAt.type === "bone" || lookAt.type === "expression", "Unknown lookAt type.");
    for (const key of ["rangeMapHorizontalInner", "rangeMapHorizontalOuter", "rangeMapVerticalDown", "rangeMapVerticalUp"]) {
      const range = object(lookAt[key], key);
      finite(range.inputMaxValue, 0.001, 180, "lookAt input range");
      finite(range.outputScale, 0, lookAt.type === "bone" ? 180 : 1, "lookAt output range");
    }
    if (lookAt.offsetFromHeadBone !== undefined) vector(lookAt.offsetFromHeadBone, 3, "lookAt offset", 10);
    if (lookAt.type === "bone" ? bones.leftEye && bones.rightEye :
      gazePresets.every(p => expressions.some(e => e.name === p && e.usable))) gaze = lookAt.type;
    else diagnostics.push("Gaze unavailable: author both eye bones or all four directional expressions and re-export lookAt.");
  } else diagnostics.push("Gaze unavailable: author VRM1 lookAt with usable eye controls.");
  const spring = object(json.extensions, "extensions").VRMC_springBone;
  let secondaryMotion = false;
  if (spring !== undefined) {
    const s = object(spring, "springBone");
    requireValid(s.specVersion === "1.0", "Only springBone 1.0 is supported.");
    const colliders = optionalArray(s.colliders, "colliders", 128);
    for (const c of colliders) {
      const collider = object(c, "collider"); at(nodes, collider.node, "collider node");
      const shape = object(collider.shape, "collider shape");
      requireValid(Object.keys(shape).length === 1 && (shape.sphere !== undefined || shape.capsule !== undefined), "Unsupported collider shape.");
      const geometry = object(shape.sphere ?? shape.capsule, "collider geometry");
      vector(geometry.offset, 3, "collider offset", 10); finite(geometry.radius, 0, 10, "collider radius");
      if (shape.capsule !== undefined) vector(geometry.tail, 3, "capsule tail", 10);
    }
    const groups = optionalArray(s.colliderGroups, "colliderGroups", 128);
    for (const g of groups) for (const c of array(object(g, "colliderGroup").colliders, "collider indices", 128)) at(colliders, c, "collider");
    const used = new Set<number>();
    for (const entry of optionalArray(s.springs, "springs", 64)) {
      const chain = object(entry, "spring");
      if (chain.center !== undefined) at(nodes, chain.center, "spring center");
      for (const g of optionalArray(chain.colliderGroups, "spring groups", 128)) at(groups, g, "spring group");
      const joints = array(chain.joints, "spring joints", 128);
      for (const j of joints) {
        const joint = object(j, "spring joint");
        const id = integer(joint.node, 0, nodes.length - 1, "spring joint node");
        requireValid(reachable.has(id) && !used.has(id) && !assigned.has(id), "Spring joints must be unique, reachable, and separate from humanoid controls.");
        used.add(id);
        finite(joint.hitRadius ?? 0, 0, 10, "hitRadius"); finite(joint.stiffness ?? 1, 0, 100, "stiffness");
        finite(joint.gravityPower ?? 0, 0, 100, "gravityPower"); finite(joint.dragForce ?? 0.5, 0, 1, "dragForce");
        if (joint.gravityDir !== undefined) vector(joint.gravityDir, 3, "gravityDir", 1);
      }
      if (joints.length >= 2) secondaryMotion = true;
    }
  }
  if (!secondaryMotion) diagnostics.push("Secondary motion unavailable: author a supported VRMC_springBone chain.");
  const usable = expressions.filter(e => e.usable);
  if (!usable.some(e => mouthPresets.includes(e.name as typeof mouthPresets[number])))
    diagnostics.push("No usable vowel presets: supply an explicit mapping to authored facial expressions, choose another supported source, or omit mouth.");
  diagnostics.push("ARKit detail is never inferred from names or vowel presets; author and review each explicit coefficient mapping.");
  diagnostics.push("Body/VRMA playback is not implemented in this slice.");
  return Object.freeze({
    format: "VRM1", expressions: Object.freeze(expressions), humanoidBones: Object.freeze(Object.keys(bones)), gaze,
    secondaryMotion, bodyPlayback: false, facialDetail: usable.some(e => !e.preset) ? "explicit-mapping-required" : usable.length ? "preset-only" : "absent",
    diagnostics: Object.freeze(diagnostics),
  });
}
