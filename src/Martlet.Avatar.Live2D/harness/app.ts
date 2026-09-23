import { LIMITS, Live2DAdapter, LocalModelBundle, requireCondition, type ChannelMapping } from "../lib/index.js";
import { sdk } from "./sdk-local.js";

const canvas = document.querySelector("canvas")!;
const output = document.querySelector("pre")!;
const picker = document.querySelector<HTMLInputElement>("#files")!;
const mappings = document.querySelector<HTMLTextAreaElement>("#mappings")!;
const channels = document.querySelector<HTMLTextAreaElement>("#channels")!;
let adapter: Live2DAdapter | undefined;
let sequence = 0;
let identity = {
  sessionId: crypto.randomUUID(), turnId: crypto.randomUUID(), requestId: crypto.randomUUID(),
  sourceId: "local-preview", epoch: 0,
};
const show = (value: unknown) => { output.textContent = JSON.stringify(value, null, 2); };
const action = (id: string, run: () => void | Promise<void>) => {
  document.querySelector(id)!.addEventListener("click", () => {
    Promise.resolve().then(run).catch(error => {
      show({ code: error.code ?? "HARNESS_ERROR", message: String(error) });
    });
  });
};

action("#load", async () => {
  const selected = [...(picker.files ?? [])];
  requireCondition(selected.length > 0 && selected.length <= LIMITS.files, "RESOURCE_LIMIT", "Choose at most 128 local files.");
  requireCondition(selected.reduce((sum, file) => sum + file.size, 0) <= LIMITS.totalBytes &&
    selected.every(file => file.size <= LIMITS.fileBytes), "RESOURCE_LIMIT", "Selected files exceed the byte budget.");
  const input = new Map<string, Uint8Array>();
  for (const file of selected) {
    const relative = file.webkitRelativePath || file.name;
    const name = file.webkitRelativePath ? relative.slice(relative.indexOf("/") + 1) : relative;
    requireCondition(!input.has(name), "DUPLICATE_ASSET", `Duplicate file ${name}.`);
    input.set(name, new Uint8Array(await file.arrayBuffer()));
  }
  const models = [...input.keys()].filter(name => name.endsWith(".model3.json"));
  requireCondition(models.length === 1, "MODEL_SELECTION", "Select a folder containing exactly one .model3.json.");
  const bundle = new LocalModelBundle(input, models[0]!);
  adapter?.dispose();
  adapter = new Live2DAdapter(canvas, { sdk, onDiagnostic: show });
  sequence = 0;
  identity = { ...identity, turnId: crypto.randomUUID(), requestId: crypto.randomUUID(), epoch: identity.epoch + 1 };
  show(await adapter.load(bundle));
});

action("#configure", () => {
  requireCondition(adapter, "MODEL_NOT_LOADED", "Load a model first.");
  requireCondition(mappings.value.length <= LIMITS.jsonBytes, "RESOURCE_LIMIT", "Profile is too large.");
  const parsed: unknown = JSON.parse(mappings.value);
  requireCondition(Array.isArray(parsed), "INVALID_MAPPING", "Profile must be a mapping array.");
  // The production MappingPlan validates all fields and ranges, including input from this local editor.
  show(adapter.configure(parsed as ChannelMapping[]));
  adapter.resetEpoch(identity);
});

action("#apply", () => {
  requireCondition(adapter, "MODEL_NOT_LOADED", "Load and configure a model first.");
  requireCondition(channels.value.length <= LIMITS.jsonBytes, "RESOURCE_LIMIT", "Channel input is too large.");
  const parsed: unknown = JSON.parse(channels.value);
  requireCondition(parsed !== null && typeof parsed === "object" && !Array.isArray(parsed),
    "INVALID_CHANNELS", "Channels must be an object.");
  const result = adapter.applyFrame({
    identity, sequence: sequence++, configurationId: adapter.configurationId,
    channels: parsed as Record<string, number>,
  });
  show(result);
  adapter.update(0);
  adapter.startClock();
});

action("#stop", () => adapter?.stop());
action("#dispose", () => { adapter?.dispose(); adapter = undefined; show({ state: "disposed" }); });
window.addEventListener("pagehide", () => adapter?.dispose());
show({ state: "idle", message: "No model, audio, capture, analyzer, or network activity is started automatically." });
