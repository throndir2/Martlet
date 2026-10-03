/** Martlet's own head gestures, the same on every model: offsets added to the normalized look direction (x right, y up). */
export const GESTURES = Object.freeze(["nod", "shake"] as const);
export type Gesture = typeof GESTURES[number];

export function isGesture(name: string): name is Gesture {
  return (GESTURES as readonly string[]).includes(name);
}

/** The look offset `seconds` into a gesture, or undefined once it is over. Nod: down and up twice; shake: three turns
 *  that fade in and out. */
export function gestureOffset(name: Gesture, seconds: number): { x: number; y: number } | undefined {
  if (name === "nod") {
    if (seconds >= 1.1) return undefined;
    const phase = Math.sin(Math.PI * seconds / 0.55);
    return { x: 0, y: -0.65 * phase * phase };
  }
  if (seconds >= 1.2) return undefined;
  return { x: 0.6 * Math.sin(2 * Math.PI * seconds / 0.4) * Math.sin(Math.PI * seconds / 1.2), y: 0 };
}
