import { boundedInteger, Diagnostic, Live2DError, requireCondition } from "./diagnostics.js";

// What the Live2D adapter takes and gives (identities, composed frames, results, model summaries, mouth readings) and the
// browser services it uses.

export interface RenderIdentity {
  readonly sessionId: string;
  readonly turnId: string;
  readonly requestId: string;
  readonly sourceId: string;
  readonly epoch: number;
}

/** In-process sink after host validation, playback-time gating and aspect composition; not a wire contract. */
export interface ComposedFrame {
  readonly identity: RenderIdentity;
  readonly sequence: number;
  readonly configurationId: string;
  readonly channels: Readonly<Record<string, number>>;
}

export interface ComposedParameters {
  readonly identity: RenderIdentity;
  readonly sequence: number;
  readonly configurationId: string;
  readonly parameters: Readonly<Record<string, number>>;
}

export interface FrameResult {
  readonly accepted: boolean;
  readonly diagnostics: readonly Diagnostic[];
}

export interface BrowserServices {
  /** Decodes a PNG, resized to `size` when given. */
  decodeTexture(bytes: Uint8Array<ArrayBuffer>, signal: AbortSignal,
    size?: { readonly width: number; readonly height: number }): Promise<ImageBitmap>;
  requestFrame(callback: FrameRequestCallback): number;
  cancelFrame(handle: number): void;
  now(): number;
}

/** What the loaded model drives, after Martlet's fallbacks, for status and diagnostics. */
export interface ModelSummary {
  readonly textures: number;
  readonly textureDivisor: number;
  readonly eyeBlink: readonly string[];
  readonly lipSync: readonly string[];
  readonly motionGroups: readonly string[];
  readonly expressions: number;
  readonly physics: boolean;
  readonly animated: boolean;
}

/** How long the voice keeps the character's mouth after its last sound, in seconds, so the pauses between its words and
 *  sentences don't give the mouth back to the emotes. */
export const VOICE_HOLD_SECONDS = 1;

/** Who moves the mouth now (Live2DAdapter.mouthReading). Each opening is 0 at the model's rest and 1 fully open. */
export interface MouthReading {
  /** The parameter read: ParamMouthOpenY when the model has it, otherwise its first lip-sync parameter. */
  readonly parameter: string;
  /** 0 to 1: how much the voice has the mouth now; it eases in when the voice starts and out after VOICE_HOLD_SECONDS. */
  readonly voice: number;
  /** The voice (or a composed frame) moved the mouth within the last VOICE_HOLD_SECONDS. */
  readonly speaking: boolean;
  /** The voice's loudness on the mouth now (0 to 1). */
  readonly level: number;
  /** How far the emotes (expressions, gestures such as mouth_open, motions) open the mouth before the voice takes it. */
  readonly emote: number;
  /** How far the mouth is open now. */
  readonly open: number;
}

export const browserServices: BrowserServices = {
  decodeTexture(bytes, signal, size) {
    requireCondition(typeof createImageBitmap === "function", "MISSING_IMAGE_DECODER",
      "This runtime requires createImageBitmap PNG decoding.");
    return new Promise((resolve, reject) => {
      let settled = false;
      const finish = (error?: Error, image?: ImageBitmap) => {
        if (settled) { image?.close(); return; }
        settled = true;
        clearTimeout(timer);
        signal.removeEventListener("abort", abort);
        if (error) reject(error);
        else if (image) resolve(image);
      };
      const abort = () => finish(new Live2DError("LOAD_CANCELLED", "Model load was cancelled."));
      const timer = setTimeout(() => finish(new Live2DError("TEXTURE_TIMEOUT", "PNG decode exceeded 20 seconds.")), 20_000);
      signal.addEventListener("abort", abort, { once: true });
      if (signal.aborted) { abort(); return; }
      createImageBitmap(new Blob([bytes], { type: "image/png" }), {
        premultiplyAlpha: "premultiply", colorSpaceConversion: "none", imageOrientation: "none",
        ...(size ? { resizeWidth: size.width, resizeHeight: size.height, resizeQuality: "high" as const } : {}),
      }).then(image => finish(undefined, image),
        error => finish(new Live2DError("INVALID_TEXTURE", `PNG decode failed: ${String(error)}`)));
    });
  },
  requestFrame: callback => requestAnimationFrame(callback),
  cancelFrame: handle => cancelAnimationFrame(handle),
  now: () => performance.now(),
};

/** Checks a render identity: three nonempty UUIDs, a bounded epoch and a bounded source ID. */
export function validateIdentity(identity: RenderIdentity): void {
  requireCondition(identity !== null && typeof identity === "object", "INVALID_IDENTITY", "Render identity is required.");
  for (const key of ["sessionId", "turnId", "requestId"] as const) {
    requireCondition(typeof identity[key] === "string" &&
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(identity[key]) &&
      identity[key] !== "00000000-0000-0000-0000-000000000000",
    "INVALID_IDENTITY", `${key} must be a nonempty UUID.`);
  }
  boundedInteger(identity.epoch, 2_147_483_647, "epoch");
  requireCondition(typeof identity.sourceId === "string" && identity.sourceId.trim().length > 0 &&
    identity.sourceId.length <= 256 && !/[\u0000-\u001f]/.test(identity.sourceId),
  "INVALID_IDENTITY", "sourceId must be a bounded nonempty host-selected binding.");
}

export function sameIdentity(a: RenderIdentity, b: RenderIdentity): boolean {
  return a.epoch === b.epoch && a.sessionId.toLowerCase() === b.sessionId.toLowerCase() &&
    a.turnId.toLowerCase() === b.turnId.toLowerCase() && a.requestId.toLowerCase() === b.requestId.toLowerCase() &&
    a.sourceId === b.sourceId;
}
