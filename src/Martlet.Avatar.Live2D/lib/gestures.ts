/** Martlet's own gestures, played on any Live2D model that has the standard Cubism parameters they move: look offsets
 *  (x right, y up) added to the head direction and additive offsets to standard parameters. After the reply gestures come
 *  the voice emotes, played when the voice makes their sound or tone (laugh, sigh, gasp...), then the touch and mood
 *  gestures (wink, pout, shy...). */
export const GESTURES = Object.freeze(["nod", "shake", "tilt", "bow", "sway", "smile", "blush", "surprise",
  "laugh", "chuckle", "sigh", "gasp", "cough", "clear_throat", "groan", "sniff", "shush", "inhale", "exhale", "mumble", "hum",
  "sneeze", "whistle", "happy", "sarcastic", "angry", "fear", "crying", "whispering", "dramatic",
  "wink", "pout", "shy", "giggle", "flinch", "lean_in", "look_away", "think", "eye_roll", "drowsy"] as const);
export type Gesture = typeof GESTURES[number];

/** The gestures that can be held (a renderer action with `hold: true`): eased into and kept, gently alive, until ended. */
export const HOLDABLE_GESTURES = Object.freeze(["pout", "shy", "look_away", "drowsy"] as const);
export type HoldableGesture = typeof HOLDABLE_GESTURES[number];
export const isHoldable = (name: string): name is HoldableGesture => (HOLDABLE_GESTURES as readonly string[]).includes(name);

export interface GestureFrame {
  readonly look: { readonly x: number; readonly y: number };
  readonly parameters: Readonly<Record<string, number>>;
}

const angleX = ["ParamAngleX"], angleY = ["ParamAngleY"], angleZ = ["ParamAngleZ"];

/** The standard parameters each gesture needs; a model missing any of them doesn't get it. */
export const GESTURE_REQUIREMENTS: Readonly<Record<Gesture, readonly string[]>> = Object.freeze({
  nod: angleY,
  shake: angleX,
  tilt: angleZ,
  bow: angleY,
  sway: ["ParamBodyAngleZ"],
  smile: ["ParamEyeLSmile", "ParamEyeRSmile"],
  blush: ["ParamCheek"],
  surprise: ["ParamBrowLY", "ParamBrowRY"],
  laugh: angleY, chuckle: angleY, sigh: angleY, gasp: angleY, cough: angleY, clear_throat: angleY, groan: angleZ, sniff: angleY,
  shush: angleY, inhale: angleY, exhale: angleY, mumble: angleX, hum: angleZ, sneeze: angleY, whistle: angleZ,
  happy: ["ParamEyeLSmile", "ParamEyeRSmile"], sarcastic: angleZ, angry: ["ParamBrowLY", "ParamBrowRY"], fear: angleX,
  crying: angleY, whispering: angleZ, dramatic: angleZ,
  wink: ["ParamEyeLOpen"], pout: ["ParamMouthForm"], shy: ["ParamAngleX", "ParamAngleY"], giggle: angleY, flinch: angleY,
  lean_in: angleZ, look_away: angleX, think: angleY, eye_roll: ["ParamEyeBallX", "ParamEyeBallY"],
  drowsy: ["ParamEyeLOpen", "ParamEyeROpen"],
});

const DURATION: Readonly<Record<Gesture, number>> = Object.freeze({
  nod: 1.1, shake: 1.2, tilt: 1.8, bow: 2, sway: 2.4, smile: 3.5, blush: 4, surprise: 2,
  laugh: 2, chuckle: 1.4, sigh: 2.4, gasp: 1.6, cough: 1.5, clear_throat: 1.2, groan: 2.2, sniff: 1, shush: 2, inhale: 1.6,
  exhale: 1.8, mumble: 2, hum: 2.6, sneeze: 1.6, whistle: 2, happy: 2.4, sarcastic: 1.8, angry: 2.4, fear: 2, crying: 3,
  whispering: 2.2, dramatic: 2.4,
  wink: 1.1, pout: 2.6, shy: 3.5, giggle: 1.6, flinch: 1.4, lean_in: 2.8, look_away: 2.4, think: 2.8, eye_roll: 1.8, drowsy: 4.5,
});

export function isGesture(name: string): name is Gesture {
  return (GESTURES as readonly string[]).includes(name);
}

/** The gestures a model with `parameterIds` can play. */
export function supportedGestures(parameterIds: Iterable<string>): readonly Gesture[] {
  const present = new Set(parameterIds);
  return GESTURES.filter(name => GESTURE_REQUIREMENTS[name].every(id => present.has(id)));
}

