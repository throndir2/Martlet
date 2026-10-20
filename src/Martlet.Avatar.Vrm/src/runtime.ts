import * as THREE from "three";
import { VRM, type VRMExpression, VRMHumanBoneList } from "@pixiv/three-vrm";
import { blinkPresets, finite, gazePresets, integer, mouthPresets, object, requireValid, VrmError, type VrmCapabilities } from "./inspect.js";
import { type EyeFields, type EyeHint, type EyesFrom, type FaceFrame, readEyeHint, VrmEyes } from "./eyes.js";
import { capturePose, hitTestVrm, type RestPose, type VrmHit } from "./touch.js";
import { animationName, loadVrmAnimation, type VrmAnimationInfo, type VrmAnimationReading, VrmClipPlayer } from "./animation.js";
import { addPose, BLUSH_EXPRESSION, type BlushLevel, blushLevel, type BoneName, BREATH_SECONDS, breathing, CHEEK_TURN, cycle,
  emptyPose, envelope, eyeRange, GESTURE_BONES, GESTURE_FADE, GESTURE_SECONDS, type GesturePose, gesturePose, holdable,
  type HoldableGesture, moodPose, mouthShape, sharePart, smoothstep, VOICE_HOLD_SECONDS, VRM_GESTURES, VRM_HOLD_PARTS,
  VRM_HOLDABLE_GESTURES, VRM_RELAXED_ARM, VRM_RELAXED_FINGERS, type VrmArmReading, type VrmGesture, type VrmIdleReading,
  type VrmMouthReading } from "./poses.js";
export { VRM_GESTURES, type VrmGesture, VRM_HOLDABLE_GESTURES, VRM_BLUSH_LEVELS, type VrmGesturePart, VRM_HOLD_PARTS,
  VOICE_HOLD_SECONDS, type VrmMouthReading, type GesturePose, CHEEK_TURN, cheekFrame, flinchJolt, drowse, BREATH_SECONDS,
  breathing, VRM_RELAXED_ARM, VRM_RELAXED_FINGERS, type VrmIdleReading, type VrmArmReading, gesturePose } from "./poses.js";
import { type CoefficientInput, type ComposedParameterInput, type CompositionRevision, identifier, identityKey, type InputMode,
  type LocalPose, type PlaybackIdentity, type Selection, validateIdentity } from "./playback.js";
export { type PlaybackIdentity, type CoefficientInput, type CompositionRevision, type InputMode, type ComposedParameterInput,
  type ExpressionMapping, type Selection, type LocalPose } from "./playback.js";
import { loadLocalVrm, releaseResources } from "./loading.js";
export { loadLocalVrm } from "./loading.js";

/** No audio devices, providers, clocks or networks are started by this runtime. */
export class VrmRuntime {
  private model: VRM | undefined;
  private inspected: VrmCapabilities | undefined;
  private selection: Selection | undefined;
  private revision: CompositionRevision | undefined;
  private inputMode: InputMode | undefined;
  private identity: string | undefined;
  private sequence = -1;
  private sampleOffset = -1;
  private playbackOffset = -1;
  private generation = 0;
  private disposed = false;
  private pending = false;
  private pose: LocalPose = {};
  private readonly gazeTarget = new THREE.Object3D();
  private idle = false;
  private idleTime = 0;
  private breathPhase = 0;
  private inhale = 0;
  private sway = 0;
  private speech = 0;
  private speechTarget = 0;
  private speechAge = Number.POSITIVE_INFINITY;
  private nextBlink = 2 + Math.random() * 3;
  private blinkTime = -1;
  private lookTarget = { x: 0, y: 0 };
  private look = { x: 0, y: 0 };
  private composedAge = Number.POSITIVE_INFINITY;
  private readonly actions = new Map<string, { target: number; value: number }>();
  /** Expressions held on (lingering emotes) until turned off. */
  private readonly heldExpressions = new Set<string>();
  private gesture: { name: VrmGesture; seconds: number } | undefined;
  private blush: { name: string; level: BlushLevel; seconds: number; hold: boolean } | undefined;
  private held: { name: HoldableGesture; seconds: number; progress: number; on: boolean }[] = [];
  // 0 to 1: how much the voice has the mouth now (eased), whether it spoke within the last VOICE_HOLD_SECONDS, and how far a
  // held open mouth opens it before the voice takes it.
  private talk = 0;
  private speaking = false;
  private heldMouth = 0;
  // The expressions whose overrideMouth is "none" while the voice has the mouth, with the model's own setting to give back.
  private readonly mouthOverrides = new Map<VRMExpression, VRMExpression["overrideMouth"]>();
  private face: Readonly<Record<string, number>> = {};
  // Expressions the gestures' face wrote last frame and their values before, put back before the next frame is composed.
  private readonly faceRestore = new Map<string, number>();
  private hipsRest: number | undefined;
  private faceWidth = 0.14;
  private eyes: VrmEyes | undefined;
  /** The face vision measured (see setEyeHint), or undefined. */
  private featureHint: EyeHint | undefined;
  // Each new pose (update, reset, load) counts up; a hit test measures the skinned meshes' bounds again only in a new pose.
  private poses = 0;
  private measured = -1;
  /** Every node's world matrix in the rest pose (see startIdle), which touches are traced back to. */
  private rest: RestPose | undefined;
  /** A VRM 0.x model: its normalized bones face -Z, so turns about X and Z are mirrored (see animateIdle). */
  private mirrored = false;
  /** The VRMA clips kept for this model and the ones playing (see loadAnimation). */
  private readonly clips = new VrmClipPlayer();

  get capabilities(): VrmCapabilities | undefined { return this.inspected; }
  get scene(): THREE.Group | undefined { return this.model?.scene; }
  get isLoaded(): boolean { return this.model !== undefined; }

  /** Each humanoid bone the model has with its node, for touch zones. */
  get humanoidNodes(): readonly (readonly [string, THREE.Object3D])[] {
    const model = this.model;
    if (!model) return [];
    return VRMHumanBoneList.flatMap(name => {
      const node = model.humanoid.getRawBoneNode(name);
      return node ? [[name, node] as const] : [];
    });
  }

  /** The model's spring-bone chains (a tail, hair, a skirt), each its joints' nodes from the root to the tip, for touch zones.
   *  The importer makes a joint of each node and the next one in a VRM 1.0 chain, and of each node under a VRM 0.x bone group's
   *  root with its first child. So a node that is no joint's child starts a chain (in VRM 0.x also a branch that is not its
   *  parent's first child), which goes on through each joint's child to the tip. */
  get springNodes(): readonly (readonly THREE.Object3D[])[] {
    const joints = [...this.model?.springBoneManager?.joints ?? []];
    const byBone = new Map(joints.map(joint => [joint.bone, joint] as const));
    const children = new Set(joints.flatMap(joint => joint.child ? [joint.child] : []));
    return joints.filter(joint => !children.has(joint.bone)).map(root => {
      const chain = [root.bone];
      for (let joint = byBone.get(root.bone); joint?.child && !chain.includes(joint.child); joint = byBone.get(joint.child)) chain.push(joint.child);
      return Object.freeze(chain);
    });
  }

