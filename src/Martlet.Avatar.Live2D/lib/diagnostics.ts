export class Live2DError extends Error {
  constructor(public readonly code: string, message: string) {
    super(message);
    this.name = "Live2DError";
  }
}

export interface Diagnostic {
  readonly code: string;
  readonly message: string;
}

export function requireCondition(value: unknown, code: string, message: string): asserts value {
  if (!value) throw new Live2DError(code, message);
}

export function finite(value: number, label: string): void {
  requireCondition(Number.isFinite(value), "INVALID_NUMBER", `${label} must be finite.`);
}

export function boundedInteger(value: number, maximum: number, label: string): void {
  requireCondition(Number.isSafeInteger(value) && value >= 0 && value <= maximum,
    "RESOURCE_LIMIT", `${label} must be an integer in 0..${maximum}.`);
}
