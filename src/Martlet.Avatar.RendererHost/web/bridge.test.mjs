import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFile } from "node:fs/promises";

// Links app.js's imports: the page's own modules (./overlay.js...) for real, the adapters as `Renderer`.
function link(context, Renderer) {
  const modules = new Map();
  return async function linker(specifier, referencing) {
    if (specifier.startsWith("./") || specifier.startsWith("../") && !/Martlet\.Avatar\.(Live2D|Vrm)/.test(specifier)) {
      const url = new URL(specifier, referencing.identifier).href;
      if (!modules.has(url)) {
        const module = new vm.SourceTextModule(await readFile(new URL(url), "utf8"), { context, identifier: url });
        modules.set(url, module);
        await module.link(linker);
      }
      return modules.get(url);
    }
    return new vm.SyntheticModule(
      specifier.includes("Live2D") ? ["Live2DAdapter", "LocalModelBundle"] : ["VrmAvatarAdapter"],
      function () {
        if (specifier.includes("Live2D")) {
          this.setExport("Live2DAdapter", Renderer); this.setExport("LocalModelBundle", class {});
        } else this.setExport("VrmAvatarAdapter", Renderer);
      }, { context });
  };
}

async function page(Renderer, posts) {
  let onMessage, draw;
  const element = { clientWidth: 500, clientHeight: 500, width: 500, height: 500 };
  const context = vm.createContext({
    document: { getElementById: () => element },
    window: { chrome: { webview: {
      postMessage: data => posts.push(data),
      addEventListener: (_event, callback) => { onMessage = callback; }
    } } },
    requestAnimationFrame: callback => { draw = callback; },
    fetch: async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(1) }),
    ArrayBuffer, Uint8Array, Map, Set, Error
  });
  const module = new vm.SourceTextModule(await readFile(new URL("app.js", import.meta.url), "utf8"),
    { context, identifier: new URL("app.js", import.meta.url).href });
  await module.link(link(context, Renderer));
  await module.evaluate();
  return { send: message => onMessage({ data: message }), draw: now => draw(now) };
}

test("a blush the model can't show is drawn over the face, held until turned off", async () => {
  const posts = [];
  const played = [];
  class Renderer {
    load() { return Promise.resolve({ expressions: [] }); }
    resize() {}
    update() {}
    setView() {}
    dispose() {}
    playGesture(name, hold) { played.push([name, hold]); return name === "nod"; }
    releaseGesture(name) { played.push(["release", name]); return false; }
    faceAnchor() { return { x: 250, y: 100, width: 80, angle: 0, cheekLeft: { x: 230, y: 115 }, cheekRight: { x: 270, y: 115 },
      eyeLeft: { x: 234, y: 100 }, eyeRight: { x: 266, y: 100 }, mouth: { x: 250, y: 130 }, top: { x: 250, y: 50 } }; }
  }
  const { send } = await page(Renderer, posts);
  await send({ kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } });
  await send({ kind: "action", data: { kind: "gesture", name: "nod" } });
  assert.deepEqual({ ...posts.at(-1) }, { started: true });
  await send({ kind: "action", data: { kind: "gesture", name: "blush", hold: true } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1))), { started: true, overlay: true, face: { x: 250, y: 100, width: 80 } });
  assert.deepEqual(played.at(-1), ["blush", true], "the model's own blush is tried first");
  await send({ kind: "action", data: { kind: "gesture", name: "blush", on: false } });
  assert.equal(posts.at(-1).started, true);
  assert.deepEqual(played.at(-1), ["release", "blush"]);
  await send({ kind: "action", data: { kind: "gesture", name: "sparkle_nonexistent" } });
  assert.equal(posts.at(-1).started, false);
});

test("shared Live2D/VRM page leaves the desktop visible behind the canvas", async () => {
  const html = await readFile(new URL("index.html", import.meta.url), "utf8");
  assert.match(html, /html,body\{[^}]*background:transparent[;}]/);
  assert.match(html, /canvas\{[^}]*background:transparent[;}]/);
  assert.match(html, /overflow:hidden/);
  assert.doesNotMatch(html, /background:\s*#/);
});

