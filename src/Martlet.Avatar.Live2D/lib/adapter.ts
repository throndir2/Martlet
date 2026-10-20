import { LIMITS, LocalModelBundle, pngDimensions, scaledSize } from "./assets.js";
import { boundedInteger, Diagnostic, finite, Live2DError, requireCondition } from "./diagnostics.js";
import { type EyeFields, eyeFrame, type EyeHint, type EyesFrom, eyeShape, hintEye, hintMiddle, irisBox, meshMisses, pinEye,
  readEyeHint } from "./eyes.js";
import { type Carrier, type CheekFrame, type Face, faceFeatures, type FaceFeatures, faceFromBox, faceFromHint, type FaceHint,
  faceSkin, type FaceSource, type FeatureOffsets, bounds, headRoll, measuredFeatures, pinFace, type PinnedFace, type Point,
  trackFace, turnFace } from "./face.js";
import { BLUSH_PARAMETERS, type Gesture, GesturePlayer, type GestureState, isBlush, isGesture, supportedGestures } from "./gestures.js";
import { CHAIN_LIMITS, findSwingingChains, type SwingingChain } from "./chains.js";
import { Capabilities, ChannelMapping, inspectParameters, MappingPlan, Parameter } from "./mapping.js";
import { checkRuntime, type Animator, type AnimatorAssets, CubismMoc, CubismModel, CubismRenderer, SdkModules } from "./sdk.js";
import { HAIR_PART, hitTestModel, type Live2DHit } from "./touch.js";
import { checkGl, type EyeState, findCarriers, findEyesSafely, findFace, findSkin, NO_EYES, validateGeometry, visibleTop }
  from "./modelProbe.js";
import { type BrowserServices, browserServices, type ComposedFrame, type ComposedParameters, type FrameResult, type ModelSummary,
  type MouthReading, type RenderIdentity, sameIdentity, validateIdentity, VOICE_HOLD_SECONDS } from "./contract.js";
export { type BrowserServices, browserServices, type ComposedFrame, type ComposedParameters, type FrameResult, type ModelSummary,
  type MouthReading, type RenderIdentity, VOICE_HOLD_SECONDS } from "./contract.js";

