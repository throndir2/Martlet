import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { VRM, VRMHumanBoneList, VRMLoaderPlugin } from "@pixiv/three-vrm";
import { blinkPresets, finite, gazePresets, inspectVrm, integer, mouthPresets, object, requireValid, VrmError,
  type VrmCapabilities } from "./inspect.js";
import { type EyeFields, type EyeHint, type EyesFrom, type FaceFrame, VrmEyes } from "./eyes.js";
import { hitTestVrm, type VrmHit } from "./touch.js";

type BoneName = Parameters<VRM["humanoid"]["getNormalizedBoneNode"]>[0];

/** Martlet's own gestures, played on any VRM that has the humanoid bones they move. After the reply gestures come the voice
 *  emotes, played when the voice makes their sound or tone (laugh, sigh, gasp...), then the touch and mood gestures (wink,
 *  pout, shy...), which also use the model's preset expressions (blinkLeft, relaxed, happy...) when it has them, then the
 *  held face parts (eyes_up turns the eye bones, mouth_open the oh or aa mouth expression), and last the stronger blush
 *  levels (see VRM_BLUSH_LEVELS). */
export const VRM_GESTURES = Object.freeze(["nod", "shake", "tilt", "bow", "sway", "wave", "shrug", "bounce", "blush",
  "laugh", "chuckle", "sigh", "gasp", "cough", "clear_throat", "groan", "sniff", "shush", "inhale", "exhale", "mumble", "hum",
  "sneeze", "whistle", "happy", "sarcastic", "angry", "fear", "crying", "whispering", "dramatic",
  "wink", "pout", "shy", "giggle", "flinch", "lean_in", "look_away", "think", "eye_roll", "drowsy",
  "eyes_up", "mouth_open", "blush_deep", "blush_fierce"] as const);
export type VrmGesture = typeof VRM_GESTURES[number];
/** The gestures that can be held (an action with `hold: true`): eased into and kept, gently alive, until ended. The model's
 *  own blush can be held too, at every blush level (see `playGesture`). */
export const VRM_HOLDABLE_GESTURES = Object.freeze(["pout", "shy", "look_away", "drowsy", "eyes_up", "mouth_open"] as const);
type HoldableGesture = typeof VRM_HOLDABLE_GESTURES[number];
const holdable = (name: string): name is HoldableGesture => (VRM_HOLDABLE_GESTURES as readonly string[]).includes(name);
/** Martlet's blush levels, faintest first. Each shows the model's own blush or cheek expression fully (for 4 seconds, or
 *  held); the renderer page draws the stronger ones over it, and draws every level on a model without one. */
export const VRM_BLUSH_LEVELS = Object.freeze(["blush", "blush_deep", "blush_fierce"] as const);
type BlushLevel = typeof VRM_BLUSH_LEVELS[number];
const blushLevel = (name: string): name is BlushLevel => (VRM_BLUSH_LEVELS as readonly string[]).includes(name);
/** A part of the face or body a held gesture moves. */
export type VrmGesturePart = "eyes" | "mouth" | "cheeks" | "brows" | "head";
/** The parts each held gesture moves (the head part includes the body; the model's own blush, at every level, is the
 *  cheeks). Held gestures layer: holding one lets go only of the held gestures that move a part it moves too. */
export const VRM_HOLD_PARTS: Readonly<Record<HoldableGesture | BlushLevel, readonly VrmGesturePart[]>> = Object.freeze({
  pout: ["mouth", "brows", "head"], shy: ["eyes", "mouth", "head"], look_away: ["eyes", "head"], drowsy: ["eyes", "head"],
  blush: ["cheeks"], eyes_up: ["eyes"], mouth_open: ["mouth"], blush_deep: ["cheeks"], blush_fierce: ["cheeks"],
});
const sharePart = (a: HoldableGesture | BlushLevel, b: HoldableGesture | BlushLevel) =>
  VRM_HOLD_PARTS[a].some(part => VRM_HOLD_PARTS[b].includes(part));
const head: readonly BoneName[] = ["head"], headSpine: readonly BoneName[] = ["head", "spine"];
const GESTURE_BONES: Readonly<Record<VrmGesture, readonly BoneName[]>> = Object.freeze({
  nod: head, shake: head, tilt: head, bow: ["spine"], sway: ["spine"],
  wave: ["rightUpperArm", "rightLowerArm"], shrug: ["leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm"], bounce: ["hips"],
  // An authored blush or cheek expression when the model has one; otherwise the page draws a glow on the cheeks, found
  // from the head (see faceAnchor). So do the stronger blush levels.
  blush: head, blush_deep: head, blush_fierce: head,
  laugh: headSpine, chuckle: head, sigh: headSpine, gasp: headSpine, cough: headSpine, clear_throat: head, groan: head, sniff: head,
  shush: headSpine, inhale: headSpine, exhale: headSpine, mumble: head, hum: head, sneeze: headSpine, whistle: head,
  happy: headSpine, sarcastic: head, angry: headSpine, fear: headSpine, crying: headSpine, whispering: headSpine,
  dramatic: ["head", "leftUpperArm", "rightUpperArm"],
  wink: head, pout: head, shy: headSpine, giggle: headSpine, flinch: headSpine, lean_in: headSpine, look_away: head, think: head,
  eye_roll: head, drowsy: headSpine,
  // The eye bones turn up; the mouth needs the oh or aa expression (see gestures).
  eyes_up: ["leftEye", "rightEye"], mouth_open: head,
});
const GESTURE_SECONDS: Readonly<Record<VrmGesture, number>> = Object.freeze({
  nod: 1.1, shake: 1.2, tilt: 1.8, bow: 2, sway: 2.4, wave: 2.4, shrug: 1.8, bounce: 1.2, blush: 4,
  laugh: 2, chuckle: 1.4, sigh: 2.4, gasp: 1.6, cough: 1.5, clear_throat: 1.2, groan: 2.2, sniff: 1, shush: 2, inhale: 1.6,
  exhale: 1.8, mumble: 2, hum: 2.6, sneeze: 1.6, whistle: 2, happy: 2.4, sarcastic: 1.8, angry: 2.4, fear: 2, crying: 3,
  whispering: 2.2, dramatic: 2.4,
  wink: 1.1, pout: 2.6, shy: 3.5, giggle: 1.6, flinch: 1.4, lean_in: 2.8, look_away: 2.4, think: 2.8, eye_roll: 1.8, drowsy: 4.5,
  eyes_up: 3, mouth_open: 2.6, blush_deep: 4, blush_fierce: 4,
});
const GESTURE_FADE: Partial<Record<VrmGesture, number>> = { bounce: 0.15, gasp: 0.12, fear: 0.15, cough: 0.1, sniff: 0.1, sneeze: 0.1,
  wink: 0.12, giggle: 0.15, flinch: 0.05, eye_roll: 0.2 };
/** A custom expression that is the model's own blush. */
const BLUSH_EXPRESSION = /blush|cheek|照れ|赤面|頬|脸红|臉紅|홍조/i;

/** The mouth expression a held open mouth uses: `oh` (a round open mouth), else `aa`; undefined when the model has neither. */
function mouthShape(model: VRM): "oh" | "aa" | undefined {
  const expressions = model.expressionManager;
  return expressions?.getExpression("oh") ? "oh" : expressions?.getExpression("aa") ? "aa" : undefined;
}

/** How far the eyes turn (radians) at a held eyes gesture's full strength: the model's own look-up range for its eye bones
 *  when it has one (5° to 20°), else 12°. */
function eyeRange(model: VRM, gaze: VrmCapabilities["gaze"] | undefined): number {
  const scale = gaze === "bone" ? (model.lookAt?.applier as { rangeMapVerticalUp?: { outputScale?: unknown } } | undefined)
    ?.rangeMapVerticalUp?.outputScale : undefined;
  return (typeof scale === "number" && Number.isFinite(scale) ? Math.max(5, Math.min(20, scale)) : 12) * Math.PI / 180;
}