const smooth = (x: number) => { const c = Math.max(0, Math.min(1, x)); return c * c * (3 - 2 * c); };

// 0 to 1 over `fade` seconds, held, then back to 0 by `total` (over `fadeOut` seconds when given).
function envelope(seconds: number, total: number, fade: number, fadeOut = fade): number {
  return Math.min(smooth(seconds / fade), smooth((total - seconds) / fadeOut));
}

/** How far back (positive) or forward (negative) the head is during a sneeze: back as it builds, snapping forward, recovering. */
export function sneezeLift(t: number): number {
  if (t < 0.7) return 0.4 * smooth(t / 0.7);
  if (t < 0.85) return 0.4 - 1.2 * smooth((t - 0.7) / 0.15);
  return -0.8 * (1 - smooth((t - 0.85) / 0.75));
}

const none = Object.freeze({});
const wave = (t: number, period: number) => Math.sin(2 * Math.PI * t / period);
const eyes = (open: number) => ({ ParamEyeLOpen: open, ParamEyeROpen: open });
const brows = (y: number) => ({ ParamBrowLY: y, ParamBrowRY: y });
const eyeSmile = (amount: number) => ({ ParamEyeLSmile: amount, ParamEyeRSmile: amount, ParamMouthForm: amount });

/** The gesture's frame `seconds` into it, or undefined once it is over. */
export function gestureFrame(name: Gesture, seconds: number): GestureFrame | undefined {
  const total = DURATION[name];
  if (seconds >= total) return undefined;
  const t = seconds;
  switch (name) {
    case "nod": {
      const phase = Math.sin(Math.PI * t / 0.55);
      return { look: { x: 0, y: -0.65 * phase * phase }, parameters: none };
    }
    case "shake":
      return { look: { x: 0.6 * Math.sin(2 * Math.PI * t / 0.4) * Math.sin(Math.PI * t / 1.2), y: 0 }, parameters: none };
    case "tilt":
      return { look: { x: 0, y: 0 }, parameters: { ParamAngleZ: 18 * envelope(t, total, 0.35) } };
    case "bow": {
      const amount = envelope(t, total, 0.5);
      return { look: { x: 0, y: -0.9 * amount }, parameters: { ParamBodyAngleY: -6 * amount } };
    }
    case "sway": {
      const side = Math.sin(2 * Math.PI * t / 1.2) * envelope(t, total, 0.4);
      return { look: { x: 0, y: 0 }, parameters: { ParamBodyAngleZ: 8 * side, ParamAngleZ: 6 * side } };
    }
    case "smile": {
      const amount = envelope(t, total, 0.4);
      return { look: { x: 0, y: 0 }, parameters: { ParamEyeLSmile: amount, ParamEyeRSmile: amount, ParamMouthForm: amount } };
    }
    case "blush":
      return { look: { x: 0, y: 0 }, parameters: { ParamCheek: envelope(t, total, 0.6) } };
    case "surprise": {
      const amount = envelope(t, total, 0.15);
      return { look: { x: 0, y: 0.1 * amount }, parameters: {
        ParamBrowLY: 0.8 * amount, ParamBrowRY: 0.8 * amount, ParamEyeLOpen: 0.3 * amount, ParamEyeROpen: 0.3 * amount,
      } };
    }
    case "laugh": {
      const amount = envelope(t, total, 0.2, 0.4), bob = Math.abs(wave(t, 0.64));
      return { look: { x: 0, y: (0.2 - 0.3 * bob) * amount }, parameters: {
        ...eyeSmile(amount), ParamBodyAngleY: 3 * bob * amount, ParamAngleZ: 4 * wave(t, 1) * amount,
      } };
    }
    case "chuckle": {
      const amount = envelope(t, total, 0.2, 0.3);
      return { look: { x: 0, y: -0.15 * Math.abs(wave(t, 0.7)) * amount }, parameters: eyeSmile(0.6 * amount) };
    }
    case "sigh": {
      const amount = envelope(t, total, 0.6);
      return { look: { x: 0, y: -0.45 * amount }, parameters: { ...eyes(-0.4 * amount), ParamBodyAngleY: -3 * amount } };
    }
    case "gasp": {
      const amount = envelope(t, total, 0.12, 0.6);
      return { look: { x: 0, y: 0.35 * amount }, parameters: { ...brows(0.8 * amount), ...eyes(0.35 * amount) } };
    }
    case "cough": {
      const jerk = t < 1.2 ? Math.max(0, wave(t, 0.4)) ** 3 : 0;
      return { look: { x: 0, y: -0.5 * jerk }, parameters: { ...eyes(-0.5 * jerk), ParamBodyAngleY: -4 * jerk } };
    }
    case "clear_throat": {
      const amount = envelope(t, total, 0.25);
      return { look: { x: 0.25 * amount, y: -0.25 * amount }, parameters: eyes(-0.2 * amount) };
    }
    case "groan": {
      const amount = envelope(t, total, 0.4);
      return { look: { x: 0, y: 0.3 * amount }, parameters: { ParamAngleZ: -10 * amount, ...eyes(-0.6 * amount), ...brows(-0.3 * amount) } };
    }
    case "sniff":
      return { look: { x: 0, y: 0.15 * (t < 0.6 ? Math.max(0, wave(t, 0.3)) : 0) }, parameters: none };
    case "shush": {
      const amount = envelope(t, total, 0.3);
      return { look: { x: 0, y: -0.3 * amount }, parameters: { ParamAngleZ: 6 * amount, ...eyes(-0.35 * amount), ParamBodyAngleY: 2 * amount } };
    }
    case "inhale": {
      const amount = envelope(t, total, 0.5);
      return { look: { x: 0, y: 0.25 * amount }, parameters: { ...brows(0.2 * amount), ParamBreath: 0.8 * amount, ParamBodyAngleY: 2 * amount } };
    }
    case "exhale": {
      const amount = envelope(t, total, 0.5);
      return { look: { x: 0, y: -0.3 * amount }, parameters: { ...eyes(-0.3 * amount), ParamBodyAngleY: -2 * amount } };
    }
    case "mumble": {
      const amount = envelope(t, total, 0.35);
      return { look: { x: -0.35 * amount, y: -0.35 * amount }, parameters: eyes(-0.2 * amount) };
    }
    case "hum": {
      const amount = envelope(t, total, 0.5);
      return { look: { x: 0, y: 0 }, parameters: { ParamAngleZ: 8 * wave(t, 1.3) * amount, ...eyes(-0.5 * amount) } };
    }
    case "sneeze": {
      const closed = t < 0.5 ? 0.5 * smooth(t / 0.5) : t < 1.1 ? 1 : smooth((total - t) / 0.5);
      return { look: { x: 0, y: sneezeLift(t) }, parameters: eyes(-0.8 * closed) };
    }
    case "whistle": {
      const amount = envelope(t, total, 0.35);
      return { look: { x: 0.3 * amount, y: 0.35 * amount }, parameters: { ParamAngleZ: 8 * amount, ParamMouthForm: -0.5 * amount } };
    }
    case "happy": {
      const amount = envelope(t, total, 0.3);
      return { look: { x: 0, y: 0.1 * wave(t, 0.6) * amount }, parameters: { ...eyeSmile(amount), ParamAngleZ: 6 * wave(t, 1.2) * amount } };
    }
    case "sarcastic": {
      const amount = envelope(t, total, 0.3);
      return { look: { x: 0, y: 0.15 * amount }, parameters: {
        ParamEyeBallY: 0.9 * amount, ParamEyeBallX: -0.6 * Math.cos(Math.PI * t / total) * amount, ParamAngleZ: 8 * amount,
        ...eyes(-0.3 * amount),
      } };
    }
    case "angry": {
      const amount = envelope(t, total, 0.25);
      return { look: { x: 0, y: -0.2 * amount }, parameters: {
        ...brows(-0.8 * amount), ParamBrowLForm: -amount, ParamBrowRForm: -amount, ParamMouthForm: -0.8 * amount, ...eyes(-0.2 * amount),
      } };
    }
    case "fear": {
      const amount = envelope(t, total, 0.15, 0.5);
      return { look: { x: 0.08 * wave(t, 0.12) * amount, y: 0.15 * amount }, parameters: { ...brows(0.6 * amount), ...eyes(0.3 * amount) } };
    }
    case "crying": {
      const amount = envelope(t, total, 0.5), sob = Math.max(0, wave(t, 0.7));
      return { look: { x: 0, y: -(0.5 + 0.1 * sob) * amount }, parameters: {
        ...eyes(-0.7 * amount), ...brows(0.4 * amount), ParamMouthForm: -0.7 * amount, ParamBodyAngleY: -2 * sob * amount,
      } };
    }
    case "whispering": {
      const amount = envelope(t, total, 0.35);
      return { look: { x: 0.3 * amount, y: -0.15 * amount }, parameters: { ParamAngleZ: 12 * amount, ParamBodyAngleZ: 5 * amount, ...eyes(-0.2 * amount) } };
    }
    case "dramatic": {
      const amount = envelope(t, total, 0.3);
      return { look: { x: 0.4 * Math.sin(Math.PI * t / total) * amount, y: 0.45 * amount }, parameters: {
        ParamAngleZ: -15 * amount, ParamBodyAngleZ: -10 * amount, ...brows(0.5 * amount), ...eyes(-0.4 * amount),
      } };
    }
    case "wink": {
      const closed = envelope(t, total, 0.12, 0.3);
      return { look: { x: 0.08 * closed, y: 0.05 * closed }, parameters: {
        ParamEyeLOpen: -closed, ParamEyeLSmile: 0.8 * closed, ParamMouthForm: 0.5 * closed, ParamAngleZ: 6 * closed, ParamBrowLY: -0.2 * closed,
      } };
    }
    case "pout": case "shy": case "look_away": case "drowsy":
      return moodFrame(name, t, envelope(t, total, 0.45, 0.6));
    case "giggle": {
      const amount = envelope(t, total, 0.15, 0.35), bounce = Math.abs(wave(t, 0.36));
      return { look: { x: 0, y: (0.08 - 0.18 * bounce) * amount }, parameters: {
        ...eyeSmile(amount), ParamBodyAngleY: 2.5 * bounce * amount, ParamBodyAngleZ: 3 * wave(t, 0.72) * amount, ParamAngleZ: 6 * amount,
      } };
    }
    case "flinch": {
      const jolt = flinchJolt(t) * smooth((total - t) / 0.4), tremble = 0.04 * wave(t, 0.09) * jolt;
      return { look: { x: 0.3 * jolt + tremble, y: 0.3 * jolt }, parameters: {
        ParamBodyAngleY: 5 * jolt, ParamBodyAngleX: -5 * jolt, ...eyes(-0.6 * jolt), ...brows(0.6 * jolt),
      } };
    }
    case "lean_in": {
      const amount = envelope(t, total, 0.5), nuzzle = wave(t, 1.4);
      return { look: { x: 0, y: -0.2 * amount }, parameters: {
        ParamAngleZ: (12 + 3 * nuzzle) * amount, ParamBodyAngleY: -4 * amount, ParamBodyAngleZ: 4 * amount, ...eyes(-0.55 * amount),
        ParamEyeLSmile: 0.6 * amount, ParamEyeRSmile: 0.6 * amount, ParamMouthForm: 0.6 * amount,
      } };
    }
    case "think": {
      const amount = envelope(t, total, 0.45), ponder = 0.06 * wave(t, 2.2);
      return { look: { x: (-0.35 + ponder) * amount, y: 0.4 * amount }, parameters: {
        ParamEyeBallX: -0.4 * amount, ParamEyeBallY: 0.5 * amount, ParamAngleZ: 7 * amount, ParamBrowLY: 0.4 * amount,
        ParamBrowRY: 0.1 * amount, ParamMouthForm: -0.2 * amount,
      } };
    }
    case "eye_roll": {
      const amount = envelope(t, total, 0.2, 0.4), angle = Math.PI * (0.9 - 0.8 * smooth((t - 0.2) / 1.1));
      return { look: { x: 0, y: 0.12 * amount }, parameters: {
        ParamEyeBallX: 0.9 * Math.cos(angle) * amount, ParamEyeBallY: 0.9 * Math.sin(angle) * amount, ParamAngleZ: 5 * amount,
        ...eyes(-0.2 * amount),
      } };
    }
  }
}

