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
function at(values: readonly unknown[], index: unknown, label: string): JsonObject {
  return object(values[integer(index, 0, values.length - 1, label)], label);
}
function vector(value: unknown, length: number, label: string, max = 10000): number[] {
  const values = array(value, label, length);
  requireValid(values.length === length, `${label} requires ${length} values.`);
  return values.map(v => finite(v, -max, max, label));
}

export const LIMITS = Object.freeze({
  /** A VRM file's size limit; the same as Martlet's (LocalAvatarFiles and CharacterModelLibrary). */
  fileBytes: 512 * 1024 * 1024,
  jsonBytes: 2 * 1024 * 1024,
  nodes: 512,
  accessors: 2048,
  decodedGeometryBytes: 64 * 1024 * 1024,
  texturePixels: 16 * 1024 * 1024,
  expressions: 128,
});
/** The extensions a VRM 1.0 model (VRMC_vrm) may use. */
const allowedExtensions = new Set([
  "VRMC_vrm", "VRMC_materials_mtoon", "VRMC_springBone",
  "KHR_materials_unlit", "KHR_texture_transform", "KHR_materials_emissive_strength",
]);
/** The extensions a VRM 0.x model (the VRM extension: meta, humanoid, blend shapes, first person, secondary animation and
 *  MToon material properties) may use. */
const allowedExtensionsV0 = new Set(["VRM", "KHR_materials_unlit", "KHR_texture_transform", "KHR_materials_emissive_strength"]);
/** Names some VRM 0.x exporters (the Blender VRM add-on) list in extensionsUsed without using them; the importer ignores them. */
const declaredOnlyV0 = new Set(["VRMC_materials_mtoon", "VRMC_springBone"]);
/** VRM 0.x blend shape preset names and the VRM 1.0 expression names three-vrm gives them; any other preset ("unknown") keeps
 *  the group's own name. */