/** How a voice emote moves the body `t` seconds in at weight `w`: head yaw (gx, right) and pitch (gy, up) like the look,
 *  head roll (tilt), spine bend (forward), lean (sideways) and turn (spineY, to the character's left), shoulders (up), arms
 *  opened out to the sides (open), the eyes turned on their own (eyeX right, eyeY up, -1 to 1 of their range), a held open
 *  mouth (0 to 1) and preset expressions by name to weights (face; ones the model lacks are skipped). */
export interface GesturePose {
  gx: number; gy: number; tilt: number; spineX: number; spineZ: number; shoulders: number; open: number; spineY: number;
  eyeX: number; eyeY: number; mouth: number;
  face: Record<string, number>;
}

const cycle = (t: number, period: number) => Math.sin(2 * Math.PI * t / period);
const smoothstep = (x: number) => { const c = Math.max(0, Math.min(1, x)); return c * c * (3 - 2 * c); };

/** How far each cheek's surface turns out from the face's front, toward its side: about where an anime face's cheek is. */
export const CHEEK_TURN = 35 * Math.PI / 180;

/** A cheek as `project` (world to canvas pixels) shows it from a camera at `camera`: the canvas steps for one face width
 *  (`width`) along `across` and `down` on its surface, and how much it shows (1 facing the camera, fading to 0 as it turns
 *  edge-on or away). */
export function cheekFrame(cheek: THREE.Vector3, across: THREE.Vector3, down: THREE.Vector3, normal: THREE.Vector3, width: number,
  camera: THREE.Vector3, project: (world: THREE.Vector3) => { x: number; y: number }):
  { right: { x: number; y: number }; down: { x: number; y: number }; visible: number } {
  const step = 0.05;
  const along = (direction: THREE.Vector3) => {
    const to = project(cheek.clone().addScaledVector(direction, step * width));
    const from = project(cheek.clone().addScaledVector(direction, -step * width));
    return { x: (to.x - from.x) / (2 * step), y: (to.y - from.y) / (2 * step) };
  };
  const facing = normal.dot(camera.clone().sub(cheek).normalize());
  return { right: along(across), down: along(down), visible: smoothstep((facing - 0.05) / 0.25) };
}

/** How far a flinch has jerked back `t` seconds in: snapping back within 80ms, then settling. */
export function flinchJolt(t: number): number { return t < 0.08 ? smoothstep(t / 0.08) : Math.exp(-(t - 0.08) * 2.8); }

/** How far a drowsy head has nodded off `t` seconds in: sinking slowly over a 6-second cycle, then catching itself. */
export function drowse(t: number): number {
  const p = (t % 6) / 6;
  return p < 0.8 ? smoothstep(p / 0.8) : 1 - smoothstep((p - 0.8) / 0.1);
}

/** A holdable gesture's pose at weight `w`, kept alive by `t` (pout huffs, shy peeks back, look_away glances back, drowsy
 *  nods off and catches itself, raised eyes drift, an open mouth breathes), so a held pose never looks frozen. */
function moodPose(pose: GesturePose, name: HoldableGesture, t: number, w: number): void {
  switch (name) {
    case "pout": pose.gx = (-0.25 + 0.05 * cycle(t, 2.6)) * w; pose.gy = -0.1 * w; pose.tilt = -0.08 * w; pose.face = { angry: 0.3 * w }; break;
    case "shy": {
      const peek = Math.max(0, cycle(t, 5.5)) ** 4;
      pose.gx = (-0.35 + 0.22 * peek) * w; pose.gy = (-0.45 + 0.2 * peek) * w; pose.tilt = 0.15 * w;
      pose.spineY = 0.12 * w; pose.spineX = 0.05 * w; pose.shoulders = 0.4 * w; pose.face = { relaxed: 0.45 * w }; break;
    }
    case "look_away": {
      const glance = Math.max(0, cycle(t - 2, 6)) ** 6;
      pose.gx = (0.6 - 0.3 * glance) * w; pose.gy = 0.05 * w; pose.tilt = -0.05 * w; pose.spineY = -0.05 * w; break;
    }
    case "drowsy": {
      const droop = drowse(t);
      pose.gy = -(0.35 + 0.25 * droop) * w; pose.tilt = (0.06 + 0.1 * droop) * w; pose.spineX = 0.08 * w; pose.shoulders = -0.5 * w;
      pose.face = { blink: (0.5 + 0.35 * droop) * w }; break;
    }
    case "eyes_up": pose.eyeX = 0.1 * cycle(t, 7.3) * w; pose.eyeY = (0.9 + 0.05 * cycle(t, 4.7)) * w; break;
    case "mouth_open": pose.mouth = (0.75 + 0.08 * cycle(t, 2.8)) * w; break;
  }
}

const emptyPose = (): GesturePose => ({ gx: 0, gy: 0, tilt: 0, spineX: 0, spineZ: 0, shoulders: 0, open: 0, spineY: 0, eyeX: 0, eyeY: 0,
  mouth: 0, face: {} });

function addPose(into: GesturePose, pose: GesturePose): void {
  for (const key of ["gx", "gy", "tilt", "spineX", "spineZ", "shoulders", "open", "spineY", "eyeX", "eyeY", "mouth"] as const) into[key] += pose[key];
  const face = { ...into.face };
  for (const [name, weight] of Object.entries(pose.face)) face[name] = Math.max(face[name] ?? 0, weight);
  into.face = face;
}