  /** Relaxed arms and hands, breathing, a slow sway, blinking, cursor-follow and loudness lip-sync while no mapped A2F turn is
   *  active. Its first pose (looking ahead, before any time passes) is the rest pose: the touch zones picture shows it, and
   *  touches are traced back to it (see hitTestRay). */
  startIdle(): void {
    const model = this.loaded();
    this.idle = true;
    this.update(0);
    this.rest = capturePose(model.scene);
  }

  setLipSync(level: number): void {
    this.loaded();
    finite(level, 0, 1, "lip-sync level");
    this.speechTarget = level;
    this.speechAge = 0;
  }

  setLook(x: number, y: number): void {
    this.loaded();
    finite(x, -1, 1, "look x"); finite(y, -1, 1, "look y");
    this.lookTarget = { x, y };
  }

  /**
   * Fades an authored emotion or custom expression in (`on`) or out, as an emote. One passing emote shows at a time: a new one
   * fades the others out, except held (lingering) ones (`hold`), which stay on together until each is turned off. Mouth, blink
   * and gaze presets belong to lip-sync, blinking and gaze, so they are refused.
   */
  setAction(name: string, on: boolean, hold = false): boolean {
    const model = this.loaded();
    if (typeof name !== "string" || !model.expressionManager?.getExpression(name) ||
      [...mouthPresets, ...blinkPresets, ...gazePresets].includes(name as never)) return false;
    if (on && !hold) for (const [other, state] of this.actions) if (other !== name && !this.heldExpressions.has(other)) state.target = 0;
    if (hold) { if (on) this.heldExpressions.add(name); else this.heldExpressions.delete(name); }
    else if (this.heldExpressions.has(name)) return true;
    const state = this.actions.get(name) ?? { target: 0, value: 0 };
    state.target = on ? 1 : 0;
    this.actions.set(name, state);
    return true;
  }

  /** Martlet's gestures this model has the humanoid bones for (and, for mouth_open, the oh or aa mouth expression). */
  get gestures(): readonly VrmGesture[] {
    const model = this.model;
    return model ? VRM_GESTURES.filter(name => GESTURE_BONES[name].every(bone => model.humanoid.getNormalizedBoneNode(bone)) &&
      (name !== "mouth_open" || mouthShape(model) !== undefined)) : [];
  }

  /** Starts one of Martlet's gestures (see `gestures`), replacing one already playing: head gestures, a bow, a sway, a wave,
   *  a shrug, an excited bounce, a voice emote (a laugh, a sigh, a gasp...) or a touch or mood gesture (a wink, a flinch...).
   *  With `hold`, a holdable one (VRM_HOLDABLE_GESTURES) eases in and stays until `endGesture`. Held gestures layer: holding
   *  one lets go only of the held ones that move a part it moves too (VRM_HOLD_PARTS), crossfading from them. A gesture
   *  played meanwhile plays on top, the held poses easing back partway and resuming after. A blush level shows the model's
   *  own blush or cheek expression (4 seconds, or held: the cheeks); false when it has none (the page draws one). */
  playGesture(name: string, hold = false): boolean {
    const model = this.loaded();
    if (!(this.gestures as readonly string[]).includes(name)) return false;
    if (blushLevel(name)) {
      const reserved: readonly string[] = [...mouthPresets, ...blinkPresets, ...gazePresets];
      const own = model.expressionManager?.expressions.map(e => e.expressionName)
        .find(n => !reserved.includes(n) && BLUSH_EXPRESSION.test(n));
      // Played once while it is held, it stays held, as the other held gestures do; held, passing emotes leave it on.
      const keep = hold || this.blush?.hold === true;
      if (!own || !this.setAction(own, true, keep)) return false;
      if (hold) this.letGo(name);
      this.blush = { name: own, level: name, seconds: 0, hold: keep };
      return true;
    }
    if (hold && holdable(name)) {
      this.letGo(name);
      const held = this.held.find(h => h.name === name);
      if (held) held.on = true;
      else this.held.push({ name, seconds: 0, progress: 0, on: true });
      return true;
    }
    if (name === "bounce") this.hipsRest ??= model.humanoid.normalizedRestPose.hips?.position?.[1] ??
      model.humanoid.getNormalizedBoneNode("hips")?.position.y;
    this.gesture = { name: name as VrmGesture, seconds: 0 };
    return true;
  }

  /** Lets go of the held gestures (the held blush included) that move a part `name` moves too. */
  private letGo(name: HoldableGesture | BlushLevel): void {
    for (const held of this.held) if (held.name !== name && sharePart(held.name, name)) held.on = false;
    if (!blushLevel(name) && this.blush?.hold && sharePart(this.blush.level, name)) this.endGesture(this.blush.level);
  }

  /** Lets a held gesture go (eased out); one playing once just finishes. */
  endGesture(name: string): void {
    for (const held of this.held) if (held.name === name) held.on = false;
    if (this.blush?.level === name) { this.setAction(this.blush.name, false, this.blush.hold); this.blush = undefined; }
  }

  /** The gesture playing once, if any, and every gesture held (a held blush by its level). */
  get gestureState(): { readonly playing?: VrmGesture; readonly held: readonly (HoldableGesture | BlushLevel)[] } {
    const held: (HoldableGesture | BlushLevel)[] = this.held.filter(h => h.on).map(h => h.name);
    if (this.blush?.hold) held.push(this.blush.level);
    return Object.freeze({ ...(this.gesture ? { playing: this.gesture.name } : {}), held: Object.freeze(held) });
  }

  /**
   * Reads a VRMA clip (a .vrma file's bytes: binary glTF with VRMC_vrm_animation 1.0) with the official loader, retargets it to
   * this model (VRM 0.x or 1.0) and keeps it as `name` while the model stays loaded, replacing a clip of that name; at most 16
   * are kept, the oldest goes first. Play it with playAnimation. The clip moves the hips up and down; with `inPlace` (the
   * default) they stay at rest across the ground, so a clip that walks or drifts keeps the character in its window. Throws a
   * VrmError for a file that isn't a usable clip.
   */
  async loadAnimation(name: string, buffer: ArrayBuffer, inPlace = true): Promise<VrmAnimationInfo> {
    const model = this.loaded();
    animationName(name);
    const animation = await loadVrmAnimation(buffer);
    requireValid(!this.disposed && this.model === model, "The model changed while the clip was read.");
    return this.clips.keep(model, name, animation, inPlace !== false);
  }

