import * as THREE from "three";
import { finite, integer, requireValid, type VrmCapabilities } from "./inspect.js";
import { VrmRuntime } from "./runtime.js";

/** Host-driven WebGL renderer. Construct one per canvas; no hidden RAF or audio activation. */
export class VrmAvatarAdapter extends VrmRuntime {
  private renderer: THREE.WebGLRenderer;
  private world = new THREE.Scene();
  private camera = new THREE.PerspectiveCamera(30, 1, 0.01, 100);
  private view = { zoom: 1, x: 0, y: 0, frame: 1 };
  private top: number | undefined;
  private closed = false;

  constructor(canvas: HTMLCanvasElement) {
    super();
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
    this.renderer.setPixelRatio(1);
    this.world.add(new THREE.HemisphereLight(0xffffff, 0x555566, 2));
    const light = new THREE.DirectionalLight(0xffffff, 2);
    light.position.set(1, 2, 3);
    this.world.add(light);
  }

  override async load(buffer: ArrayBuffer): Promise<VrmCapabilities> {
    const result = await super.load(buffer);
    const scene = this.scene!;
    this.world.add(scene);
    scene.updateWorldMatrix(true, true);
    const box = new THREE.Box3().setFromObject(scene);
    if (!box.isEmpty()) {
      const center = box.getCenter(new THREE.Vector3());
      const size = box.getSize(new THREE.Vector3()).length();
      finite(size, 0.0001, 100, "Rendered model size");
      this.camera.position.set(center.x, center.y, center.z + size * 2);
      this.camera.lookAt(center);
      // The vertical field of view is fixed, so the fitted top does not depend on the canvas aspect.
      this.camera.updateMatrixWorld();
      this.camera.updateProjectionMatrix();
      let top = Number.NEGATIVE_INFINITY;
      for (const x of [box.min.x, box.max.x]) for (const z of [box.min.z, box.max.z])
        top = Math.max(top, new THREE.Vector3(x, box.max.y, z).project(this.camera).y);
      this.top = Number.isFinite(top) ? top : undefined;
      this.updateProjection();
    } else {
      this.camera.position.set(0, 1, 3);
      this.camera.lookAt(0, 1, 0);
    }
    return result;
  }

  resize(width: number, height: number): void {
    requireValid(!this.closed, "Renderer is disposed.");
    integer(width, 1, 4096, "canvas width"); integer(height, 1, 4096, "canvas height");
    this.renderer.setSize(width, height, false);
    this.camera.aspect = width / height;
    this.updateProjection();
  }

  /**
   * Camera zoom applied after fitting: screen = fitted * zoom + (x, y), in the frame's clip space. The model is centered
   * in a frame spanning `frame` of the canvas width (0.1 to 1); the canvas beyond it on each side is room the model can
   * move into without being cut off.
   */
  setView(zoom: number, x: number, y: number, frame = 1): void {
    requireValid(!this.closed, "Renderer is disposed.");
    finite(zoom, 1, 32, "view zoom"); finite(x, -1000, 1000, "view x"); finite(y, -1000, 1000, "view y");
    finite(frame, 0.1, 1, "view frame");
    this.view = { zoom, x, y, frame };
    this.updateProjection();
  }

  /** Top of the character (top of the head) in fitted clip space, before view zoom/pan; 1 is the canvas top. */
  get contentTop(): number | undefined { return this.top; }

  private updateProjection(): void {
    this.camera.updateProjectionMatrix();
    const { zoom, x, y, frame } = this.view;
    this.camera.projectionMatrix.premultiply(new THREE.Matrix4().set(zoom, 0, 0, x * frame, 0, zoom, 0, y, 0, 0, 1, 0, 0, 0, 0, 1));
    this.camera.projectionMatrixInverse.copy(this.camera.projectionMatrix).invert();
  }

  override update(deltaSeconds: number): void {
    super.update(deltaSeconds);
    requireValid(!this.renderer.getContext().isContextLost(), "WebGL context lost; dispose and recreate the adapter.");
    this.renderer.render(this.world, this.camera);
  }

  override dispose(): void {
    if (this.closed) return;
    this.closed = true;
    super.dispose();
    this.world.clear();
    this.renderer.dispose();
    this.renderer.forceContextLoss();
  }
}
