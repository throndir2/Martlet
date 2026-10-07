// Browser entry compiled with the downloaded official Framework source into sdk.js (never committed).
import { CubismFramework } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/live2dcubismframework";
import { CubismMoc } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/model/cubismmoc";
import type { CubismModel as FrameworkModel } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/model/cubismmodel";
import { CubismRenderer_WebGL } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/rendering/cubismrenderer_webgl";
import { CubismMatrix44 } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/math/cubismmatrix44";
import { CubismMotion } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/motion/cubismmotion";
import { CubismMotionManager } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/motion/cubismmotionmanager";
import { CubismExpressionMotion } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/motion/cubismexpressionmotion";
import { CubismExpressionMotionManager } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/motion/cubismexpressionmotionmanager";
import { CubismEyeBlink } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/effect/cubismeyeblink";
import { BreathParameterData, CubismBreath } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/effect/cubismbreath";
import { CubismPhysics } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/physics/cubismphysics";
import { CubismPose } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/effect/cubismpose";
import type { CubismIdHandle } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/id/cubismid";
import { csmVector } from "../vendor/CubismSdkForWeb-5-r.4/Framework/src/type/csmvector";
import { FRAMEWORK_REVISION, type Animator, type AnimatorAssets, type AnimatorInput, type CubismModel, type SdkModules }
  from "../lib/sdk.js";

const PRIORITY_IDLE = 1;
const PRIORITY_NORMAL = 2;