  /** Plays the kept clip `name` once over the idle pose (see VrmClipPlayer): it blends in, then gives the body back to the
   *  idle pose, breathing and sway by its end. Lip-sync, blinking, the look, expressions and touches keep working. False when
   *  no clip has that name or the idle motion isn't running (a still picture). */
  playAnimation(name: string): boolean {
    this.loaded();
    return this.idle && typeof name === "string" && this.clips.play(name);
  }

  /** Blends the playing clip (only `name`, when given) back to the idle pose; false when none played. */
  stopAnimation(name?: string): boolean { this.loaded(); return this.clips.stop(name); }

  /** The clip that plays now (name, seconds into it, its length and how much of the body it has), or undefined. */
  get animationReading(): VrmAnimationReading | undefined { return this.model ? this.clips.reading : undefined; }

  /** The clips kept for the loaded model, oldest first. */
  get animations(): readonly VrmAnimationInfo[] { return this.model ? this.clips.kept : []; }

  /** The held gestures' combined pose `deltaSeconds` later, eased back by `duck` (0..1) under a gesture playing once. */
  private advanceHeld(deltaSeconds: number, duck: number): GesturePose {
    const pose = emptyPose();
    for (const held of this.held) {
      held.seconds += deltaSeconds;
      held.progress = Math.max(0, Math.min(1, held.progress + (held.on ? 1 : -1) * deltaSeconds / 0.8));
      const layer = emptyPose();
      moodPose(layer, held.name, held.seconds, smoothstep(held.progress) * (1 - duck));
      addPose(pose, layer);
    }
    this.held = this.held.filter(held => held.on || held.progress > 0);
    return pose;
  }

  /** Shows the gestures' face (preset expressions such as blinkLeft or relaxed) over what blinking and emotes set, never
   *  over lip-sync, gaze or expressions a composed (Audio2Face) turn drives. */
  private applyFace(model: VRM): void {
    const expressions = model.expressionManager;
    if (!expressions) return;
    const composed = this.identity !== undefined ? new Set(this.selection?.mappings.map(m => m.expression)) : undefined;
    for (const [name, weight] of Object.entries(this.face)) {
      if (weight < 0.001 || !expressions.getExpression(name) || composed?.has(name) ||
        [...mouthPresets, ...gazePresets].includes(name as never)) continue;
      const before = expressions.getValue(name) ?? 0;
      this.faceRestore.set(name, before);
      expressions.setValue(name, Math.min(1, Math.max(before, weight)));
    }
  }

  private restoreFace(model: VRM): void {
    for (const [name, value] of this.faceRestore) model.expressionManager?.setValue(name, value);
    this.faceRestore.clear();
  }

  /** While the voice has the mouth, the emotes showing (expressions turned on, passing or held, and the gestures' face) never
   *  block or blend the mouth expressions it moves: an emote that sets the mouth (a VRoid surprised, an authored shout) would
   *  otherwise hold it still while the character talks. Their overrideMouth is "none" until the voice is done; then each gets
   *  the model's own setting back, so the mouth is the emote's again. */
  private giveMouth(model: VRM): void {
    const expressions = model.expressionManager;
    const emotes = this.speaking && expressions ? new Set([...this.actions.keys(), ...this.faceRestore.keys()]) : new Set<string>();
    for (const [expression, override] of this.mouthOverrides) {
      if (emotes.has(expression.expressionName)) continue;
      expression.overrideMouth = override;
      this.mouthOverrides.delete(expression);
    }
    for (const name of emotes) {
      const expression = expressions!.getExpression(name);
      if (!expression || expression.overrideMouth === "none" || this.mouthOverrides.has(expression)) continue;
      this.mouthOverrides.set(expression, expression.overrideMouth);
      expression.overrideMouth = "none";
    }
  }

  private giveMouthBack(): void {
    for (const [expression, override] of this.mouthOverrides) expression.overrideMouth = override;
    this.mouthOverrides.clear();
  }

