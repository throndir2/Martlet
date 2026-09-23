import { Live2DAdapter, LocalModelBundle } from "../../Martlet.Avatar.Live2D/lib/index.ts";
import { VrmAvatarAdapter } from "../../Martlet.Avatar.Vrm/src/index.ts";

const canvas = document.getElementById("avatar");
let adapter, renderer, revision, configurationId, active = false, last = 0, failed = false;
const post = value => window.chrome.webview.postMessage(value);
const resource = name => fetch(`asset/${name}`).then(response => {
  if (!response.ok) throw new Error("Local resource unavailable.");
  return response.arrayBuffer();
});
const mouth = new Set(["aa", "ih", "ou", "ee", "oh"]);
const blink = new Set(["blink", "blinkLeft", "blinkRight"]);
window.chrome.webview.addEventListener("message", async ({ data: message }) => {
  try {
    const data = message.data;
    if (message.kind === "load") {
      renderer = data.renderer;
      if (renderer === "Live2D") {
        await new Promise((resolve, reject) => {
          const script = document.createElement("script");
          script.src = "asset/core.js"; script.onload = resolve; script.onerror = reject;
          document.head.append(script);
        });
        const { sdk } = await import(/* @vite-ignore */ "./asset/sdk.js");
        adapter = new Live2DAdapter(canvas, { sdk, onDiagnostic: diagnostic => {
          if (diagnostic.code === "CONTEXT_LOST") { active = false; failed = true; post({ error: "avatar.context_lost" }); }
        } });
        const files = new Map();
        for (const name of data.assets.filter(name => !["core.js", "sdk.js"].includes(name)))
          files.set(name, new Uint8Array(await resource(name)));
        const capabilities = await adapter.load(new LocalModelBundle(files, data.modelFile));
        post({ modelId: data.resourceRevision.toLowerCase(), parameters: capabilities.parameters.map(p => ({
          id: p.id, minimum: p.minimum, maximum: p.maximum, neutral: p.neutral,
          aspects: p.groups.includes("LipSync") ? ["Mouth"] : p.groups.includes("EyeBlink") ? ["Expression"] : ["Mouth", "Expression"]
        })) });
      } else if (renderer === "Vrm") {
        adapter = new VrmAvatarAdapter(canvas);
        const capabilities = await adapter.load(await resource(data.modelFile));
        post({ modelId: data.resourceRevision.toLowerCase(), parameters: capabilities.expressions.filter(p => p.usable &&
          !p.name.startsWith("look")).map(p => ({ id: p.name, minimum: 0, maximum: 1, neutral: 0,
          aspects: [mouth.has(p.name) ? "Mouth" : "Expression"] })) });
      } else throw new Error("Unsupported renderer.");
      active = true;
    } else if (message.kind === "configure") {
      revision = data;
      if (renderer === "Live2D") {
        adapter.configureTargets(data.targets.map(t => t.target));
        configurationId = adapter.configurationId;
      } else {
        adapter.configure({ faceSource: data.sourceId, faceMode: "authored-explicit",
          mappings: data.targets.map((t, index) => ({ channel: `target${index}`, expression: t.target,
            aspect: t.aspect === "Mouth" ? "mouth" : blink.has(t.target) ? "blink" : "expression", minimum: 0, maximum: 1 })),
          gaze: false, head: false, secondaryMotion: false },
        { modelRevision: data.modelRevision, mappingRevision: data.mappingRevision });
      }
      post({});
    } else if (message.kind === "reset") {
      if (renderer === "Live2D") adapter.resetEpoch(data); else adapter.reset(data);
      post({});
    } else if (message.kind === "apply") {
      if (!revision || data.modelRevision !== revision.modelRevision || data.mappingRevision !== revision.mappingRevision)
        throw new Error("Stale mapping.");
      if (renderer === "Live2D") {
        const result = adapter.applyComposedParameters({ identity: data.identity, sequence: data.sequence,
          configurationId, parameters: data.parameters });
        if (!result.accepted) throw new Error("Frame rejected.");
      } else {
        const result = adapter.applyComposedParameters(data, data.actualPlaybackSampleOffset);
        if (result === false) throw new Error("Frame rejected.");
      }
      post({});
    } else if (message.kind === "stop") { adapter.stop(); post({}); }
    else throw new Error("Unsupported command.");
  } catch {
    active = false; failed = true;
    adapter?.stop(); post({ error: "avatar.renderer_rejected" });
  }
});
function draw(now) {
  if (active && !failed && adapter) {
    try {
      const width = Math.min(2048, Math.max(1, Math.floor(canvas.clientWidth)));
      const height = Math.min(2048, Math.max(1, Math.floor(canvas.clientHeight)));
      if (renderer === "Vrm") adapter.resize(width, height);
      else if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
      adapter.update(Math.min(0.1, last ? (now - last) / 1000 : 0));
    } catch { failed = true; active = false; post({ error: "avatar.renderer_failed" }); }
  }
  last = now;
  requestAnimationFrame(draw);
}
requestAnimationFrame(draw);
post({ ready: true });
