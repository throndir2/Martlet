import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { VRM, VRMLoaderPlugin } from "@pixiv/three-vrm";
import { blinkPresets, finite, gazePresets, inspectVrm, integer, mouthPresets, object, requireValid, VrmError,
  type VrmCapabilities } from "./inspect.js";

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
function identifier(value: unknown, label: string): asserts value is string {
  requireValid(typeof value === "string" && /^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/.test(value), `Invalid ${label}.`);
}
function validateIdentity(identity: PlaybackIdentity): void {
  requireValid(identity !== null && typeof identity === "object", "Playback identity is required.");
  for (const key of ["sessionId", "turnId", "requestId"] as const)
    requireValid(typeof identity[key] === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(identity[key])
      && identity[key] !== "00000000-0000-0000-0000-000000000000", `${key} must be a nonempty UUID.`);
  identifier(identity.sourceId, "sourceId");
  integer(identity.epoch, 0, 2147483647, "epoch");
  requireValid([16000, 24000, 44100, 48000].includes(identity.sampleRate), "Unsupported original PCM sample rate.");
}
function identityKey(identity: PlaybackIdentity): string {
  return [identity.sessionId.toLowerCase(), identity.turnId.toLowerCase(), identity.requestId.toLowerCase(),
    identity.sourceId, identity.epoch, identity.sampleRate].join("|");
}

function releaseResources(value: unknown, seen = new Set<unknown>()): void {
  if (!value || seen.has(value)) return;
  seen.add(value);
  if (value instanceof THREE.Object3D) {
    value.traverse(node => {
      if (node instanceof THREE.Mesh) {
        releaseResources(node.geometry, seen);
        releaseResources(node.material, seen);
      }
      if (node instanceof THREE.SkinnedMesh) node.skeleton.dispose();
    });
  } else if (Array.isArray(value)) value.forEach(v => releaseResources(v, seen));
  else if (value instanceof THREE.Material) {
    Object.values(value).forEach(v => { if (v instanceof THREE.Texture) releaseResources(v, seen); });
    value.dispose();
  } else if (value instanceof THREE.Texture) {
    const image: unknown = value.source.data;
    if (typeof ImageBitmap !== "undefined" && image instanceof ImageBitmap && !seen.has(image)) {
      seen.add(image); image.close();
    }
    value.dispose();
  } else if (value instanceof THREE.BufferGeometry) value.dispose();
}