/** How far a flinch has jerked back `t` seconds in: snapping back within 80ms, then settling. */
export function flinchJolt(t: number): number {
  return t < 0.08 ? smooth(t / 0.08) : Math.exp(-(t - 0.08) * 2.8);
}

/** How far a drowsy head has nodded off `t` seconds in: sinking slowly over a 6-second cycle, then catching itself. */
export function drowse(t: number): number {
  const p = (t % 6) / 6;
  return p < 0.8 ? smooth(p / 0.8) : 1 - smooth((p - 0.8) / 0.1);
}

/** A holdable gesture's pose at strength `a`, kept alive by `t` (pout huffs, shy peeks back now and then, look_away glances
 *  back, drowsy nods off and catches itself), so a held pose never looks frozen. */
function moodFrame(name: HoldableGesture, t: number, a: number): GestureFrame {
  switch (name) {
    case "pout": {
      const huff = 0.05 * wave(t, 2.6);
      return { look: { x: (-0.25 + huff) * a, y: -0.1 * a }, parameters: {
        ParamMouthForm: -a, ParamCheekPuff: 0.8 * a, ...brows(-0.3 * a), ParamAngleZ: -5 * a,
      } };
    }
    case "shy": {
      const peek = Math.max(0, wave(t, 5.5)) ** 4;
      return { look: { x: (-0.4 + 0.25 * peek) * a, y: (-0.45 + 0.2 * peek) * a }, parameters: {
        ParamAngleZ: 8 * a, ParamBodyAngleX: -4 * a, ParamEyeLSmile: 0.5 * a, ParamEyeRSmile: 0.5 * a, ...eyes(-0.3 * a),
        ParamCheek: 0.5 * a, ParamMouthForm: 0.3 * a,
      } };
    }
    case "look_away": {
      const glance = Math.max(0, wave(t - 2, 6)) ** 6;
      return { look: { x: (0.65 - 0.3 * glance) * a, y: 0.05 * a }, parameters: { ParamEyeBallX: (0.6 - 0.4 * glance) * a, ParamAngleZ: -4 * a } };
    }
    case "drowsy": {
      const droop = drowse(t);
      return { look: { x: 0, y: -(0.25 + 0.3 * droop) * a }, parameters: {
        ...eyes(-(0.45 + 0.4 * droop) * a), ParamAngleZ: (4 + 6 * droop) * a, ParamBodyAngleY: -2 * a, ...brows(-0.15 * a),
      } };
    }
  }
}