export function gesturePose(name: VrmGesture, t: number, w: number): GesturePose {
  const pose = emptyPose();
  switch (name) {
    case "laugh": { const bob = Math.abs(cycle(t, 0.64)); pose.gy = (0.25 - 0.35 * bob) * w; pose.spineX = (0.06 * bob - 0.05) * w; break; }
    case "chuckle": pose.gy = -0.25 * Math.abs(cycle(t, 0.7)) * w; break;
    case "sigh": pose.gy = -0.5 * w; pose.spineX = 0.12 * w; pose.shoulders = -w; break;
    case "gasp": pose.gy = 0.6 * w; pose.spineX = -0.12 * w; pose.shoulders = 0.5 * w; break;
    case "cough": { const jerk = t < 1.2 ? Math.max(0, cycle(t, 0.4)) ** 3 : 0; pose.gy = -0.6 * jerk * w; pose.spineX = 0.2 * jerk * w; break; }
    case "clear_throat": pose.gx = 0.3 * w; pose.gy = -0.3 * w; break;
    case "groan": pose.gy = 0.45 * w; pose.tilt = -0.2 * w; break;
    case "sniff": pose.gy = 0.25 * (t < 0.6 ? Math.max(0, cycle(t, 0.3)) : 0) * w; break;
    case "shush": pose.gy = -0.35 * w; pose.tilt = 0.1 * w; pose.spineX = 0.1 * w; break;
    case "inhale": pose.gy = 0.25 * w; pose.spineX = -0.08 * w; pose.shoulders = 0.6 * w; break;
    case "exhale": pose.gy = -0.3 * w; pose.spineX = 0.1 * w; pose.shoulders = -0.5 * w; break;
    case "mumble": pose.gx = -0.4 * w; pose.gy = -0.4 * w; break;
    case "hum": pose.tilt = 0.15 * cycle(t, 1.3) * w; pose.gy = -0.1 * w; break;
    case "sneeze": {
      const lift = t < 0.7 ? 0.4 * smoothstep(t / 0.7) : t < 0.85 ? 0.4 - 1.2 * smoothstep((t - 0.7) / 0.15) : -0.8 * (1 - smoothstep((t - 0.85) / 0.75));
      pose.gy = lift * w; pose.spineX = -0.3 * lift * w; break;
    }
    case "whistle": pose.gx = 0.3 * w; pose.gy = 0.4 * w; pose.tilt = 0.15 * w; break;
    case "happy": pose.gy = 0.15 * cycle(t, 0.6) * w; pose.tilt = 0.12 * cycle(t, 1.2) * w; pose.spineZ = 0.05 * cycle(t, 1.2) * w; break;
    case "sarcastic": pose.gx = 0.3 * Math.cos(Math.PI * t / GESTURE_SECONDS.sarcastic) * w; pose.gy = 0.35 * w; pose.tilt = 0.15 * w; break;
    case "angry": pose.gy = -0.3 * w; pose.spineX = 0.08 * w; pose.shoulders = 0.3 * w; break;
    case "fear": pose.gx = 0.08 * cycle(t, 0.12) * w; pose.gy = 0.2 * w; pose.spineX = -0.12 * w; pose.shoulders = 0.7 * w; break;
    case "crying": {
      const sob = Math.max(0, cycle(t, 0.7));
      pose.gy = -(0.6 + 0.1 * sob) * w; pose.spineX = (0.12 + 0.04 * sob) * w; pose.shoulders = 0.3 * sob * w; break;
    }
    case "whispering": pose.gx = 0.3 * w; pose.tilt = 0.25 * w; pose.spineX = 0.12 * w; pose.spineZ = 0.06 * w; break;
    case "dramatic": pose.gx = 0.3 * Math.sin(Math.PI * t / GESTURE_SECONDS.dramatic) * w; pose.gy = 0.4 * w; pose.spineX = -0.1 * w; pose.open = w; break;
    case "wink": pose.gx = 0.05 * w; pose.gy = 0.05 * w; pose.tilt = 0.1 * w; pose.face = { blinkLeft: w, happy: 0.2 * w }; break;
    case "pout": case "shy": case "look_away": case "drowsy": case "eyes_up": case "mouth_open": moodPose(pose, name, t, w); break;
    case "giggle": {
      const bounce = Math.abs(cycle(t, 0.36));
      pose.gy = (0.08 - 0.2 * bounce) * w; pose.spineX = 0.05 * bounce * w; pose.shoulders = 0.6 * bounce * w; pose.tilt = 0.1 * w;
      pose.spineZ = 0.04 * cycle(t, 0.72) * w; pose.face = { happy: 0.8 * w }; break;
    }
    case "flinch": {
      const jolt = flinchJolt(t) * smoothstep((GESTURE_SECONDS.flinch - t) / 0.4) * w;
      pose.gx = (0.3 + 0.04 * cycle(t, 0.09)) * jolt; pose.gy = 0.35 * jolt; pose.spineX = -0.15 * jolt; pose.spineY = -0.1 * jolt;
      pose.shoulders = 0.8 * jolt; pose.face = { surprised: 0.6 * jolt }; break;
    }
    case "lean_in": pose.spineX = 0.12 * w; pose.tilt = (0.22 + 0.05 * cycle(t, 1.4)) * w; pose.gy = -0.15 * w; pose.face = { relaxed: 0.6 * w }; break;
    case "think": pose.gx = (-0.3 + 0.05 * cycle(t, 2.2)) * w; pose.gy = 0.45 * w; pose.tilt = 0.12 * w; break;
    case "eye_roll": {
      const angle = Math.PI * (0.9 - 0.8 * smoothstep((t - 0.2) / 1.1));
      pose.gx = 0.3 * Math.cos(angle) * w; pose.gy = (0.1 + 0.3 * Math.sin(angle)) * w; pose.tilt = 0.08 * w; pose.face = { blink: 0.2 * w }; break;
    }
    default: break;
  }
  return pose;
}

// 0 to 1 over `fade` seconds, held, then back to 0 by `total`.
function envelope(seconds: number, total: number, fade: number): number {
  const x = Math.max(0, Math.min(1, Math.min(seconds, total - seconds) / fade));
  return x * x * (3 - 2 * x);
}

/** Adapter-local controls, not the shared AvatarFrame wire envelope. */
export interface PlaybackIdentity {
  sessionId: string;
  turnId: string;
  requestId: string;
  sourceId: string;
  epoch: number;
  sampleRate: number;
}
export interface CoefficientInput extends CompositionRevision {
  identity: PlaybackIdentity;
  sequence: number;
  sampleOffset: number;
  coefficients: Readonly<Record<string, number>>;
}
export interface CompositionRevision {
  modelRevision: string;
  mappingRevision: string;
}
export type InputMode = "composed" | "coefficients";
export interface ComposedParameterInput extends CompositionRevision {
  identity: PlaybackIdentity;
  sequence: number;
  sampleOffset: number;
  parameters: Readonly<Record<string, number>>;
}
export interface ExpressionMapping {
  channel: string;
  expression: string;
  aspect: "mouth" | "expression" | "blink";
  minimum: number;
  maximum: number;
}
export interface Selection {
  /** Preserve the selected source's identity; the upstream host chooses A2F or an explicit compatible alternative. */
  faceSource: string;
  faceMode: "authored-explicit" | "reduced-vowel-jaw-only";
  mappings: readonly ExpressionMapping[];
  gaze: boolean;
  head: boolean;
  secondaryMotion: boolean;
}
export interface LocalPose {
  gaze?: readonly [number, number, number];
  head?: readonly [number, number, number, number];
}
function identifier(value: unknown, label: string): asserts value is string {
  requireValid(typeof value === "string" && /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/.test(value), `Invalid ${label}.`);
}
function validateIdentity(identity: PlaybackIdentity): void {
  requireValid(identity !== null && typeof identity === "object", "Playback identity is required.");
  for (const key of ["sessionId", "turnId", "requestId"] as const)
    requireValid(typeof identity[key] === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(identity[key])
      && identity[key] !== "00000000-0000-0000-0000-000000000000", `${key} must be a nonempty UUID.`);
  identifier(identity.sourceId, "sourceId");
  integer(identity.epoch, 0, 2147483647, "epoch");
  requireValid([16000, 24000, 44100, 48000].includes(identity.sampleRate), "Unsupported original PCM sample rate.");
}
function identityKey(identity: PlaybackIdentity): string {
  return [identity.sessionId.toLowerCase(), identity.turnId.toLowerCase(), identity.requestId.toLowerCase(),
    identity.sourceId, identity.epoch, identity.sampleRate].join("|");
}

function releaseResources(value: unknown, seen = new Set<unknown>()): void {
  if (!value || seen.has(value)) return;
  seen.add(value);
  if (value instanceof THREE.Object3D) {
    value.traverse(node => {
      if (node instanceof THREE.Mesh) {
        releaseResources(node.geometry, seen);
        releaseResources(node.material, seen);
      }
      if (node instanceof THREE.SkinnedMesh) node.skeleton.dispose();
    });
  } else if (Array.isArray(value)) value.forEach(v => releaseResources(v, seen));
  else if (value instanceof THREE.Material) {
    Object.values(value).forEach(v => { if (v instanceof THREE.Texture) releaseResources(v, seen); });
    value.dispose();
  } else if (value instanceof THREE.Texture) {
    const image: unknown = value.source.data;
    if (typeof ImageBitmap !== "undefined" && image instanceof ImageBitmap && !seen.has(image)) {
      seen.add(image); image.close();
    }
    value.dispose();
  } else if (value instanceof THREE.BufferGeometry) value.dispose();
}