  /**
   * The face in world space, for drawings over it: the middle of the eyes, the head's directions (`side` is the viewer's
   * right when it faces the camera) and the face's width. From the eye bones when the model has them, otherwise estimated
   * from the head bone and the model's height. Each cheek is a surface turned out to its side (CHEEK_TURN): `...Across` runs
   * one unit along it toward the viewer's right (as wide as `side` from the front) and `...Normal` points out of it, so a
   * turned head shows the near cheek wider and the far one narrower, and hides it. With a face measured by vision (see
   * setEyeHint) the cheeks and mouth are where vision saw them (`cheekSize` their radius in face widths), and on a model
   * without eye bones `middle` (where drawings center on the face) moves to the measured eye line; `center` stays the face
   * the measurement is relative to. Undefined without a head bone.
   */
  faceGeometry(): { center: THREE.Vector3; middle: THREE.Vector3; side: THREE.Vector3; up: THREE.Vector3; forward: THREE.Vector3;
    width: number; eyeLeft: THREE.Vector3; eyeRight: THREE.Vector3; mouth: THREE.Vector3; top: THREE.Vector3;
    cheekLeft: THREE.Vector3; cheekRight: THREE.Vector3; cheekLeftAcross: THREE.Vector3; cheekRightAcross: THREE.Vector3;
    cheekLeftNormal: THREE.Vector3; cheekRightNormal: THREE.Vector3; cheekSize?: number } | undefined {
    const model = this.model;
    const head = model?.humanoid.getNormalizedBoneNode("head");
    if (!model || !head) return undefined;
    head.updateWorldMatrix(true, false);
    const rotation = head.getWorldQuaternion(new THREE.Quaternion());
    // VRM 1.0 faces +Z with the character's left (the viewer's right) at +X. A VRM 0.x model's normalized bones face -Z with
    // the character's left at -X (rotateVRM0 turns its root), so its head is read half a turn around.
    if (this.mirrored) rotation.multiply(new THREE.Quaternion(0, 1, 0, 0));
    const side = new THREE.Vector3(1, 0, 0).applyQuaternion(rotation), up = new THREE.Vector3(0, 1, 0).applyQuaternion(rotation);
    const forward = new THREE.Vector3(0, 0, 1).applyQuaternion(rotation);
    const at = (node: THREE.Object3D | null | undefined) => {
      if (!node) return undefined;
      node.updateWorldMatrix(true, false);
      return node.getWorldPosition(new THREE.Vector3());
    };
    // The character's left eye is on the viewer's right.
    const viewerRight = at(model.humanoid.getNormalizedBoneNode("leftEye")), viewerLeft = at(model.humanoid.getNormalizedBoneNode("rightEye"));
    let width = this.faceWidth, center: THREE.Vector3;
    if (viewerRight && viewerLeft && viewerRight.distanceTo(viewerLeft) > 1e-4) {
      width = Math.max(viewerRight.distanceTo(viewerLeft) * 2.3, width * 0.4);
      center = viewerRight.clone().add(viewerLeft).multiplyScalar(0.5);
    } else center = at(head)!.addScaledVector(up, 0.45 * width).addScaledVector(forward, 0.45 * width);
    const point = (x: number, y: number, z: number) => center.clone().addScaledVector(side, x * width)
      .addScaledVector(up, y * width).addScaledVector(forward, z * width);
    // What vision measured (face widths from `center`, y down); without eye bones the eye line it saw moves the middle.
    const hint = this.featureHint, measured = (p: { x: number; y: number }, z: number) => point(p.x, -p.y, z);
    const bones = !!(viewerRight && viewerLeft && viewerRight.distanceTo(viewerLeft) > 1e-4);
    const middle = !bones && hint?.left && hint.right
      ? measured({ x: (hint.left.eye.x + hint.right.eye.x) / 2, y: (hint.left.eye.y + hint.right.eye.y) / 2 }, 0) : center.clone();
    const shift = middle.clone().sub(center);
    const moved = (x: number, y: number, z: number) => point(x, y, z).add(shift);
    const cheeks = hint?.cheekLeft && hint.cheekRight ? { left: hint.cheekLeft, right: hint.cheekRight } : undefined;
    // The viewer's left cheek (the character's right) turns out toward -side, the viewer's right one toward +side.
    const tan = Math.tan(CHEEK_TURN), cos = Math.cos(CHEEK_TURN), sin = Math.sin(CHEEK_TURN);
    return { center, middle, side, up, forward, width,
      eyeLeft: viewerLeft ?? (hint?.left ? measured(hint.left.eye, 0) : moved(-0.2, 0, 0)),
      eyeRight: viewerRight ?? (hint?.right ? measured(hint.right.eye, 0) : moved(0.2, 0, 0)),
      cheekLeft: cheeks ? measured(cheeks.left, 0.08) : moved(-0.28, -0.22, 0.08),
      cheekRight: cheeks ? measured(cheeks.right, 0.08) : moved(0.28, -0.22, 0.08),
      cheekLeftAcross: side.clone().addScaledVector(forward, tan), cheekRightAcross: side.clone().addScaledVector(forward, -tan),
      cheekLeftNormal: forward.clone().multiplyScalar(cos).addScaledVector(side, -sin),
      cheekRightNormal: forward.clone().multiplyScalar(cos).addScaledVector(side, sin),
      mouth: hint?.mouth ? measured(hint.mouth, 0.1) : moved(0, -0.42, 0.1), top: moved(0, 0.75, -0.1),
      ...(cheeks ? { cheekSize: (cheeks.left.r + cheeks.right.r) / 2 } : {}) };
  }

  /** Uses the face measured by vision (see EyeHint in eyes.ts): its eyes for what the model's eye bones and meshes can't give,
   *  and its cheeks and mouth (and, without eye bones, its eye line) for the face (see faceGeometry); undefined clears it.
   *  Returns where the eyes come from now. */
  setEyeHint(hint: EyeHint | undefined): EyesFrom {
    this.loaded();
    this.featureHint = readEyeHint(hint);
    return this.eyes?.setHint(this.featureHint) ?? "estimate";
  }

  /** Where the eyes' irises and openings come from (see EyesFrom in eyes.ts). */
  get eyesFrom(): EyesFrom { return this.eyes?.from ?? "estimate"; }

  /** Each eye's iris, opening and middle for the face now (`face`, see faceGeometry) as `project` (world to canvas pixels)
   *  shows it, and where they came from (see VrmEyes.fields); eyes that can't be read now are only left out. */
  eyeFields(face: FaceFrame, project: (world: THREE.Vector3) => { x: number; y: number }): EyeFields {
    try { return this.eyes?.fields(face, project) ?? { eyesFrom: "estimate" }; }
    catch { return { eyesFrom: this.eyesFrom }; }
  }

  /** The playing gesture and how far into it, advanced by `deltaSeconds`; undefined once it is over. */
  private advanceGesture(deltaSeconds: number): { name: VrmGesture; t: number; weight: number } | undefined {
    const gesture = this.gesture;
    if (!gesture) return undefined;
    const t = gesture.seconds += deltaSeconds;
    const total = GESTURE_SECONDS[gesture.name];
    if (t >= total) { this.gesture = undefined; return undefined; }
    return { name: gesture.name, t, weight: envelope(t, total, GESTURE_FADE[gesture.name] ?? 0.35) };
  }

  private updateActions(model: VRM, deltaSeconds: number): void {
    const blush = this.blush;
    if (blush && !blush.hold && (blush.seconds += deltaSeconds) >= GESTURE_SECONDS[blush.level] - 0.6) this.endGesture(blush.level);
    const expressions = model.expressionManager;
    if (!expressions) return;
    for (const [name, state] of this.actions) {
      state.value += (state.target - state.value) * Math.min(1, deltaSeconds * 6);
      if (state.target === 0 && state.value < 0.002) {
        expressions.setValue(name, 0);
        this.actions.delete(name);
      } else expressions.setValue(name, state.value);
    }
  }

