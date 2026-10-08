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
    endGesture(name) { played.push(["release", name]); }
    get gestureState() { return { playing: "nod" }; }
    faceAnchor() { return { x: 250, y: 100, width: 80, angle: 0, cheekLeft: { x: 230, y: 115 }, cheekRight: { x: 270, y: 115 },
      eyeLeft: { x: 234, y: 100 }, eyeRight: { x: 266, y: 100 }, mouth: { x: 250, y: 130 }, top: { x: 250, y: 50 } }; }
  }
  const { send } = await page(Renderer, posts);
  await send({ kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } });
  await send({ kind: "action", data: { kind: "gesture", name: "nod" } });
  assert.equal(posts.at(-1).started, true);
  await send({ kind: "action", data: { kind: "gesture", name: "blush", hold: true } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1))), { started: true, overlay: true, face: { x: 250, y: 100, width: 80, tilt: 0 }, gesture: { playing: "nod", held: ["blush"] } });
  assert.deepEqual(played.at(-1), ["blush", true], "the model's own blush is tried first");
  await send({ kind: "action", data: { kind: "gesture", name: "blush", on: false, hold: true } });
  assert.equal(posts.at(-1).started, true);
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1).gesture.held)), [], "turned off, the drawn blush is no longer held");
  assert.deepEqual(played.at(-1), ["release", "blush"]);
  await send({ kind: "action", data: { kind: "gesture", name: "sparkle_nonexistent" } });
  assert.equal(posts.at(-1).started, false);
});

test("Martlet's overlay emotes play on any model, held ones reported with the held gestures", async () => {
  const posts = [];
  class Renderer {
    load() { return Promise.resolve({ expressions: [] }); }
    resize() {}
    update() {}
    setView() {}
    dispose() {}
    playGesture() { return false; }
    endGesture() {}
    get gestureState() { return { held: [] }; }
    faceAnchor() { return { x: 250, y: 100, width: 80, angle: 0, cheekLeft: { x: 230, y: 115 }, cheekRight: { x: 270, y: 115 },
      eyeLeft: { x: 234, y: 100 }, eyeRight: { x: 266, y: 100 }, mouth: { x: 250, y: 130 }, top: { x: 250, y: 50 } }; }
  }
  const { send } = await page(Renderer, posts);
  await send({ kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } });
  for (const name of ["sweat", "anger", "hearts", "sparkles", "tears", "gloom", "question", "exclaim", "sleepy", "music"]) {
    await send({ kind: "action", data: { kind: "gesture", name } });
    assert.equal(posts.at(-1).started, true, name);
    assert.equal(posts.at(-1).overlay, true, name);
  }
  const held = () => JSON.parse(JSON.stringify(posts.at(-1).gesture.held));
  await send({ kind: "action", data: { kind: "gesture", name: "gloom", hold: true } });
  assert.deepEqual(held(), ["gloom"]);
  await send({ kind: "action", data: { kind: "gesture", name: "sleepy", hold: true } });
  assert.deepEqual(held(), ["gloom", "sleepy"], "held drawings layer: holding another keeps the one held before");
  await send({ kind: "action", data: { kind: "gesture", name: "gloom" } });
  assert.deepEqual(held(), ["gloom", "sleepy"], "played once while held, a drawing stays held");
  await send({ kind: "action", data: { kind: "gesture", name: "sleepy", on: false, hold: true } });
  assert.deepEqual(held(), ["gloom"], "turning one off leaves the other on");
  await send({ kind: "action", data: { kind: "gesture", name: "gloom", on: false, hold: true } });
  assert.deepEqual(held(), []);
});

