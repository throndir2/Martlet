import * as THREE from "three";
import { VRM } from "@pixiv/three-vrm";
import { type VrmCapabilities } from "./inspect.js";

// Martlet's own gestures on a VRM: the gesture lists, the bones and face parts each one moves, and the poses they make over time.

export type BoneName = Parameters<VRM["humanoid"]["getNormalizedBoneNode"]>[0];

/** Martlet's own gestures, played on any VRM that has the humanoid bones they move. After the reply gestures come the voice
 *  emotes, played when the voice makes their sound or tone (laugh, sigh, gasp...), then the touch and mood gestures (wink,
 *  pout, shy...), which also use the model's preset expressions (blinkLeft, relaxed, happy...) when it has them, then the
 *  held face parts (eyes_up turns the eye bones, mouth_open the oh or aa mouth expression), and last the stronger blush
 *  levels (see VRM_BLUSH_LEVELS). */
export const VRM_GESTURES = Object.freeze(["nod", "shake", "tilt", "bow", "sway", "wave", "shrug", "bounce", "blush",
  "laugh", "chuckle", "sigh", "gasp", "cough", "clear_throat", "groan", "sniff", "shush", "inhale", "exhale", "mumble", "hum",
  "sneeze", "whistle", "happy", "sarcastic", "angry", "fear", "crying", "whispering", "dramatic", "expressive",
  "wink", "pout", "shy", "giggle", "flinch", "lean_in", "look_away", "think", "eye_roll", "drowsy",
  "eyes_up", "mouth_open", "blush_deep", "blush_fierce"] as const);
export type VrmGesture = typeof VRM_GESTURES[number];
/** The gestures that can be held (an action with `hold: true`): eased into and kept, gently alive, until ended. The model's
 *  own blush can be held too, at every blush level (see `playGesture`). */
export const VRM_HOLDABLE_GESTURES = Object.freeze(["pout", "shy", "look_away", "drowsy", "eyes_up", "mouth_open"] as const);
export type HoldableGesture = typeof VRM_HOLDABLE_GESTURES[number];
export const holdable = (name: string): name is HoldableGesture => (VRM_HOLDABLE_GESTURES as readonly string[]).includes(name);
/** Martlet's blush levels, faintest first. Each shows the model's own blush or cheek expression fully (for 4 seconds, or
 *  held); the renderer page draws the stronger ones over it, and draws every level on a model without one. */
export const VRM_BLUSH_LEVELS = Object.freeze(["blush", "blush_deep", "blush_fierce"] as const);
export type BlushLevel = typeof VRM_BLUSH_LEVELS[number];
export const blushLevel = (name: string): name is BlushLevel => (VRM_BLUSH_LEVELS as readonly string[]).includes(name);
/** A part of the face or body a held gesture moves. */
export type VrmGesturePart = "eyes" | "mouth" | "cheeks" | "brows" | "head";
/** The parts each held gesture moves (the head part includes the body; the model's own blush, at every level, is the
 *  cheeks). Held gestures layer: holding one lets go only of the held gestures that move a part it moves too. */
export const VRM_HOLD_PARTS: Readonly<Record<HoldableGesture | BlushLevel, readonly VrmGesturePart[]>> = Object.freeze({
  pout: ["mouth", "brows", "head"], shy: ["eyes", "mouth", "head"], look_away: ["eyes", "head"], drowsy: ["eyes", "head"],
  blush: ["cheeks"], eyes_up: ["eyes"], mouth_open: ["mouth"], blush_deep: ["cheeks"], blush_fierce: ["cheeks"],
});
export const sharePart = (a: HoldableGesture | BlushLevel, b: HoldableGesture | BlushLevel) =>
  VRM_HOLD_PARTS[a].some(part => VRM_HOLD_PARTS[b].includes(part));
