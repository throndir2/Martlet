import { VrmAvatarAdapter, LIMITS, mouthPresets, blinkPresets, requireValid, type PlaybackIdentity } from "../src/index.js";
import { fixture } from "../test/fixture.js";

function element<T extends HTMLElement>(id: string, type: { new (...args: never[]): T }): T {
  const result = document.getElementById(id);
  requireValid(result instanceof type, `Missing harness element ${id}.`);
  return result;
}
const canvas = element("avatar", HTMLCanvasElement);
const file = element("file", HTMLInputElement);
const synthetic = element("synthetic", HTMLButtonElement);
const controls = element("controls", HTMLFieldSetElement);
const expression = element("expression", HTMLSelectElement);
const weight = element("weight", HTMLInputElement);
const gaze = element("gaze", HTMLInputElement);
const spring = element("spring", HTMLInputElement);
const dispose = element("dispose", HTMLButtonElement);
const status = element("status", HTMLParagraphElement);
const capabilities = element("capabilities", HTMLPreElement);
let avatar: VrmAvatarAdapter | undefined;
let raf = 0;
let sequence = 0;
let previous = 0;
let identity: PlaybackIdentity;

function report(error: unknown): void {
  status.textContent = error instanceof Error ? error.message : String(error);
  console.error(error);
}
function tick(now: number): void {
  if (!avatar?.isLoaded) return;
  try {
    avatar.update(previous ? Math.min((now - previous) / 1000, 0.1) : 0);
    previous = now;
    raf = requestAnimationFrame(tick);
  } catch (error) { report(error); }
}
function configure(): void {
  requireValid(avatar, "No renderer.");
  const name = expression.value;
  const aspect = mouthPresets.some(p => p === name) ? "mouth" : blinkPresets.some(p => p === name) ? "blink" : "expression";
  avatar.configure({
    faceSource: "manual-preview", faceMode: aspect === "mouth" ? "reduced-vowel-jaw-only" : "authored-explicit",
    mappings: name ? [{ channel: "manual", expression: name, aspect, minimum: 0, maximum: 1 }] : [],
    gaze: gaze.checked, head: false, secondaryMotion: spring.checked,
  });
  identity = { sessionId: crypto.randomUUID(), turnId: crypto.randomUUID(), requestId: crypto.randomUUID(),
    sourceId: "manual-preview", epoch: 0, sampleRate: 24000 };
  sequence = 0;
  avatar.reset(identity);
  if (gaze.checked) avatar.setPose({ gaze: [1, 1.5, 2] });
  weight.value = "0";
  status.textContent = "Manual preview only; no Audio2Face or playback clock is active.";
}
async function load(bytes: ArrayBuffer): Promise<void> {
  file.disabled = true; synthetic.disabled = true; controls.disabled = true;
  try {
    avatar ??= new VrmAvatarAdapter(canvas);
    const result = await avatar.load(bytes);
    avatar.resize(640, 640);
    expression.replaceChildren(new Option("Omit face control", ""));
    for (const entry of result.expressions.filter(e => e.usable && !["lookUp", "lookDown", "lookLeft", "lookRight"].includes(e.name)))
      expression.add(new Option(entry.name, entry.name));
    gaze.checked = false; spring.checked = false;
    gaze.disabled = result.gaze === "absent"; spring.disabled = !result.secondaryMotion;
    capabilities.textContent = JSON.stringify(result, null, 2);
    configure();
    dispose.disabled = false;
    cancelAnimationFrame(raf); previous = 0; raf = requestAnimationFrame(tick);
  } finally {
    file.disabled = false; synthetic.disabled = false; controls.disabled = !avatar?.isLoaded;
  }
}
file.addEventListener("change", () => {
  const selected = file.files?.[0];
  if (!selected) return;
  if (!/\.(vrm|glb)$/i.test(selected.name) || selected.size > LIMITS.fileBytes) {
    report(new Error("Select a local .vrm/.glb no larger than 32 MiB.")); return;
  }
  void selected.arrayBuffer().then(load).catch(report);
});
synthetic.addEventListener("click", () => { void load(fixture()).catch(report); });
for (const control of [expression, gaze, spring]) control.addEventListener("change", () => {
  try { configure(); } catch (error) { report(error); }
});
weight.addEventListener("input", () => {
  try {
    requireValid(avatar, "No renderer.");
    avatar.applyFrame({ identity, sequence: sequence++, sampleOffset: 0,
      coefficients: expression.value ? { manual: Number(weight.value) } : {} }, 0);
  } catch (error) { report(error); }
});
element("stop", HTMLButtonElement).addEventListener("click", () => {
  avatar?.stop(); weight.value = "0";
  status.textContent = "Stopped. Change an explicit control selection to start a new manual preview.";
});
dispose.addEventListener("click", () => {
  cancelAnimationFrame(raf); avatar?.dispose();
  controls.disabled = true; dispose.disabled = true; file.disabled = true; synthetic.disabled = true;
  status.textContent = "Disposed. Reload this page to create a fresh renderer.";
});
window.addEventListener("pagehide", () => { cancelAnimationFrame(raf); avatar?.dispose(); });