export const VRM0_PRESET_NAMES: Readonly<Record<string, string>> = Object.freeze({
  a: "aa", i: "ih", u: "ou", e: "ee", o: "oh", blink: "blink", blink_l: "blinkLeft", blink_r: "blinkRight",
  joy: "happy", angry: "angry", sorrow: "sad", fun: "relaxed",
  lookup: "lookUp", lookdown: "lookDown", lookleft: "lookLeft", lookright: "lookRight", neutral: "neutral",
});
/** VRM 0.x thumb bones and the VRM 1.0 names three-vrm gives them. */
const vrm0Thumbs: Readonly<Record<string, string>> = Object.freeze({
  leftThumbProximal: "leftThumbMetacarpal", leftThumbIntermediate: "leftThumbProximal",
  rightThumbProximal: "rightThumbMetacarpal", rightThumbIntermediate: "rightThumbProximal",
});
/** VRM 0.x material color properties a blend shape can set, and the VRM 1.0 binding type three-vrm makes of them. */
const vrm0MaterialColors: Readonly<Record<string, string>> = Object.freeze({
  _Color: "color", _EmissionColor: "emissionColor", _ShadeColor: "shadeColor", _RimColor: "rimColor", _OutlineColor: "outlineColor",
});
/** A name an object's prototype already has (constructor, toString, __proto__...): never a usable key in a plain object. */
const inherited = (name: string) => name in Object.prototype || name === "prototype";
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
/** The model's metadata, the same shape for VRM 1.0 and VRM 0.x. Format checks only, never consent or usage rights. */
export interface VrmMetaSummary {
  /** "1" for VRM 1.0, "0" for VRM 0.x (three-vrm's `meta.metaVersion`). */
  readonly metaVersion: "0" | "1";
  /** VRM 1.0 `name`; VRM 0.x `title` (empty when the model has none). */
  readonly name: string;
  readonly version?: string;
  /** VRM 1.0 `authors`; VRM 0.x `author` (none when empty). */
  readonly authors: readonly string[];
  /** VRM 1.0 `licenseUrl` (the VRM Public License); VRM 0.x has none. */
  readonly licenseUrl?: string;
  /** VRM 0.x `licenseName` (CC0, CC_BY, Redistribution_Prohibited, Other...); VRM 1.0 has none. */
  readonly licenseName?: string;
  readonly otherLicenseUrl?: string;
  /** Who may use the model as an avatar: VRM 1.0 `avatarPermission`; VRM 0.x `allowedUserName` (OnlyAuthor,
   *  ExplicitlyLicensedPerson, Everyone). */
  readonly avatarPermission?: "onlyAuthor" | "onlySeparatelyLicensedPerson" | "everyone";
  /** VRM 1.0 `allowExcessivelyViolentUsage`; VRM 0.x `violentUssageName` Allow or Disallow. */
  readonly allowExcessivelyViolentUsage?: boolean;
  /** VRM 1.0 `allowExcessivelySexualUsage`; VRM 0.x `sexualUssageName` Allow or Disallow. */
  readonly allowExcessivelySexualUsage?: boolean;
  /** VRM 1.0 `commercialUsage` other than personalNonProfit; VRM 0.x `commercialUssageName` Allow or Disallow. */
  readonly allowCommercialUsage?: boolean;
  /** The glTF image of the thumbnail: VRM 1.0 `thumbnailImage`; the source image of VRM 0.x `texture`. */
  readonly thumbnailImage?: number;
}
export interface VrmCapabilities {
  /** "VRM1" for a VRMC_vrm model, "VRM0" for a VRM 0.x (VRM extension) model; three-vrm turns a VRM 0.x model to face +Z. */
  readonly format: "VRM0" | "VRM1";
  /** The model's own spec version: "1.0" for VRM 1.0, the VRM extension's specVersion (usually "0.0") for VRM 0.x. */
  readonly specVersion: string;
  readonly meta: VrmMetaSummary;
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

/** Checks the JSON's size, depth, numbers, strings and forbidden keys, and that every extension is one of `allowed` and
 *  declared consistently. `declaredOnly` names may also be listed in extensionsUsed; with `lenient` (VRM 0.x, whose exporters
 *  often list extensions they don't use) an allowed extension in extensionsUsed need not be used. */
function checkTree(json: JsonObject, allowed: ReadonlySet<string>, declaredOnly: ReadonlySet<string> = new Set(), lenient = false): void {
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
            requireValid(allowed.has(name), `Unsupported extension ${name}; re-export without it.`);
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
    const unused = key === "extensionsUsed";
    for (const name of optionalArray(json[key], key, 16)) {
      requireValid(typeof name === "string" && (allowed.has(name) || unused && declaredOnly.has(name)), `Unsupported ${key} entry ${String(name)}.`);
      requireValid(declared.has(name) && (present.has(name) || unused && (lenient || declaredOnly.has(name))),
        `${key} contains undeclared or absent extension ${name}.`);
    }
  }
}

/** A JPEG's width and height from its frame header (SOF0 baseline, SOF1 extended or SOF2 progressive), read before the
 *  browser decodes it. Other JPEG kinds (lossless, arithmetic, hierarchical) are rejected. */
function jpegSize(bytes: DataView): { width: number; height: number } {
  requireValid(bytes.byteLength >= 4 && bytes.getUint16(0) === 0xffd8, "Invalid JPEG signature.");
  let offset = 2;
  while (offset + 4 <= bytes.byteLength) {
    requireValid(bytes.getUint8(offset) === 0xff, "Invalid JPEG marker.");
    const marker = bytes.getUint8(offset + 1);
    // A marker may follow fill bytes (more 0xFF).
    if (marker === 0xff) { offset++; continue; }
    offset += 2;
    if (marker === 0x01 || marker >= 0xd0 && marker <= 0xd7) continue;
    requireValid(marker !== 0xd9 && marker !== 0xda, "JPEG has no frame header before its image data.");
    const length = bytes.getUint16(offset);
    requireValid(length >= 2 && offset + length <= bytes.byteLength, "Truncated JPEG segment.");
    if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) {
      requireValid(marker <= 0xc2, "Only baseline, extended or progressive JPEG textures are supported; re-export as PNG.");
      requireValid(length >= 8, "Truncated JPEG frame header.");
      return { height: integer(bytes.getUint16(offset + 3), 1, 4096, "JPEG height"), width: integer(bytes.getUint16(offset + 5), 1, 4096, "JPEG width") };
    }
    offset += length;
  }
  throw new VrmError("invalid-input", "JPEG frame header not found.");
}