export const head: readonly BoneName[] = ["head"], headSpine: readonly BoneName[] = ["head", "spine"];
export const GESTURE_BONES: Readonly<Record<VrmGesture, readonly BoneName[]>> = Object.freeze({
  nod: head, shake: head, tilt: head, bow: ["spine"], sway: ["spine"],
  wave: ["rightUpperArm", "rightLowerArm"], shrug: ["leftUpperArm", "rightUpperArm", "leftLowerArm", "rightLowerArm"], bounce: ["hips"],
  // An authored blush or cheek expression when the model has one; otherwise the page draws a glow on the cheeks, found
  // from the head (see faceAnchor). So do the stronger blush levels.
  blush: head, blush_deep: head, blush_fierce: head,
  laugh: headSpine, chuckle: head, sigh: headSpine, gasp: headSpine, cough: headSpine, clear_throat: head, groan: head, sniff: head,
  shush: headSpine, inhale: headSpine, exhale: headSpine, mumble: head, hum: head, sneeze: headSpine, whistle: head,
  happy: headSpine, sarcastic: head, angry: headSpine, fear: headSpine, crying: headSpine, whispering: headSpine,
  dramatic: ["head", "leftUpperArm", "rightUpperArm"], expressive: headSpine,
  wink: head, pout: head, shy: headSpine, giggle: headSpine, flinch: headSpine, lean_in: headSpine, look_away: head, think: head,
  eye_roll: head, drowsy: headSpine,
  // The eye bones turn up; the mouth needs the oh or aa expression (see gestures).
  eyes_up: ["leftEye", "rightEye"], mouth_open: head,
});
export const GESTURE_SECONDS: Readonly<Record<VrmGesture, number>> = Object.freeze({
  nod: 1.1, shake: 1.2, tilt: 1.8, bow: 2, sway: 2.4, wave: 2.4, shrug: 1.8, bounce: 1.2, blush: 4,
  laugh: 2, chuckle: 1.4, sigh: 2.4, gasp: 1.6, cough: 1.5, clear_throat: 1.2, groan: 2.2, sniff: 1, shush: 2, inhale: 1.6,
  exhale: 1.8, mumble: 2, hum: 2.6, sneeze: 1.6, whistle: 2, happy: 2.4, sarcastic: 1.8, angry: 2.4, fear: 2, crying: 3,
  whispering: 2.2, dramatic: 2.4, expressive: 2.2,
  wink: 1.1, pout: 2.6, shy: 3.5, giggle: 1.6, flinch: 1.4, lean_in: 2.8, look_away: 2.4, think: 2.8, eye_roll: 1.8, drowsy: 4.5,
  eyes_up: 3, mouth_open: 2.6, blush_deep: 4, blush_fierce: 4,
});
export const GESTURE_FADE: Partial<Record<VrmGesture, number>> = { bounce: 0.15, gasp: 0.12, fear: 0.15, cough: 0.1, sniff: 0.1, sneeze: 0.1,
  wink: 0.12, giggle: 0.15, flinch: 0.05, eye_roll: 0.2 };
/** A custom expression that is the model's own blush. */
export const BLUSH_EXPRESSION = /blush|cheek|照れ|赤面|頬|脸红|臉紅|홍조/i;

/** The mouth expression a held open mouth uses: `oh` (a round open mouth), else `aa`; undefined when the model has neither. */
export function mouthShape(model: VRM): "oh" | "aa" | undefined {
  const expressions = model.expressionManager;
  return expressions?.getExpression("oh") ? "oh" : expressions?.getExpression("aa") ? "aa" : undefined;
}

/** How long the voice keeps the character's mouth after its last sound, in seconds, so the pauses between its words and
 *  sentences don't give the mouth back to the emotes. */
export const VOICE_HOLD_SECONDS = 1;

/** Who moves the mouth now (VrmRuntime.mouthReading). */
export interface VrmMouthReading {
  /** 0 to 1: how much the voice has the mouth now; it eases in when the voice starts and out after VOICE_HOLD_SECONDS. */
  readonly voice: number;
  /** The voice (a lip-sync level or a composed frame) moved the mouth within the last VOICE_HOLD_SECONDS. */
  readonly speaking: boolean;
  /** The voice's loudness on the mouth now (0 to 1). */
  readonly level: number;
  /** How far a held open mouth (mouth_open) opens the mouth before the voice takes it (0 to 1). */
  readonly emote: number;
  /** How far the mouth expressions (aa, ih, ou, ee, oh) open the mouth now, after what blocks them (0 to 1). */
  readonly open: number;
  /** 0 to 1: how much the expressions showing block the mouth expressions now (their overrideMouth). */
  readonly blocked: number;
}

/** How far the eyes turn (radians) at a held eyes gesture's full strength: the model's own look-up range for its eye bones
 *  when it has one (5° to 20°), else 12°. */
