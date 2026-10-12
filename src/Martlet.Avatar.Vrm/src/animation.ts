import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import type { VRM, VRMHumanBoneName } from "@pixiv/three-vrm";
import { createVRMAnimationHumanoidTracks, VRMAnimation, VRMAnimationLoaderPlugin } from "@pixiv/three-vrm-animation";
import { finite, object, requireValid, VrmError } from "./inspect.js";

/** Bounds of a VRM Animation clip (a .vrma file: binary glTF with the VRMC_vrm_animation extension). */
export const VRMA_LIMITS = Object.freeze({
  /** A clip's size limit: about two minutes of a full body at 30 frames a second. Martlet's hosting layer uses the same. */
  bytes: 4 * 1024 * 1024,
  jsonBytes: 1024 * 1024,
  /** The longest clip, in seconds. */
  seconds: 120,
  nodes: 1024,
  accessors: 4096,
  /** The clips kept for one model, each by its name; the oldest goes first. */
  clips: 16,
  /** The longest name of a kept clip (room for a prefix and a SHA-256 in hex). */
  name: 128,
});

/** A kept clip on the loaded model: its name, its length in seconds, the humanoid bones it turns on this model, whether it
 *  moves the hips and whether it keeps them in place across the ground (see VrmClipPlayer.keep). */
export interface VrmAnimationInfo {
  readonly name: string;
  readonly duration: number;
  readonly bones: readonly string[];
  readonly hips: boolean;
  readonly inPlace: boolean;
}

/** The clip that plays now (see VrmRuntime.animationReading): its name, how far into it (seconds), its length and how much
 *  of the body it has now (0 to 1: it blends in, holds, then blends out back to the idle pose). */
export interface VrmAnimationReading {
  readonly name: string;
  readonly time: number;
  readonly duration: number;
  readonly weight: number;
}

/** A clip name Martlet keeps: 1 to 128 characters, no control characters and no spaces at its ends. */
export function animationName(value: unknown): string {
  requireValid(typeof value === "string" && value.length >= 1 && value.length <= VRMA_LIMITS.name && value.trim() === value &&
    !/[\u0000-\u001f\u007f]/.test(value), `A clip name is 1 to ${VRMA_LIMITS.name} characters without control characters.`);
  return value;
}

/** Checks a clip's binary glTF before the loader reads it: its size, header and JSON, the VRMC_vrm_animation extension, and
 *  that all its data is inside the file (no URLs, no pictures, no cubic-spline tracks). */
function preflight(buffer: ArrayBuffer): void {
  requireValid(buffer instanceof ArrayBuffer && buffer.byteLength >= 20 && buffer.byteLength <= VRMA_LIMITS.bytes,
    `A VRMA clip is 20 bytes to ${VRMA_LIMITS.bytes / 1024 / 1024} MB.`);
  const view = new DataView(buffer);
  requireValid(view.getUint32(0, true) === 0x46546c67 && view.getUint32(4, true) === 2 && view.getUint32(8, true) === buffer.byteLength,
    "A VRMA clip is a binary glTF 2.0 file.");
  const length = view.getUint32(12, true);
  requireValid(view.getUint32(16, true) === 0x4e4f534a && length <= VRMA_LIMITS.jsonBytes && 20 + length <= buffer.byteLength,
    "The clip's glTF JSON is missing or too large.");
  let json: unknown;
  try { json = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(new Uint8Array(buffer, 20, length))); }
  catch { throw new VrmError("invalid-input", "The clip's glTF JSON can't be read."); }
  const document = object(json, "The clip's glTF JSON");
  const list = (value: unknown, label: string, max: number): unknown[] => {
    if (value === undefined) return [];
    requireValid(Array.isArray(value) && value.length <= max, `The clip has too many ${label}.`);
    return value;
  };
  requireValid(list(document.extensionsUsed, "extensions", 64).includes("VRMC_vrm_animation"),
    "This glTF file is not a VRM Animation: it has no VRMC_vrm_animation extension.");
  for (const required of list(document.extensionsRequired, "extensions", 64))
    requireValid(required === "VRMC_vrm_animation", `The clip requires an extension Martlet can't read: ${String(required).slice(0, 64)}.`);
  const extension = object(object(document.extensions, "The clip's extensions").VRMC_vrm_animation, "VRMC_vrm_animation");
  object(object(extension.humanoid, "The clip's humanoid").humanBones, "The clip's humanoid bones");
  list(document.nodes, "nodes", VRMA_LIMITS.nodes);
  list(document.accessors, "accessors", VRMA_LIMITS.accessors);
  for (const buffer of list(document.buffers, "buffers", 16))
    requireValid(object(buffer, "A buffer").uri === undefined, "A VRMA clip keeps all its data inside the file.");
  requireValid(list(document.images, "pictures", 0).length === 0, "A VRMA clip must not carry pictures.");
  const animations = list(document.animations, "animations", 64);
  requireValid(animations.length >= 1, "The clip has no animation.");
  for (const animation of animations)
    for (const sampler of list(object(animation, "An animation").samplers, "samplers", 4096))
      requireValid(object(sampler, "A sampler").interpolation !== "CUBICSPLINE", "Martlet plays VRMA clips with LINEAR or STEP keys only.");
}

