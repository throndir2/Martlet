/** Martlet's own gestures, played on any Live2D model that has the standard Cubism parameters they move: look offsets
 *  (x right, y up) added to the head direction and additive offsets to standard parameters. After the reply gestures come
 *  the voice emotes, played when the voice makes their sound or tone (laugh, sigh, gasp...). */
export const GESTURES = Object.freeze(["nod", "shake", "tilt", "bow", "sway", "smile", "blush", "surprise",
  "laugh", "chuckle", "sigh", "gasp", "cough", "clear_throat", "groan", "sniff", "shush", "inhale", "exhale", "mumble", "hum",
  "sneeze", "whistle", "happy", "sarcastic", "angry", "fear", "crying", "whispering", "dramatic"] as const);
export type Gesture = typeof GESTURES[number];

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
  // Every model blushes: with ParamCheek when it has it, otherwise Martlet draws a glow on the cheeks (the renderer page's
  // overlay; see BLUSH_PARAMETERS).
  blush: [],
  surprise: ["ParamBrowLY", "ParamBrowRY"],
  laugh: angleY, chuckle: angleY, sigh: angleY, gasp: angleY, cough: angleY, clear_throat: angleY, groan: angleZ, sniff: angleY,
  shush: angleY, inhale: angleY, exhale: angleY, mumble: angleX, hum: angleZ, sneeze: angleY, whistle: angleZ,
  happy: ["ParamEyeLSmile", "ParamEyeRSmile"], sarcastic: angleZ, angry: ["ParamBrowLY", "ParamBrowRY"], fear: angleX,
  crying: angleY, whispering: angleZ, dramatic: angleZ,
});

const DURATION: Readonly<Record<Gesture, number>> = Object.freeze({
  nod: 1.1, shake: 1.2, tilt: 1.8, bow: 2, sway: 2.4, smile: 3.5, blush: 4, surprise: 2,
  laugh: 2, chuckle: 1.4, sigh: 2.4, gasp: 1.6, cough: 1.5, clear_throat: 1.2, groan: 2.2, sniff: 1, shush: 2, inhale: 1.6,
  exhale: 1.8, mumble: 2, hum: 2.6, sneeze: 1.6, whistle: 2, happy: 2.4, sarcastic: 1.8, angry: 2.4, fear: 2, crying: 3,
  whispering: 2.2, dramatic: 2.4,
});

export function isGesture(name: string): name is Gesture {
  return (GESTURES as readonly string[]).includes(name);
}

/** The parameters the model's own blush needs; without them the adapter declines and the page draws one instead. */
export const BLUSH_PARAMETERS: readonly string[] = Object.freeze(["ParamCheek"]);

/** Gestures that can be held: their fade in seconds. Held, they stay fully faded in; released, they fade out as long. */
export const GESTURE_HOLD: Readonly<Partial<Record<Gesture, number>>> = Object.freeze({ blush: 0.6 });

/** How long a gesture lasts, in seconds. */
export const gestureSeconds = (name: Gesture): number => DURATION[name];

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
  }
}