export function eyeRange(model: VRM, gaze: VrmCapabilities["gaze"] | undefined): number {
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

export const cycle = (t: number, period: number) => Math.sin(2 * Math.PI * t / period);
export const smoothstep = (x: number) => { const c = Math.max(0, Math.min(1, x)); return c * c * (3 - 2 * c); };

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

/** Seconds one calm breath takes at rest (about 14 breaths a minute); it changes a little over time. */
export const BREATH_SECONDS = 4.2;

/** How full the chest is (0 breathed out, 1 breathed in) at `phase` (one breath for each unit): a quicker breath in (38% of
 *  the breath), a slower breath out (50%) and a short rest before the next one. */
export function breathing(phase: number): number {
  const p = phase - Math.floor(phase);
  return p < 0.38 ? smoothstep(p / 0.38) : p < 0.88 ? 1 - smoothstep((p - 0.38) / 0.5) : 0;
}

/** How the left arm hangs at rest (the right one mirrors it), as XYZ Euler angles in radians on the normalized humanoid
 *  bones. They turn from the VRM T-pose, where the arm, hand and four fingers point along +X with the palm down and the
 *  thumb points 45 degrees forward: the arm hangs about 15 degrees out from the body and a little forward, the elbow
 *  softly bent, the wrist turned a little toward the thigh. */
export const VRM_RELAXED_ARM = Object.freeze({ upperArm: [-0.08, 0, -1.3], lowerArm: [0, -0.3, 0], hand: [0, 0, -0.12] } as const);

/** How the left hand's fingers rest (the right ones mirror them), as XYZ Euler angles in radians: each finger curls in a
 *  little more than the one before it toward the little finger, and the thumb lies in, toward the index finger. */
export const VRM_RELAXED_FINGERS: readonly (readonly [string, number, number, number])[] = Object.freeze([
  ["ThumbMetacarpal", 0.3, 0.15, -0.3], ["ThumbProximal", 0, 0.3, 0], ["ThumbDistal", 0, 0.25, 0],
  ["IndexProximal", 0, 0, -0.25], ["IndexIntermediate", 0, 0, -0.35], ["IndexDistal", 0, 0, -0.25],
  ["MiddleProximal", 0, 0, -0.3], ["MiddleIntermediate", 0, 0, -0.45], ["MiddleDistal", 0, 0, -0.3],
  ["RingProximal", 0, 0, -0.35], ["RingIntermediate", 0, 0, -0.5], ["RingDistal", 0, 0, -0.3],
  ["LittleProximal", 0, 0, -0.4], ["LittleIntermediate", 0, 0, -0.55], ["LittleDistal", 0, 0, -0.3],
] as const);

/** What the idle body does now (see `VrmRuntime.idleReading`). */
export interface VrmIdleReading {
  readonly idle: boolean;
  /** How far into the breath (0 to 1), how full the chest is (0 to 1) and the breaths a minute now. */
  readonly breathing: { readonly phase: number; readonly inhale: number; readonly perMinute: number };
  /** Each arm's angle from straight down and its elbow's bend, in degrees, measured on the posed bones. */
  readonly arms: { readonly left?: VrmArmReading; readonly right?: VrmArmReading };
  /** How far each hand's middle finger curls, in degrees (0 is straight, as in the T-pose). */
  readonly curl: { readonly left?: number; readonly right?: number };
  /** The spine's slow sideways lean now, in degrees. */
  readonly sway: number;
}
export interface VrmArmReading { readonly fromDown: number; readonly elbow: number }

/** A holdable gesture's pose at weight `w`, kept alive by `t` (pout huffs, shy peeks back, look_away glances back, drowsy
 *  nods off and catches itself, raised eyes drift, an open mouth breathes), so a held pose never looks frozen. */
export function moodPose(pose: GesturePose, name: HoldableGesture, t: number, w: number): void {
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

export const emptyPose = (): GesturePose => ({ gx: 0, gy: 0, tilt: 0, spineX: 0, spineZ: 0, shoulders: 0, open: 0, spineY: 0, eyeX: 0, eyeY: 0,
  mouth: 0, face: {} });

export function addPose(into: GesturePose, pose: GesturePose): void {
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
    case "expressive": {
      const bob = Math.abs(cycle(t, 0.55));
      pose.gx = 0.12 * cycle(t, 1.1) * w; pose.gy = (0.3 - 0.45 * bob) * w; pose.tilt = 0.12 * cycle(t, 1.1) * w;
      pose.spineX = 0.05 * bob * w; pose.shoulders = 0.3 * bob * w; pose.face = { happy: 0.4 * w }; break;
    }
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
export function envelope(seconds: number, total: number, fade: number): number {
  const x = Math.max(0, Math.min(1, Math.min(seconds, total - seconds) / fade));
  return x * x * (3 - 2 * x);
}
