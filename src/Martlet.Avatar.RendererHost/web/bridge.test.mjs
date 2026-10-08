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
  assert.deepEqual(JSON.parse(JSON.stringify(posts.at(-1))), { started: true, overlay: true, face: { x: 250, y: 100, width: 80, tilt: 0 }, gesture: { playing: "nod", held: "blush" } });
  assert.deepEqual(played.at(-1), ["blush", true], "the model's own blush is tried first");
  await send({ kind: "action", data: { kind: "gesture", name: "blush", on: false, hold: true } });
  assert.equal(posts.at(-1).started, true);
  assert.equal(posts.at(-1).gesture.held, undefined, "turned off, the drawn blush is no longer held");
  assert.deepEqual(played.at(-1), ["release", "blush"]);
  await send({ kind: "action", data: { kind: "gesture", name: "sparkle_nonexistent" } });
  assert.equal(posts.at(-1).started, false);
});

test("Martlet's overlay emotes play on any model, a held one reported as the held gesture", async () => {
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
  await send({ kind: "action", data: { kind: "gesture", name: "gloom", hold: true } });
  assert.equal(posts.at(-1).gesture.held, "gloom");
  await send({ kind: "action", data: { kind: "gesture", name: "sleepy", hold: true } });
  assert.equal(posts.at(-1).gesture.held, "sleepy", "holding another lets the one held before go");
  await send({ kind: "action", data: { kind: "gesture", name: "sleepy", on: false, hold: true } });
  assert.equal(posts.at(-1).gesture.held, undefined);
});

test("one blush level shows at a time, the stronger ones drawn over the model's own blush and on any model without one", async () => {
  const posts = [], played = [];
  let own = true;
  class Renderer {
    #held;
    load() { return Promise.resolve({ expressions: [] }); }
    resize() {}
    update() {}
    setView() {}
    dispose() {}
    playGesture(name, hold) { played.push([name, hold]); if (!own) return false; if (hold) this.#held = name; return true; }
    endGesture(name) { played.push(["release", name]); if (this.#held === name) this.#held = undefined; }
    get gestureState() { return this.#held ? { held: this.#held } : {}; }
    faceAnchor() { return { x: 250, y: 100, width: 80, angle: 0, tracking: "bones", cheekLeft: { x: 230, y: 115 }, cheekRight: { x: 270, y: 115 } }; }
  }
  const { send, draw } = await page(Renderer, posts);
  await send({ kind: "load", data: { renderer: "Vrm", resourceRevision: "a".repeat(64), modelFile: "model.vrm" } });
  const reply = () => JSON.parse(JSON.stringify(posts.at(-1)));
  const overlays = async () => { await send({ kind: "face", data: { id: 1 } }); return reply().faceReading.overlays; };
  let now = 0;
  const wait = seconds => { for (let i = 0; i < seconds * 10; i++) draw(now += 100); };

  // The model's own blush shows the blush, and Martlet draws nothing over it.
  await send({ kind: "action", data: { kind: "gesture", name: "blush", hold: true } });
  assert.deepEqual(reply(), { started: true, gesture: { held: "blush" } });
  assert.deepEqual(await overlays(), []);
  // A deep blush: the model's own blush with Martlet's deep blush drawn over it; the blush before lets go.
  await send({ kind: "action", data: { kind: "gesture", name: "blush_deep", hold: true } });
  assert.deepEqual(reply(), { started: true, overlay: true, face: { x: 250, y: 100, width: 80, tilt: 0, tracking: "bones" },
    gesture: { held: "blush_deep" } });
  assert.deepEqual(played.slice(-3), [["release", "blush"], ["release", "blush_fierce"], ["blush_deep", true]]);
  assert.deepEqual(await overlays(), ["blush_deep"]);
  // A fierce flush replaces it, and a passing blush replaces the fierce flush.
  await send({ kind: "action", data: { kind: "gesture", name: "blush_fierce", hold: true } });
  assert.equal(reply().gesture.held, "blush_fierce");
  wait(1);
  assert.deepEqual(await overlays(), ["blush_fierce"], "the deep blush faded out");
  await send({ kind: "action", data: { kind: "gesture", name: "blush" } });
  assert.deepEqual(reply(), { started: true, gesture: {} });
  wait(1);
  assert.deepEqual(await overlays(), []);

  // A model without a blush of its own: Martlet draws every level, one at a time.
  own = false;
  for (const level of ["blush", "blush_deep", "blush_fierce"]) {
    await send({ kind: "action", data: { kind: "gesture", name: level, hold: true } });
    assert.deepEqual([reply().started, reply().overlay, reply().gesture.held], [true, true, level], level);
  }
  wait(1);
  assert.deepEqual(await overlays(), ["blush_fierce"]);
  await send({ kind: "action", data: { kind: "gesture", name: "blush_fierce", on: false, hold: true } });
  assert.equal(reply().gesture.held, undefined);
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