/** Reads a VRMA clip with the official loader (@pixiv/three-vrm-animation): its first animation, with every track in the clip's
 *  own humanoid space. Nothing is fetched: a clip with a URL fails. */
export async function loadVrmAnimation(buffer: ArrayBuffer): Promise<VRMAnimation> {
  preflight(buffer);
  const snapshot = buffer.slice(0);
  const manager = new THREE.LoadingManager();
  manager.setURLModifier(url => { throw new VrmError("forbidden-resource", `URL loading is forbidden: ${url.slice(0, 64)}`); });
  const loader = new GLTFLoader(manager);
  loader.register(parser => new VRMAnimationLoaderPlugin(parser));
  let gltf;
  try { gltf = await loader.parseAsync(snapshot, ""); }
  catch (error) {
    if (error instanceof VrmError) throw error;
    throw new VrmError("import-failed", `The VRMA clip can't be read: ${String((error as Error)?.message ?? error).slice(0, 200)}`);
  }
  const animation: unknown = (gltf.userData as { vrmAnimations?: unknown[] }).vrmAnimations?.[0];
  requireValid(animation instanceof VRMAnimation, "The clip has no VRM animation the loader could read.");
  finite(animation.duration, 0.01, VRMA_LIMITS.seconds, "The clip's length in seconds");
  requireValid(animation.humanoidTracks.rotation.size + animation.humanoidTracks.translation.size > 0, "The clip moves no humanoid bone.");
  for (const track of [...animation.humanoidTracks.rotation.values(), ...animation.humanoidTracks.translation.values()])
    requireValid(track.validate() && [...track.values].every(Number.isFinite), "The clip has a track with invalid keys.");
  return animation;
}

/** Bones a clip never takes: the eyes stay with the gaze and eye gestures, the jaw with lip-sync. */
const KEPT_BONES: ReadonlySet<string> = new Set(["leftEye", "rightEye", "jaw"]);
/** Bones whose idle motion stays on top of a clip: the look (head and neck), nods, the breath (chest, shoulders) and the sway
 *  (spine) are small turns from the rest pose, so the clip's turn is applied with Martlet's own after it. Every other bone the
 *  clip turns (the arms, hands, fingers, hips and legs) shows the clip instead of Martlet's relaxed pose. */
const LAYERED_BONES: ReadonlySet<string> = new Set(["spine", "chest", "upperChest", "neck", "head", "leftShoulder", "rightShoulder"]);
/** Seconds a playing clip takes to give the body back when it is stopped or another clip starts. */
const LET_GO_SECONDS = 0.3;

interface ClipBone { readonly bone: string; readonly node: THREE.Object3D; readonly layered: boolean; readonly keys: THREE.Interpolant }
interface ClipHips { readonly node: THREE.Object3D; readonly keys: THREE.Interpolant; readonly first: THREE.Vector3; readonly rest: THREE.Vector3;
  readonly inPlace: boolean }
interface Clip { readonly info: VrmAnimationInfo; readonly bones: readonly ClipBone[]; readonly hips: ClipHips | undefined; readonly fade: number }
interface Layer { readonly clip: Clip; time: number; letGo: number | undefined }