/** Production importer also used by CPU-only tests. Only built-in, bundled loader code is registered. */
export async function loadLocalVrm(buffer: ArrayBuffer): Promise<{ vrm: VRM; capabilities: VrmCapabilities }> {
  requireValid(buffer instanceof ArrayBuffer && buffer.byteLength <= 32 * 1024 * 1024, "VRM buffer exceeds limit.");
  // Own the bytes across async decoding; caller mutation must not bypass preflight.
  const snapshot = buffer.slice(0);
  const capabilities = inspectVrm(snapshot);
  const manager = new THREE.LoadingManager();
  const errors: string[] = [];
  manager.onError = () => { errors.push("Embedded resource decode failed."); };
  manager.setURLModifier(url => {
    throw new VrmError("forbidden-resource", `URL loading is forbidden: ${url.slice(0, 64)}`);
  });
  const loader = new GLTFLoader(manager);
  const resources = new Set<unknown>();
  const released = new Set<unknown>();
  let failed = false;
  loader.register(parser => {
    const images = new Map<number, Promise<THREE.Texture>>();
    // Per-parser boundary avoids GLTFLoader's success-only object-URL revocation.
    // Decode embedded bytes directly: no fetch, shared URL hooks, or object URLs.
    parser.loadImageSource = (sourceIndex) => {
      let pending = images.get(sourceIndex);
      if (!pending) {
        pending = (async () => {
          try {
            requireValid(typeof createImageBitmap === "function", "Embedded PNG requires browser createImageBitmap support.");
            const json = object(parser.json, "parser JSON");
            requireValid(Array.isArray(json.images), "Missing embedded images.");
            const image = object(json.images[sourceIndex], "embedded image");
            const bytes: unknown = await parser.getDependency("bufferView", Number(image.bufferView));
            requireValid(bytes instanceof ArrayBuffer && image.mimeType === "image/png", "Expected validated embedded PNG bytes.");
            const bitmap = await createImageBitmap(new Blob([bytes], { type: "image/png" }),
              { premultiplyAlpha: "none", colorSpaceConversion: "none" });
            const texture = new THREE.Texture(bitmap);
            texture.needsUpdate = true;
            texture.userData.mimeType = "image/png";
            resources.add(texture);
            if (failed) {
              releaseResources(texture, released);
              throw new VrmError("import-failed", "Embedded PNG completed after import failed.");
            }
            return texture;
          } catch (error) {
            errors.push("Embedded resource decode failed.");
            throw error;
          }
        })();
        images.set(sourceIndex, pending);
        return pending;
      }
      return pending.then(texture => {
        const clone = texture.clone();
        resources.add(clone);
        if (failed) releaseResources(clone, released);
        return clone;
      });
    };
    const original = parser.getDependency.bind(parser);
    parser.getDependency = async (type, index) => {
      const result: unknown = await original(type, index);
      resources.add(result);
      if (failed) releaseResources(result, released);
      return result;
    };
    return new VRMLoaderPlugin(parser);
  });
  const fail = (): void => {
    failed = true;
    resources.forEach(value => releaseResources(value, released));
  };
  let gltf;
  try { gltf = await loader.parseAsync(snapshot, ""); }
  catch (error) { fail(); throw error; }
  const candidate: unknown = gltf.userData.vrm;
  if (!(candidate instanceof VRM)) {
    fail();
    throw new VrmError("import-failed", "The importer did not produce a VRM1 runtime.");
  }
  try {
    requireValid(errors.length === 0, errors.join(" "));
    requireValid(candidate.meta.metaVersion === "1", "Importer did not produce VRM1 metadata.");
    for (const expression of capabilities.expressions.filter(e => e.usable)) {
      const imported = candidate.expressionManager?.getExpression(expression.name);
      requireValid(imported && imported.binds.length > 0, `Importer omitted usable bindings for authored expression ${expression.name}.`);
    }
    if (capabilities.secondaryMotion)
      requireValid(candidate.springBoneManager && [...candidate.springBoneManager.joints]
        .some(joint => joint.child && joint.initialLocalChildPosition.lengthSq() > 1e-12),
      "Importer did not produce usable spring joints; re-export a nonzero-length VRMC_springBone chain.");
    if (capabilities.gaze !== "absent") requireValid(candidate.lookAt, "Importer omitted the advertised gaze controller.");
    if (candidate.lookAt) candidate.lookAt.autoUpdate = false;
    candidate.scene.updateWorldMatrix(true, true);
    const box = new THREE.Box3().setFromObject(candidate.scene);
    if (!box.isEmpty()) finite(box.getSize(new THREE.Vector3()).length(), 0.0001, 100, "Rendered model size");
    return { vrm: candidate, capabilities };
  } catch (error) {
    fail();
    releaseResources(candidate.scene, released);
    throw error;
  }
}

/** No audio devices, providers, clocks or networks are started by this runtime. */
export class VrmRuntime {
  private model: VRM | undefined;
  private inspected: VrmCapabilities | undefined;
  private selection: Selection | undefined;
  private revision: CompositionRevision | undefined;
  private inputMode: InputMode | undefined;
  private identity: string | undefined;
  private sequence = -1;
  private sampleOffset = -1;
  private playbackOffset = -1;
  private generation = 0;
  private disposed = false;
  private pending = false;
  private pose: LocalPose = {};
  private readonly gazeTarget = new THREE.Object3D();
  private idle = false;
  private idleTime = 0;
  private speech = 0;
  private speechTarget = 0;
  private speechAge = Number.POSITIVE_INFINITY;
  private nextBlink = 2 + Math.random() * 3;
  private blinkTime = -1;
  private lookTarget = { x: 0, y: 0 };
  private look = { x: 0, y: 0 };
  private composedAge = Number.POSITIVE_INFINITY;
  private readonly actions = new Map<string, { target: number; value: number }>();
  /** Expressions held on (lingering emotes) until turned off. */
  private readonly heldExpressions = new Set<string>();
  private gesture: { name: VrmGesture; seconds: number } | undefined;
  private blush: { name: string; level: BlushLevel; seconds: number; hold: boolean } | undefined;
  private held: { name: HoldableGesture; seconds: number; progress: number; on: boolean }[] = [];
  // 0 to 1: how much the voice moves the mouth now (eased), so a held open mouth makes room for lip-sync.
  private talk = 0;
  private face: Readonly<Record<string, number>> = {};
  // Expressions the gestures' face wrote last frame and their values before, put back before the next frame is composed.
  private readonly faceRestore = new Map<string, number>();
  private hipsRest: number | undefined;
  private faceWidth = 0.14;
  private eyes: VrmEyes | undefined;

  get capabilities(): VrmCapabilities | undefined { return this.inspected; }
  get scene(): THREE.Group | undefined { return this.model?.scene; }
  get isLoaded(): boolean { return this.model !== undefined; }

  /** Each humanoid bone the model has with its node, for touch zones. */
  get humanoidNodes(): readonly (readonly [string, THREE.Object3D])[] {
    const model = this.model;
    if (!model) return [];
    return VRMHumanBoneList.flatMap(name => {
      const node = model.humanoid.getRawBoneNode(name);
      return node ? [[name, node] as const] : [];
    });
  }

  /** Relaxed arms, breathing, blinking, cursor-follow and loudness lip-sync while no mapped A2F turn is active. */
  startIdle(): void { this.loaded(); this.idle = true; }

  setLipSync(level: number): void {
    this.loaded();
    finite(level, 0, 1, "lip-sync level");
    this.speechTarget = level;
    this.speechAge = 0;
  }

  setLook(x: number, y: number): void {
    this.loaded();
    finite(x, -1, 1, "look x"); finite(y, -1, 1, "look y");
    this.lookTarget = { x, y };
  }

  /**
   * Fades an authored emotion or custom expression in (`on`) or out, as an emote. One passing emote shows at a time: a new one
   * fades the others out, except held (lingering) ones (`hold`), which stay on together until each is turned off. Mouth, blink
   * and gaze presets belong to lip-sync, blinking and gaze, so they are refused.
   */
  setAction(name: string, on: boolean, hold = false): boolean {
    const model = this.loaded();
    if (typeof name !== "string" || !model.expressionManager?.getExpression(name) ||
      [...mouthPresets, ...blinkPresets, ...gazePresets].includes(name as never)) return false;
    if (on && !hold) for (const [other, state] of this.actions) if (other !== name && !this.heldExpressions.has(other)) state.target = 0;
    if (hold) { if (on) this.heldExpressions.add(name); else this.heldExpressions.delete(name); }
    else if (this.heldExpressions.has(name)) return true;
    const state = this.actions.get(name) ?? { target: 0, value: 0 };
    state.target = on ? 1 : 0;
    this.actions.set(name, state);
    return true;
  }