test("actual shell latches idle draw failure and never acknowledges a later apply", async () => {
  const posts = [];
  let onMessage, draw, applies = 0;
  class Renderer {
    load() { return Promise.resolve({ expressions: [{ name: "aa", usable: true }] }); }
    configure() {}
    reset() {}
    resize() {}
    update() { throw new Error("controlled WebGL loss"); }
    stop() {}
    dispose() {}
    applyComposedParameters() { applies++; return true; }
  }
  const context = vm.createContext({
    document: { getElementById: () => ({ clientWidth: 500, clientHeight: 500 }) },
    window: { chrome: { webview: {
      postMessage: data => posts.push(data),
      addEventListener: (_event, callback) => { onMessage = callback; }
    } } },
    requestAnimationFrame: callback => { draw = callback; },
    fetch: async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(1) }),
    ArrayBuffer, Uint8Array, Map, Set, Error
  });
  const module = new vm.SourceTextModule(await readFile(new URL("app.js", import.meta.url), "utf8"),
    { context, identifier: new URL("app.js", import.meta.url).href });
  await module.link(link(context, Renderer));
  await module.evaluate();
  await onMessage({ data: { kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } } });
  await onMessage({ data: { kind: "configure", data: { targets: [{ target: "aa", aspect: "Mouth" }],
    sourceId: "nvidia-audio2face", modelRevision: "model", mappingRevision: "mapping" } } });
  draw(1);
  assert.equal(posts.at(-1).error, "avatar.renderer_failed");
  const start = posts.length;
  await onMessage({ data: { kind: "apply", data: { modelRevision: "model", mappingRevision: "mapping", parameters: { aa: 1 } } } });
  assert.equal(applies, 0);
  assert.equal(posts.length, start + 1);
  assert.equal(posts.at(-1).error, "avatar.renderer_rejected");
});

test("a tap is hit-tested and answered unprompted; a failing hit test is a miss, never a renderer failure", async () => {
  const posts = [];
  let onMessage, throwing = false;
  class Renderer {
    load() { return Promise.resolve({ expressions: [] }); }
    setView() {}
    startIdle() {}
    hitTest(x, y) {
      if (throwing) throw new Error("controlled hit test failure");
      return x < 0.5 ? { bone: "head", node: "HairRoot", hair: true, mesh: "Hair", material: "HairMat", point: { x, y, z: 0 } } : undefined;
    }
    dispose() {}
  }
  const context = vm.createContext({
    document: { getElementById: () => ({ clientWidth: 500, clientHeight: 500 }) },
    window: { chrome: { webview: {
      postMessage: data => posts.push(data),
      addEventListener: (_event, callback) => { onMessage = callback; }
    } } },
    requestAnimationFrame: () => {},
    fetch: async () => ({ ok: true, arrayBuffer: async () => new ArrayBuffer(1) }),
    ArrayBuffer, Uint8Array, Map, Set, Error, Number
  });
  const module = new vm.SourceTextModule(await readFile(new URL("app.js", import.meta.url), "utf8"),
    { context, identifier: new URL("app.js", import.meta.url).href });
  await module.link(link(context, Renderer));
  await module.evaluate();
  await onMessage({ data: { kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } } });
  await onMessage({ data: { kind: "touch", data: { id: 1, x: 0.25, y: 0.1 } } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1))), { touch: { id: 1, hit: true, hitAreas: [], drawables: [], bone: "head",
    node: "HairRoot", hair: true, mesh: "Hair", material: "HairMat" } });
  await onMessage({ data: { kind: "touch", data: { id: 2, x: 0.75, y: 0.1 } } });
  assert.equal(posts.at(-1).touch.hit, false);
  throwing = true;
  await onMessage({ data: { kind: "touch", data: { id: 3, x: 0.25, y: 0.1 } } });
  assert.equal(posts.at(-1).touch.hit, false);
  assert.ok(!posts.some(post => post.error));
});
