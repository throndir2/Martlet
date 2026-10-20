import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { VRM, VRMLoaderPlugin, VRMUtils } from "@pixiv/three-vrm";
import { finite, inspectVrm, LIMITS, object, requireValid, VrmError, type VrmCapabilities } from "./inspect.js";

// Loading a local VRM file with only the bundled loader code, and releasing every GPU resource of a model.

export function releaseResources(value: unknown, seen = new Set<unknown>()): void {
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
    // MToon (a shader material, which every VRM 0.x MToon material becomes) keeps its textures in its uniforms.
    const uniforms: unknown = (value as { uniforms?: unknown }).uniforms;
    if (uniforms && typeof uniforms === "object")
      Object.values(uniforms).forEach(u => { const v: unknown = (u as { value?: unknown } | null)?.value; if (v instanceof THREE.Texture) releaseResources(v, seen); });
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
  requireValid(buffer instanceof ArrayBuffer && buffer.byteLength <= LIMITS.fileBytes, "VRM buffer exceeds limit.");
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
            requireValid(typeof createImageBitmap === "function", "Embedded images require browser createImageBitmap support.");
            const json = object(parser.json, "parser JSON");
            requireValid(Array.isArray(json.images), "Missing embedded images.");
            const image = object(json.images[sourceIndex], "embedded image");
            const bytes: unknown = await parser.getDependency("bufferView", Number(image.bufferView));
            const type = image.mimeType;
            requireValid(bytes instanceof ArrayBuffer && (type === "image/png" || type === "image/jpeg"), "Expected validated embedded PNG or JPEG bytes.");
            const bitmap = await createImageBitmap(new Blob([bytes], { type }),
              { premultiplyAlpha: "none", colorSpaceConversion: "none" });
            const texture = new THREE.Texture(bitmap);
            texture.needsUpdate = true;
            texture.userData.mimeType = type;
            resources.add(texture);
            if (failed) {
              releaseResources(texture, released);
              throw new VrmError("import-failed", "Embedded image completed after import failed.");
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
    throw new VrmError("import-failed", "The importer did not produce a VRM runtime.");
  }
  try {
    requireValid(errors.length === 0, errors.join(" "));
    requireValid(candidate.meta.metaVersion === capabilities.meta.metaVersion,
      `Importer did not produce VRM ${capabilities.meta.metaVersion === "0" ? "0.x" : "1.0"} metadata.`);
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
    // A VRM 0.x model faces -Z; turned 180 degrees it faces +Z (the camera) like a VRM 1.0 one. Its spring bones start again
    // from the turned pose, so they don't swing across on the first frame.
    VRMUtils.rotateVRM0(candidate);
    // updateMatrixWorld (not updateWorldMatrix) also renews each skinned mesh's bind inverse, as the first render does, so
    // skinned vertices read now (the eyes, touch bounds) are where they are drawn.
    candidate.scene.updateMatrixWorld(true);
    if (capabilities.format === "VRM0") candidate.springBoneManager?.setInitState();
    const box = new THREE.Box3().setFromObject(candidate.scene);
    if (!box.isEmpty()) finite(box.getSize(new THREE.Vector3()).length(), 0.0001, 100, "Rendered model size");
    return { vrm: candidate, capabilities };
  } catch (error) {
    fail();
    releaseResources(candidate.scene, released);
    throw error;
  }
}
