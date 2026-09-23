import assert from "node:assert/strict";
import test from "node:test";
import { LIMITS, LocalModelBundle, MappingPlan, inspectParameters, localPath, pngDimensions } from "../dist/index.js";
import { bundle, environment, files, mouthMapping, png } from "./fixtures.mjs";

const code = expected => error => error.code === expected;

test("strict local paths reject traversal, remote references, escapes, scripts and archives", () => {
  for (const path of ["../a.png", "/a.png", "C:\\a.png", "\\\\host\\a.png", "https://x/a.png",
    "//x/a.png", "a/../b.png", "./a.png", "%2e%2e/a.png", "a.png?x", "a.png#x", "a\\b.png", "a//b.png"]) {
    assert.throws(() => localPath(path), code("UNSAFE_PATH"), path);
  }
  for (const path of ["plugin.js", "avatar.zip"]) {
    const input = files();
    input.set(path, new Uint8Array(1));
    assert.throws(() => new LocalModelBundle(input, "avatar.model3.json"), code("UNSUPPORTED_ASSET"));
  }
});

test("model inspection is detached from caller buffers and reports authored groups", () => {
  const input = files();
  const inspected = new LocalModelBundle(input, "avatar.model3.json");
  input.get("avatar.moc3")[0] = 0;
  assert.equal(inspected.read("avatar.moc3")[0], 77);
  const copy = inspected.read("avatar.moc3");
  copy[0] = 0;
  assert.equal(inspected.read("avatar.moc3")[0], 77);
  assert.deepEqual(inspected.description.groups.lipSync, ["CustomMouth", "AbsentMouth"]);
  assert.ok(Object.isFrozen(inspected.description.groups.lipSync));
});

test("all optional asset references are local, present and inactive", () => {
  for (const value of ["https://example.com/physics.json", "../physics.json"]) {
    const input = files({ FileReferences: { Moc: "avatar.moc3", Textures: ["texture.png"], Physics: value } });
    assert.throws(() => new LocalModelBundle(input, "avatar.model3.json"), code("UNSAFE_PATH"));
  }
  const input = files({
    FileReferences: {
      Moc: "avatar.moc3", Textures: ["texture.png"], Physics: "physics.json",
      Motions: { Idle: [{ File: "idle.motion3.json", Sound: "voice.wav" }] },
    },
  });
  input.set("physics.json", new TextEncoder().encode("123"));
  input.set("idle.motion3.json", new TextEncoder().encode("123"));
  assert.throws(() => new LocalModelBundle(input, "avatar.model3.json"), code("MISSING_ASSET"));
  input.set("voice.wav", new Uint8Array(0));
  const inspected = new LocalModelBundle(input, "avatar.model3.json");
  assert.deepEqual(inspected.description.diagnostics.map(d => d.code), ["INACTIVE_METADATA", "INACTIVE_MOTIONS"]);
});

test("strict metadata rejects unknown/plugin fields and malformed groups", () => {
  for (const metadata of [{ Plugin: "code.js" }, { Version: 2 }, { Groups: "bad" },
    { Groups: [{ Target: "Parameter", Name: "LipSync", Ids: ["x", "x"] }] }]) {
    assert.throws(() => new LocalModelBundle(files(metadata), "avatar.model3.json"));
  }
  const input = files();
  input.set("avatar.model3.json", new Uint8Array([255]));
  assert.throws(() => new LocalModelBundle(input, "avatar.model3.json"), code("INVALID_MODEL_JSON"));
});