/** A held gesture's frame `seconds` after it was asked for, at `weight` 0..1. */
export function heldFrame(name: HoldableGesture, seconds: number, weight: number): GestureFrame {
  return moodFrame(name, seconds, weight);
}

function combine(frames: readonly GestureFrame[]): GestureFrame {
  const look = { x: 0, y: 0 }, parameters: Record<string, number> = {};
  for (const frame of frames) {
    look.x += frame.look.x; look.y += frame.look.y;
    for (const [id, value] of Object.entries(frame.parameters)) parameters[id] = (parameters[id] ?? 0) + value;
  }
  return { look, parameters };
}

/** What a character is doing with Martlet's gestures: the one playing once and the one held, if any. */
export interface GestureState { readonly playing?: Gesture; readonly held?: HoldableGesture }

const HOLD_EASE = 0.8;

/**
 * Plays Martlet's gestures: one at a time, a new one replacing the last, plus at most one held gesture (`hold`), eased in
 * over 0.8s and kept until `end`, then eased out. A gesture played while one is held plays on top, the held pose easing
 * back partway for it and resuming after; a new held gesture crossfades from the last.
 */
export class GesturePlayer {
  #playing: { name: Gesture; seconds: number } | undefined;
  #held: { name: HoldableGesture; seconds: number; progress: number; on: boolean }[] = [];