  /** Martlet's gestures this model has the humanoid bones for (and, for mouth_open, the oh or aa mouth expression). */
  get gestures(): readonly VrmGesture[] {
    const model = this.model;
    return model ? VRM_GESTURES.filter(name => GESTURE_BONES[name].every(bone => model.humanoid.getNormalizedBoneNode(bone)) &&
      (name !== "mouth_open" || mouthShape(model) !== undefined)) : [];
  }

  /** Starts one of Martlet's gestures (see `gestures`), replacing one already playing: head gestures, a bow, a sway, a wave,
   *  a shrug, an excited bounce, a voice emote (a laugh, a sigh, a gasp...) or a touch or mood gesture (a wink, a flinch...).
   *  With `hold`, a holdable one (VRM_HOLDABLE_GESTURES) eases in and stays until `endGesture`. Held gestures layer: holding
   *  one lets go only of the held ones that move a part it moves too (VRM_HOLD_PARTS), crossfading from them. A gesture
   *  played meanwhile plays on top, the held poses easing back partway and resuming after. A blush level shows the model's
   *  own blush or cheek expression (4 seconds, or held: the cheeks); false when it has none (the page draws one). */
  playGesture(name: string, hold = false): boolean {
    const model = this.loaded();
    if (!(this.gestures as readonly string[]).includes(name)) return false;
    if (blushLevel(name)) {
      const reserved: readonly string[] = [...mouthPresets, ...blinkPresets, ...gazePresets];
      const own = model.expressionManager?.expressions.map(e => e.expressionName)
        .find(n => !reserved.includes(n) && BLUSH_EXPRESSION.test(n));
      // Played once while it is held, it stays held, as the other held gestures do; held, passing emotes leave it on.
      const keep = hold || this.blush?.hold === true;
      if (!own || !this.setAction(own, true, keep)) return false;
      if (hold) this.letGo(name);
      this.blush = { name: own, level: name, seconds: 0, hold: keep };
      return true;
    }
    if (hold && holdable(name)) {
      this.letGo(name);
      const held = this.held.find(h => h.name === name);
      if (held) held.on = true;
      else this.held.push({ name, seconds: 0, progress: 0, on: true });
      return true;
    }
    if (name === "bounce") this.hipsRest ??= model.humanoid.getNormalizedBoneNode("hips")?.position.y;
    this.gesture = { name: name as VrmGesture, seconds: 0 };
    return true;
  }

  /** Lets go of the held gestures (the held blush included) that move a part `name` moves too. */
  private letGo(name: HoldableGesture | BlushLevel): void {
    for (const held of this.held) if (held.name !== name && sharePart(held.name, name)) held.on = false;
    if (!blushLevel(name) && this.blush?.hold && sharePart(this.blush.level, name)) this.endGesture(this.blush.level);
  }

  /** Lets a held gesture go (eased out); one playing once just finishes. */
  endGesture(name: string): void {
    for (const held of this.held) if (held.name === name) held.on = false;
    if (this.blush?.level === name) { this.setAction(this.blush.name, false, this.blush.hold); this.blush = undefined; }
  }

  /** The gesture playing once, if any, and every gesture held (a held blush by its level). */
  get gestureState(): { readonly playing?: VrmGesture; readonly held: readonly (HoldableGesture | BlushLevel)[] } {
    const held: (HoldableGesture | BlushLevel)[] = this.held.filter(h => h.on).map(h => h.name);
    if (this.blush?.hold) held.push(this.blush.level);
    return Object.freeze({ ...(this.gesture ? { playing: this.gesture.name } : {}), held: Object.freeze(held) });
  }

  /** The held gestures' combined pose `deltaSeconds` later, eased back by `duck` (0..1) under a gesture playing once. */
  private advanceHeld(deltaSeconds: number, duck: number): GesturePose {
    const pose = emptyPose();
    for (const held of this.held) {
      held.seconds += deltaSeconds;
      held.progress = Math.max(0, Math.min(1, held.progress + (held.on ? 1 : -1) * deltaSeconds / 0.8));
      const layer = emptyPose();
      moodPose(layer, held.name, held.seconds, smoothstep(held.progress) * (1 - duck));
      addPose(pose, layer);
    }
    this.held = this.held.filter(held => held.on || held.progress > 0);
    return pose;
  }

  /** Shows the gestures' face (preset expressions such as blinkLeft or relaxed) over what blinking and emotes set, never
   *  over lip-sync, gaze or expressions a composed (Audio2Face) turn drives. */
  private applyFace(model: VRM): void {
    const expressions = model.expressionManager;
    if (!expressions) return;
    const composed = this.identity !== undefined ? new Set(this.selection?.mappings.map(m => m.expression)) : undefined;
    for (const [name, weight] of Object.entries(this.face)) {
      if (weight < 0.001 || !expressions.getExpression(name) || composed?.has(name) ||
        [...mouthPresets, ...gazePresets].includes(name as never)) continue;
      const before = expressions.getValue(name) ?? 0;
      this.faceRestore.set(name, before);
      expressions.setValue(name, Math.min(1, Math.max(before, weight)));
    }
  }

  private restoreFace(model: VRM): void {
    for (const [name, value] of this.faceRestore) model.expressionManager?.setValue(name, value);
    this.faceRestore.clear();
  }

  /**
   * The face in world space, for drawings over it: the middle of the eyes, the head's directions (`side` is the viewer's
   * right when it faces the camera) and the face's width. From the eye bones when the model has them, otherwise estimated
   * from the head bone and the model's height. Each cheek is a surface turned out to its side (CHEEK_TURN): `...Across` runs
   * one unit along it toward the viewer's right (as wide as `side` from the front) and `...Normal` points out of it, so a
   * turned head shows the near cheek wider and the far one narrower, and hides it. Undefined without a head bone.
   */
  faceGeometry(): { center: THREE.Vector3; side: THREE.Vector3; up: THREE.Vector3; forward: THREE.Vector3; width: number;
    eyeLeft: THREE.Vector3; eyeRight: THREE.Vector3; mouth: THREE.Vector3; top: THREE.Vector3;
    cheekLeft: THREE.Vector3; cheekRight: THREE.Vector3; cheekLeftAcross: THREE.Vector3; cheekRightAcross: THREE.Vector3;
    cheekLeftNormal: THREE.Vector3; cheekRightNormal: THREE.Vector3 } | undefined {
    const model = this.model;
    const head = model?.humanoid.getNormalizedBoneNode("head");
    if (!model || !head) return undefined;
    head.updateWorldMatrix(true, false);
    const rotation = head.getWorldQuaternion(new THREE.Quaternion());
    // VRM 1.0 faces +Z with the character's left (the viewer's right) at +X.
    const side = new THREE.Vector3(1, 0, 0).applyQuaternion(rotation), up = new THREE.Vector3(0, 1, 0).applyQuaternion(rotation);
    const forward = new THREE.Vector3(0, 0, 1).applyQuaternion(rotation);
    const at = (node: THREE.Object3D | null | undefined) => {
      if (!node) return undefined;
      node.updateWorldMatrix(true, false);
      return node.getWorldPosition(new THREE.Vector3());
    };
    // The character's left eye is on the viewer's right.
    const viewerRight = at(model.humanoid.getNormalizedBoneNode("leftEye")), viewerLeft = at(model.humanoid.getNormalizedBoneNode("rightEye"));
    let width = this.faceWidth, center: THREE.Vector3;
    if (viewerRight && viewerLeft && viewerRight.distanceTo(viewerLeft) > 1e-4) {
      width = Math.max(viewerRight.distanceTo(viewerLeft) * 2.3, width * 0.4);
      center = viewerRight.clone().add(viewerLeft).multiplyScalar(0.5);
    } else center = at(head)!.addScaledVector(up, 0.45 * width).addScaledVector(forward, 0.45 * width);
    const point = (x: number, y: number, z: number) => center.clone().addScaledVector(side, x * width)
      .addScaledVector(up, y * width).addScaledVector(forward, z * width);
    // The viewer's left cheek (the character's right) turns out toward -side, the viewer's right one toward +side.
    const tan = Math.tan(CHEEK_TURN), cos = Math.cos(CHEEK_TURN), sin = Math.sin(CHEEK_TURN);
    return { center, side, up, forward, width,
      eyeLeft: viewerLeft ?? point(-0.2, 0, 0), eyeRight: viewerRight ?? point(0.2, 0, 0),
      cheekLeft: point(-0.28, -0.22, 0.08), cheekRight: point(0.28, -0.22, 0.08),
      cheekLeftAcross: side.clone().addScaledVector(forward, tan), cheekRightAcross: side.clone().addScaledVector(forward, -tan),
      cheekLeftNormal: forward.clone().multiplyScalar(cos).addScaledVector(side, -sin),
      cheekRightNormal: forward.clone().multiplyScalar(cos).addScaledVector(side, sin),
      mouth: point(0, -0.42, 0.1), top: point(0, 0.75, -0.1) };
  }