  private animateIdle(model: VRM, deltaSeconds: number): void {
    this.idleTime += deltaSeconds;
    // One breath every BREATH_SECONDS, a little faster or slower as time goes on, and shallower while the voice speaks.
    this.breathPhase += deltaSeconds / this.breathSeconds();
    this.inhale = breathing(this.breathPhase) * (1 - 0.4 * this.talk);
    this.sway = 0.012 * Math.sin(2 * Math.PI * this.idleTime / 7.3);
    const follow = Math.min(1, deltaSeconds * 5);
    this.look = { x: this.look.x + (this.lookTarget.x - this.look.x) * follow, y: this.look.y + (this.lookTarget.y - this.look.y) * follow };
    const bone = (name: BoneName) => model.humanoid.getNormalizedBoneNode(name);
    // The angles below are for a VRM 1.0 model's normalized bones, which face +Z. A VRM 0.x model's face -Z (rotateVRM0
    // turns its root instead), so its turns about X and Z are mirrored to move it the same way.
    const sign = this.mirrored ? -1 : 1;
    const turn = (name: BoneName, x: number, y: number, z: number) => bone(name)?.rotation.set(sign * x, y, sign * z);
    const inhale = this.inhale;
    const gesture = this.advanceGesture(deltaSeconds);
    const w = gesture?.weight ?? 0;
    const lerp = (from: number, to: number, amount: number) => from + (to - from) * amount;
    const wave = gesture?.name === "wave" ? w : 0, shrug = gesture?.name === "shrug" ? w : 0;
    const pose = this.advanceHeld(deltaSeconds, 0.6 * w);
    if (gesture) addPose(pose, gesturePose(gesture.name, gesture.t, w));
    this.face = pose.face;
    const open = pose.open;
    // A breath lifts the shoulders; the arms go up with them but keep hanging, swinging out only a little.
    const shoulders = 0.2 * shrug + 0.12 * pose.shoulders + 0.07 * inhale;
    const waving = wave > 0 ? 0.35 * Math.sin(2 * Math.PI * gesture!.t / 0.5) : 0;
    // The arms and hands rest relaxed; a shrug, a wave (the right one) or arms opened out straighten them and open the hand.
    const relaxLeft = (1 - shrug) * (1 - open), relaxRight = relaxLeft * (1 - wave);
    const [forward, , hang] = VRM_RELAXED_ARM.upperArm, elbow = VRM_RELAXED_ARM.lowerArm[1], wrist = VRM_RELAXED_ARM.hand[2];
    // Each arm swings out 0.015 radians at a full breath: against its shoulder's lift, or by itself on a model without one.
    const downLeft = -hang + (bone("leftShoulder") ? 0.055 : -0.015) * inhale;
    const downRight = -hang + (bone("rightShoulder") ? 0.055 : -0.015) * inhale;
    turn("leftUpperArm", forward * relaxLeft, 0, lerp(lerp(-downLeft, -0.95, shrug), -0.15, open));
    turn("rightUpperArm", forward * relaxRight, 0, lerp(lerp(lerp(downRight, 0.95, shrug), -0.25, wave), 0.15, open));
    turn("leftLowerArm", 0, lerp(lerp(elbow, -1.1, shrug), 0, open), 0);
    turn("rightLowerArm", 0, lerp(lerp(lerp(-elbow, 1.1, shrug), 0, wave), 0, open), lerp(0, -1.4 + waving, wave));
    turn("leftHand", 0, 0, wrist * relaxLeft);
    turn("rightHand", 0, 0, -wrist * relaxRight);
    for (const [part, x, y, z] of VRM_RELAXED_FINGERS) {
      turn(`left${part}` as BoneName, x * relaxLeft, y * relaxLeft, z * relaxLeft);
      turn(`right${part}` as BoneName, x * relaxRight, -y * relaxRight, -z * relaxRight);
    }
    turn("leftShoulder", 0, 0, shoulders);
    turn("rightShoulder", 0, 0, -shoulders);
    // Breathing in opens the chest a little (only a little: leaning back would sink the head in a front view); the neck takes
    // most of it back, so the head stays level.
    const chest = bone("chest"), upperChest = bone("upperChest");
    turn("chest", -0.01 * inhale, 0, 0);
    turn("upperChest", -0.008 * inhale, 0, 0);
    const opened = ((chest ? 0.01 : 0) + (upperChest ? 0.008 : 0)) * inhale;
    const side = gesture?.name === "sway" ? w * Math.sin(2 * Math.PI * gesture.t / 1.2) : 0;
    turn("spine", (gesture?.name === "bow" ? 0.35 * w : 0) + pose.spineX, pose.spineY, 0.08 * side + pose.spineZ + this.sway);
    const hips = bone("hips");
    if (hips && this.hipsRest !== undefined)
      hips.position.y = this.hipsRest + (gesture?.name === "bounce" ? 0.035 * w * Math.abs(Math.sin(2 * Math.PI * gesture.t / 0.6)) : 0);
    if (!this.selection?.head) {
      let gx = 0, gy = 0;
      if (gesture?.name === "nod") { const phase = Math.sin(Math.PI * gesture.t / 0.55); gy = -0.9 * phase * phase; }
      if (gesture?.name === "shake") gx = 0.8 * Math.sin(2 * Math.PI * gesture.t / 0.4) * Math.sin(Math.PI * gesture.t / 1.2);
      if (gesture?.name === "bow") gy = -0.5 * w;
      gx += pose.gx; gy += pose.gy;
      const tilt = (gesture?.name === "tilt" ? 0.3 * w : 0) + (gesture?.name === "shrug" ? 0.12 * w : 0) - 0.06 * side + pose.tilt;
      const x = this.look.x + gx, y = this.look.y + gy;
      turn("neck", -y * 0.15 + 0.7 * opened, x * 0.2, Math.sin(this.idleTime * 0.7) * 0.02 - 0.6 * this.sway);
      turn("head", -y * 0.2, x * 0.3, tilt);
    }
    // Only a held eyes gesture (eyes_up) turns the eyes while idle; the head keeps following the look.
    const range = eyeRange(model, this.inspected?.gaze), clamp = (value: number) => Math.max(-1, Math.min(1, value));
    for (const eye of ["leftEye", "rightEye"] as const) turn(eye, -range * clamp(pose.eyeY), range * clamp(pose.eyeX), 0);
    const expressions = model.expressionManager;
    if (!expressions) return;
    this.speechAge += deltaSeconds;
    const target = this.speechAge > 0.3 ? 0 : this.speechTarget;
    this.speech += (target - this.speech) * Math.min(1, deltaSeconds * (target > this.speech ? 30 : 14));
    // Fresh composed (Audio2Face) frames own the mouth; loudness resumes when they stop.
    const composing = this.identity !== undefined && this.composedAge < 0.25;
    // The voice (a lip-sync level or a composed frame) has the mouth from its first sound until VOICE_HOLD_SECONDS after its
    // last. Meanwhile a held open mouth gives way to it entirely, and it opens again once the voice is done.
    this.speaking = Math.min(this.speechAge, this.identity !== undefined ? this.composedAge : Number.POSITIVE_INFINITY) <= VOICE_HOLD_SECONDS;
    // The voice takes the mouth quickly, so its first sound shows, and gives it back gently.
    this.talk += ((this.speaking ? 1 : 0) - this.talk) * Math.min(1, deltaSeconds * (this.speaking ? 10 : 5));
    this.heldMouth = Math.min(1, pose.mouth);
    const mouthOpen = this.heldMouth * (1 - this.talk), shape = mouthShape(model);
    if (!composing && expressions.getExpression("aa")) expressions.setValue("aa", Math.min(1, this.speech + (shape === "aa" ? mouthOpen : 0)));
    if (shape === "oh" && !(this.identity !== undefined && this.selection?.mappings.some(m => m.expression === "oh")))
      expressions.setValue("oh", mouthOpen);
    const blinkOwned = this.identity !== undefined && (this.selection?.mappings.some(m => m.aspect === "blink") ?? false);
    if (blinkOwned || !expressions.getExpression("blink")) return;
    if (this.blinkTime < 0 && (this.nextBlink -= deltaSeconds) <= 0) this.blinkTime = 0;
    if (this.blinkTime >= 0) {
      this.blinkTime += deltaSeconds;
      const phase = this.blinkTime / 0.2;
      expressions.setValue("blink", phase < 0.5 ? phase * 2 : Math.max(0, 2 - phase * 2));
      if (phase >= 1) { this.blinkTime = -1; this.nextBlink = 2 + Math.random() * 4; }
    }
  }