  /** Plays `name` once, or holds it when `hold` and it can be held (see `HOLDABLE_GESTURES`). */
  play(name: Gesture, hold = false): void {
    if (hold && isHoldable(name)) {
      for (const held of this.#held) held.on = held.name === name;
      if (!this.#held.some(held => held.name === name)) this.#held.push({ name, seconds: 0, progress: 0, on: true });
    } else this.#playing = { name, seconds: 0 };
  }

  /** Lets a held gesture go; a gesture playing once just finishes. */
  end(name: string): void {
    for (const held of this.#held) if (held.name === name) held.on = false;
  }

  clear(): void { this.#playing = undefined; this.#held = []; }

  get state(): GestureState {
    const held = this.#held.find(h => h.on)?.name;
    return Object.freeze({ ...(this.#playing ? { playing: this.#playing.name } : {}), ...(held ? { held } : {}) });
  }

  /** The combined frame `deltaSeconds` later, or undefined when nothing plays. */
  advance(deltaSeconds: number): GestureFrame | undefined {
    const frames: GestureFrame[] = [];
    let duck = 0;
    if (this.#playing) {
      const playing = this.#playing;
      playing.seconds += deltaSeconds;
      const frame = gestureFrame(playing.name, playing.seconds);
      if (frame) { frames.push(frame); duck = 0.6 * envelope(playing.seconds, DURATION[playing.name], 0.25); }
      else this.#playing = undefined;
    }
    for (const held of this.#held) {
      held.seconds += deltaSeconds;
      held.progress = Math.max(0, Math.min(1, held.progress + (held.on ? 1 : -1) * deltaSeconds / HOLD_EASE));
      frames.push(moodFrame(held.name, held.seconds, smooth(held.progress) * (1 - duck)));
    }
    this.#held = this.#held.filter(held => held.on || held.progress > 0);
    return frames.length ? combine(frames) : undefined;
  }
}