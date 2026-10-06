/** Martlet's own gestures, played on any Live2D model that has the standard Cubism parameters they move: look offsets
 *  (x right, y up) added to the head direction and additive offsets to standard parameters. */
export const GESTURES = Object.freeze(["nod", "shake", "tilt", "bow", "sway", "smile", "blush", "surprise"] as const);
export type Gesture = typeof GESTURES[number];

export interface GestureFrame {
  readonly look: { readonly x: number; readonly y: number };
  readonly parameters: Readonly<Record<string, number>>;
}

/** The standard parameters each gesture needs; a model missing any of them doesn't get it. */
export const GESTURE_REQUIREMENTS: Readonly<Record<Gesture, readonly string[]>> = Object.freeze({
  nod: ["ParamAngleY"],
  shake: ["ParamAngleX"],
  tilt: ["ParamAngleZ"],
  bow: ["ParamAngleY"],
  sway: ["ParamBodyAngleZ"],
  smile: ["ParamEyeLSmile", "ParamEyeRSmile"],
  blush: ["ParamCheek"],
  surprise: ["ParamBrowLY", "ParamBrowRY"],
});

const DURATION: Readonly<Record<Gesture, number>> = Object.freeze({
  nod: 1.1, shake: 1.2, tilt: 1.8, bow: 2, sway: 2.4, smile: 3.5, blush: 4, surprise: 2,
});

export function isGesture(name: string): name is Gesture {
  return (GESTURES as readonly string[]).includes(name);
}

/** The gestures a model with `parameterIds` can play. */
export function supportedGestures(parameterIds: Iterable<string>): readonly Gesture[] {
  const present = new Set(parameterIds);
  return GESTURES.filter(name => GESTURE_REQUIREMENTS[name].every(id => present.has(id)));
}

// 0 to 1 over `fade` seconds, held, then back to 0 by `total`.
function envelope(seconds: number, total: number, fade: number): number {
  const edge = Math.min(seconds, total - seconds) / fade;
  if (edge >= 1) return 1;
  const x = Math.max(0, edge);
  return x * x * (3 - 2 * x);
}

const none = Object.freeze({});

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
  }
}