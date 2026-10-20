import { integer, object, requireValid } from "./inspect.js";

// The playback controls the runtime takes (identities, coefficient and composed inputs, mappings) and their checks.

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
export function identifier(value: unknown, label: string): asserts value is string {
  requireValid(typeof value === "string" && /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/.test(value), `Invalid ${label}.`);
}
export function validateIdentity(identity: PlaybackIdentity): void {
  requireValid(identity !== null && typeof identity === "object", "Playback identity is required.");
  for (const key of ["sessionId", "turnId", "requestId"] as const)
    requireValid(typeof identity[key] === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(identity[key])
      && identity[key] !== "00000000-0000-0000-0000-000000000000", `${key} must be a nonempty UUID.`);
  identifier(identity.sourceId, "sourceId");
  integer(identity.epoch, 0, 2147483647, "epoch");
  requireValid([16000, 24000, 44100, 48000].includes(identity.sampleRate), "Unsupported original PCM sample rate.");
}
export function identityKey(identity: PlaybackIdentity): string {
  return [identity.sessionId.toLowerCase(), identity.turnId.toLowerCase(), identity.requestId.toLowerCase(),
    identity.sourceId, identity.epoch, identity.sampleRate].join("|");
}