  /** Seconds the breath takes now: BREATH_SECONDS, up to 10% faster or slower over a cycle of about 27 seconds. */
  private breathSeconds(): number { return BREATH_SECONDS * (1 + 0.1 * Math.sin(this.idleTime * 0.23)); }

  /** What the idle body does now, for Martlet's MCP (see VrmIdleReading): the breath, each arm's hang and elbow bend measured
   *  on the posed bones, how far the middle fingers curl and the sway. Undefined before a model loads. */
  get idleReading(): VrmIdleReading | undefined {
    const model = this.model;
    if (!model) return undefined;
    model.scene.updateWorldMatrix(true, true);
    const at = (name: BoneName) => model.humanoid.getRawBoneNode(name)?.getWorldPosition(new THREE.Vector3());
    const degrees = (radians: number) => Math.round(radians * 1800 / Math.PI) / 10;
    const round = (value: number) => Math.round(value * 1000) / 1000;
    const arm = (side: "left" | "right"): VrmArmReading | undefined => {
      const shoulder = at(`${side}UpperArm`), elbow = at(`${side}LowerArm`), wrist = at(`${side}Hand`);
      if (!shoulder || !elbow || !wrist) return undefined;
      const upper = elbow.clone().sub(shoulder), lower = wrist.clone().sub(elbow);
      if (upper.lengthSq() < 1e-12 || lower.lengthSq() < 1e-12) return undefined;
      return { fromDown: degrees(upper.angleTo(new THREE.Vector3(0, -1, 0))), elbow: degrees(upper.angleTo(lower)) };
    };
    const curl = (side: "left" | "right"): number | undefined => {
      const nodes = ["MiddleProximal", "MiddleIntermediate", "MiddleDistal"]
        .map(part => model.humanoid.getNormalizedBoneNode(`${side}${part}` as BoneName));
      return nodes.every(node => node)
        ? degrees(nodes.reduce((sum, node) => sum + 2 * Math.acos(Math.min(1, Math.abs(node!.quaternion.w))), 0)) : undefined;
    };
    const left = arm("left"), right = arm("right"), curlLeft = curl("left"), curlRight = curl("right");
    return Object.freeze({
      idle: this.idle,
      breathing: { phase: round(this.breathPhase - Math.floor(this.breathPhase)), inhale: round(this.inhale),
        perMinute: Math.round(600 / this.breathSeconds()) / 10 },
      arms: { ...(left ? { left } : {}), ...(right ? { right } : {}) },
      curl: { ...(curlLeft !== undefined ? { left: curlLeft } : {}), ...(curlRight !== undefined ? { right: curlRight } : {}) },
      sway: degrees(this.sway),
    });
  }

  /** Who moves the mouth now, for Martlet's MCP (character_mouth; see VrmMouthReading): how much the voice has it, its
   *  loudness, how far a held open mouth would open it, how far the mouth expressions open it after what blocks them, and how
   *  much the expressions showing block them. Undefined before a model with expressions loads. */
  get mouthReading(): VrmMouthReading | undefined {
    const expressions = this.model?.expressionManager;
    if (!expressions) return undefined;
    const blocked = Math.min(1, expressions.expressions.reduce((sum, expression) => sum + expression.overrideMouthAmount, 0));
    const open = Math.max(0, ...mouthPresets.map(name => expressions.getValue(name) ?? 0)) * (1 - blocked);
    return Object.freeze({ voice: this.talk, speaking: this.speaking, level: this.speech, emote: this.heldMouth,
      open: Math.min(1, open), blocked });
  }

  async load(buffer: ArrayBuffer): Promise<VrmCapabilities> {
    this.alive();
    requireValid(!this.pending, "A load is already in progress; wait for it before reloading.");
    this.pending = true;
    const generation = ++this.generation;
    try {
      const result = await loadLocalVrm(buffer);
      if (this.disposed || generation !== this.generation) {
        releaseResources(result.vrm.scene);
        throw new VrmError("load-canceled", "Load was canceled by disposal.");
      }
      this.releaseModel();
      this.model = result.vrm;
      this.inspected = result.capabilities;
      this.mirrored = result.capabilities.format === "VRM0";
      // A face is about a twelfth as wide as the figure is tall.
      const box = new THREE.Box3().setFromObject(result.vrm.scene);
      this.faceWidth = box.isEmpty() ? 0.14 : Math.max(0.01, 0.09 * (box.max.y - box.min.y));
      // Measured now, while the model is at rest; a model that breaks it only gets no eyes from its bones and meshes.
      try { this.eyes = new VrmEyes(result.vrm); } catch { this.eyes = undefined; }
      this.featureHint = undefined;
      return result.capabilities;
    } finally { this.pending = false; }
  }