const smoothstep = (x: number) => { const c = Math.max(0, Math.min(1, x)); return c * c * (3 - 2 * c); };

/**
 * Plays VRMA clips on one loaded VRM, over Martlet's procedural idle pose. A kept clip plays once: it blends in, then blends out
 * back to the idle pose by its end (a quarter of its length each way, at most 0.4 seconds). A new clip crossfades from the one
 * playing. Each frame: `rest` first puts the bones the clips turned back to the rest pose, then the idle pose and gestures turn
 * theirs, then `apply` blends the clips over them. The face (expressions, blink, lip-sync), the eyes and the jaw stay
 * Martlet's: a clip's expression and look-at tracks are not played.
 */
export class VrmClipPlayer {
  private readonly clips = new Map<string, Clip>();
  private layers: Layer[] = [];
  private readonly turned = new Set<THREE.Object3D>();
  private hips: ClipHips | undefined;

  /** Keeps `animation` as `name` for `model`, replacing a clip of that name; the oldest goes when 16 are kept. The hips move
   *  up and down as the clip has them (a crouch, a bow, a small jump). With `inPlace` (the default) they stay at rest across the
   *  ground, so a clip that walks or drifts keeps the character in its window; without it they move from where the clip starts. */
  keep(model: VRM, name: string, animation: VRMAnimation, inPlace = true): VrmAnimationInfo {
    animationName(name);
    const metaVersion = model.meta.metaVersion === "0" ? "0" : "1";
    const tracks = createVRMAnimationHumanoidTracks(animation, model.humanoid, metaVersion);
    const bones: ClipBone[] = [];
    for (const [bone, track] of tracks.rotation) {
      const node = model.humanoid.getNormalizedBoneNode(bone as VRMHumanBoneName);
      if (!node || KEPT_BONES.has(bone)) continue;
      bones.push({ bone, node, layered: LAYERED_BONES.has(bone),
        keys: new THREE.QuaternionLinearInterpolant(track.times, track.values, 4, new Float32Array(4)) });
    }
    let hips: ClipHips | undefined;
    const move = tracks.translation.get("hips"), hipsNode = model.humanoid.getNormalizedBoneNode("hips");
    const restHips = model.humanoid.normalizedRestPose.hips?.position;
    if (move && hipsNode && restHips && move.values.length >= 3)
      hips = { node: hipsNode, keys: new THREE.LinearInterpolant(move.times, move.values, 3, new Float32Array(3)),
        first: new THREE.Vector3(move.values[0], move.values[1], move.values[2]), rest: new THREE.Vector3().fromArray(restHips), inPlace };
    requireValid(bones.length > 0 || hips, "The clip moves none of this model's humanoid bones.");
    const duration = animation.duration;
    const info: VrmAnimationInfo = Object.freeze({ name, duration, bones: Object.freeze(bones.map(b => b.bone)), hips: hips !== undefined,
      inPlace });
    this.clips.delete(name);
    while (this.clips.size >= VRMA_LIMITS.clips) this.clips.delete(this.clips.keys().next().value!);
    this.clips.set(name, { info, bones, hips, fade: Math.min(0.4, duration / 4) });
    return info;
  }

  /** The kept clips, oldest first. */
  get kept(): readonly VrmAnimationInfo[] { return [...this.clips.values()].map(c => c.info); }

  /** Plays the kept clip `name` once from its start, crossfading from one playing; false when no clip has that name. */
  play(name: string): boolean {
    const clip = this.clips.get(name);
    if (!clip) return false;
    for (const layer of this.layers) layer.letGo ??= 0;
    this.layers.push({ clip, time: 0, letGo: undefined });
    if (this.layers.length > 4) this.layers.splice(0, this.layers.length - 4);
    return true;
  }

  /** Blends the playing clip (only `name`, when given) back to the idle pose; false when none played. */
  stop(name?: string): boolean {
    let stopped = false;
    for (const layer of this.layers) if (layer.letGo === undefined && (name === undefined || layer.clip.info.name === name)) {
      layer.letGo = 0; stopped = true;
    }
    return stopped;
  }

