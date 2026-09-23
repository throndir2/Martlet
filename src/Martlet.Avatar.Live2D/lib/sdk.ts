import { Live2DError, requireCondition } from "./diagnostics.js";

export const FRAMEWORK_REVISION = "8df84780f2aa1298f3b30965cdae143e049f3c8e";
export const CORE_VERSION = 0x05010000;

// Structural ports for the inspected official 5-r.4 APIs; no SDK implementation is redistributed.
export interface CubismModel {
  getParameterCount(): number;
  getParameterId(index: number): { getString(): { s: string } };
  getParameterMinimumValue(index: number): number;
  getParameterMaximumValue(index: number): number;
  getParameterDefaultValue(index: number): number;
  setParameterValueByIndex(index: number, value: number, weight?: number): void;
  getCanvasWidth(): number;
  getCanvasHeight(): number;
  getDrawableCount(): number;
  getDrawableVertexCount(index: number): number;
  getDrawableVertexIndexCount(index: number): number;
  getDrawableTextureIndex(index: number): number;
  update(): void;
}

export interface CubismMoc {
  createModel(): CubismModel | null;
  deleteModel(model: CubismModel): void;
  release(): void;
}

export interface CubismMatrix {
  setMatrix(values: Float32Array): void;
  getArray(): Float32Array;
}

export interface CubismRenderer {
  initialize(model: CubismModel, maskBufferCount?: number): void;
  startUp(gl: WebGLRenderingContext): void;
  bindTexture(slot: number, texture: WebGLTexture): void;
  setIsPremultipliedAlpha(value: boolean): void;
  setMvpMatrix(matrix: CubismMatrix): void;
  setRenderState(framebuffer: WebGLFramebuffer | null, viewport: number[]): void;
  drawModel(): void;
  release(): void;
}

export interface SdkModules {
  readonly revision: typeof FRAMEWORK_REVISION;
  readonly CubismFramework: {
    isStarted(): boolean;
    isInitialized(): boolean;
    startUp(): boolean;
    initialize(memorySize?: number): void;
    dispose(): void;
    cleanUp(): void;
  };
  readonly CubismMoc: { create(bytes: ArrayBuffer, checkConsistency: boolean): CubismMoc | null };
  readonly CubismRenderer_WebGL: new () => CubismRenderer;
  readonly CubismMatrix44: new () => CubismMatrix;
}

interface CoreVersionApi {
  csmGetVersion(): number;
  csmGetMocVersion(bytes: ArrayBuffer): number;
  csmGetLatestMocVersion(): number;
}

export function checkRuntime(sdk: SdkModules | undefined): CoreVersionApi {
  requireCondition(sdk, "MISSING_SDK", "Supply locally installed official Cubism Web Framework 5-r.4 modules.");
  requireCondition(sdk.revision === FRAMEWORK_REVISION, "SDK_VERSION_MISMATCH",
    `Framework must be 5-r.4 revision ${FRAMEWORK_REVISION}.`);
  const core: unknown = Reflect.get(globalThis, "Live2DCubismCore");
  requireCondition(core !== null && typeof core === "object", "MISSING_CORE",
    "Load your lawfully obtained local SDK 5-r.4 Core script before Framework initialization.");
  const version: unknown = Reflect.get(core, "Version");
  requireCondition(version !== null && (typeof version === "object" || typeof version === "function") &&
    ["csmGetVersion", "csmGetMocVersion", "csmGetLatestMocVersion"].every(
      key => typeof Reflect.get(version, key) === "function"),
  "MISSING_CORE", "Core Version APIs are missing; use the unmodified SDK 5-r.4 Core.");
  const api = version as CoreVersionApi;
  if (api.csmGetVersion() !== CORE_VERSION) {
    throw new Live2DError("CORE_VERSION_MISMATCH",
      "This lane requires SDK 5-r.4 Core 05.01.0000 (0x05010000); do not downgrade or convert the model.");
  }
  return api;
}