  configure(selection: Selection, revision: CompositionRevision, inputMode: InputMode = "composed"): void {
    const model = this.loaded();
    const capabilities = this.inspected!;
    requireValid(selection !== null && typeof selection === "object", "Selection required.");
    requireValid(revision !== null && typeof revision === "object", "Model/mapping revision binding is required.");
    identifier(revision.modelRevision, "model revision");
    identifier(revision.mappingRevision, "mapping revision");
    requireValid(inputMode === "composed" || inputMode === "coefficients", "Unknown input mode.");
    identifier(selection.faceSource, "face source");
    requireValid(["authored-explicit", "reduced-vowel-jaw-only"].includes(selection.faceMode), "Explicit face mapping mode required.");
    for (const key of ["gaze", "head", "secondaryMotion"] as const) requireValid(typeof selection[key] === "boolean", `${key} must be explicitly selected or omitted (false).`);
    requireValid(Array.isArray(selection.mappings) && selection.mappings.length <= 128, "At most 128 mappings accepted.");
    requireValid(!selection.gaze || capabilities.gaze !== "absent", "Gaze controls absent; author eyes/lookAt, choose a supported model, or explicitly omit gaze.");
    requireValid(!selection.head || capabilities.humanoidBones.includes("head"), "Head control absent.");
    requireValid(!selection.secondaryMotion || capabilities.secondaryMotion, "Secondary motion absent; author spring joints or omit this aspect.");
    const owned = new Set<string>();
    const ownedMorphs = new Set<string>();
    const ownedMaterials = new Set<string>();
    if (selection.gaze && capabilities.gaze === "expression")
      capabilities.expressions.filter(e => gazePresets.includes(e.name as typeof gazePresets[number]))
        .forEach(e => {
          e.morphTargets.forEach(t => ownedMorphs.add(t));
          e.materialTargets.forEach(t => ownedMaterials.add(t));
        });
    for (const mapping of selection.mappings) {
      identifier(mapping.channel, "coefficient channel");
      requireValid(["mouth", "expression", "blink"].includes(mapping.aspect), "Invalid mapping aspect.");
      const target = capabilities.expressions.find(e => e.name === mapping.expression && e.usable);
      requireValid(target, `Expression ${mapping.expression} is absent or has no usable morph binding; author it or omit this mapping.`);
      requireValid(!owned.has(target.name), `Duplicate expression writer: ${target.name}.`);
      requireValid(!gazePresets.includes(target.name as typeof gazePresets[number]), "Directional expressions belong exclusively to local gaze.");
      if (mouthPresets.includes(target.name as typeof mouthPresets[number])) requireValid(mapping.aspect === "mouth", "Vowel presets must have mouth ownership.");
      if (blinkPresets.includes(target.name as typeof blinkPresets[number])) requireValid(mapping.aspect === "blink", "Blink presets must have blink ownership.");
      if (selection.faceMode === "reduced-vowel-jaw-only") requireValid(mapping.aspect === "mouth", "Reduced mode accepts only explicitly mapped mouth controls; it is not detailed facial animation.");
      finite(mapping.minimum, 0, 1, "mapping minimum"); finite(mapping.maximum, 0, 1, "mapping maximum");
      requireValid(mapping.minimum < mapping.maximum, "Mapping range must increase within 0..1.");
      for (const morph of target.morphTargets) {
        requireValid(!ownedMorphs.has(morph), `Morph ${morph} has competing selected expression/gaze writers; select a non-overlapping set.`);
      }
      for (const material of target.materialTargets)
        requireValid(!ownedMaterials.has(material), `Material ${material} has competing selected expression/gaze writers.`);
      target.morphTargets.forEach(t => ownedMorphs.add(t));
      target.materialTargets.forEach(t => ownedMaterials.add(t));
      owned.add(target.name);
    }
    // Validate everything before changing an active turn.
    this.clearControls();
    this.selection = structuredClone(selection);
    this.revision = { ...revision };
    this.inputMode = inputMode;
    const expressions = model.expressionManager;
    if (expressions) {
      expressions.mouthExpressionNames = [...new Set([...mouthPresets, ...selection.mappings.filter(m => m.aspect === "mouth").map(m => m.expression)])];
      expressions.blinkExpressionNames = [...new Set([...blinkPresets, ...selection.mappings.filter(m => m.aspect === "blink").map(m => m.expression)])];
    }
  }

  reset(identity: PlaybackIdentity): void {
    this.loaded();
    validateIdentity(identity);
    requireValid(this.selection, "Configure aspect ownership before reset.");
    requireValid(identity.sourceId === this.selection.faceSource, "Identity source differs from configured face owner.");
    this.clearControls();
    this.identity = identityKey(identity);
  }

  /** Host must pass device-rendered playback samples, never submitted/queued/software-consumed samples. */
  applyFrame(input: CoefficientInput, actualPlaybackSampleOffset: number): void {
    const model = this.loaded();
    this.validateFrame(input, actualPlaybackSampleOffset);
    requireValid(this.inputMode === "coefficients", "Coefficient input is disabled in composed input mode.");
    this.validateRevision(input);
    const selection = this.selection!;
    this.validateValues(input.coefficients, new Set(selection.mappings.map(m => m.channel)));
    for (const mapping of selection.mappings)
      model.expressionManager!.setValue(mapping.expression, mapping.minimum + input.coefficients[mapping.channel]! * (mapping.maximum - mapping.minimum));
    this.acceptFrame(input, actualPlaybackSampleOffset);
  }

  /** Shared host has already performed mapping: apply exact authored targets once, without affine conversion. */
  applyComposedParameters(input: ComposedParameterInput, actualPlaybackSampleOffset: number): void {
    const model = this.loaded();
    this.validateFrame(input, actualPlaybackSampleOffset);
    requireValid(this.inputMode === "composed", "Composed input is disabled in coefficient input mode.");
    this.validateRevision(input);
    this.validateValues(input.parameters, new Set(this.selection!.mappings.map(m => m.expression)));
    for (const [target, value] of Object.entries(input.parameters)) model.expressionManager!.setValue(target, value);
    this.acceptFrame(input, actualPlaybackSampleOffset);
  }

  private validateRevision(input: CompositionRevision): void {
    requireValid(this.revision && input.modelRevision === this.revision.modelRevision && input.mappingRevision === this.revision.mappingRevision,
      "Input requires the current bound model/mapping revision.");
  }
  private validateFrame(input: Pick<CoefficientInput, "identity" | "sequence" | "sampleOffset">, actualPlaybackSampleOffset: number): void {
    requireValid(this.selection && this.identity, "Configure and reset a trusted playback identity before applying frames.");
    validateIdentity(input.identity);
    requireValid(identityKey(input.identity) === this.identity, "Stale or foreign playback identity.");
    integer(input.sequence, 0, 2147483647, "sequence");
    requireValid(input.sequence > this.sequence, "Frame sequence must strictly increase.");
    integer(input.sampleOffset, 0, Number.MAX_SAFE_INTEGER, "sampleOffset");
    integer(actualPlaybackSampleOffset, 0, Number.MAX_SAFE_INTEGER, "actualPlaybackSampleOffset");
    requireValid(input.sampleOffset >= this.sampleOffset && actualPlaybackSampleOffset >= this.playbackOffset, "Sample clocks must not rewind without explicit reset.");
    requireValid(input.sampleOffset <= actualPlaybackSampleOffset
      && actualPlaybackSampleOffset - input.sampleOffset <= input.identity.sampleRate / 4, "Frame is future or more than 250ms late; schedule against device-rendered PCM.");
  }
  private validateValues(values: Readonly<Record<string, number>>, expected: ReadonlySet<string>): void {
    requireValid(values !== null && typeof values === "object" && !Array.isArray(values), "Coefficients/parameters must be a record.");
    const entries = Object.entries(values);
    requireValid(entries.length <= 128, "Too many coefficients.");
    requireValid(entries.length === expected.size && entries.every(([key]) => expected.has(key)),
      "Pass exactly the explicitly selected channels; unmapped inputs must be resolved/omitted by the host compatibility gate.");
    entries.forEach(([key, value]) => finite(value, 0, 1, key));
  }
  private acceptFrame(input: Pick<CoefficientInput, "sequence" | "sampleOffset">, actualPlaybackSampleOffset: number): void {
    this.sequence = input.sequence;
    this.sampleOffset = input.sampleOffset;
    this.playbackOffset = actualPlaybackSampleOffset;
    this.composedAge = 0;
  }