/** Production importer also used by CPU-only tests. Only built-in, bundled loader code is registered. */
export async function loadLocalVrm(buffer: ArrayBuffer): Promise<{ vrm: VRM; capabilities: VrmCapabilities }> {
  requireValid(buffer instanceof ArrayBuffer && buffer.byteLength <= 32 * 1024 * 1024, "VRM buffer exceeds limit.");
  // Own the bytes across async decoding; caller mutation must not bypass preflight.
  const snapshot = buffer.slice(0);
  const capabilities = inspectVrm(snapshot);
  const manager = new THREE.LoadingManager();
  const errors: string[] = [];
  manager.onError = () => { errors.push("Embedded resource decode failed."); };
  manager.setURLModifier(url => {
    throw new VrmError("forbidden-resource", `URL loading is forbidden: ${url.slice(0, 64)}`);
  });
  const loader = new GLTFLoader(manager);
  const resources = new Set<unknown>();
  const released = new Set<unknown>();
  let failed = false;
  loader.register(parser => {
    const images = new Map<number, Promise<THREE.Texture>>();
    // Per-parser boundary avoids GLTFLoader's success-only object-URL revocation.
    // Decode embedded bytes directly: no fetch, shared URL hooks, or object URLs.
    parser.loadImageSource = (sourceIndex) => {
      let pending = images.get(sourceIndex);
      if (!pending) {
        pending = (async () => {
          try {
            requireValid(typeof createImageBitmap === "function", "Embedded PNG requires browser createImageBitmap support.");
            const json = object(parser.json, "parser JSON");
            requireValid(Array.isArray(json.images), "Missing embedded images.");
            const image = object(json.images[sourceIndex], "embedded image");
            const bytes: unknown = await parser.getDependency("bufferView", Number(image.bufferView));
            requireValid(bytes instanceof ArrayBuffer && image.mimeType === "image/png", "Expected validated embedded PNG bytes.");
            const bitmap = await createImageBitmap(new Blob([bytes], { type: "image/png" }),
              { premultiplyAlpha: "none", colorSpaceConversion: "none" });
            const texture = new THREE.Texture(bitmap);
            texture.needsUpdate = true;
            texture.userData.mimeType = "image/png";
            resources.add(texture);
            if (failed) {
              releaseResources(texture, released);
              throw new VrmError("import-failed", "Embedded PNG completed after import failed.");
            }
            return texture;
          } catch (error) {
            errors.push("Embedded resource decode failed.");
            throw error;
          }
        })();
        images.set(sourceIndex, pending);
        return pending;
      }
      return pending.then(texture => {
        const clone = texture.clone();
        resources.add(clone);
        if (failed) releaseResources(clone, released);
        return clone;
      });
    };
    const original = parser.getDependency.bind(parser);
    parser.getDependency = async (type, index) => {
      const result: unknown = await original(type, index);
      resources.add(result);
      if (failed) releaseResources(result, released);
      return result;
    };
    return new VRMLoaderPlugin(parser);
  });
  const fail = (): void => {
    failed = true;
    resources.forEach(value => releaseResources(value, released));
  };
  let gltf;
  try { gltf = await loader.parseAsync(snapshot, ""); }
  catch (error) { fail(); throw error; }
  const candidate: unknown = gltf.userData.vrm;
  if (!(candidate instanceof VRM)) {
    fail();
    throw new VrmError("import-failed", "The importer did not produce a VRM1 runtime.");
  }
  try {
    requireValid(errors.length === 0, errors.join(" "));
    requireValid(candidate.meta.metaVersion === "1", "Importer did not produce VRM1 metadata.");
    for (const expression of capabilities.expressions.filter(e => e.usable)) {
      const imported = candidate.expressionManager?.getExpression(expression.name);
      requireValid(imported && imported.binds.length > 0, `Importer omitted usable bindings for authored expression ${expression.name}.`);
    }
    if (capabilities.secondaryMotion)
      requireValid(candidate.springBoneManager && [...candidate.springBoneManager.joints]
        .some(joint => joint.child && joint.initialLocalChildPosition.lengthSq() > 1e-12),
      "Importer did not produce usable spring joints; re-export a nonzero-length VRMC_springBone chain.");
    if (capabilities.gaze !== "absent") requireValid(candidate.lookAt, "Importer omitted the advertised gaze controller.");
    if (candidate.lookAt) candidate.lookAt.autoUpdate = false;
    candidate.scene.updateWorldMatrix(true, true);
    const box = new THREE.Box3().setFromObject(candidate.scene);
    if (!box.isEmpty()) finite(box.getSize(new THREE.Vector3()).length(), 0.0001, 100, "Rendered model size");
    return { vrm: candidate, capabilities };
  } catch (error) {
    fail();
    releaseResources(candidate.scene, released);
    throw error;
  }
}

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

  get capabilities(): VrmCapabilities | undefined { return this.inspected; }
  get scene(): THREE.Group | undefined { return this.model?.scene; }
  get isLoaded(): boolean { return this.model !== undefined; }

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
    if (this.identity && this.selection?.head) {
      model.humanoid.getNormalizedBoneNode("head")!.quaternion.fromArray(this.pose.head ?? [0, 0, 0, 1]);
    }
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
    if (this.identity && this.selection?.secondaryMotion && deltaSeconds > 0) model.springBoneManager?.update(deltaSeconds);
    for (const material of model.materials ?? []) {
      if ("update" in material && typeof material.update === "function") material.update(deltaSeconds);
    }
  }

  stop(): void { this.loaded(); this.clearControls(); }

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
  }
}
