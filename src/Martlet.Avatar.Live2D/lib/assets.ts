import { boundedInteger, Diagnostic, Live2DError, requireCondition } from "./diagnostics.js";

export const LIMITS = Object.freeze({
  files: 128,
  totalBytes: 128 * 1024 * 1024,
  fileBytes: 64 * 1024 * 1024,
  jsonBytes: 1024 * 1024,
  textures: 16,
  /** Largest PNG accepted from the model folder, per side and in total. */
  sourceTextureDimension: 8192,
  sourceTexturePixels: 256 * 1024 * 1024,
  /** GPU upload budget; larger source textures are halved until they fit. */
  textureDimension: 4096,
  texturePixels: 32 * 1024 * 1024,
  parameters: 1024,
  drawables: 2048,
  vertices: 250_000,
  indices: 750_000,
  canvasDimension: 2048,
});

export interface MotionReference {
  readonly file: string;
  readonly fadeIn?: number;
  readonly fadeOut?: number;
}

export interface ModelDescription {
  readonly modelFile: string;
  readonly moc: string;
  readonly textures: readonly string[];
  /** Every texture is uploaded at 1/textureDivisor of its PNG size (1, 2 or 4); UVs are normalized, so art is unchanged. */
  readonly textureDivisor: number;
  readonly groups: Readonly<{ lipSync: readonly string[]; eyeBlink: readonly string[] }>;
  readonly motions: Readonly<Record<string, readonly MotionReference[]>>;
  readonly expressions: readonly { readonly name: string; readonly file: string }[];
  readonly physics?: string;
  readonly pose?: string;
  readonly diagnostics: readonly Diagnostic[];
}