  /** The newest clip still playing, or undefined. */
  get reading(): VrmAnimationReading | undefined {
    const layer = [...this.layers].reverse().find(l => l.letGo === undefined) ?? this.layers.at(-1);
    if (!layer) return undefined;
    const round = (value: number) => Math.round(value * 1000) / 1000;
    return Object.freeze({ name: layer.clip.info.name, time: round(Math.min(layer.time, layer.clip.info.duration)),
      duration: round(layer.clip.info.duration), weight: round(this.weight(layer)) });
  }

  /** Puts the bones the clips turned back in the rest pose (and the hips where they rest), before the idle pose is set. */
  rest(): void {
    for (const node of this.turned) node.quaternion.identity();
    this.hips?.node.position.copy(this.hips.rest);
    if (this.layers.length === 0) { this.turned.clear(); this.hips = undefined; }
  }

  /** Advances the clips by `deltaSeconds` and blends them over the pose the idle motion and gestures just set. */
  apply(deltaSeconds: number): void {
    for (const layer of this.layers) {
      layer.time += deltaSeconds;
      if (layer.letGo !== undefined) layer.letGo += deltaSeconds;
    }
    this.layers = this.layers.filter(l => l.time < l.clip.info.duration && (l.letGo === undefined || l.letGo < LET_GO_SECONDS));
    if (this.layers.length === 0) return;
    // Each bone's clip turn and how much of the bone the clips have: layered newest over oldest, like crossfades.
    const turns = new Map<THREE.Object3D, { turn: THREE.Quaternion; weight: number; layered: boolean }>();
    let hips: { at: THREE.Vector3; weight: number; clip: ClipHips } | undefined;
    const quaternion = new THREE.Quaternion(), point = new THREE.Vector3();
    for (const layer of this.layers) {
      const weight = this.weight(layer);
      if (weight <= 0) continue;
      const time = Math.min(layer.time, layer.clip.info.duration);
      for (const bone of layer.clip.bones) {
        const keys = bone.keys.evaluate(time);
        quaternion.set(keys[0]!, keys[1]!, keys[2]!, keys[3]!).normalize();
        const seen = turns.get(bone.node);
        if (!seen) { turns.set(bone.node, { turn: quaternion.clone(), weight, layered: bone.layered }); continue; }
        const total = 1 - (1 - seen.weight) * (1 - weight);
        seen.turn.slerp(quaternion, weight / total);
        seen.weight = total;
      }
      const move = layer.clip.hips;
      if (move) {
        const keys = move.keys.evaluate(time);
        // The hips' height as the clip has it, with a gesture's lift (a bounce) on top. Across the ground they stay at rest (in
        // place), or move from where the clip starts, so the character never jumps to where the clip's root began.
        const lift = keys[1]! + move.node.position.y - move.rest.y;
        if (move.inPlace) point.set(move.rest.x, lift, move.rest.z);
        else point.set(keys[0]! - move.first.x + move.rest.x, lift, keys[2]! - move.first.z + move.rest.z);
        if (!hips) hips = { at: point.clone(), weight, clip: move };
        else { const total = 1 - (1 - hips.weight) * (1 - weight); hips.at.lerp(point, weight / total); hips.weight = total; }
      }
    }
    for (const [node, { turn, weight, layered }] of turns) {
      this.turned.add(node);
      node.quaternion.slerp(layered ? turn.multiply(node.quaternion) : turn, weight);
    }
    if (hips) {
      this.hips = hips.clip;
      hips.clip.node.position.lerp(hips.at, hips.weight);
    }
  }

  /** Forgets every clip (another model loads, or this one goes). */
  clear(): void { this.clips.clear(); this.layers = []; this.turned.clear(); this.hips = undefined; }

  // How much of the body a playing clip has now: its blend in and out, and its let-go when stopped or replaced.
  private weight(layer: Layer): number {
    const { duration } = layer.clip.info, fade = layer.clip.fade;
    const envelope = smoothstep(Math.min(layer.time, duration - layer.time) / fade);
    return envelope * (layer.letGo === undefined ? 1 : 1 - smoothstep(layer.letGo / LET_GO_SECONDS));
  }
}