// How far a mouth parameter's value opens the mouth: 0 at the model's rest, 1 at its maximum.
const opening = (parameter: Parameter, value: number): number =>
  parameter.maximum > parameter.neutral ? Math.max(0, Math.min(1, (value - parameter.neutral) / (parameter.maximum - parameter.neutral))) : 0;

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
  // Seconds since the voice (a lip-sync level or a composed frame) last moved the mouth, whether it has the mouth now and how
  // much (0 to 1, eased), the mouth parameters it takes (ParamMouthOpenY first) and the last mouth reading's openings.
  #voiceAge = Number.POSITIVE_INFINITY;
  #speaking = false;
  #talk = 0;
  #mouth: readonly Parameter[] = [];
  #mouthEmote = 0;
  #mouthOpen = 0;
  #lookTarget = { x: 0, y: 0 };
  #look = { x: 0, y: 0 };
  #gestures = new GesturePlayer();
  #view = { zoom: 1, x: 0, y: 0, frame: 1 };
  #modelTop: number | undefined;
  #faceSource: FaceSource | undefined;
  #faceHint: Face | undefined;
  #carriers: readonly Carrier[] = [];
  /** The ID of the drawable the face is pinned to when it is the face's skin (see faceSkin). */
  #skin: string | undefined;
  #pinned: PinnedFace | undefined;
  #hintPinned: PinnedFace | undefined;
  /** The face's parts as vision measured them (see setEyeHint): their offsets, the face at rest with its middle on the
   *  measured eye line, that face pinned to the model's meshes, and the cheeks' radius in face widths. */
  #measured: { readonly offsets: FeatureOffsets; readonly rest: Face; readonly pinned?: PinnedFace; readonly cheekSize?: number }
    | undefined;
  #faceProbeMilliseconds = 0;
  #eyes: EyeState = NO_EYES;
  #eyeBlinkIds: readonly string[] = [];
  #lipSyncIds: readonly string[] = [];
  #partNames: ReadonlyMap<string, string> = new Map();
  /** Every drawable's vertex positions in the rest pose (as the model loads, which the touch zones picture shows), so a touch
   *  can be traced back to where it was then (see hitTest). */
  #rest: readonly Float32Array[] = [];
  /** The drawables that sit in a part the model names as hair (see HAIR_PART), for touches. */
  #hair: ReadonlySet<number> = new Set();

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

  /** Speech loudness 0..1; decays to closed when not refreshed for 300ms. While the voice speaks (until VOICE_HOLD_SECONDS
   *  after its last level), it has the mouth: what the emotes put on the mouth eases out, and comes back after. */
  setLipSync(level: number): void {
    this.#ready();
    finite(level, "lip-sync level");
    this.#lipSyncTarget = Math.max(0, Math.min(1, level));
    this.#lipSyncAge = 0;
    this.#voiceAge = 0;
  }

  /** Who moves the mouth now, for Martlet's MCP (character_mouth); undefined while the model isn't animated or has no mouth
   *  parameter. */
  get mouthReading(): MouthReading | undefined {
    const [mouth] = this.#mouth;
    if (!this.animated || !mouth) return undefined;
    return Object.freeze({ parameter: mouth.id, voice: this.#talk, speaking: this.#speaking, level: this.#lipSync,
      emote: this.#mouthEmote, open: this.#mouthOpen });
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
    this.#view = { zoom: Math.max(0.1, Math.min(32, zoom)), x, y, frame: Math.max(0.1, Math.min(1, frame)) };
  }

  /** Top of the visible character (top of the head) in fitted clip space, before view zoom/pan; 1 is the canvas top. */
  get contentTop(): number | undefined {
    const model = this.#resources?.model;
    return model && this.#modelTop !== undefined && !this.#loading ? this.#modelTop * this.#fitScale(model) : undefined;
  }

  /** Each visible drawable's bounds now, as fractions of the canvas (origin top-left, +y down), and the ID of the part it belongs
   *  to (when the SDK says), for touch zones. */
  drawableBounds(): { id: string; left: number; top: number; right: number; bottom: number; part?: string }[] {
    this.#ready();
    const model = this.#resources!.model!;
    const aspect = this.#canvas.width / this.#canvas.height;
    const view = this.#view;
    const scale = this.#fitScale(model) * view.zoom;
    const parts = this.#partIds(model);
    const bounds = [];
    for (let i = 0; i < model.getDrawableCount() && bounds.length < 2000; i++) {
      if (!model.getDrawableDynamicFlagIsVisible(i) || model.getDrawableOpacity(i) < 0.05) continue;
      const vertices = model.getDrawableVertices(i);
      let left = Infinity, right = -Infinity, top = Infinity, bottom = -Infinity;
      for (let v = 0; v + 1 < vertices.length; v += 2) {
        const x = (vertices[v]! * scale / aspect + view.x * view.frame + 1) / 2;
        const y = (1 - (vertices[v + 1]! * scale + view.y)) / 2;
        left = Math.min(left, x); right = Math.max(right, x); top = Math.min(top, y); bottom = Math.max(bottom, y);
      }
      const part = parts[model.getDrawableParentPartIndex?.(i) ?? -1];
      if (Number.isFinite(left) && Number.isFinite(top) && right > left && bottom > top)
        bounds.push({ id: model.getDrawableId(i).getString().s, left, top, right, bottom, ...(part ? { part } : {}) });
    }
    return bounds;
  }

  /** The model's own parts (groups of drawables and parts) for touch zones: each part's ID, the name the model's DisplayInfo
   *  file gives it, if any, and its parent part's ID (none for a top part). At most `LIMITS.parts`; empty when the SDK can't
   *  say. */
  modelParts(): { id: string; name?: string; parent?: string }[] {
    this.#ready();
    const model = this.#resources!.model!;
    const ids = this.#partIds(model);
    const parents = model.getPartParentPartIndices?.();
    return ids.map((id, i) => {
      const name = this.#partNames.get(id), parent = parents ? ids[parents[i] ?? -1] : undefined;
      return { id, ...(name ? { name } : {}), ...(parent ? { parent } : {}) };
    });
  }

  #partIds(model: CubismModel): string[] {
    if (!model.getPartCount || !model.getPartId) return [];
    const ids: string[] = [];
    for (let i = 0; i < Math.min(model.getPartCount(), LIMITS.parts); i++) ids.push(model.getPartId(i).getString().s);
    return ids;
  }

  /** The parts of the model that swing on their own (a tail, a ponytail, a skirt, ears), for touch zones: the drawables each of
   *  its physics settings moves, root first, and where they can reach, as fractions of the canvas (origin top-left, +y down)
   *  as last framed (see findSwingingChains). The pose doesn't change. Empty without a physics file. */
  swingingChains(): SwingingChain[] {
    this.#ready();
    const model = this.#resources!.model!;
    const settings = this.#bundle?.physicsSettings() ?? [];
    if (settings.length === 0) return [];
    const aspect = this.#canvas.width / this.#canvas.height;
    const view = this.#view;
    const scale = this.#fitScale(model) * view.zoom;
    const until = this.#services.now() + CHAIN_LIMITS.milliseconds;
    return findSwingingChains(model, settings,
      (x, y) => [(x * scale / aspect + view.x * view.frame + 1) / 2, (1 - (y * scale + view.y)) / 2], () => this.#services.now() > until);
  }

  /** The drawables that sit in a part the model names as hair (its ID or DisplayInfo name, or a part it sits in), such as
   *  Hiyori's side locks (前髪右, in 横髪 PartHairSide); none when the SDK can't say. */
  #hairDrawables(model: CubismModel): ReadonlySet<number> {
    const hair = new Set<number>();
    const ids = this.#partIds(model), parents = model.getPartParentPartIndices?.();
    if (!model.getDrawableParentPartIndex || ids.length === 0) return hair;
    const named = ids.map(id => HAIR_PART.test(id) || HAIR_PART.test(this.#partNames.get(id) ?? ""));
    for (let i = 0; i < model.getDrawableCount(); i++)
      for (let part = model.getDrawableParentPartIndex(i), depth = 0; part >= 0 && part < ids.length && depth < 64;
        part = parents?.[part] ?? -1, depth++)
        if (named[part]) { hair.add(i); break; }
    return hair;
  }

  playMotion(group: string): boolean {
    this.#ready();
    return this.#resources?.animator?.playMotion(group) ?? false;
  }

  setExpression(name: string | null): boolean {
    this.#ready();
    return this.#resources?.animator?.setExpression(name) ?? false;
  }

  /** Turns a lingering expression on or off. Held expressions layer with each other and with `setExpression`'s. */
  holdExpression(name: string, on: boolean): boolean {
    this.#ready();
    return this.#resources?.animator?.holdExpression?.(name, on) ?? false;
  }

  /** Martlet's gestures this model has the standard parameters for. */
  get gestures(): readonly Gesture[] { return this.animated ? supportedGestures(this.#parameters.map(p => p.id)) : []; }

  /**
   * What of the character is at a point of the canvas (`x`, `y` fractions 0..1, origin top-left, +y down) as last drawn:
   * the authored hit areas there and the visible drawables, topmost first, and whether the topmost is hair (`hair`: the
   * model names a part it sits in as hair). `restCanvas` is where the touched point of the character was in the rest pose
   * (the pose the touch zones picture shows), traced on the touched drawable's own triangles and drawn in the canvas's
   * framing now (fractions, like `x` and `y`): the same spot of the skin however the head turns, tilts or nods, or an idle
   * motion moves the body. Undefined when the point misses the model.
   */
  hitTest(x: number, y: number): (Live2DHit & { readonly restCanvas?: { readonly x: number; readonly y: number } }) | undefined {
    this.#ready();
    finite(x, "touch x");
    finite(y, "touch y");
    const model = this.#resources!.model!;
    const view = this.#view;
    const scale = this.#fitScale(model) * view.zoom;
    const aspect = this.#canvas.width / this.#canvas.height;
    const modelX = (2 * x - 1 - view.x * view.frame) * aspect / scale;
    const modelY = (1 - 2 * y - view.y) / scale;
    const hit = hitTestModel(model, this.#bundle?.description.hitAreas ?? [], modelX, modelY, i => this.#rest[i], i => this.#hair.has(i));
    if (!hit?.rest) return hit;
    const restCanvas = { x: (hit.rest.x * scale / aspect + view.x * view.frame + 1) / 2, y: (1 - (hit.rest.y * scale + view.y)) / 2 };
    return [restCanvas.x, restCanvas.y].every(Number.isFinite) ? Object.freeze({ ...hit, restCanvas: Object.freeze(restCanvas) }) : hit;
  }

  /** Starts one of Martlet's gestures (see `gestures`), replacing one already playing, or with `hold` keeps a holdable one
   *  (shy, drowsy, pout, look_away, a blush level, eyes_up, mouth_open) until `endGesture`; a gesture played meanwhile plays
   *  on top of it. Held gestures layer: holding one lets go only of those that move a part it moves too (`HOLD_PARTS`). */
  gesture(name: string, hold = false): boolean {
    this.#ready();
    if (!isGesture(name) || !this.gestures.includes(name)) return false;
    // Without ParamCheek the page draws every blush level over the face instead.
    if (isBlush(name) && !BLUSH_PARAMETERS.every(id => this.#parameters.some(p => p.id === id))) return false;
    this.#gestures.play(name, hold);
    return true;
  }

  /** Lets a held gesture go (eased out); one playing once just finishes. */
  endGesture(name: string): void { this.#gestures.end(name); }

  /** The gesture playing once, if any, and every gesture held. */
  get gestureState(): GestureState { return this.#gestures.state; }

  /**
   * Refines where the face is with what vision found (fractions of the model's canvas, 0,0 at its top left, y down; the
   * width a fraction of its width); undefined goes back to Martlet's estimate. Vision sees the pose drawn now, so the hint
   * is pinned to the mesh vertices where they are now and then follows them like the estimate.
   */
  setFaceHint(hint: FaceHint | undefined): void {
    const model = this.#resources?.model;
    this.#faceHint = hint && model ? faceFromHint(hint, model.getCanvasWidth(), model.getCanvasHeight()) : undefined;
    const now = model ? this.#carriers.map(c => {
      const vertices = model.getDrawableVertices(c.drawable);
      return { ...c, x: vertices[2 * c.vertex]!, y: vertices[2 * c.vertex + 1]! };
    }) : [];
    this.#hintPinned = this.#faceHint ? pinFace(this.#faceHint, now) : undefined;
  }

  /** How many mesh vertices the face is pinned to (0: it follows the head's angles instead), the ID of the drawable they
   *  belong to when they are the face's skin (`skin`, see faceSkin; absent when they are the vertices that ride the head),
   *  how long finding them took when the model loaded, and how long finding the eyes' meshes took then. */
  get faceTracking(): { readonly carriers: number; readonly skin?: string; readonly milliseconds: number;
    readonly eyeMilliseconds: number } {
    return { carriers: this.#carriers.length, ...(this.#skin !== undefined ? { skin: this.#skin } : {}),
      milliseconds: Math.round(this.#faceProbeMilliseconds), eyeMilliseconds: Math.round(this.#eyes.milliseconds) };
  }

  /**
   * Uses the face measured by vision (see EyeHint: face widths from the face's middle, measured in the rest pose): its eyes for
   * each eye the model's meshes can't give or give clearly away from the measured eye (see meshMisses), and its eye line,
   * cheeks and mouth for the face anchor (see measuredFeatures), so the blush and the drawings around the face sit where
   * vision saw them; undefined clears it. The hinted eyes and parts are pinned to the face like its other features, a hinted
   * iris follows ParamEyeBallX and ParamEyeBallY and its opening closes with the eye's open parameter. Returns where the eyes
   * come from now (see eyesFrom).
   */
  setEyeHint(hint: EyeHint | undefined): EyesFrom {
    const eyes = this.#eyes, face = eyes.face, read = face ? readEyeHint(hint) : undefined;
    const found = eyes.found ?? eyes.mesh;
    const keep = (side: "left" | "right") => {
      const own = found[side], seen = read?.[side];
      return own && !(seen && face && meshMisses(own, seen, face)) ? { [side]: own } : {};
    };
    const pin = (side: "left" | "right") => {
      const eye = read?.[side], pinned = eye && face ? pinEye(hintMiddle(eye, face), this.#carriers, face) : undefined;
      return pinned ? { [side]: pinned } : {};
    };
    const { hint: _, ...rest } = eyes;
    this.#eyes = { ...rest, found, mesh: { ...keep("left"), ...keep("right") }, ...(read ? { hint: read } : {}),
      hintPins: { ...pin("left"), ...pin("right") } };
    this.#measured = undefined;
    if (read && face) {
      const point = (p: { x: number; y: number } | undefined) => p && { x: p.x, y: p.y };
      const offsets: FeatureOffsets = { eyeLeft: point(read.left?.eye), eyeRight: point(read.right?.eye),
        cheekLeft: point(read.cheekLeft), cheekRight: point(read.cheekRight), mouth: point(read.mouth) };
      const features = measuredFeatures(face, offsets);
      const pinned = this.#carriers.length ? pinFace(face, this.#carriers, features) : undefined;
      this.#measured = { offsets, rest: { x: features.x, y: features.y, width: face.width, roll: face.roll },
        ...(pinned ? { pinned } : {}),
        ...(read.cheekLeft && read.cheekRight ? { cheekSize: (read.cheekLeft.r + read.cheekRight.r) / 2 } : {}) };
    }
    return this.eyesFrom;
  }

  /** Where the eyes' irises and openings come from: "mesh" when the model's meshes give both eyes, "vision" when the hint gives
   *  what they don't, "estimate" while an eye has neither (faceAnchor then leaves its iris and opening out). */
  get eyesFrom(): EyesFrom {
    const eyes = this.#eyes;
    const from = (side: "left" | "right"): EyesFrom => eyes.mesh[side] ? "mesh" : eyes.hint?.[side] && eyes.face ? "vision" : "estimate";
    const left = from("left"), right = from("right");
    return left === "estimate" || right === "estimate" ? "estimate" : left === "vision" || right === "vision" ? "vision" : "mesh";
  }

  /**
   * Where the face is now, in the canvas's drawing-buffer pixels (y down), for drawings over it: its middle, width, roll
   * (radians, clockwise), the cheeks, eyes and mouth (left and right as the viewer sees them) and the top of the head.
   * Pinned to the model's meshes (`tracking` "mesh", with each cheek's surface: one face width across and down it): to the
   * face's skin when one drawable holds it (see `faceTracking`), else to the vertices that ride the head, so it follows
   * whatever moves the head; otherwise estimated from the head's angles ("estimate"). With a face measured by vision (see
   * setEyeHint) the middle is on the measured eye line and the cheeks and mouth where vision saw them, and `cheekSize` is the
   * measured cheeks' radius in face widths. Each eye with an iris (from the model's meshes or the eye hint, see `eyesFrom`)
   * adds its iris (`irisLeft`, `irisRight`: middle and radii across and down the face) and its opening (`eyeLeftShape`,
   * `eyeRightShape`), and puts `eyeLeft`/`eyeRight` at the eye's middle. Undefined before a model shows.
   */
  faceAnchor(): { x: number; y: number; width: number; angle: number; cheekLeft: Point; cheekRight: Point; eyeLeft: Point;
    eyeRight: Point; mouth: Point; top: Point; tracking: "mesh" | "estimate"; cheekSize?: number;
    cheekLeftFrame?: { right: Point; down: Point; visible: number }; cheekRightFrame?: { right: Point; down: Point; visible: number } }
    & EyeFields | undefined {
    const model = this.#resources?.model;
    if (!model || this.#loading) return undefined;
    // A face hint (setFaceHint) replaces Martlet's estimate; the measured parts are around that estimate.
    const measured = this.#faceHint ? undefined : this.#measured;
    const pinned = this.#faceHint ? this.#hintPinned : measured?.pinned ?? this.#pinned;
    const tracked = pinned && trackFace(pinned, i => model.getDrawableVertices(i));
    let features: FaceFeatures | undefined = tracked && tracked.width > 0 ? tracked : undefined;
    // Measured parts that couldn't be pinned themselves move with the face as Martlet pins it.
    if (features && measured && !measured.pinned) features = measuredFeatures(features, measured.offsets);
    if (!features) {
      const face = this.#estimatedFace(model);
      if (!face) return undefined;
      features = measured ? measuredFeatures(face, measured.offsets) : faceFeatures(face);
    }
    const onMesh = !!tracked && (features === tracked || !!measured && !measured.pinned);
    const { width, height } = this.#canvas;
    const scale = this.#fitScale(model) * this.#view.zoom, aspect = width / height;
    const point = (p: Point): Point => ({
      x: ((p.x * scale / aspect + this.#view.x * this.#view.frame) + 1) / 2 * width,
      y: (1 - (p.y * scale + this.#view.y)) / 2 * height,
    });
    // Model units to canvas pixels: the same across and down, with y turned down.
    const step = scale / aspect * width / 2;
    const vector = (v: Point): Point => ({ x: v.x * step, y: -v.y * step });
    const frame = (f: CheekFrame) => ({ right: vector(f.right), down: vector(f.down), visible: 1 });
    const middle = point(features);
    return { x: middle.x, y: middle.y, width: features.width * step, angle: -features.roll,
      cheekLeft: point(features.cheekLeft), cheekRight: point(features.cheekRight), eyeLeft: point(features.eyeLeft),
      eyeRight: point(features.eyeRight), mouth: point(features.mouth), top: point(features.top),
      tracking: onMesh ? "mesh" : "estimate",
      ...(onMesh && tracked ? { cheekLeftFrame: frame(tracked.cheekLeftFrame), cheekRightFrame: frame(tracked.cheekRightFrame) } : {}),
      ...(measured?.cheekSize !== undefined ? { cheekSize: measured.cheekSize } : {}),
      ...this.#eyeFields(model, features, point, step, measured) };
  }

  /** The eye fields of faceAnchor (canvas pixels; `point` and `step` as there) for the face as found now, `features` (around
   *  the measured face when `measured`). Eyes that can't be read now are only left out. */
  #eyeFields(model: CubismModel, features: FaceFeatures, point: (p: Point) => Point, step: number,
    measured: { readonly rest: Face } | undefined): EyeFields {
    const eyes = this.#eyes, rest = eyes.face, eyesFrom = this.eyesFrom;
    if (!rest) return { eyesFrom };
    try {
      return { eyesFrom, ...this.#eyesNow(model, features, point, step, eyes, rest, measured?.rest ?? rest) };
    } catch { return { eyesFrom }; }
  }

  /** The eyes now; `rest` is the face the eyes were found and measured on, `restNow` the face at rest whose middle the face
   *  as found now (`features`) follows (rest itself, or its middle moved to the measured eye line). */
  #eyesNow(model: CubismModel, features: FaceFeatures, point: (p: Point) => Point, step: number, eyes: EyeState,
    rest: Face, restNow: Face): Omit<EyeFields, "eyesFrom"> {
    const origin = point({ x: 0, y: 0 });
    const pixel = (x: number, y: number): Point => ({ x: origin.x + x * step, y: origin.y - y * step });
    const at = (drawable: number) => model.getDrawableVertices(drawable);
    const shown = (drawable: number) => model.getDrawableDynamicFlagIsVisible(drawable) && model.getDrawableOpacity(drawable) >= 0.05;
    // A parameter now on -1..1 (its range's ends) or its share of its rest value (an eye's openness), 0 or 1 when absent.
    const parameter = (id: string, kind: "range" | "rest") => {
      const p = this.#parameters.find(candidate => candidate.id === id), value = p && model.getParameterValueByIndex?.(p.index);
      if (!p || typeof value !== "number" || !Number.isFinite(value)) return kind === "rest" ? 1 : 0;
      if (kind === "rest") return p.neutral > 0 ? value / p.neutral : value;
      return value >= 0 ? (p.maximum > 0 ? value / p.maximum : 0) : (p.minimum < 0 ? -value / p.minimum : 0);
    };
    const eye = (side: "left" | "right") => {
      const mesh = eyes.mesh[side], hint = eyes.hint?.[side];
      if (mesh) {
        const box = irisBox(at(mesh.iris), features.roll);
        if (!box) return undefined;
        const frame = eyeFrame(mesh.middle, eyes.meshPins[side], at, restNow, features);
        return { iris: { ...pixel(box.x, box.y), rx: box.rx * step, ry: box.ry * step }, shape: eyeShape(mesh, at, shown, pixel),
          middle: frame ? pixel(frame.x, frame.y) : pixel(box.x, box.y) };
      }
      const frame = hint && eyeFrame(hintMiddle(hint, rest), eyes.hintPins[side], at, restNow, features);
      if (!hint || !frame) return undefined;
      // ParamEyeLOpen is the character's left eye: the one on the viewer's right.
      const now = hintEye(hint, frame, rest, parameter("ParamEyeBallX", "range"), parameter("ParamEyeBallY", "range"),
        parameter(side === "left" ? "ParamEyeROpen" : "ParamEyeLOpen", "rest"), pixel);
      return { iris: { ...pixel(now.iris.x, now.iris.y), rx: now.iris.rx * step, ry: now.iris.ry * step },
        shape: { points: now.outline }, middle: now.middle };
    };
    const left = eye("left"), right = eye("right");
    return { ...(left ? { irisLeft: left.iris, eyeLeftShape: left.shape, eyeLeft: left.middle } : {}),
      ...(right ? { irisRight: right.iris, eyeRightShape: right.shape, eyeRight: right.middle } : {}) };
  }

  /** The face estimated from the head's angles (or the box of its meshes now), for a model whose face couldn't be pinned. */
  #estimatedFace(model: CubismModel): Face | undefined {
    const source = this.#faceSource;
    if (!source && !this.#faceHint) return undefined;
    const value = (id: string) => {
      const parameter = this.#parameters.find(p => p.id === id);
      return parameter && model.getParameterValueByIndex ? model.getParameterValueByIndex(parameter.index) : 0;
    };
    const roll = headRoll(value("ParamAngleZ"));
    let face: Face | undefined;
    if (this.#faceHint) face = turnFace(this.#faceHint, value("ParamAngleX"), value("ParamAngleY"), value("ParamAngleZ"));
    else if (source!.kind === "fixed") face = turnFace(source!.face, value("ParamAngleX"), value("ParamAngleY"), value("ParamAngleZ"));
    else {
      // The meshes already turn with the head; only the roll is read from the angle.
      const box = bounds(source!.drawables.map(i => model.getDrawableVertices(i)));
      face = box && faceFromBox(source!.kind, box, roll);
    }
    return face && [face.x, face.y, face.width, face.roll].every(Number.isFinite) ? face : undefined;
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
      validateGeometry(model, bundle.description.textures.length);
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
          checkGl(gl);
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
      this.#partNames = bundle.partNames();
      this.#plan = new MappingPlan(this.#parameters, bundle.description, []);
      this.#loading = false;
      this.#neutral();
      this.update(0);
      this.#modelTop = visibleTop(model);
      this.#rest = Array.from({ length: model.getDrawableCount() }, (_, i) => Float32Array.from(model.getDrawableVertices(i)));
      this.#hair = this.#hairDrawables(model);
      this.#faceSource = findFace(model, bundle);
      const rest = this.#restFace(model);
      const started = this.#services.now();
      // Pinned to the face's skin when a drawable holds the face, else to the vertices that ride the head.
      const skin = rest ? findSkin(model, rest) : undefined, onSkin = rest && skin ? pinFace(rest, skin.carriers) : undefined;
      if (skin && onSkin) [this.#carriers, this.#skin, this.#pinned] = [skin.carriers, skin.id, onSkin];
      else {
        this.#carriers = rest ? findCarriers(model, rest, this.#parameters) : [];
        this.#pinned = rest ? pinFace(rest, this.#carriers) : undefined;
      }
      this.#faceProbeMilliseconds = this.#services.now() - started;
      this.#eyes = rest ? findEyesSafely(model, rest, this.#parameters, this.#carriers, () => this.#services.now()) : NO_EYES;
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
    this.#voiceAge = 0;
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
    this.#voiceAge = 0;
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
      this.#voiceAge += deltaSeconds;
      const target = this.#lipSyncAge > 0.3 ? 0 : this.#lipSyncTarget;
      this.#lipSync += (target - this.#lipSync) * Math.min(1, deltaSeconds * (target > this.#lipSync ? 30 : 14));
      this.#speaking = this.#hasFrame || this.#voiceAge <= VOICE_HOLD_SECONDS;
      // The voice takes the mouth quickly, so its first sound shows, and gives it back gently.
      this.#talk += ((this.#speaking ? 1 : 0) - this.#talk) * Math.min(1, deltaSeconds * (this.#speaking ? 10 : 5));
      const follow = Math.min(1, deltaSeconds * 5);
      this.#look = { x: this.#look.x + (this.#lookTarget.x - this.#look.x) * follow,
        y: this.#look.y + (this.#lookTarget.y - this.#look.y) * follow };
      // A gesture holding the eyes keeps the look out of them.
      const gesture = this.#gestures.advance(deltaSeconds, this.#look);
      animator.update(deltaSeconds, { lookX: this.#look.x + (gesture?.look.x ?? 0), lookY: this.#look.y + (gesture?.look.y ?? 0),
        lipSync: this.#hasFrame ? 0 : this.#lipSync, overrides: () => { this.#giveMouth(model); apply(); },
        ...(gesture ? { gesture: gesture.parameters } : {}) });
      const [mouth] = this.#mouth, value = mouth && model.getParameterValueByIndex?.(mouth.index);
      if (mouth && value !== undefined) this.#mouthOpen = opening(mouth, value);
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
    checkGl(gl);
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

  /** Runs after idle motions, expressions, held expressions and gestures wrote their frame, before lip-sync: while the voice
   *  has the mouth (`#talk`), what they put on the mouth parameters eases back to the model's rest, so emotes that set the
   *  mouth (an open mouth, a shout) never hold it still while the character talks; lip-sync then moves it alone. As the voice
   *  lets go, their mouth comes back. Notes how far the emotes open the mouth for the mouth reading. */
  #giveMouth(model: CubismModel): void {
    if (!model.getParameterValueByIndex) return;
    for (const [i, parameter] of this.#mouth.entries()) {
      const value = model.getParameterValueByIndex(parameter.index);
      if (i === 0) this.#mouthEmote = opening(parameter, value);
      if (this.#talk > 0.001) model.setParameterValueByIndex(parameter.index, value + (parameter.neutral - value) * this.#talk, 1);
    }
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
    // The voice takes the mouth's opening: the lip-sync parameters and ParamMouthOpenY (which mouth_open opens).
    this.#mouth = Object.freeze(this.#parameters.filter(p => p.id === "ParamMouthOpenY" || lipSync.includes(p.id))
      .sort((a, b) => Number(b.id === "ParamMouthOpenY") - Number(a.id === "ParamMouthOpenY")));
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

  /** The face in the pose the model has now (at load: at rest), from how it was found. */
  #restFace(model: CubismModel): Face | undefined {
    const source = this.#faceSource;
    if (!source) return undefined;
    if (source.kind === "fixed") return source.face;
    const box = bounds(source.drawables.map(i => model.getDrawableVertices(i)));
    return box && faceFromBox(source.kind, box);
  }

  #validateCanvas(): void {
    for (const dimension of [this.#canvas.width, this.#canvas.height]) {
      boundedInteger(dimension, LIMITS.canvasDimension, "canvas dimension");
      requireCondition(dimension > 0, "RESOURCE_LIMIT", "Canvas dimensions must be positive.");
    }
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
    this.#voiceAge = Number.POSITIVE_INFINITY;
    this.#speaking = false;
    this.#talk = 0;
    this.#mouth = [];
    this.#mouthEmote = 0;
    this.#mouthOpen = 0;
    this.#lookTarget = { x: 0, y: 0 };
    this.#look = { x: 0, y: 0 };
    this.#gestures.clear();
    this.#modelTop = undefined;
    this.#faceSource = undefined;
    this.#faceHint = undefined;
    this.#carriers = [];
    this.#skin = undefined;
    this.#pinned = undefined;
    this.#hintPinned = undefined;
    this.#measured = undefined;
    this.#faceProbeMilliseconds = 0;
    this.#eyes = NO_EYES;
    this.#eyeBlinkIds = [];
    this.#lipSyncIds = [];
    this.#partNames = new Map();
    this.#rest = [];
    this.#hair = new Set();
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