  /** Uses eyes measured by vision (see EyeHint in eyes.ts) for what the model's eye bones and meshes can't give; undefined
   *  clears them. Returns where the eyes come from now. */
  setEyeHint(hint: EyeHint | undefined): EyesFrom {
    this.loaded();
    return this.eyes?.setHint(hint) ?? "estimate";
  }

  /** Where the eyes' irises and openings come from (see EyesFrom in eyes.ts). */
  get eyesFrom(): EyesFrom { return this.eyes?.from ?? "estimate"; }

  /** Each eye's iris, opening and middle for the face now (`face`, see faceGeometry) as `project` (world to canvas pixels)
   *  shows it, and where they came from (see VrmEyes.fields); eyes that can't be read now are only left out. */
  eyeFields(face: FaceFrame, project: (world: THREE.Vector3) => { x: number; y: number }): EyeFields {
    try { return this.eyes?.fields(face, project) ?? { eyesFrom: "estimate" }; }
    catch { return { eyesFrom: this.eyesFrom }; }
  }

  /** The playing gesture and how far into it, advanced by `deltaSeconds`; undefined once it is over. */
  private advanceGesture(deltaSeconds: number): { name: VrmGesture; t: number; weight: number } | undefined {
    const gesture = this.gesture;
    if (!gesture) return undefined;
    const t = gesture.seconds += deltaSeconds;
    const total = GESTURE_SECONDS[gesture.name];
    if (t >= total) { this.gesture = undefined; return undefined; }
    return { name: gesture.name, t, weight: envelope(t, total, GESTURE_FADE[gesture.name] ?? 0.35) };
  }

  private updateActions(model: VRM, deltaSeconds: number): void {
    const blush = this.blush;
    if (blush && !blush.hold && (blush.seconds += deltaSeconds) >= GESTURE_SECONDS[blush.level] - 0.6) this.endGesture(blush.level);
    const expressions = model.expressionManager;
    if (!expressions) return;
    for (const [name, state] of this.actions) {
      state.value += (state.target - state.value) * Math.min(1, deltaSeconds * 6);
      if (state.target === 0 && state.value < 0.002) {
        expressions.setValue(name, 0);
        this.actions.delete(name);
      } else expressions.setValue(name, state.value);
    }
  }

  private animateIdle(model: VRM, deltaSeconds: number): void {
    this.idleTime += deltaSeconds;
    const follow = Math.min(1, deltaSeconds * 5);
    this.look = { x: this.look.x + (this.lookTarget.x - this.look.x) * follow, y: this.look.y + (this.lookTarget.y - this.look.y) * follow };
    const bone = (name: Parameters<VRM["humanoid"]["getNormalizedBoneNode"]>[0]) => model.humanoid.getNormalizedBoneNode(name);
    const breath = Math.sin(this.idleTime * Math.PI * 2 / 4);
    const gesture = this.advanceGesture(deltaSeconds);
    const w = gesture?.weight ?? 0;
    const lerp = (from: number, to: number, amount: number) => from + (to - from) * amount;
    const wave = gesture?.name === "wave" ? w : 0, shrug = gesture?.name === "shrug" ? w : 0;
    const pose = this.advanceHeld(deltaSeconds, 0.6 * w);
    if (gesture) addPose(pose, gesturePose(gesture.name, gesture.t, w));
    this.face = pose.face;
    const open = pose.open, shoulders = 0.2 * shrug + 0.12 * pose.shoulders;
    const waving = wave > 0 ? 0.35 * Math.sin(2 * Math.PI * gesture!.t / 0.5) : 0;
    bone("leftUpperArm")?.rotation.set(0, 0, lerp(lerp(-1.2 + breath * 0.02, -0.95, shrug), -0.15, open));
    bone("rightUpperArm")?.rotation.set(0, 0, lerp(lerp(lerp(1.2 - breath * 0.02, 0.95, shrug), -0.25, wave), 0.15, open));
    bone("leftLowerArm")?.rotation.set(0, lerp(lerp(-0.15, -1.1, shrug), 0, open), 0);
    bone("rightLowerArm")?.rotation.set(0, lerp(lerp(lerp(0.15, 1.1, shrug), 0, wave), 0, open), lerp(0, -1.4 + waving, wave));
    bone("leftShoulder")?.rotation.set(0, 0, shoulders);
    bone("rightShoulder")?.rotation.set(0, 0, -shoulders);
    bone("chest")?.rotation.set(breath * 0.015, 0, 0);
    const side = gesture?.name === "sway" ? w * Math.sin(2 * Math.PI * gesture.t / 1.2) : 0;
    bone("spine")?.rotation.set((gesture?.name === "bow" ? 0.35 * w : 0) + pose.spineX, pose.spineY, 0.08 * side + pose.spineZ);
    const hips = bone("hips");
    if (hips && this.hipsRest !== undefined)
      hips.position.y = this.hipsRest + (gesture?.name === "bounce" ? 0.035 * w * Math.abs(Math.sin(2 * Math.PI * gesture.t / 0.6)) : 0);
    if (!this.selection?.head) {
      let gx = 0, gy = 0;
      if (gesture?.name === "nod") { const phase = Math.sin(Math.PI * gesture.t / 0.55); gy = -0.9 * phase * phase; }
      if (gesture?.name === "shake") gx = 0.8 * Math.sin(2 * Math.PI * gesture.t / 0.4) * Math.sin(Math.PI * gesture.t / 1.2);
      if (gesture?.name === "bow") gy = -0.5 * w;
      gx += pose.gx; gy += pose.gy;
      const tilt = (gesture?.name === "tilt" ? 0.3 * w : 0) + (gesture?.name === "shrug" ? 0.12 * w : 0) - 0.06 * side + pose.tilt;
      const x = this.look.x + gx, y = this.look.y + gy;
      bone("neck")?.rotation.set(-y * 0.15, x * 0.2, Math.sin(this.idleTime * 0.7) * 0.02);
      bone("head")?.rotation.set(-y * 0.2, x * 0.3, tilt);
    }
    // Only a held eyes gesture (eyes_up) turns the eyes while idle; the head keeps following the look.
    const range = eyeRange(model, this.inspected?.gaze), clamp = (value: number) => Math.max(-1, Math.min(1, value));
    for (const eye of ["leftEye", "rightEye"] as const) bone(eye)?.rotation.set(-range * clamp(pose.eyeY), range * clamp(pose.eyeX), 0);
    const expressions = model.expressionManager;
    if (!expressions) return;
    this.speechAge += deltaSeconds;
    const target = this.speechAge > 0.3 ? 0 : this.speechTarget;
    this.speech += (target - this.speech) * Math.min(1, deltaSeconds * (target > this.speech ? 30 : 14));
    // Fresh composed (Audio2Face) frames own the mouth; loudness resumes when they stop.
    const composing = this.identity !== undefined && this.composedAge < 0.25;
    // A held open mouth eases back while the voice moves the mouth, so lip-sync still shows on it.
    this.talk += ((composing || this.speechAge <= 0.3 ? 1 : 0) - this.talk) * Math.min(1, deltaSeconds * 5);
    const mouthOpen = Math.min(1, pose.mouth) * (1 - 0.7 * this.talk), shape = mouthShape(model);
    if (!composing && expressions.getExpression("aa")) expressions.setValue("aa", Math.min(1, this.speech + (shape === "aa" ? mouthOpen : 0)));
    if (shape === "oh" && !(this.identity !== undefined && this.selection?.mappings.some(m => m.expression === "oh")))
      expressions.setValue("oh", mouthOpen);
    const blinkOwned = this.identity !== undefined && (this.selection?.mappings.some(m => m.aspect === "blink") ?? false);
    if (blinkOwned || !expressions.getExpression("blink")) return;
    if (this.blinkTime < 0 && (this.nextBlink -= deltaSeconds) <= 0) this.blinkTime = 0;
    if (this.blinkTime >= 0) {
      this.blinkTime += deltaSeconds;
      const phase = this.blinkTime / 0.2;
      expressions.setValue("blink", phase < 0.5 ? phase * 2 : Math.max(0, 2 - phase * 2));
      if (phase >= 1) { this.blinkTime = -1; this.nextBlink = 2 + Math.random() * 4; }
    }
  }