function createAnimator(structural: CubismModel, assets: AnimatorAssets): Animator {
  const model = structural as unknown as FrameworkModel;
  const ids = CubismFramework.getIdManager();
  const present = new Set(assets.parameterIds);
  const id = (name: string): CubismIdHandle => ids.getId(name);
  const vector = (names: readonly string[]): csmVector<CubismIdHandle> => {
    const result = new csmVector<CubismIdHandle>();
    for (const name of names) if (present.has(name)) result.pushBack(id(name));
    return result;
  };
  const eyeBlinkIds = vector(assets.eyeBlinkIds);
  const lipSyncNames = assets.lipSyncIds.filter(name => present.has(name));
  if (lipSyncNames.length === 0 && present.has("ParamMouthOpenY")) lipSyncNames.push("ParamMouthOpenY");
  const lipSyncIds = vector(lipSyncNames);
  const lipSyncHandles = lipSyncNames.map(id);

  const released: (() => void)[] = [];
  const motionManager = new CubismMotionManager();
  released.push(() => motionManager.release());
  const expressionManager = new CubismExpressionMotionManager();
  released.push(() => expressionManager.release());
  const held = new Map<string, CubismExpressionMotionManager>();

  const motions = new Map<string, CubismMotion[]>();
  for (const [group, entries] of Object.entries(assets.motions)) {
    const loaded: CubismMotion[] = [];
    for (const entry of entries) {
      const motion = CubismMotion.create(entry.bytes, entry.bytes.byteLength);
      if (!motion) continue;
      if (entry.fadeIn !== undefined) motion.setFadeInTime(entry.fadeIn);
      if (entry.fadeOut !== undefined) motion.setFadeOutTime(entry.fadeOut);
      motion.setEffectIds(eyeBlinkIds, lipSyncIds);
      loaded.push(motion);
      released.push(() => motion.release());
    }
    if (loaded.length) motions.set(group, loaded);
  }
  const idleGroup = [...motions.keys()].find(group => group.toLowerCase() === "idle");

  const expressions = new Map<string, CubismExpressionMotion>();
  for (const entry of assets.expressions) {
    const expression = CubismExpressionMotion.create(entry.bytes, entry.bytes.byteLength);
    if (!expression) continue;
    expressions.set(entry.name, expression);
    released.push(() => expression.release());
  }
  // Starting an expression that changes nothing fades the current one out, rather than snapping back.
  const blankBytes = new TextEncoder().encode(JSON.stringify({ Type: "Live2D Expression", FadeInTime: 0.6, FadeOutTime: 0.6, Parameters: [] }));
  const blank = CubismExpressionMotion.create(blankBytes.buffer as ArrayBuffer, blankBytes.byteLength);
  if (blank) released.push(() => blank.release());

  let eyeBlink: CubismEyeBlink | undefined;
  if (eyeBlinkIds.getSize() > 0) {
    eyeBlink = CubismEyeBlink.create(null);
    eyeBlink.setParameterIds(eyeBlinkIds);
    released.push(() => CubismEyeBlink.delete(eyeBlink!));
  }

  const breathing = new csmVector<BreathParameterData>();
  for (const [name, offset, peak, cycle, weight] of [
    ["ParamAngleX", 0, 15, 6.5345, 0.5], ["ParamAngleY", 0, 8, 3.5345, 0.5], ["ParamAngleZ", 0, 10, 5.5345, 0.5],
    ["ParamBodyAngleX", 0, 4, 15.5345, 0.5], ["ParamBreath", 0.5, 0.5, 3.2345, 1],
  ] as const) {
    if (present.has(name)) breathing.pushBack(new BreathParameterData(id(name), offset, peak, cycle, weight));
  }
  let breath: CubismBreath | undefined;
  if (breathing.getSize() > 0) {
    breath = CubismBreath.create();
    breath.setParameters(breathing);
    released.push(() => CubismBreath.delete(breath!));
  }

  let physics: CubismPhysics | undefined;
  if (assets.physics) {
    physics = CubismPhysics.create(assets.physics, assets.physics.byteLength);
    released.push(() => CubismPhysics.delete(physics!));
  }
  let pose: CubismPose | undefined;
  if (assets.pose) {
    pose = CubismPose.create(assets.pose, assets.pose.byteLength);
    if (pose) released.push(() => CubismPose.delete(pose!));
  }

  const look = [
    ["ParamAngleX", 30, 0], ["ParamAngleY", 0, 30], ["ParamBodyAngleX", 10, 0],
    ["ParamEyeBallX", 1, 0], ["ParamEyeBallY", 0, 1],
  ].filter(([name]) => present.has(name as string))
    .map(([name, x, y]) => ({ handle: id(name as string), x: x as number, y: y as number }));
  const angleZ = present.has("ParamAngleZ") ? id("ParamAngleZ") : undefined;

  const start = (group: string, priority: number): boolean => {
    const candidates = motions.get(group);
    if (!candidates?.length) return false;
    if (!motionManager.reserveMotion(priority)) return false;
    const motion = candidates[Math.floor(Math.random() * candidates.length)]!;
    motionManager.startMotionPriority(motion, false, priority);
    return true;
  };

  model.saveParameters();
  let alive = true;
  return {
    motionGroups: Object.freeze([...motions.keys()]),
    expressions: Object.freeze([...expressions.keys()]),
    update(deltaSeconds: number, input: AnimatorInput): void {
      if (!alive) return;
      let motionUpdated = false;
      model.loadParameters();
      if (motionManager.isFinished()) {
        if (idleGroup) start(idleGroup, PRIORITY_IDLE);
      } else {
        motionUpdated = motionManager.updateMotion(model, deltaSeconds);
      }
      model.saveParameters();
      if (!motionUpdated) eyeBlink?.updateParameters(model, deltaSeconds);
      expressionManager.updateMotion(model, deltaSeconds);
      // Each lingering expression has its own manager, which reads the parameters as the ones before left them, so they
      // layer (as VTube Studio's toggles do) instead of replacing each other.
      for (const manager of held.values()) manager.updateMotion(model, deltaSeconds);
      for (const target of look) model.addParameterValueById(target.handle, input.lookX * target.x + input.lookY * target.y);
      if (angleZ) model.addParameterValueById(angleZ, input.lookX * input.lookY * -30);
      if (input.gesture) for (const [name, value] of Object.entries(input.gesture))
        if (value !== 0 && present.has(name)) model.addParameterValueById(id(name), value);
      breath?.updateParameters(model, deltaSeconds);
      input.overrides();
      physics?.evaluate(model, deltaSeconds);
      if (input.lipSync > 0) for (const handle of lipSyncHandles) model.addParameterValueById(handle, input.lipSync, 0.8);
      pose?.updateParameters(model, deltaSeconds);
    },
    playMotion(group: string): boolean {
      return alive && start(group, PRIORITY_NORMAL);
    },
    setExpression(name: string | null): boolean {
      if (!alive) return false;
      if (name === null) {
        if (blank) expressionManager.startMotion(blank, false);
        else expressionManager.stopAllMotions();
        return true;
      }
      const expression = expressions.get(name);
      if (!expression) return false;
      expressionManager.startMotion(expression, false);
      return true;
    },
    holdExpression(name: string, on: boolean): boolean {
      if (!alive) return false;
      const expression = expressions.get(name);
      if (!expression) return false;
      let manager = held.get(name);
      if (!on) {
        // Fades out; the manager stays for the next time it is turned on.
        if (manager) { if (blank) manager.startMotion(blank, false); else manager.stopAllMotions(); }
        return true;
      }
      if (!manager) {
        manager = new CubismExpressionMotionManager();
        const created = manager;
        released.push(() => created.release());
        held.set(name, manager);
      }
      manager.startMotion(expression, false);
      return true;
    },
    release(): void {
      if (!alive) return;
      alive = false;
      const errors: unknown[] = [];
      for (const action of released.reverse()) {
        try { action(); } catch (error) { errors.push(error); }
      }
      if (errors.length) throw new AggregateError(errors, "Live2D animator cleanup failed.");
    },
  };
}

export const sdk: SdkModules = {
  revision: FRAMEWORK_REVISION,
  CubismFramework, CubismMoc, CubismRenderer_WebGL, CubismMatrix44,
  createAnimator,
} as unknown as SdkModules;