// Letters and digits of any script plus a few punctuation marks; never separators, escapes, URL syntax or traversal.
// Mirrors LocalAvatarFiles.IsSafeModelName in Martlet.Avatar.Hosting.
const SEGMENT = /^[\p{L}\p{N}_(\[][\p{L}\p{N}\p{M}_. ()\[\]+&',!~@=-]*$/u;

export function localPath(value: unknown): string {
  requireCondition(typeof value === "string" && value.length > 0 && value.length <= 240,
    "UNSAFE_PATH", "A bounded relative bundle path is required.");
  requireCondition(value.split("/").every(part =>
    SEGMENT.test(part) && !part.endsWith(".") && !part.endsWith(" ")),
  "UNSAFE_PATH", `Unsafe bundle path: ${value}. No URLs, traversal, escapes or absolute paths.`);
  return value;
}

/** Upload size of a texture at the bundle's divisor. */
export function scaledSize(size: { width: number; height: number }, divisor: number): { width: number; height: number } {
  return { width: Math.max(1, Math.round(size.width / divisor)), height: Math.max(1, Math.round(size.height / divisor)) };
}

function object(value: unknown, label: string): Record<string, unknown> {
  requireCondition(value !== null && typeof value === "object" && !Array.isArray(value),
    "INVALID_MODEL_JSON", `${label} must be an object.`);
  return value as Record<string, unknown>;
}

function keys(value: Record<string, unknown>, allowed: readonly string[], label: string): void {
  requireCondition(Object.keys(value).every(key => allowed.includes(key)),
    "UNSUPPORTED_METADATA", `${label} contains an unsupported field (plugins are not supported).`);
}

function array(value: unknown, limit: number, label: string): unknown[] {
  requireCondition(Array.isArray(value) && value.length <= limit,
    "INVALID_MODEL_JSON", `${label} must be an array with at most ${limit} entries.`);
  return value;
}

function identifier(value: unknown): string {
  requireCondition(typeof value === "string" && value.length > 0 && value.length <= 256 &&
    !/[\u0000-\u001f]/.test(value), "INVALID_MODEL_JSON", "Invalid parameter identifier.");
  return value;
}

export function pngDimensions(bytes: Uint8Array): { width: number; height: number } {
  const signature = [137, 80, 78, 71, 13, 10, 26, 10];
  requireCondition(bytes.length >= 33 && signature.every((v, i) => bytes[i] === v) &&
    bytes[12] === 73 && bytes[13] === 72 && bytes[14] === 68 && bytes[15] === 82,
  "INVALID_TEXTURE", "Only PNG textures with an IHDR header are supported.");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  requireCondition(view.getUint32(8) === 13, "INVALID_TEXTURE", "Invalid PNG IHDR length.");
  const width = view.getUint32(16);
  const height = view.getUint32(20);
  requireCondition(width > 0 && height > 0 && width <= LIMITS.sourceTextureDimension &&
    height <= LIMITS.sourceTextureDimension, "RESOURCE_LIMIT",
  `PNG textures can be at most ${LIMITS.sourceTextureDimension} pixels per side.`);
  return { width, height };
}

/** Expressions and motions a model keeps outside its model3.json (VTube Studio's), named by the host, relative to the
 *  model3.json like its own references. */
export interface ModelExtras {
  readonly expressions?: readonly { readonly name: string; readonly file: string }[];
  readonly motions?: readonly { readonly group: string; readonly file: string }[];
}

/** Snapshot of user-selected bytes. Never resolves paths against a filesystem or a URL. */
export class LocalModelBundle {
  readonly description: ModelDescription;
  readonly #files = new Map<string, Uint8Array>();

  constructor(input: ReadonlyMap<string, Uint8Array>, modelFile: string, extras?: ModelExtras | null) {
    boundedInteger(input.size, LIMITS.files, "bundle file count");
    let total = 0;
    const folded = new Set<string>();
    for (const [name, bytes] of input) {
      localPath(name);
      requireCondition(/\.(json|moc3|png|wav)$/i.test(name), "UNSUPPORTED_ASSET",
        `Unsupported asset ${name}. Scripts, plugins and archives are not accepted.`);
      requireCondition(bytes instanceof Uint8Array, "INVALID_ASSET", `${name} must contain bytes.`);
      boundedInteger(bytes.byteLength, LIMITS.fileBytes, `${name} bytes`);
      if (/\.json$/i.test(name)) boundedInteger(bytes.byteLength, LIMITS.jsonBytes, `${name} JSON bytes`);
      total += bytes.byteLength;
      boundedInteger(total, LIMITS.totalBytes, "bundle bytes");
      requireCondition(!folded.has(name.toLowerCase()), "DUPLICATE_ASSET", `Case-colliding asset: ${name}.`);
      folded.add(name.toLowerCase());
      this.#files.set(name, bytes.slice());
    }
    localPath(modelFile);
    requireCondition(modelFile.endsWith(".model3.json"), "INVALID_MODEL_JSON", "Select a .model3.json file.");
    let parsed: unknown;
    try {
      parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(this.read(modelFile)));
    } catch (error) {
      if (error instanceof Live2DError) throw error;
      throw new Live2DError("INVALID_MODEL_JSON", `Model JSON is invalid UTF-8 or JSON: ${String(error)}`);
    }
    const root = object(parsed, "model");
    keys(root, ["Version", "FileReferences", "Groups", "HitAreas", "Layout"], "model");
    requireCondition(root.Version === 3, "UNSUPPORTED_MODEL_VERSION", "Only model3 Version 3 is supported.");
    const refs = object(root.FileReferences, "FileReferences");
    keys(refs, ["Moc", "Textures", "Physics", "Pose", "UserData", "DisplayInfo", "Expressions", "Motions"],
      "FileReferences");
    const base = modelFile.slice(0, modelFile.lastIndexOf("/") + 1);
    const resolve = (value: unknown, extension: string): string => {
      const name = localPath(base + localPath(value));
      requireCondition(name.toLowerCase().endsWith(extension), "UNSUPPORTED_ASSET",
        `Expected ${extension} asset: ${name}.`);
      requireCondition(this.#files.has(name), "MISSING_ASSET", `Bundle is missing ${name}.`);
      return name;
    };
    const moc = resolve(refs.Moc, ".moc3");
    const textures = array(refs.Textures, LIMITS.textures, "Textures").map(v => resolve(v, ".png"));
    requireCondition(textures.length > 0 && new Set(textures).size === textures.length,
      "INVALID_TEXTURE", "At least one unique PNG texture is required; duplicate slots are unsupported.");
    let pixels = 0;
    const sizes = textures.map(name => {
      const size = pngDimensions(this.#files.get(name)!);
      pixels += size.width * size.height;
      boundedInteger(pixels, LIMITS.sourceTexturePixels, "total texture pixels");
      return size;
    });
    // Halving keeps power-of-two atlases power-of-two (mipmapped) and shows the same art with fewer GPU pixels.
    let textureDivisor = 1;
    const fits = (divisor: number) => {
      const scaled = sizes.map(size => scaledSize(size, divisor));
      return scaled.every(size => size.width <= LIMITS.textureDimension && size.height <= LIMITS.textureDimension) &&
        scaled.reduce((sum, size) => sum + size.width * size.height, 0) <= LIMITS.texturePixels;
    };
    while (!fits(textureDivisor)) textureDivisor *= 2;
    const diagnostics: Diagnostic[] = [];
    if (textureDivisor > 1) diagnostics.push({ code: "TEXTURES_DOWNSCALED",
      message: `Textures are shown at 1/${textureDivisor} size to fit the ${LIMITS.textureDimension}-pixel GPU budget.` });
    const optional: Record<string, string> = {};
    for (const field of ["Physics", "Pose", "UserData", "DisplayInfo"]) {
      if (refs[field] !== undefined) {
        optional[field] = resolve(refs[field], ".json");
        if (field === "UserData" || field === "DisplayInfo")
          diagnostics.push({ code: "INACTIVE_METADATA", message: `${field} is declared but not executed.` });
      }
    }
    const expressions: { name: string; file: string }[] = [];
    if (refs.Expressions !== undefined) {
      for (const value of array(refs.Expressions, 128, "Expressions")) {
        const entry = object(value, "expression");
        keys(entry, ["Name", "File"], "expression");
        expressions.push({ name: identifier(entry.Name), file: resolve(entry.File, ".exp3.json") });
      }
    }
    const motionGroups: Record<string, readonly MotionReference[]> = {};
    if (refs.Motions !== undefined) {
      const motions = object(refs.Motions, "Motions");
      boundedInteger(Object.keys(motions).length, 32, "motion groups");
      let count = 0;
      for (const [group, values] of Object.entries(motions)) {
        const entries: MotionReference[] = [];
        for (const value of array(values, 128, "motions")) {
          boundedInteger(++count, 128, "motions");
          const entry = object(value, "motion");
          keys(entry, ["File", "Sound", "FadeInTime", "FadeOutTime"], "motion");
          const file = resolve(entry.File, ".motion3.json");
          if (entry.Sound !== undefined) resolve(entry.Sound, ".wav");
          for (const key of ["FadeInTime", "FadeOutTime"]) {
            if (entry[key] !== undefined) requireCondition(typeof entry[key] === "number" &&
              Number.isFinite(entry[key]) && entry[key] >= 0 && entry[key] <= 60,
            "INVALID_MODEL_JSON", `${key} must be in 0..60 seconds.`);
          }
          entries.push(Object.freeze({
            file,
            ...(typeof entry.FadeInTime === "number" ? { fadeIn: entry.FadeInTime } : {}),
            ...(typeof entry.FadeOutTime === "number" ? { fadeOut: entry.FadeOutTime } : {}),
          }));
        }
        requireCondition(group.length <= 256, "INVALID_MODEL_JSON", "Motion group name is too long.");
        motionGroups[group] = Object.freeze(entries);
      }
      diagnostics.push({ code: "MOTION_AUDIO_IGNORED", message: "Motion sounds are never played; Martlet's voice drives lip-sync." });
    }
    if (extras) {
      const declared = new Set(expressions.map(e => e.name));
      for (const value of array(extras.expressions ?? [], 128, "extra expressions")) {
        const entry = object(value, "extra expression");
        const name = identifier(entry.name);
        if (declared.has(name)) continue;
        declared.add(name);
        expressions.push({ name, file: resolve(entry.file, ".exp3.json") });
      }
      requireCondition(expressions.length <= 128, "RESOURCE_LIMIT", "At most 128 expressions are supported.");
      let count = Object.values(motionGroups).reduce((sum, entries) => sum + entries.length, 0);
      for (const value of array(extras.motions ?? [], 64, "extra motions")) {
        const entry = object(value, "extra motion");
        const group = identifier(entry.group);
        boundedInteger(++count, 192, "motions");
        motionGroups[group] = Object.freeze([...(motionGroups[group] ?? []), Object.freeze({ file: resolve(entry.file, ".motion3.json") })]);
      }
      boundedInteger(Object.keys(motionGroups).length, 96, "motion groups");
    }
    const groups = { lipSync: [] as string[], eyeBlink: [] as string[] };
    const seen = new Set<string>();
    for (const value of array(root.Groups ?? [], 32, "Groups")) {
      const group = object(value, "group");
      keys(group, ["Target", "Name", "Ids"], "group");
      requireCondition(group.Target === "Parameter", "UNSUPPORTED_METADATA", "Only Parameter groups are supported.");
      const name = identifier(group.Name);
      requireCondition(!seen.has(name), "INVALID_MODEL_JSON", `Duplicate group ${name}.`);
      seen.add(name);
      const ids = array(group.Ids, LIMITS.parameters, "group Ids").map(identifier);
      requireCondition(new Set(ids).size === ids.length, "INVALID_MODEL_JSON", `Duplicate IDs in ${name}.`);
      if (name === "LipSync") groups.lipSync = ids;
      else if (name === "EyeBlink") groups.eyeBlink = ids;
      else diagnostics.push({ code: "UNMAPPED_GROUP", message: `Unrecognized parameter group: ${name}.` });
    }
    if (root.Layout !== undefined) {
      const layout = object(root.Layout, "Layout");
      keys(layout, ["Width", "Height", "X", "Y", "CenterX", "CenterY", "Top", "Bottom", "Left", "Right"], "Layout");
      requireCondition(Object.values(layout).every(v => typeof v === "number" && Number.isFinite(v)),
        "INVALID_MODEL_JSON", "Layout values must be finite.");
      diagnostics.push({ code: "INACTIVE_LAYOUT", message: "Uses canvas-fit framing; authored Layout is not applied." });
    }
    if (root.HitAreas !== undefined) {
      for (const value of array(root.HitAreas, 128, "HitAreas")) {
        const hit = object(value, "hit area");
        keys(hit, ["Id", "Name"], "hit area");
        identifier(hit.Id);
        identifier(hit.Name);
      }
      diagnostics.push({ code: "INACTIVE_HIT_AREAS", message: "No hit-test actions or scripts are executed." });
    }
    this.description = Object.freeze({
      modelFile, moc, textures: Object.freeze(textures), textureDivisor,
      groups: Object.freeze({ lipSync: Object.freeze(groups.lipSync), eyeBlink: Object.freeze(groups.eyeBlink) }),
      motions: Object.freeze(motionGroups),
      expressions: Object.freeze(expressions.map(e => Object.freeze(e))),
      ...(optional.Physics ? { physics: optional.Physics } : {}),
      ...(optional.Pose ? { pose: optional.Pose } : {}),
      diagnostics: Object.freeze(diagnostics.map(d => Object.freeze(d))),
    });
  }

  read(name: string): Uint8Array<ArrayBuffer> {
    const bytes = this.#files.get(name);
    requireCondition(bytes, "MISSING_ASSET", `Bundle is missing ${name}.`);
    return bytes.slice();
  }
}