/** Conservative preflight, not a complete glTF conformance validator. No GPU or URI loading. Accepts a VRM 1.0 model
 *  (VRMC_vrm) or a VRM 0.x model (the VRM extension), never both. */
export function inspectVrm(buffer: ArrayBuffer): VrmCapabilities {
  const { json, bin } = parse(buffer);
  const roots = json.extensions === undefined ? {} : object(json.extensions, "extensions");
  const v1 = Object.hasOwn(roots, "VRMC_vrm"), v0 = Object.hasOwn(roots, "VRM");
  requireValid(!(v1 && v0), "The model has both VRMC_vrm (VRM 1.0) and VRM (VRM 0.x) extensions; re-export it as one VRM version.");
  requireValid(v1 || v0, "Missing the VRMC_vrm (VRM 1.0) or VRM (VRM 0.x) extension; this is not a VRM model.");
  if (v0) checkTree(json, allowedExtensionsV0, declaredOnlyV0, true);
  else checkTree(json, allowedExtensions);
  requireValid(object(json.asset, "asset").version === "2.0", "glTF asset.version must be 2.0.");
  const extension = object(roots[v0 ? "VRM" : "VRMC_vrm"], v0 ? "VRM" : "VRMC_vrm");
  if (v0) requireValid(extension.specVersion === undefined || typeof extension.specVersion === "string" && /^0(\.\d+){0,2}$/.test(extension.specVersion),
    "Unknown VRM 0.x specVersion; expected 0.0.");
  else requireValid(extension.specVersion === "1.0",
    `VRMC_vrm specVersion ${String(extension.specVersion)} is not supported; VRMC_vrm must be VRM 1.0 (VRM 0.x uses the VRM extension).`);
  const specVersion = v0 ? String(extension.specVersion ?? "0.0") : "1.0";
  const meta = object(extension.meta, "VRM meta");
  if (!v0) {
    requireValid(typeof meta.name === "string" && meta.name.length > 0, "VRM meta.name is required.");
    requireValid(array(meta.authors, "meta.authors", 32).every(v => typeof v === "string" && v.length > 0)
      && (meta.authors as unknown[]).length > 0, "VRM authors are required.");
    requireValid(meta.licenseUrl === "https://vrm.dev/licenses/1.0/", "Expected VRM 1.0 licenseUrl; inspect usage terms before use.");
  }
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
  // Each mesh's expanded vertex and morph bytes, counted again for each node that draws it.
  const meshBytes: number[] = [];
  for (const m of meshes) {
    const before = renderBytes;
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
    meshBytes.push(renderBytes - before);
  }
  let pixels = 0;
  const imagePixels: number[] = [];
  for (const i of images) {
    const image = object(i, "image");
    requireValid(image.mimeType === "image/png" || image.mimeType === "image/jpeg",
      "Only embedded PNG or JPEG textures are supported; re-export WebP/KTX2 or other images as PNG.");
    const view = at(views, image.bufferView, "image.bufferView");
    const bytes = new DataView(bin.buffer, bin.byteOffset + Number(view.byteOffset ?? 0), Number(view.byteLength));
    let width: number, height: number;
    if (image.mimeType === "image/jpeg") ({ width, height } = jpegSize(bytes));
    else {
      requireValid(Number(view.byteLength) >= 33, "Truncated PNG.");
      requireValid(bytes.getUint32(0) === 0x89504e47 && bytes.getUint32(4) === 0x0d0a1a0a
        && bytes.getUint32(8) === 13 && bytes.getUint32(12) === 0x49484452, "Invalid PNG signature/IHDR.");
      width = integer(bytes.getUint32(16), 1, 4096, "PNG width");
      height = integer(bytes.getUint32(20), 1, 4096, "PNG height");
    }
    pixels += width * height;
    imagePixels.push(width * height);
    requireValid(pixels <= LIMITS.texturePixels, "Decoded texture pixel budget exceeded.");
  }
  for (const t of textures) at(images, object(t, "texture").source, "texture source");
  // Each glTF texture (an image with its sampler) becomes its own GPU texture at most for each use of it (color or data);
  // the materials that sample it, also through a texture transform, share it, as three.js uploads an image once for each
  // set of texture settings. So the textures' pixels are bounded, not the materials times the textures.
  const sampledPixels = textures.reduce<number>((sum, t) => sum + imagePixels[Number(object(t, "texture").source)]!, 0);
  requireValid(sampledPixels <= 2 * LIMITS.texturePixels,
    "Texture instance pixel budget exceeded: the textures sample more than 32Mi pixels; reduce texture resolution or count.");
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
  let instancedBytes = 0;
  nodes.forEach((n, index) => {
    const node = object(n, "node");
    if (node.mesh !== undefined) { at(meshes, node.mesh, "node.mesh"); instancedBytes += meshBytes[Number(node.mesh)]!; }
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
  requireValid(instancedBytes <= LIMITS.decodedGeometryBytes, "Instanced geometry budget exceeded.");
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
  const bones = v0 ? vrm0Bones(object(extension.humanoid, "humanoid"))
    : object(object(extension.humanoid, "humanoid").humanBones, "humanBones");
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
  const diagnostics: string[] = [];
  const expressionRoot = v0 || extension.expressions === undefined ? {} : object(extension.expressions, "expressions");
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
  const context: Vrm0Context = { nodes, meshes, materials, textures, reachable, assigned, diagnostics };
  if (v0) expressions.push(...vrm0Expressions(extension, context));
  let gaze: VrmCapabilities["gaze"] = "absent";
  if (v0) gaze = vrm0Gaze(extension, bones, expressions, diagnostics, context);
  else if (extension.lookAt !== undefined) {
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
  const spring = v0 ? undefined : roots.VRMC_springBone;
  let secondaryMotion = v0 && vrm0Springs(extension, context);
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
  if (!secondaryMotion) diagnostics.push(v0 ? "Secondary motion unavailable: author a VRM 0.x secondaryAnimation bone group whose root has a child."
    : "Secondary motion unavailable: author a supported VRMC_springBone chain.");
  if (v0) {
    vrm0Materials(extension, context);
    diagnostics.push("VRM 0.x model: three-vrm migrates it to VRM 1.0 expressions and bones on load and turns it 180 degrees to face +Z.");
  }
  const usable = expressions.filter(e => e.usable);
  if (!usable.some(e => mouthPresets.includes(e.name as typeof mouthPresets[number])))
    diagnostics.push("No usable vowel presets: supply an explicit mapping to authored facial expressions, choose another supported source, or omit mouth.");
  diagnostics.push("ARKit detail is never inferred from names or vowel presets; author and review each explicit coefficient mapping.");
  diagnostics.push("Body/VRMA playback is not implemented in this slice.");
  return Object.freeze({
    format: v0 ? "VRM0" : "VRM1", specVersion, meta: v0 ? vrm0Meta(meta, textures, diagnostics) : vrm1Meta(meta, images),
    expressions: Object.freeze(expressions), humanoidBones: Object.freeze(Object.keys(bones)), gaze,
    secondaryMotion, bodyPlayback: false, facialDetail: usable.some(e => !e.preset) ? "explicit-mapping-required" : usable.length ? "preset-only" : "absent",
    diagnostics: Object.freeze(diagnostics),
  });
}

/** What the VRM 0.x checks need of the glTF already checked. */
interface Vrm0Context {
  readonly nodes: readonly unknown[];
  readonly meshes: readonly unknown[];
  readonly materials: readonly unknown[];
  readonly textures: readonly unknown[];
  readonly reachable: ReadonlySet<number>;
  /** The humanoid bones' nodes. */
  readonly assigned: ReadonlySet<number>;
  readonly diagnostics: string[];
}

function text(value: unknown, label: string): string | undefined {
  if (value === undefined) return undefined;
  requireValid(typeof value === "string", `${label} must be text.`);
  return value;
}

/** A VRM 0.x {x, y, z} object, each finite within `max`. */
function xyz(value: unknown, label: string, max: number): void {
  const v = object(value, label);
  for (const key of ["x", "y", "z"]) if (v[key] !== undefined) finite(v[key], -max, max, `${label}.${key}`);
}

/** Text, or undefined for anything else: optional meta fields the importer doesn't read never reject a model. */
const str = (value: unknown): string | undefined => typeof value === "string" ? value : undefined;

function vrm1Meta(meta: JsonObject, images: readonly unknown[]): VrmMetaSummary {
  const permission = str(meta.avatarPermission) ?? "onlyAuthor";
  const commercial = str(meta.commercialUsage) ?? "personalNonProfit";
  const thumbnail = meta.thumbnailImage;
  const version = str(meta.version), other = str(meta.otherLicenseUrl);
  return Object.freeze({
    metaVersion: "1", name: meta.name as string, authors: Object.freeze([...meta.authors as string[]]), licenseUrl: meta.licenseUrl as string,
    ...(version !== undefined ? { version } : {}), ...(other ? { otherLicenseUrl: other } : {}),
    ...(permission === "onlyAuthor" || permission === "onlySeparatelyLicensedPerson" || permission === "everyone" ? { avatarPermission: permission } : {}),
    allowExcessivelyViolentUsage: meta.allowExcessivelyViolentUsage === true,
    allowExcessivelySexualUsage: meta.allowExcessivelySexualUsage === true,
    allowCommercialUsage: commercial !== "personalNonProfit",
    ...(Number.isSafeInteger(thumbnail) && Number(thumbnail) >= 0 && Number(thumbnail) < images.length ? { thumbnailImage: Number(thumbnail) } : {}),
  });
}

/** VRM 0.x meta in the shape of VRM 1.0's (see VrmMetaSummary). Every field is optional in VRM 0.x. */
function vrm0Meta(meta: JsonObject, textures: readonly unknown[], diagnostics: string[]): VrmMetaSummary {
  const name = str(meta.title) ?? "", author = str(meta.author);
  if (!name) diagnostics.push("VRM 0.x meta.title is empty.");
  const usage = (spelled: unknown, misspelled: unknown): boolean | undefined => {
    const value = (str(misspelled) ?? str(spelled))?.toLowerCase();
    return value === "allow" ? true : value === "disallow" ? false : undefined;
  };
  const user = str(meta.allowedUserName);
  const permission = user === "OnlyAuthor" ? "onlyAuthor" : user === "ExplicitlyLicensedPerson" ? "onlySeparatelyLicensedPerson"
    : user === "Everyone" ? "everyone" : undefined;
  const texture = meta.texture;
  const thumbnail = Number.isSafeInteger(texture) && Number(texture) >= 0 && Number(texture) < textures.length
    ? object(textures[Number(texture)], "thumbnail texture").source : undefined;
  const version = str(meta.version), license = str(meta.licenseName), other = str(meta.otherLicenseUrl);
  const violent = usage(meta.violentUsageName, meta.violentUssageName);
  const sexual = usage(meta.sexualUsageName, meta.sexualUssageName);
  const commercial = usage(meta.commercialUsageName, meta.commercialUssageName);
  return Object.freeze({
    metaVersion: "0", name, authors: Object.freeze(author ? [author] : []),
    ...(version ? { version } : {}), ...(license ? { licenseName: license } : {}), ...(other ? { otherLicenseUrl: other } : {}),
    ...(permission ? { avatarPermission: permission } : {}),
    ...(violent !== undefined ? { allowExcessivelyViolentUsage: violent } : {}),
    ...(sexual !== undefined ? { allowExcessivelySexualUsage: sexual } : {}),
    ...(commercial !== undefined ? { allowCommercialUsage: commercial } : {}),
    ...(typeof thumbnail === "number" ? { thumbnailImage: thumbnail } : {}),
  });
}

/** VRM 0.x `humanoid.humanBones` (a list of {bone, node}) as VRM 1.0's map of roles to {node}, with the thumbs renamed as
 *  three-vrm renames them (thumbProximal is VRM 1.0's thumbMetacarpal, thumbIntermediate its thumbProximal). */
function vrm0Bones(humanoid: JsonObject): JsonObject {
  const bones: JsonObject = {};
  for (const entry of array(humanoid.humanBones, "humanoid.humanBones", 128)) {
    const bone = object(entry, "humanBone");
    requireValid(typeof bone.bone === "string", "VRM 0.x humanBone.bone must be a bone name.");
    const name = Object.hasOwn(vrm0Thumbs, bone.bone) ? vrm0Thumbs[bone.bone]! : bone.bone;
    requireValid(Object.values(VRMHumanBoneName).includes(name as VRMHumanBoneName), `Unknown humanoid role ${bone.bone}.`);
    requireValid(!Object.hasOwn(bones, name), `Humanoid bone ${bone.bone} is listed twice.`);
    bones[name] = { node: bone.node };
  }
  return bones;
}

/** VRM 0.x blend shape groups as expressions under the VRM 1.0 names three-vrm gives them (see VRM0_PRESET_NAMES); an
 *  "unknown" preset keeps its own name. Like three-vrm, a group whose name is already taken is skipped, and so is one with
 *  no name. A bind (a mesh's morph target, weight 0 to 100) drives that morph on every node that draws the mesh. */
function vrm0Expressions(extension: JsonObject, { nodes, meshes, materials, reachable, diagnostics }: Vrm0Context): ExpressionCapability[] {
  if (extension.blendShapeMaster === undefined) return [];
  const master = object(extension.blendShapeMaster, "blendShapeMaster");
  const expressions: ExpressionCapability[] = [];
  for (const g of optionalArray(master.blendShapeGroups, "blendShapeGroups", 256)) {
    const group = object(g, "blendShapeGroup");
    const preset = text(group.presetName, "blendShapeGroup.presetName"), own = text(group.name, "blendShapeGroup.name");
    requireValid(preset === undefined || !inherited(preset), `Invalid VRM 0.x presetName ${String(preset)}.`);
    const mapped = preset !== undefined && Object.hasOwn(VRM0_PRESET_NAMES, preset) ? VRM0_PRESET_NAMES[preset]! : undefined;
    const name = mapped ?? own;
    if (!name) { diagnostics.push("A VRM 0.x blend shape group without a name is ignored."); continue; }
    requireValid(name.length <= 128 && !inherited(name), `Invalid VRM 0.x blend shape name ${name.slice(0, 64)}.`);
    if (expressions.some(e => e.name === name)) { diagnostics.push(`A repeated VRM 0.x blend shape ${name} is ignored, as three-vrm ignores it.`); continue; }
    const targets: string[] = [];
    const authoredTargets = new Set<string>();
    for (const b of optionalArray(group.binds, "blendShape binds", 128)) {
      const bind = object(b, "blendShape bind");
      const meshIndex = integer(bind.mesh, 0, meshes.length - 1, "blendShape bind mesh");
      for (const p of array(object(meshes[meshIndex], "morph mesh").primitives, "primitives")) {
        const primitive = object(p, "primitive");
        integer(bind.index, 0, optionalArray(primitive.targets, "targets").length - 1, "morph target index");
      }
      const weight = finite(bind.weight ?? 100, 0, 100, "blendShape bind weight");
      nodes.forEach((n, node) => {
        if (object(n, "node").mesh !== meshIndex || !reachable.has(node)) return;
        const target = `${node}:${Number(bind.index)}`;
        requireValid(!authoredTargets.has(target), `Duplicate morph target ${target} within expression ${name}; merge or remove the duplicate binding.`);
        authoredTargets.add(target);
        if (weight > 0) targets.push(target);
      });
    }
    const materialTargets: string[] = [];
    for (const v of optionalArray(group.materialValues, "materialValues", 32)) {
      const value = object(v, "materialValue");
      const material = text(value.materialName, "materialValue.materialName"), property = text(value.propertyName, "materialValue.propertyName");
      requireValid(material !== undefined && property !== undefined, "A VRM 0.x materialValue needs a materialName and propertyName.");
      vector(value.targetValue, 4, "materialValue targetValue", 100);
      const type = property === "_MainTex_ST" ? "texture-transform" : Object.hasOwn(vrm0MaterialColors, property) ? vrm0MaterialColors[property] : undefined;
      if (!type) { diagnostics.push(`The VRM 0.x material property ${property.slice(0, 64)} in ${name} is ignored, as three-vrm ignores it.`); continue; }
      materials.forEach((m, index) => {
        const named = object(m, "material").name;
        if (named === material || named === `${material} (Outline)`) materialTargets.push(`${index}:${type}`);
      });
    }
    requireValid(group.isBinary === undefined || typeof group.isBinary === "boolean", "Invalid isBinary.");
    expressions.push(Object.freeze({
      name, preset: mapped !== undefined, usable: targets.length > 0, morphTargets: Object.freeze(targets),
      materialTargets: Object.freeze(materialTargets), overrideMouth: "none", overrideBlink: "none", overrideLookAt: "none",
    }));
    requireValid(expressions.length <= LIMITS.expressions, "Expression budget exceeded.");
  }
  return expressions;
}

/** VRM 0.x look-at (`firstPerson`): eye bones ("Bone") or the four look expressions ("BlendShape"). */
function vrm0Gaze(extension: JsonObject, bones: JsonObject, expressions: readonly ExpressionCapability[], diagnostics: string[],
  { nodes, meshes }: Vrm0Context): VrmCapabilities["gaze"] {
  if (extension.firstPerson === undefined) {
    diagnostics.push("Gaze unavailable: author VRM 0.x firstPerson lookAt with usable eye controls.");
    return "absent";
  }
  const person = object(extension.firstPerson, "firstPerson");
  if (person.firstPersonBone !== undefined) integer(person.firstPersonBone, -1, nodes.length - 1, "firstPersonBone");
  if (person.firstPersonBoneOffset !== undefined) xyz(person.firstPersonBoneOffset, "firstPersonBoneOffset", 10);
  for (const a of optionalArray(person.meshAnnotations, "meshAnnotations", 128)) {
    const annotation = object(a, "meshAnnotation");
    if (annotation.mesh !== undefined) integer(annotation.mesh, -1, meshes.length - 1, "meshAnnotation.mesh");
    text(annotation.firstPersonFlag, "meshAnnotation.firstPersonFlag");
  }
  const type = text(person.lookAtTypeName, "lookAtTypeName") ?? "Bone";
  requireValid(type === "Bone" || type === "BlendShape", "Unknown VRM 0.x lookAtTypeName; expected Bone or BlendShape.");
  for (const key of ["lookAtHorizontalInner", "lookAtHorizontalOuter", "lookAtVerticalDown", "lookAtVerticalUp"]) {
    if (person[key] === undefined) continue;
    const map = object(person[key], key);
    optionalArray(map.curve, `${key}.curve`, 64).forEach(v => finite(v, -1000, 1000, `${key}.curve`));
    if (map.xRange !== undefined) finite(map.xRange, 0, 180, `${key}.xRange`);
    if (map.yRange !== undefined) finite(map.yRange, 0, 180, `${key}.yRange`);
  }
  if (type === "Bone" ? bones.leftEye && bones.rightEye : gazePresets.every(p => expressions.some(e => e.name === p && e.usable)))
    return type === "Bone" ? "bone" : "expression";
  diagnostics.push("Gaze unavailable: author both eye bones or all four look blend shapes and re-export firstPerson lookAt.");
  return "absent";
}

/** VRM 0.x spring bones (`secondaryAnimation`). Each bone group lists root nodes; the importer makes a joint of every node
 *  under a root (the root too), whose child is the node's first child. So each chain runs from a root, or from a node that is
 *  not its parent's first child, along first children to a tip. Joints must be unique, in the scene and apart from the
 *  humanoid bones. True when some joint has a child (a chain that can swing). */
function vrm0Springs(extension: JsonObject, { nodes, reachable, assigned }: Vrm0Context): boolean {
  if (extension.secondaryAnimation === undefined) return false;
  const secondary = object(extension.secondaryAnimation, "secondaryAnimation");
  const colliderGroups = optionalArray(secondary.colliderGroups, "colliderGroups", 128);
  for (const g of colliderGroups) {
    const group = object(g, "colliderGroup");
    at(nodes, group.node, "colliderGroup node");
    for (const c of optionalArray(group.colliders, "colliders", 128)) {
      const collider = object(c, "collider");
      if (collider.offset !== undefined) xyz(collider.offset, "collider offset", 10);
      finite(collider.radius ?? 0, 0, 10, "collider radius");
    }
  }
  const used = new Set<number>();
  let swings = false;
  const visit = (id: number): void => {
    requireValid(reachable.has(id) && !used.has(id) && !assigned.has(id),
      "VRM 0.x spring bones (every node under a boneGroup root) must be unique, reachable, and separate from humanoid controls.");
    used.add(id);
    const children = optionalArray(object(nodes[id], "node").children, "children");
    if (children.length) swings = true;
    for (const child of children) visit(Number(child));
  };
  for (const entry of optionalArray(secondary.boneGroups, "boneGroups", 128)) {
    const group = object(entry, "boneGroup");
    text(group.comment, "boneGroup.comment");
    finite(group.stiffiness ?? 1, 0, 100, "stiffiness"); finite(group.gravityPower ?? 0, 0, 100, "gravityPower");
    finite(group.dragForce ?? 0.5, 0, 1, "dragForce"); finite(group.hitRadius ?? 0, 0, 10, "hitRadius");
    if (group.gravityDir !== undefined) xyz(group.gravityDir, "gravityDir", 1);
    if (group.center !== undefined) integer(group.center, -1, nodes.length - 1, "boneGroup center");
    for (const c of optionalArray(group.colliderGroups, "boneGroup colliderGroups", 128)) at(colliderGroups, c, "boneGroup colliderGroup");
    for (const root of optionalArray(group.bones, "boneGroup bones", 128)) visit(integer(root, 0, nodes.length - 1, "boneGroup bone"));
  }
  return swings;
}

/** VRM 0.x MToon material properties (`materialProperties`), which three-vrm turns into MToon materials: bounded values and
 *  texture indices that exist. */
function vrm0Materials(extension: JsonObject, { textures }: Vrm0Context): void {
  const entries = (value: unknown, label: string): [string, unknown][] => {
    if (value === undefined) return [];
    const map = Object.entries(object(value, label));
    requireValid(map.length <= 128, `${label} has too many entries.`);
    return map;
  };
  for (const p of optionalArray(extension.materialProperties, "materialProperties", 128)) {
    const properties = object(p, "materialProperties");
    text(properties.name, "materialProperties.name"); text(properties.shader, "materialProperties.shader");
    if (properties.renderQueue !== undefined) integer(properties.renderQueue, -1, 10000, "materialProperties.renderQueue");
    for (const [key, value] of entries(properties.floatProperties, "floatProperties")) finite(value, -1e6, 1e6, `floatProperties.${key}`);
    for (const [key, value] of entries(properties.vectorProperties, "vectorProperties"))
      array(value, `vectorProperties.${key}`, 4).forEach(v => finite(v, -1e6, 1e6, `vectorProperties.${key}`));
    for (const [key, value] of entries(properties.textureProperties, "textureProperties")) at(textures, value, `textureProperties.${key}`);
    for (const [key, value] of entries(properties.keywordMap, "keywordMap"))
      requireValid(typeof value === "boolean", `keywordMap.${key} must be true or false.`);
    for (const [key, value] of entries(properties.tagMap, "tagMap")) text(value, `tagMap.${key}`);
  }
}