  /** Local procedural controls only; not an A2F/AvatarFrame pose protocol. */
  setPose(pose: LocalPose): void {
    this.loaded();
    requireValid(this.selection && this.identity, "Configure/reset before setting local pose.");
    requireValid(pose !== null && typeof pose === "object", "Local pose required.");
    if (pose.gaze !== undefined) {
      requireValid(this.selection.gaze && Array.isArray(pose.gaze) && pose.gaze.length === 3, "Gaze not selected or invalid world-space target.");
      pose.gaze.forEach(v => finite(v, -100, 100, "gaze"));
    }
    if (pose.head !== undefined) {
      requireValid(this.selection.head && Array.isArray(pose.head) && pose.head.length === 4, "Head not selected or invalid normalized quaternion.");
      pose.head.forEach(v => finite(v, -1, 1, "head"));
      requireValid(Math.abs(Math.hypot(...pose.head) - 1) < 0.001, "Head quaternion must be normalized.");
    }
    this.pose = structuredClone(pose);
  }

  update(deltaSeconds: number): void {
    const model = this.loaded();
    finite(deltaSeconds, 0, 0.1, "deltaSeconds");
    this.poses++;
    this.composedAge += deltaSeconds;
    // The bones a clip turned go back to rest first, so the idle pose below is the clip's base.
    this.clips.rest();
    if (this.identity && this.selection?.head) {
      // The head quaternion is for VRM 1.0's normalized bones; a VRM 0.x model's are mirrored (see animateIdle).
      const [x, y, z, w] = this.pose.head ?? [0, 0, 0, 1], sign = this.mirrored ? -1 : 1;
      model.humanoid.getNormalizedBoneNode("head")!.quaternion.set(sign * x, y, sign * z, w);
    }
    this.restoreFace(model);
    if (this.idle) this.animateIdle(model, deltaSeconds); else this.face = {};
    if (this.idle) this.clips.apply(deltaSeconds);
    this.updateActions(model, deltaSeconds);
    this.applyFace(model);
    this.giveMouth(model);
    model.humanoid.update();
    const neutralEyes: { node: THREE.Object3D; rotation: THREE.Quaternion }[] = [];
    if (model.lookAt && this.identity && this.selection?.gaze) {
      if (this.inspected?.gaze === "bone") {
        model.lookAt.reset();
        model.lookAt.update(0);
        for (const name of ["leftEye", "rightEye"] as const) {
          const node = model.humanoid.getRawBoneNode(name)!;
          neutralEyes.push({ node, rotation: node.quaternion.clone() });
        }
      }
      if (this.pose.gaze) {
        this.gazeTarget.position.fromArray(this.pose.gaze);
        model.lookAt.lookAt(this.gazeTarget.position);
      } else model.lookAt.reset();
      model.lookAt.update(deltaSeconds);
    }
    // The official manager applies overrideMouth/Blink/LookAt, including custom classifications.
    model.expressionManager?.update();
    if (model.lookAt && this.identity && this.selection?.gaze && this.inspected?.gaze === "bone") {
      let multiplier = 1;
      for (const expression of model.expressionManager?.expressions ?? [])
        multiplier -= expression.overrideLookAtAmount;
      // Blend the mapped output, not the input angles: authored range maps may saturate.
      for (const eye of neutralEyes)
        eye.node.quaternion.slerpQuaternions(eye.rotation, eye.node.quaternion.clone(), Math.max(0, multiplier));
    }
    model.nodeConstraintManager?.update();
    if ((this.idle || this.identity && this.selection?.secondaryMotion) && deltaSeconds > 0) model.springBoneManager?.update(deltaSeconds);
    for (const material of model.materials ?? []) {
      if ("update" in material && typeof material.update === "function") material.update(deltaSeconds);
    }
  }

  stop(): void { this.loaded(); this.clearControls(); }

  /** What of the posed model a ray hits first (see touch.ts), or undefined when it misses, with where the hit point was in the
   *  rest pose once idle has started (see startIdle). The skinned meshes' bounds are measured again once per pose, so a batch
   *  of points (a stroke) measures them once. */
  hitTestRay(raycaster: THREE.Raycaster): VrmHit | undefined {
    const model = this.loaded();
    const humanoid = new Map<THREE.Object3D, string>();
    for (const [name, bone] of Object.entries(model.humanoid.rawHumanBones)) if (bone?.node) humanoid.set(bone.node, name);
    const springs = new Set<THREE.Object3D>([...model.springBoneManager?.joints ?? []].map(joint => joint.bone));
    const measure = this.measured !== this.poses;
    this.measured = this.poses;
    return hitTestVrm(model.scene, humanoid, raycaster, springs, measure, this.rest);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    ++this.generation;
    this.releaseModel();
  }

  private alive(): void { requireValid(!this.disposed, "VRM runtime is disposed; create a new instance."); }
  private loaded(): VRM { this.alive(); requireValid(this.model, "Load a VRM before using the runtime."); return this.model; }
  private clearControls(): void {
    this.identity = undefined; this.sequence = -1; this.sampleOffset = -1; this.playbackOffset = -1; this.pose = {};
    this.composedAge = Number.POSITIVE_INFINITY;
    this.faceRestore.clear(); this.face = {};
    this.giveMouthBack();
    this.poses++;
    if (this.model) {
      this.model.humanoid.resetNormalizedPose();
      this.model.humanoid.update();
      this.model.lookAt?.reset();
      this.model.lookAt?.update(0);
      this.model.expressionManager?.resetValues();
      this.model.expressionManager?.update();
      this.model.springBoneManager?.reset();
    }
  }
  private releaseModel(): void {
    this.clearControls();
    if (this.model) {
      this.model.scene.removeFromParent();
      releaseResources(this.model.scene);
    }
    this.model = undefined; this.inspected = undefined; this.selection = undefined; this.revision = undefined; this.inputMode = undefined;
    this.eyes = undefined;
    this.featureHint = undefined;
    this.actions.clear(); this.heldExpressions.clear(); this.gesture = undefined; this.held = []; this.blush = undefined; this.hipsRest = undefined;
    this.rest = undefined;
    this.mirrored = false;
    this.clips.clear();
    this.talk = 0; this.speaking = false; this.heldMouth = 0;
  }
}