  async load(buffer: ArrayBuffer): Promise<VrmCapabilities> {
    this.alive();
    requireValid(!this.pending, "A load is already in progress; wait for it before reloading.");
    this.pending = true;
    const generation = ++this.generation;
    try {
      const result = await loadLocalVrm(buffer);
      if (this.disposed || generation !== this.generation) {
        releaseResources(result.vrm.scene);
        throw new VrmError("load-canceled", "Load was canceled by disposal.");
      }
      this.releaseModel();
      this.model = result.vrm;
      this.inspected = result.capabilities;
      // A face is about a twelfth as wide as the figure is tall.
      const box = new THREE.Box3().setFromObject(result.vrm.scene);
      this.faceWidth = box.isEmpty() ? 0.14 : Math.max(0.01, 0.09 * (box.max.y - box.min.y));
      // Measured now, while the model is at rest; a model that breaks it only gets no eyes from its bones and meshes.
      try { this.eyes = new VrmEyes(result.vrm); } catch { this.eyes = undefined; }
      return result.capabilities;
    } finally { this.pending = false; }
  }

  configure(selection: Selection, revision: CompositionRevision, inputMode: InputMode = "composed"): void {
    const model = this.loaded();
    const capabilities = this.inspected!;
    requireValid(selection !== null && typeof selection === "object", "Selection required.");
    requireValid(revision !== null && typeof revision === "object", "Model/mapping revision binding is required.");
    identifier(revision.modelRevision, "model revision");
    identifier(revision.mappingRevision, "mapping revision");
    requireValid(inputMode === "composed" || inputMode === "coefficients", "Unknown input mode.");
    identifier(selection.faceSource, "face source");
    requireValid(["authored-explicit", "reduced-vowel-jaw-only"].includes(selection.faceMode), "Explicit face mapping mode required.");
    for (const key of ["gaze", "head", "secondaryMotion"] as const) requireValid(typeof selection[key] === "boolean", `${key} must be explicitly selected or omitted (false).`);
    requireValid(Array.isArray(selection.mappings) && selection.mappings.length <= 128, "At most 128 mappings accepted.");
    requireValid(!selection.gaze || capabilities.gaze !== "absent", "Gaze controls absent; author eyes/lookAt, choose a supported model, or explicitly omit gaze.");
    requireValid(!selection.head || capabilities.humanoidBones.includes("head"), "Head control absent.");
    requireValid(!selection.secondaryMotion || capabilities.secondaryMotion, "Secondary motion absent; author spring joints or omit this aspect.");
    const owned = new Set<string>();
    const ownedMorphs = new Set<string>();
    const ownedMaterials = new Set<string>();
    if (selection.gaze && capabilities.gaze === "expression")
      capabilities.expressions.filter(e => gazePresets.includes(e.name as typeof gazePresets[number]))
        .forEach(e => {
          e.morphTargets.forEach(t => ownedMorphs.add(t));
          e.materialTargets.forEach(t => ownedMaterials.add(t));
        });
    for (const mapping of selection.mappings) {
      identifier(mapping.channel, "coefficient channel");
      requireValid(["mouth", "expression", "blink"].includes(mapping.aspect), "Invalid mapping aspect.");
      const target = capabilities.expressions.find(e => e.name === mapping.expression && e.usable);
      requireValid(target, `Expression ${mapping.expression} is absent or has no usable morph binding; author it or omit this mapping.`);
      requireValid(!owned.has(target.name), `Duplicate expression writer: ${target.name}.`);
      requireValid(!gazePresets.includes(target.name as typeof gazePresets[number]), "Directional expressions belong exclusively to local gaze.");
      if (mouthPresets.includes(target.name as typeof mouthPresets[number])) requireValid(mapping.aspect === "mouth", "Vowel presets must have mouth ownership.");
      if (blinkPresets.includes(target.name as typeof blinkPresets[number])) requireValid(mapping.aspect === "blink", "Blink presets must have blink ownership.");
      if (selection.faceMode === "reduced-vowel-jaw-only") requireValid(mapping.aspect === "mouth", "Reduced mode accepts only explicitly mapped mouth controls; it is not detailed facial animation.");
      finite(mapping.minimum, 0, 1, "mapping minimum"); finite(mapping.maximum, 0, 1, "mapping maximum");
      requireValid(mapping.minimum < mapping.maximum, "Mapping range must increase within 0..1.");
      for (const morph of target.morphTargets) {
        requireValid(!ownedMorphs.has(morph), `Morph ${morph} has competing selected expression/gaze writers; select a non-overlapping set.`);
      }
      for (const material of target.materialTargets)
        requireValid(!ownedMaterials.has(material), `Material ${material} has competing selected expression/gaze writers.`);
      target.morphTargets.forEach(t => ownedMorphs.add(t));
      target.materialTargets.forEach(t => ownedMaterials.add(t));
      owned.add(target.name);
    }
    // Validate everything before changing an active turn.
    this.clearControls();
    this.selection = structuredClone(selection);
    this.revision = { ...revision };
    this.inputMode = inputMode;
    const expressions = model.expressionManager;
    if (expressions) {
      expressions.mouthExpressionNames = [...new Set([...mouthPresets, ...selection.mappings.filter(m => m.aspect === "mouth").map(m => m.expression)])];
      expressions.blinkExpressionNames = [...new Set([...blinkPresets, ...selection.mappings.filter(m => m.aspect === "blink").map(m => m.expression)])];
    }
  }

  reset(identity: PlaybackIdentity): void {
    this.loaded();
    validateIdentity(identity);
    requireValid(this.selection, "Configure aspect ownership before reset.");
    requireValid(identity.sourceId === this.selection.faceSource, "Identity source differs from configured face owner.");
    this.clearControls();
    this.identity = identityKey(identity);
  }

  /** Host must pass device-rendered playback samples, never submitted/queued/software-consumed samples. */
  applyFrame(input: CoefficientInput, actualPlaybackSampleOffset: number): void {
    const model = this.loaded();
    this.validateFrame(input, actualPlaybackSampleOffset);
    requireValid(this.inputMode === "coefficients", "Coefficient input is disabled in composed input mode.");
    this.validateRevision(input);
    const selection = this.selection!;
    this.validateValues(input.coefficients, new Set(selection.mappings.map(m => m.channel)));
    for (const mapping of selection.mappings)
      model.expressionManager!.setValue(mapping.expression, mapping.minimum + input.coefficients[mapping.channel]! * (mapping.maximum - mapping.minimum));
    this.acceptFrame(input, actualPlaybackSampleOffset);
  }

  /** Shared host has already performed mapping: apply exact authored targets once, without affine conversion. */
  applyComposedParameters(input: ComposedParameterInput, actualPlaybackSampleOffset: number): void {
    const model = this.loaded();
    this.validateFrame(input, actualPlaybackSampleOffset);
    requireValid(this.inputMode === "composed", "Composed input is disabled in coefficient input mode.");
    this.validateRevision(input);
    this.validateValues(input.parameters, new Set(this.selection!.mappings.map(m => m.expression)));
    for (const [target, value] of Object.entries(input.parameters)) model.expressionManager!.setValue(target, value);
    this.acceptFrame(input, actualPlaybackSampleOffset);
  }

