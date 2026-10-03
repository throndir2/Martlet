import { Live2DAdapter, LocalModelBundle } from "../../Martlet.Avatar.Live2D/lib/index.ts";
import { VrmAvatarAdapter } from "../../Martlet.Avatar.Vrm/src/index.ts";

const canvas = document.getElementById("avatar");
let adapter, renderer, revision, configurationId, active = false, last = 0, failed = false, reportedTop;
let view = { zoom: 1, x: 0, y: 0 };
const post = value => window.chrome.webview.postMessage(value);
// Percent-encodes every character but A-Z a-z 0-9 - . _ ~ (like .NET's Uri.EscapeDataString), so the host matches the
// request to the asset exactly for names such as 简.moc3 or "texture (1).png".
const encodePath = name => name.split("/").map(part => encodeURIComponent(part)
  .replace(/[!'()*]/g, c => "%" + c.charCodeAt(0).toString(16).toUpperCase())).join("/");
const resource = name => fetch(`asset/${encodePath(name)}`).then(response => {
  if (!response.ok) throw new Error(`Local resource ${name} unavailable.`);
  return response.arrayBuffer();
});
// Why a model couldn't load, for the host's log and the person choosing it; never a stack or a local path.
const reason = error => String(error?.message ?? error).replace(/[\u0000-\u001f]/g, " ").slice(0, 300);
const mouth = new Set(["aa", "ih", "ou", "ee", "oh"]);
const blink = new Set(["blink", "blinkLeft", "blinkRight"]);
window.chrome.webview.addEventListener("message", async ({ data: message }) => {
  if (message.kind === "look") {
    // Fire-and-forget cursor follow from the host window; never replies and never fails the renderer.
    try { if (active && !failed) adapter?.setLook?.(message.data.x, message.data.y); } catch { }
    return;
  }
  if (message.kind === "view") {
    // Fire-and-forget camera zoom/pan from the host window; never replies and never fails the renderer.
    try { view = message.data; if (active && !failed) adapter?.setView?.(view.zoom, view.x, view.y); } catch { }
    return;
  }
  try {
    if (failed) throw new Error("Renderer is terminally failed; inspect again.");
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
        })), model: adapter.modelSummary });
      } else if (renderer === "Vrm") {
        adapter = new VrmAvatarAdapter(canvas);
        const capabilities = await adapter.load(await resource(data.modelFile));
        adapter.startIdle?.();
        post({ modelId: data.resourceRevision.toLowerCase(), parameters: capabilities.expressions.filter(p => p.usable &&
          !p.name.startsWith("look")).map(p => ({ id: p.name, minimum: 0, maximum: 1, neutral: 0,
          aspects: [mouth.has(p.name) ? "Mouth" : "Expression"] })) });
      } else throw new Error("Unsupported renderer.");
      try { adapter.setView(view.zoom, view.x, view.y); } catch { }
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
    else if (message.kind === "mouth") {
      const level = Number(data.level);
      if (!Number.isFinite(level) || level < 0 || level > 1) throw new Error("Invalid mouth level.");
      adapter.setLipSync(level);
      post({});
    } else if (message.kind === "motion") {
      post({ started: renderer === "Live2D" ? adapter.playMotion(String(data.group)) : false });
    }
    else throw new Error("Unsupported command.");
  } catch (error) {
    active = false; failed = true;
    try { adapter?.dispose(); }
    finally { post(message.kind === "load" ? { error: "avatar.model_rejected", detail: reason(error) } : { error: "avatar.renderer_rejected" }); }
  }
});
function draw(now) {
  if (active && !failed && adapter) {
    try {
      const ratio = Math.min(2048 / Math.max(1, canvas.clientWidth, canvas.clientHeight), window.devicePixelRatio || 1);
      const width = Math.min(2048, Math.max(1, Math.round(canvas.clientWidth * ratio)));
      const height = Math.min(2048, Math.max(1, Math.round(canvas.clientHeight * ratio)));
      if (renderer === "Vrm") adapter.resize(width, height);
      else if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
      adapter.update(Math.min(0.1, last ? (now - last) / 1000 : 0));
    } catch { failed = true; active = false; post({ error: "avatar.renderer_failed" }); }
    // Unsolicited, fire-and-forget: where the top of the head sits, so the host's zoom keeps it in view.
    try {
      const top = adapter.contentTop;
      if (!failed && Number.isFinite(top) && !(Math.abs(top - reportedTop) < 0.002)) { reportedTop = top; post({ bounds: { top } }); }
    } catch { }
  }
  last = now;
  requestAnimationFrame(draw);
}
requestAnimationFrame(draw);
post({ ready: true });