test("encoded bytes, JSON, file count and decoded texture budgets are enforced", () => {
  assert.throws(() => pngDimensions(png(4097, 1)), code("RESOURCE_LIMIT"));
  assert.throws(() => pngDimensions(png(0, 1)), code("RESOURCE_LIMIT"));
  assert.throws(() => pngDimensions(new Uint8Array(33)), code("INVALID_TEXTURE"));
  const oversized = files();
  oversized.set("extra.json", new Uint8Array(LIMITS.jsonBytes + 1));
  assert.throws(() => new LocalModelBundle(oversized, "avatar.model3.json"), code("RESOURCE_LIMIT"));
  oversized.delete("extra.json");
  oversized.set("extra.moc3", new Uint8Array(LIMITS.fileBytes + 1));
  assert.throws(() => new LocalModelBundle(oversized, "avatar.model3.json"), code("RESOURCE_LIMIT"));
  const many = files();
  for (let i = 0; i < 128; i++) many.set(`${i}.png`, png());
  assert.throws(() => new LocalModelBundle(many, "avatar.model3.json"), code("RESOURCE_LIMIT"));
  const total = files();
  for (let i = 0; i < 4; i++) total.set(`${i}.moc3`, new Uint8Array(LIMITS.fileBytes));
  assert.throws(() => new LocalModelBundle(total, "avatar.model3.json"), code("RESOURCE_LIMIT"));
  const textureBudget = files({ FileReferences: { Moc: "avatar.moc3", Textures: ["texture.png", "2.png", "3.png"] } });
  for (const name of ["texture.png", "2.png", "3.png"]) textureBudget.set(name, png(4096, 4096));
  assert.throws(() => new LocalModelBundle(textureBudget, "avatar.model3.json"), code("RESOURCE_LIMIT"));
});

test("mapping inspects real reported custom bounds and never invents a conventional mouth", () => {
  const env = environment();
  const description = bundle().description;
  const parameters = inspectParameters(env.model, description);
  const plan = new MappingPlan(parameters, description, [
    mouthMapping,
    { ...mouthMapping, parameterId: "ParamMouthOpenY" },
    { channel: "semantics.blink_left", parameterId: "CustomEye", aspect: "expression",
      outputMinimum: 0.9, outputMaximum: 0.2 },
  ]);
  assert.equal(plan.capabilities.parameters[0].minimum, -2);
  assert.equal(plan.capabilities.mappings.length, 2);
  assert.deepEqual(plan.resolve({ "semantics.mouth_open": 0.5, "semantics.blink_left": 1 }).writes,
    [{ index: 0, value: 1 }, { index: 1, value: 0.2 }]);
  assert.ok(plan.capabilities.diagnostics.some(d => d.code === "ABSENT_GROUP_PARAMETER"));
  assert.ok(plan.capabilities.diagnostics.some(d => d.code === "ABSENT_MAPPING_PARAMETER"));
  assert.deepEqual(plan.capabilities.unmappedParameters, ["GazeHorizontal"]);
  assert.equal(plan.resolve({}).writes[0].value, -1);
});

test("mapping rejects duplicate writers, nonfinite frames and guessed 0..1 output ranges", () => {
  const env = environment();
  const description = bundle().description;
  const parameters = inspectParameters(env.model, description);
  assert.throws(() => new MappingPlan(parameters, description, [mouthMapping, mouthMapping]), code("DUPLICATE_WRITER"));
  assert.throws(() => new MappingPlan(parameters, description, [{ ...mouthMapping, outputMaximum: 5 }]),
    code("INVALID_MAPPING_RANGE"));
  assert.throws(() => new MappingPlan(parameters, description, [
    { ...mouthMapping, parameterId: "CustomEye", outputMinimum: 0, outputMaximum: 1 },
  ]), code("INVALID_MAPPING_RANGE"));
  const plan = new MappingPlan(parameters, description, [mouthMapping]);
  for (const value of [NaN, Infinity, -0.01, 1.01, "0.5"]) {
    assert.throws(() => plan.resolve({ "semantics.mouth_open": value }), code("INVALID_CHANNEL_VALUE"));
  }
  assert.deepEqual(plan.resolve({ "blendshapes.jawOpen": 1 }).unmappedChannels, ["blendshapes.jawOpen"]);
});

test("Core metadata count and invalid bounds fail closed", () => {
  const env = environment();
  env.model.getParameterCount = () => LIMITS.parameters + 1;
  assert.throws(() => inspectParameters(env.model, bundle().description), code("RESOURCE_LIMIT"));
  env.model.getParameterCount = () => 1;
  env.model.getParameterDefaultValue = () => 100;
  assert.throws(() => inspectParameters(env.model, bundle().description), code("INVALID_MODEL_PARAMETERS"));
});
