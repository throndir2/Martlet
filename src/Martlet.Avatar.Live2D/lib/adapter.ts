import { LIMITS, LocalModelBundle, pngDimensions, scaledSize } from "./assets.js";
import { boundedInteger, Diagnostic, finite, Live2DError, requireCondition } from "./diagnostics.js";
import { Capabilities, ChannelMapping, inspectParameters, MappingPlan, Parameter } from "./mapping.js";
import { checkRuntime, type Animator, type AnimatorAssets, CubismMoc, CubismModel, CubismRenderer, SdkModules } from "./sdk.js";

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

interface Resources {
  readonly gl: WebGLRenderingContext;
  readonly sdk: SdkModules;
  readonly abort: AbortController;
  readonly textures: WebGLTexture[];
  moc?: CubismMoc;
  model?: CubismModel;
  renderer?: CubismRenderer;
  animator?: Animator;
}

let activeAdapter: Live2DAdapter | undefined;

function validateIdentity(identity: RenderIdentity): void {
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

function sameIdentity(a: RenderIdentity, b: RenderIdentity): boolean {
  return a.epoch === b.epoch && a.sessionId.toLowerCase() === b.sessionId.toLowerCase() &&
    a.turnId.toLowerCase() === b.turnId.toLowerCase() && a.requestId.toLowerCase() === b.requestId.toLowerCase() &&
    a.sourceId === b.sourceId;
}

/** Highest vertex of the visible meshes in model units (y up), or undefined when nothing is visible. */
function visibleTop(model: CubismModel): number | undefined {
  let top = Number.NEGATIVE_INFINITY;
  for (let i = 0; i < model.getDrawableCount(); i++) {
    if (!model.getDrawableDynamicFlagIsVisible(i) || model.getDrawableOpacity(i) < 0.05) continue;
    const vertices = model.getDrawableVertices(i);
    for (let v = 1; v < vertices.length; v += 2) top = Math.max(top, vertices[v]!);
  }
  return Number.isFinite(top) ? top : undefined;
}

export class Live2DAdapter {
  readonly #canvas: HTMLCanvasElement;
  readonly #sdk: SdkModules | undefined;
  readonly #services: BrowserServices;
  readonly #report: (diagnostic: Diagnostic) => void;
  #resources: Resources | undefined;
  #bundle: LocalModelBundle | undefined;
  #parameters: readonly Parameter[] = [];
  #plan: MappingPlan | undefined;
  #approvedTargets = new Map<string, Parameter>();
  #inputMode: "channels" | "parameters" = "channels";
  #configurationId = crypto.randomUUID();
  #identity: RenderIdentity | undefined;
  #sequence = -1;
  #writes: readonly { index: number; value: number }[] = [];
  #generation = 0;
  #loading = false;
  #disposed = false;
  #frameHandle: number | undefined;
  #clockGeneration = 0;
  #lastTimestamp: number | undefined;
  #age = 0;
  #hasFrame = false;
  #lipSyncTarget = 0;
  #lipSync = 0;
  #lipSyncAge = Number.POSITIVE_INFINITY;
  #lookTarget = { x: 0, y: 0 };
  #look = { x: 0, y: 0 };
  #view = { zoom: 1, x: 0, y: 0, frame: 1 };
  #modelTop: number | undefined;
  #eyeBlinkIds: readonly string[] = [];
  #lipSyncIds: readonly string[] = [];

  constructor(canvas: HTMLCanvasElement, options: {
    sdk?: SdkModules;
    onDiagnostic: (diagnostic: Diagnostic) => void;
    services?: BrowserServices;
  }) {
    this.#canvas = canvas;
    this.#sdk = options.sdk;
    this.#services = options.services ?? browserServices;
    this.#report = options.onDiagnostic;
    canvas.addEventListener("webglcontextlost", this.#contextLost);
  }

  get capabilities(): Capabilities | undefined { return this.#plan?.capabilities; }
  get configurationId(): string { return this.#configurationId; }
  /** True when the Framework animator drives idle motions, blinking, breathing, physics and pose. */
  get animated(): boolean { return this.#resources?.animator !== undefined; }
  get motionGroups(): readonly string[] { return this.#resources?.animator?.motionGroups ?? []; }
  get expressions(): readonly string[] { return this.#resources?.animator?.expressions ?? []; }

  get modelSummary(): ModelSummary | undefined {
    const description = this.#bundle?.description;
    if (!description || !this.#resources?.model || this.#loading) return undefined;
    return Object.freeze({
      textures: description.textures.length, textureDivisor: description.textureDivisor,
      eyeBlink: this.animated ? this.#eyeBlinkIds : [], lipSync: this.animated ? this.#lipSyncIds : [],
      motionGroups: this.motionGroups, expressions: this.expressions.length,
      physics: this.animated && description.physics !== undefined, animated: this.animated,
    });
  }

  /** Speech loudness 0..1; decays to closed when not refreshed for 300ms. */
  setLipSync(level: number): void {
    this.#ready();
    finite(level, "lip-sync level");
    this.#lipSyncTarget = Math.max(0, Math.min(1, level));
    this.#lipSyncAge = 0;
  }

  /** Normalized -1..1 look direction (x right, y up). */
  setLook(x: number, y: number): void {
    this.#ready();
    finite(x, "look x");
    finite(y, "look y");
    this.#lookTarget = { x: Math.max(-1, Math.min(1, x)), y: Math.max(-1, Math.min(1, y)) };
  }

  /**
   * Camera zoom applied after fitting: screen = fitted * zoom + (x, y), in the frame's clip space. The model is fitted
   * and centered in a frame spanning `frame` of the canvas width (0.1 to 1); the canvas beyond it on each side is room
   * the model can move into without being cut off.
   */
  setView(zoom: number, x: number, y: number, frame = 1): void {
    finite(zoom, "view zoom");
    finite(x, "view x");
    finite(y, "view y");
    finite(frame, "view frame");
    this.#view = { zoom: Math.max(1, Math.min(32, zoom)), x, y, frame: Math.max(0.1, Math.min(1, frame)) };
  }

  /** Top of the visible character (top of the head) in fitted clip space, before view zoom/pan; 1 is the canvas top. */
  get contentTop(): number | undefined {
    const model = this.#resources?.model;
    return model && this.#modelTop !== undefined && !this.#loading ? this.#modelTop * this.#fitScale(model) : undefined;
  }

  playMotion(group: string): boolean {
    this.#ready();
    return this.#resources?.animator?.playMotion(group) ?? false;
  }

  setExpression(name: string | null): boolean {
    this.#ready();
    return this.#resources?.animator?.setExpression(name) ?? false;
  }

  async load(bundle: LocalModelBundle): Promise<Capabilities> {
    this.#ensureAlive();
    requireCondition(!this.#loading, "LOAD_IN_PROGRESS", "Wait for load completion or dispose this adapter.");
    const core = checkRuntime(this.#sdk);
    const sdk = this.#sdk;
    requireCondition(sdk, "MISSING_SDK", "Official Framework modules are required.");
    requireCondition(!activeAdapter || activeAdapter === this, "RUNTIME_BUSY",
      "Use a dedicated realm: only one active Live2D renderer is supported.");
    this.#release();
    requireCondition(!sdk.CubismFramework.isStarted(), "RUNTIME_BUSY",
      "Framework is already owned by another host; use a dedicated browser realm.");
    this.#validateCanvas();
    const gl = this.#canvas.getContext("webgl", { alpha: true, premultipliedAlpha: true, antialias: true });
    requireCondition(gl, "MISSING_WEBGL", "A working dedicated WebGL canvas is required.");
    requireCondition(!gl.isContextLost(), "CONTEXT_LOST", "Recreate the renderer after WebGL context loss.");
    const generation = ++this.#generation;
    const resources: Resources = { gl, sdk, abort: new AbortController(), textures: [] };
    this.#resources = resources;
    activeAdapter = this;
    this.#loading = true;
    try {
      requireCondition(sdk.CubismFramework.startUp(), "SDK_START_FAILED", "Cubism Framework startup failed.");
      sdk.CubismFramework.initialize(64 * 1024 * 1024);
      requireCondition(sdk.CubismFramework.isInitialized(), "SDK_START_FAILED", "Cubism Framework initialization failed.");
      const bytes = bundle.read(bundle.description.moc);
      requireCondition(bytes.length >= 8 && bytes[0] === 77 && bytes[1] === 79 && bytes[2] === 67 && bytes[3] === 51,
        "INVALID_MOC", "MOC3 signature is missing.");
      const moc = sdk.CubismMoc.create(bytes.buffer, true);
      requireCondition(moc, "INVALID_MOC", "Core rejected the MOC consistency check or could not allocate it.");
      resources.moc = moc;
      const version = moc.getMocVersion();
      requireCondition(Number.isInteger(version) && version > 0 && version <= core.csmGetLatestMocVersion(),
        "UNSUPPORTED_MOC", "This MOC is unsupported by Core 05.01.0000; obtain compatible original assets.");
      const model = moc.createModel();
      requireCondition(model, "MODEL_CREATE_FAILED", "Core could not create this model.");
      resources.model = model;
      this.#parameters = inspectParameters(model, bundle.description);
      this.#validateGeometry(model, bundle.description.textures.length);
      const renderer = new sdk.CubismRenderer_WebGL();
      resources.renderer = renderer;
      renderer.initialize(model, 1);
      renderer.startUp(gl);
      renderer.setIsPremultipliedAlpha(true);
      for (const [slot, path] of bundle.description.textures.entries()) {
        const textureBytes = bundle.read(path);
        const source = pngDimensions(textureBytes);
        const expected = scaledSize(source, bundle.description.textureDivisor);
        requireCondition(Math.max(expected.width, expected.height) <= gl.getParameter(gl.MAX_TEXTURE_SIZE),
          "RESOURCE_LIMIT", "Texture dimensions exceed the local GPU limit.");
        const resized = expected.width !== source.width || expected.height !== source.height;
        const image = await this.#services.decodeTexture(textureBytes, resources.abort.signal, resized ? expected : undefined);
        try {
          requireCondition(generation === this.#generation && !resources.abort.signal.aborted,
            "LOAD_CANCELLED", "This model load is no longer active.");
          requireCondition(image.width === expected.width && image.height === expected.height,
            "INVALID_TEXTURE", "Decoded PNG dimensions differ from its bounded header.");
          const texture = gl.createTexture();
          requireCondition(texture, "TEXTURE_CREATE_FAILED", "WebGL texture allocation failed.");
          resources.textures.push(texture);
          gl.bindTexture(gl.TEXTURE_2D, texture);
          gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
          // Atlases are drawn far below native size; trilinear mipmaps stop minification aliasing.
          // WebGL1 only supports mipmaps for power-of-two textures.
          const powerOfTwo = (expected.width & (expected.width - 1)) === 0 &&
            (expected.height & (expected.height - 1)) === 0;
          if (powerOfTwo) gl.generateMipmap(gl.TEXTURE_2D);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, powerOfTwo ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
          gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
          this.#checkGl(gl);
          renderer.bindTexture(slot, texture);
        } finally {
          image.close();
        }
      }
      gl.bindTexture(gl.TEXTURE_2D, null);
      if (sdk.createAnimator) {
        try {
          resources.animator = sdk.createAnimator(model, this.#animatorAssets(bundle));
        } catch (error) {
          this.#report({ code: "ANIMATION_UNAVAILABLE",
            message: `Idle animation disabled: ${error instanceof Error ? error.message : String(error)}` });
        }
      }
      this.#bundle = bundle;
      this.#plan = new MappingPlan(this.#parameters, bundle.description, []);
      this.#loading = false;
      this.#neutral();
      this.update(0);
      this.#modelTop = visibleTop(model);
      return this.#plan.capabilities;
    } catch (error) {
      if (this.#resources === resources) this.#release();
      throw error;
    } finally {
      if (generation === this.#generation) this.#loading = false;
    }
  }

  configure(mappings: readonly ChannelMapping[]): Capabilities {
    this.#ready();
    requireCondition(this.#bundle, "MODEL_NOT_LOADED", "Load a local model first.");
    const next = new MappingPlan(this.#parameters, this.#bundle.description, mappings);
    this.stop();
    this.#plan = next;
    this.#configurationId = crypto.randomUUID();
    this.#inputMode = "channels";
    this.#approvedTargets.clear();
    return next.capabilities;
  }

  /** The shared host already scaled these targets. This path performs no second channel transform. */
  configureTargets(parameterIds: readonly string[]): readonly Parameter[] {
    this.#ready();
    boundedInteger(parameterIds.length, LIMITS.parameters, "approved target count");
    const targets = new Map<string, Parameter>();
    for (const id of parameterIds) {
      const parameter = this.#parameters.find(p => p.id === id);
      requireCondition(parameter, "ABSENT_PARAMETER", `Core reports no parameter ${id}.`);
      requireCondition(!targets.has(id), "DUPLICATE_WRITER", `Duplicate composed target ${id}.`);
      targets.set(id, parameter);
    }
    this.stop();
    this.#approvedTargets = targets;
    this.#configurationId = crypto.randomUUID();
    this.#inputMode = "parameters";
    return Object.freeze([...targets.values()]);
  }

  resetEpoch(identity: RenderIdentity): void {
    this.#ready();
    validateIdentity(identity);
    this.stop();
    this.#identity = Object.freeze({ ...identity });
    this.#sequence = -1;
  }

  applyFrame(frame: ComposedFrame): FrameResult {
    this.#ready();
    requireCondition(this.#inputMode === "channels", "INPUT_MODE_MISMATCH", "Use applyComposedParameters for approved final targets.");
    const rejection = this.#rejectStale(frame);
    if (rejection) return rejection;
    requireCondition(this.#plan, "MODEL_NOT_LOADED", "Configure the loaded model first.");
    const result = this.#plan.resolve(frame.channels);
    this.#writes = result.writes;
    this.#sequence = frame.sequence;
    this.#age = 0;
    this.#hasFrame = true;
    return {
      accepted: true,
      diagnostics: result.unmappedChannels.map(channel => ({
        code: "UNMAPPED_CHANNEL", message: `${channel} has no active mapping.`,
      })),
    };
  }

  applyComposedParameters(frame: ComposedParameters): FrameResult {
    this.#ready();
    requireCondition(this.#inputMode === "parameters", "INPUT_MODE_MISMATCH", "Explicitly configureTargets before applying final parameters.");
    const rejection = this.#rejectStale(frame);
    if (rejection) return rejection;
    const parameters = frame.parameters;
    requireCondition(parameters !== null && typeof parameters === "object" && !Array.isArray(parameters),
      "INVALID_PARAMETERS", "Final parameters must be a finite numeric record.");
    const entries = Object.entries(parameters);
    boundedInteger(entries.length, LIMITS.parameters, "final parameter count");
    for (const [id, value] of entries) {
      const parameter = this.#approvedTargets.get(id);
      requireCondition(parameter, "UNAPPROVED_PARAMETER", `Parameter ${id} is not an approved composed target.`);
      requireCondition(typeof value === "number" && Number.isFinite(value) &&
        value >= parameter.minimum && value <= parameter.maximum,
      "INVALID_PARAMETER_VALUE", `${id} must remain within the actual Core-reported bounds.`);
    }
    this.#writes = [...this.#approvedTargets.values()].map(parameter => ({
      index: parameter.index,
      value: Object.hasOwn(parameters, parameter.id) ? parameters[parameter.id]! : parameter.neutral,
    }));
    this.#sequence = frame.sequence;
    this.#age = 0;
    this.#hasFrame = true;
    return { accepted: true, diagnostics: [] };
  }

  update(deltaSeconds: number): void {
    this.#ready();
    finite(deltaSeconds, "deltaSeconds");
    requireCondition(deltaSeconds >= 0, "INVALID_CLOCK", "deltaSeconds must not be negative.");
    this.#age += deltaSeconds;
    if (this.#hasFrame && this.#age > 0.25) {
      this.#neutral();
      this.stopClock();
      this.#report({ code: "FRAME_EXPIRED", message: "No fresh composed frame for 250ms; owned controls reset and clock stopped." });
    }
    const resources = this.#resources;
    requireCondition(resources?.model && resources.renderer, "MODEL_NOT_LOADED", "Load a local model first.");
    const { gl, model, renderer, sdk, animator } = resources;
    requireCondition(!gl.isContextLost(), "CONTEXT_LOST", "Recreate the renderer after WebGL context loss.");
    this.#validateCanvas();
    const writes = this.#writes;
    const apply = () => { for (const write of writes) model.setParameterValueByIndex(write.index, write.value, 1); };
    if (animator) {
      this.#lipSyncAge += deltaSeconds;
      const target = this.#lipSyncAge > 0.3 ? 0 : this.#lipSyncTarget;
      this.#lipSync += (target - this.#lipSync) * Math.min(1, deltaSeconds * (target > this.#lipSync ? 30 : 14));
      const follow = Math.min(1, deltaSeconds * 5);
      this.#look = { x: this.#look.x + (this.#lookTarget.x - this.#look.x) * follow,
        y: this.#look.y + (this.#lookTarget.y - this.#look.y) * follow };
      animator.update(deltaSeconds, { lookX: this.#look.x, lookY: this.#look.y,
        lipSync: this.#hasFrame ? 0 : this.#lipSync, overrides: apply });
    } else {
      // No SDK motion/expression/physics writer runs after these composed parameter writes.
      apply();
    }
    model.update();
    const aspect = this.#canvas.width / this.#canvas.height;
    const view = this.#view;
    const scale = this.#fitScale(model) * view.zoom;
    const matrix = new sdk.CubismMatrix44();
    matrix.setMatrix(new Float32Array([scale / aspect, 0, 0, 0, 0, scale, 0, 0, 0, 0, 1, 0, view.x * view.frame, view.y, 0, 1]));
    renderer.setMvpMatrix(matrix);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, this.#canvas.width, this.#canvas.height);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    renderer.setRenderState(null, [0, 0, this.#canvas.width, this.#canvas.height]);
    renderer.drawModel();
    this.#checkGl(gl);
  }

  startClock(): void {
    this.#ready();
    requireCondition(this.#identity, "MISSING_IDENTITY", "Explicitly resetEpoch before starting the render clock.");
    if (this.#frameHandle !== undefined) return;
    const generation = ++this.#clockGeneration;
    this.#lastTimestamp = this.#services.now();
    const tick: FrameRequestCallback = timestamp => {
      if (generation !== this.#clockGeneration || this.#disposed) return;
      this.#frameHandle = undefined;
      const previous = this.#lastTimestamp;
      this.#lastTimestamp = timestamp;
      try {
        finite(timestamp, "frame timestamp");
        this.update(previous === undefined ? 0 : Math.max(0, (timestamp - previous) / 1000));
      } catch (error) {
        this.stopClock();
        this.#report({
          code: error instanceof Live2DError ? error.code : "RENDER_FAILED",
          message: error instanceof Error ? error.message : String(error),
        });
        return;
      }
      if (generation === this.#clockGeneration) this.#frameHandle = this.#services.requestFrame(tick);
    };
    this.#frameHandle = this.#services.requestFrame(tick);
  }

  stopClock(): void {
    ++this.#clockGeneration;
    if (this.#frameHandle !== undefined) this.#services.cancelFrame(this.#frameHandle);
    this.#frameHandle = undefined;
    this.#lastTimestamp = undefined;
  }

  stop(): void {
    this.#ready();
    this.stopClock();
    this.#identity = undefined;
    this.#sequence = -1;
    this.#neutral();
    this.update(0);
  }

  dispose(): void {
    if (this.#disposed) return;
    this.#disposed = true;
    this.#canvas.removeEventListener("webglcontextlost", this.#contextLost);
    this.#release();
  }

  readonly #contextLost = (event: Event): void => {
    event.preventDefault();
    this.stopClock();
    try {
      this.#report({ code: "CONTEXT_LOST", message: "Live2D context lost; dispose and recreate the renderer." });
    } finally {
      this.dispose();
    }
  };

  #neutral(): void {
    // The animator restores its own saved state each frame; only static models need explicit neutral writes.
    this.#writes = this.#resources?.animator ? [] : this.#parameters.map(p => ({ index: p.index, value: p.neutral }));
    this.#hasFrame = false;
    this.#age = 0;
  }

  #animatorAssets(bundle: LocalModelBundle): AnimatorAssets {
    const description = bundle.description;
    const buffer = (name: string): ArrayBuffer => bundle.read(name).buffer;
    const present = new Set(this.#parameters.map(p => p.id));
    const declared = (ids: readonly string[]) => ids.filter(id => present.has(id));
    // Models made for face tracking (VTube Studio) often leave these groups empty; use the standard Cubism eye and
    // mouth parameters then, so the character still blinks and talks.
    let eyeBlink = declared(description.groups.eyeBlink);
    if (eyeBlink.length === 0) eyeBlink = declared(["ParamEyeLOpen", "ParamEyeROpen"]);
    let lipSync = declared(description.groups.lipSync);
    if (lipSync.length === 0) lipSync = declared(["ParamMouthOpenY"]);
    this.#eyeBlinkIds = Object.freeze(eyeBlink);
    this.#lipSyncIds = Object.freeze(lipSync);
    return {
      parameterIds: this.#parameters.map(p => p.id),
      motions: Object.fromEntries(Object.entries(description.motions).map(([group, entries]) => [group,
        entries.map(entry => ({
          bytes: buffer(entry.file),
          ...(entry.fadeIn !== undefined ? { fadeIn: entry.fadeIn } : {}),
          ...(entry.fadeOut !== undefined ? { fadeOut: entry.fadeOut } : {}),
        }))])),
      expressions: description.expressions.map(entry => ({ name: entry.name, bytes: buffer(entry.file) })),
      ...(description.physics ? { physics: buffer(description.physics) } : {}),
      ...(description.pose ? { pose: buffer(description.pose) } : {}),
      eyeBlinkIds: this.#eyeBlinkIds,
      lipSyncIds: this.#lipSyncIds,
    };
  }

  #ensureAlive(): void {
    requireCondition(!this.#disposed, "DISPOSED", "This adapter is disposed; create a new instance.");
  }

  #ready(): void {
    this.#ensureAlive();
    requireCondition(this.#resources?.model && !this.#loading, "MODEL_NOT_LOADED", "Load must finish before using the model.");
  }

  #rejectStale(frame: { identity: RenderIdentity; sequence: number; configurationId: string }): FrameResult | undefined {
    requireCondition(frame !== null && typeof frame === "object", "INVALID_FRAME", "A composed frame is required.");
    validateIdentity(frame.identity);
    boundedInteger(frame.sequence, 2_147_483_647, "sequence");
    if (!this.#identity || !sameIdentity(frame.identity, this.#identity)) return {
      accepted: false, diagnostics: [{ code: "STALE_IDENTITY", message: "Frame does not match the trusted active render identity." }],
    };
    if (frame.sequence <= this.#sequence) return {
      accepted: false, diagnostics: [{ code: "STALE_SEQUENCE", message: "Frame sequence is not newer than the active composed stream." }],
    };
    if (frame.configurationId !== this.#configurationId) return {
      accepted: false, diagnostics: [{ code: "STALE_CONFIGURATION", message: "Model or mapping configuration has changed." }],
    };
    return undefined;
  }

  /** Model units to clip space that fits the whole model canvas into the frame (see setView) of the render canvas. */
  #fitScale(model: CubismModel): number {
    const frameAspect = this.#canvas.width * this.#view.frame / this.#canvas.height;
    return Math.min(2 / model.getCanvasHeight(), 2 * frameAspect / model.getCanvasWidth());
  }

  #validateCanvas(): void {
    for (const dimension of [this.#canvas.width, this.#canvas.height]) {
      boundedInteger(dimension, LIMITS.canvasDimension, "canvas dimension");
      requireCondition(dimension > 0, "RESOURCE_LIMIT", "Canvas dimensions must be positive.");
    }
  }

  #validateGeometry(model: CubismModel, textureCount: number): void {
    for (const value of [model.getCanvasWidth(), model.getCanvasHeight()]) {
      requireCondition(Number.isFinite(value) && value >= 0.001 && value <= 10_000,
        "INVALID_MODEL_GEOMETRY", "Core returned invalid canvas dimensions.");
    }
    const count = model.getDrawableCount();
    boundedInteger(count, LIMITS.drawables, "drawable count");
    requireCondition(count > 0, "INVALID_MODEL_GEOMETRY", "The model has no drawable meshes.");
    let vertices = 0;
    let indices = 0;
    for (let i = 0; i < count; i++) {
      const vertexCount = model.getDrawableVertexCount(i);
      const indexCount = model.getDrawableVertexIndexCount(i);
      boundedInteger(vertexCount, LIMITS.vertices, "mesh vertices");
      boundedInteger(indexCount, LIMITS.indices, "mesh indices");
      vertices += vertexCount;
      indices += indexCount;
      boundedInteger(vertices, LIMITS.vertices, "model vertices");
      boundedInteger(indices, LIMITS.indices, "model indices");
      const texture = model.getDrawableTextureIndex(i);
      boundedInteger(texture, textureCount - 1, "drawable texture index");
    }
  }

  #checkGl(gl: WebGLRenderingContext): void {
    const error = gl.getError();
    requireCondition(error === gl.NO_ERROR, "WEBGL_ERROR", `Live2D WebGL operation failed (0x${error.toString(16)}).`);
  }

  #release(): void {
    this.stopClock();
    ++this.#generation;
    const resources = this.#resources;
    this.#resources = undefined;
    this.#bundle = undefined;
    this.#plan = undefined;
    this.#approvedTargets.clear();
    this.#inputMode = "channels";
    this.#configurationId = crypto.randomUUID();
    this.#parameters = [];
    this.#writes = [];
    this.#identity = undefined;
    this.#sequence = -1;
    this.#hasFrame = false;
    this.#age = 0;
    this.#loading = false;
    this.#lipSyncTarget = 0;
    this.#lipSync = 0;
    this.#lipSyncAge = Number.POSITIVE_INFINITY;
    this.#lookTarget = { x: 0, y: 0 };
    this.#look = { x: 0, y: 0 };
    this.#modelTop = undefined;
    this.#eyeBlinkIds = [];
    this.#lipSyncIds = [];
    if (!resources) return;
    const errors: unknown[] = [];
    const release = (action: () => void) => {
      try { action(); } catch (error) { errors.push(error); }
    };
    release(() => resources.abort.abort());
    release(() => resources.animator?.release());
    release(() => resources.renderer?.release());
    for (const texture of resources.textures) release(() => resources.gl.deleteTexture(texture));
    release(() => {
      if (!resources.gl.isContextLost()) {
        resources.gl.bindFramebuffer(resources.gl.FRAMEBUFFER, null);
        resources.gl.clearColor(0, 0, 0, 0);
        resources.gl.clear(resources.gl.COLOR_BUFFER_BIT);
      }
    });
    release(() => {
      if (resources.model) resources.moc?.deleteModel(resources.model);
    });
    release(() => resources.moc?.release());
    release(() => {
      if (resources.sdk.CubismFramework.isInitialized()) resources.sdk.CubismFramework.dispose();
    });
    release(() => resources.sdk.CubismFramework.cleanUp());
    if (activeAdapter === this) activeAdapter = undefined;
    if (errors.length) throw new AggregateError(errors, "Live2D cleanup failed.");
  }
}