test("held drawings show on top of every gesture the model holds, and the reply names them all", async () => {
  const posts = [];
  const held = new Set();
  class Renderer {
    load() { return Promise.resolve({ expressions: [] }); }
    resize() {}
    update() {}
    setView() {}
    dispose() {}
    // The model holds its own gestures (layered by the adapter) and has no blush of its own, so the page draws it.
    playGesture(name, hold) { if (!["eyes_up", "mouth_open", "wink"].includes(name)) return false; if (hold) held.add(name); return true; }
    endGesture(name) { held.delete(name); }
    get gestureState() { return { held: [...held] }; }
    faceAnchor() { return { x: 250, y: 100, width: 80, angle: 0, cheekLeft: { x: 230, y: 115 }, cheekRight: { x: 270, y: 115 } }; }
  }
  const { send } = await page(Renderer, posts);
  await send({ kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } });
  for (const name of ["eyes_up", "mouth_open", "blush", "hearts"]) await send({ kind: "action", data: { kind: "gesture", name, hold: true } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1).gesture)), { held: ["eyes_up", "mouth_open", "blush", "hearts"] },
    "the model's held gestures, then the held drawings");
  await send({ kind: "action", data: { kind: "gesture", name: "wink" } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1).gesture.held)), ["eyes_up", "mouth_open", "blush", "hearts"],
    "a gesture played once lets none of them go");
  await send({ kind: "action", data: { kind: "gesture", name: "mouth_open", on: false, hold: true } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1).gesture.held)), ["eyes_up", "blush", "hearts"]);
  await send({ kind: "face", data: { id: 3 } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1))).faceReading.overlays, ["blush", "hearts"], "both drawings show");
  assert.ok(!posts.some(post => post.error));
});

test("the face Martlet draws over is read unprompted for MCP: how it is followed, where and what is under each cheek", async () => {
  const posts = [];
  class Renderer {
    load() { return Promise.resolve({ expressions: [] }); }
    resize() {}
    update() {}
    setView() {}
    dispose() {}
    playGesture() { return false; }
    endGesture() {}
    get gestureState() { return {}; }
    hitTest(x) { return x < 0.5 ? { bone: "head", mesh: "Face" } : undefined; }
    faceAnchor() { return { x: 250, y: 100, width: 80, angle: Math.PI / 18, tracking: "bones",
      cheekLeft: { x: 230, y: 115 }, cheekRight: { x: 270, y: 115 },
      cheekLeftFrame: { right: { x: 100, y: 0 }, down: { x: 0, y: 80 }, visible: 1 },
      cheekRightFrame: { right: { x: 40, y: 0 }, down: { x: 0, y: 80 }, visible: 0.25 } }; }
  }
  const { send } = await page(Renderer, posts);
  await send({ kind: "face", data: { id: 1 } });
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1))), { faceReading: { id: 1, found: false } }, "nothing before a model shows");
  await send({ kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } });
  await send({ kind: "action", data: { kind: "gesture", name: "blush" } });
  assert.equal(posts.at(-1).face.tracking, "bones", "the reply says how the face is followed");
  await send({ kind: "face", data: { id: 2 } });
  const reading = JSON.parse(JSON.stringify(posts.at(-1))).faceReading;
  assert.deepEqual([reading.id, reading.found, reading.tracking, reading.x, reading.y, reading.width, reading.tilt],
    [2, true, "bones", 0.5, 0.2, 0.16, 10]);
  assert.deepEqual(reading.cheekLeft, { x: 0.46, y: 0.23, visible: 1, across: 1.25, hit: true, drawables: [], bone: "head", mesh: "Face" });
  assert.deepEqual([reading.cheekRight.visible, reading.cheekRight.across, reading.cheekRight.hit], [0.25, 0.5, false]);
  assert.deepEqual(reading.overlays, ["blush"]);
  assert.ok(!posts.some(post => post.error));
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
  throwing = false;
  await onMessage({ data: { kind: "touches", data: { id: 4, points: [{ x: 0.25, y: 0.1 }, { x: 0.75, y: 0.1 }, { x: 0.3, y: 0.2 }] } } });
  assert.equal(posts.at(-1).touches.id, 4);
  assert.deepEqual(posts.at(-1).touches.hits.map(hit => hit.hit), [true, false, true]);
  assert.equal(posts.at(-1).touches.hits[0].bone, "head");
  assert.ok(!posts.some(post => post.error));
});