  private validateRevision(input: CompositionRevision): void {
    requireValid(this.revision && input.modelRevision === this.revision.modelRevision && input.mappingRevision === this.revision.mappingRevision,
      "Input requires the current bound model/mapping revision.");
  }
  private validateFrame(input: Pick<CoefficientInput, "identity" | "sequence" | "sampleOffset">, actualPlaybackSampleOffset: number): void {
    requireValid(this.selection && this.identity, "Configure and reset a trusted playback identity before applying frames.");
    validateIdentity(input.identity);
    requireValid(identityKey(input.identity) === this.identity, "Stale or foreign playback identity.");
    integer(input.sequence, 0, 2147483647, "sequence");
    requireValid(input.sequence > this.sequence, "Frame sequence must strictly increase.");
    integer(input.sampleOffset, 0, Number.MAX_SAFE_INTEGER, "sampleOffset");
    integer(actualPlaybackSampleOffset, 0, Number.MAX_SAFE_INTEGER, "actualPlaybackSampleOffset");
    requireValid(input.sampleOffset >= this.sampleOffset && actualPlaybackSampleOffset >= this.playbackOffset, "Sample clocks must not rewind without explicit reset.");
    requireValid(input.sampleOffset <= actualPlaybackSampleOffset
      && actualPlaybackSampleOffset - input.sampleOffset <= input.identity.sampleRate / 4, "Frame is future or more than 250ms late; schedule against device-rendered PCM.");
  }
  private validateValues(values: Readonly<Record<string, number>>, expected: ReadonlySet<string>): void {
    requireValid(values !== null && typeof values === "object" && !Array.isArray(values), "Coefficients/parameters must be a record.");
    const entries = Object.entries(values);
    requireValid(entries.length <= 128, "Too many coefficients.");
    requireValid(entries.length === expected.size && entries.every(([key]) => expected.has(key)),
      "Pass exactly the explicitly selected channels; unmapped inputs must be resolved/omitted by the host compatibility gate.");
    entries.forEach(([key, value]) => finite(value, 0, 1, key));
  }
  private acceptFrame(input: Pick<CoefficientInput, "sequence" | "sampleOffset">, actualPlaybackSampleOffset: number): void {
    this.sequence = input.sequence;
    this.sampleOffset = input.sampleOffset;
    this.playbackOffset = actualPlaybackSampleOffset;
    this.composedAge = 0;
  }

  /** Local procedural controls only; not an A2F/AvatarFrame pose protocol. */
  setPose(pose: LocalPose): void {
    this.loaded();
    requireValid(this.selection && this.identity, "Configure/reset before setting local pose.");
    requireValid(pose !== null && typeof pose === "object", "Local pose required.");
    if (pose.gaze !== undefined) {
      requireValid(this.selection.gaze && Array.isArray(pose.gaze) && pose.gaze.length === 3, "Gaze not selected or invalid world-space target.");
      pose.gaze.forEach(v => finite(v, -100, 100, "gaze"));
    }
    if (pose.head !== undefined) {
      requireValid(this.selection.head && Array.isArray(pose.head) && pose.head.length === 4, "Head not selected or invalid normalized quaternion.");
      pose.head.forEach(v => finite(v, -1, 1, "head"));
      requireValid(Math.abs(Math.hypot(...pose.head) - 1) < 0.001, "Head quaternion must be normalized.");
    }
    this.pose = structuredClone(pose);
  }

  update(deltaSeconds: number): void {
    const model = this.loaded();
    finite(deltaSeconds, 0, 0.1, "deltaSeconds");
    this.composedAge += deltaSeconds;
    if (this.identity && this.selection?.head) {
      model.humanoid.getNormalizedBoneNode("head")!.quaternion.fromArray(this.pose.head ?? [0, 0, 0, 1]);
    }
    this.restoreFace(model);
    if (this.idle) this.animateIdle(model, deltaSeconds); else this.face = {};
    this.updateActions(model, deltaSeconds);
    this.applyFace(model);
    model.humanoid.update();
    const neutralEyes: { node: THREE.Object3D; rotation: THREE.Quaternion }[] = [];
    if (model.lookAt && this.identity && this.selection?.gaze) {
      if (this.inspected?.gaze === "bone") {
        model.lookAt.reset();
        model.lookAt.update(0);
        for (const name of ["leftEye", "rightEye"] as const) {
          const node = model.humanoid.getRawBoneNode(name)!;
          neutralEyes.push({ node, rotation: node.quaternion.clone() });
        }
      }
      if (this.pose.gaze) {
        this.gazeTarget.position.fromArray(this.pose.gaze);
        model.lookAt.lookAt(this.gazeTarget.position);
      } else model.lookAt.reset();
      model.lookAt.update(deltaSeconds);
    }
    // The official manager applies overrideMouth/Blink/LookAt, including custom classifications.
    model.expressionManager?.update();
    if (model.lookAt && this.identity && this.selection?.gaze && this.inspected?.gaze === "bone") {
      let multiplier = 1;
      for (const expression of model.expressionManager?.expressions ?? [])
        multiplier -= expression.overrideLookAtAmount;
      // Blend the mapped output, not the input angles: authored range maps may saturate.
      for (const eye of neutralEyes)
        eye.node.quaternion.slerpQuaternions(eye.rotation, eye.node.quaternion.clone(), Math.max(0, multiplier));
    }
    model.nodeConstraintManager?.update();
    if ((this.idle || this.identity && this.selection?.secondaryMotion) && deltaSeconds > 0) model.springBoneManager?.update(deltaSeconds);
    for (const material of model.materials ?? []) {
      if ("update" in material && typeof material.update === "function") material.update(deltaSeconds);
    }
  }

  stop(): void { this.loaded(); this.clearControls(); }

  /** What of the posed model a ray hits first (see touch.ts), or undefined when it misses. */
  hitTestRay(raycaster: THREE.Raycaster): VrmHit | undefined {
    const model = this.loaded();
    const humanoid = new Map<THREE.Object3D, string>();
    for (const [name, bone] of Object.entries(model.humanoid.rawHumanBones)) if (bone?.node) humanoid.set(bone.node, name);
    const springs = new Set<THREE.Object3D>([...model.springBoneManager?.joints ?? []].map(joint => joint.bone));
    return hitTestVrm(model.scene, humanoid, raycaster, springs);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    ++this.generation;
    this.releaseModel();
  }

  private alive(): void { requireValid(!this.disposed, "VRM runtime is disposed; create a new instance."); }
  private loaded(): VRM { this.alive(); requireValid(this.model, "Load a VRM before using the runtime."); return this.model; }
  private clearControls(): void {
    this.identity = undefined; this.sequence = -1; this.sampleOffset = -1; this.playbackOffset = -1; this.pose = {};
    this.composedAge = Number.POSITIVE_INFINITY;
    this.faceRestore.clear(); this.face = {};
    if (this.model) {
      this.model.humanoid.resetNormalizedPose();
      this.model.humanoid.update();
      this.model.lookAt?.reset();
      this.model.lookAt?.update(0);
      this.model.expressionManager?.resetValues();
      this.model.expressionManager?.update();
      this.model.springBoneManager?.reset();
    }
  }
  private releaseModel(): void {
    this.clearControls();
    if (this.model) {
      this.model.scene.removeFromParent();
      releaseResources(this.model.scene);
    }
    this.model = undefined; this.inspected = undefined; this.selection = undefined; this.revision = undefined; this.inputMode = undefined;
    this.eyes = undefined;
    this.actions.clear(); this.heldExpressions.clear(); this.gesture = undefined; this.held = []; this.blush = undefined; this.hipsRest = undefined;
    this.talk = 0;
  }
}
