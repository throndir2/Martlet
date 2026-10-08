import assert from "node:assert/strict";
import test from "node:test";
import { findSwingingChains, LocalModelBundle } from "../dist/index.js";
import { files } from "./fixtures.mjs";

// A model stand-in whose vertices follow its parameters: a tail of three drawables that bends more toward its tip (Swing and
// Curl, both driven by its physics), a body that moves all drawables (BodyX), and hair that never moves.
function model() {
  const ids = ["Swing", "Curl", "BodyX", "Feed"];
  const values = [0, 0, 0, 0];
  const rest = { Hair: [0, 1, 0.2, 1, 0.2, 1.2], Tail0: [0, 0, 0.1, 0, 0.1, -0.2], Tail1: [0, -0.2, 0.1, -0.2, 0.1, -0.4],
    Tail2: [0, -0.4, 0.1, -0.4, 0.1, -0.6], Body: [-0.5, 0.5, 0.5, 0.5, 0.5, -0.5], Face: [-0.2, 1, 0.2, 1, 0.2, 0.7],
    ArmL: [0.5, 0.5, 0.7, 0.5, 0.7, 0], ArmR: [-0.7, 0.5, -0.5, 0.5, -0.5, 0] };
  const names = Object.keys(rest);
  const reach = { Hair: 0, Tail0: 0.1, Tail1: 0.4, Tail2: 0.9, Body: 0, Face: 0, ArmL: 0, ArmR: 0 };
  let vertices = names.map(name => Float32Array.from(rest[name]));
  let updates = 0;
  return {
    values, updates: () => updates,
    getParameterCount: () => ids.length,
    getParameterId: i => ({ getString: () => ({ s: ids[i] }) }),
    getParameterMinimumValue: () => -30,
    getParameterMaximumValue: () => 30,
    getParameterDefaultValue: () => 0,
    getParameterValueByIndex: i => values[i],
    setParameterValueByIndex: (i, value) => { values[i] = value; },
    getCanvasWidth: () => 2,
    getCanvasHeight: () => 2,
    getDrawableCount: () => names.length,
    getDrawableId: d => ({ getString: () => ({ s: names[d] }) }),
    getDrawableVertices: d => vertices[d],
    getDrawableOpacity: () => 1,
    getDrawableDynamicFlagIsVisible: () => true,
    update() {
      updates++;
      const swing = (values[0] + values[1]) / 30, body = values[2] / 30;
      vertices = names.map(name => Float32Array.from(rest[name], (v, k) => v + (k % 2 === 0 ? swing * reach[name] + body * 0.3 : 0)));
    },
  };
}

const project = (x, y) => [(x + 1) / 2, (1 - y) / 2];
const settings = [
  { name: "尾巴", outputs: ["Swing"] },
  { name: "尾巴(2)", outputs: ["Curl"] },
  { name: "Body X", outputs: ["BodyX"] },
  // Feeds another setting only (its output is an input): skipped, never moved.
  { name: "OX", outputs: ["Feed"], inputs: [] },
  { name: "Feed reader", outputs: ["Missing"], inputs: ["Feed"] },
];

test("a tail its physics swings is one chain from root to tip; a body sway and a setting that only feeds others are left out", () => {
  const m = model();
  const chains = findSwingingChains(m, settings, project);
  assert.equal(chains.length, 1);
  const [tail] = chains;
  assert.equal(tail.name, "尾巴 / 尾巴(2)", "settings named alike that swing the same drawables are one chain");
  assert.deepEqual([...tail.drawables], ["Tail0", "Tail1", "Tail2"], "root (moves least) first");
  assert.ok(tail.left < 0.5 && tail.right > 0.55, `it reaches both ways: ${JSON.stringify(tail)}`);
  assert.ok(tail.top <= 0.5 && tail.bottom >= 0.8, JSON.stringify(tail));
  assert.deepEqual([...m.values], [0, 0, 0, 0], "every parameter is put back");
  assert.deepEqual([...m.getDrawableVertices(1)], [0, 0, 0.1, 0, 0.1, -0.2].map(v => Math.fround(v)), "and the pose with it");
});

test("measuring stops when it is late, and a model that can't read its parameters swings nothing", () => {
  const m = model();
  assert.deepEqual(findSwingingChains(m, settings, project, () => true), []);
  assert.deepEqual([...m.values], [0, 0, 0, 0]);
  const blind = model();
  delete blind.getParameterValueByIndex;
  assert.deepEqual(findSwingingChains(blind, settings, project), []);
  assert.deepEqual(findSwingingChains(model(), [], project), []);
});

test("the bundle reads each physics setting's name, outputs and inputs from its physics3.json, as data only", () => {
  const input = files({ FileReferences: { Moc: "avatar.moc3", Textures: ["texture.png"], Physics: "avatar.physics3.json" } });
  input.set("avatar.physics3.json", new TextEncoder().encode(JSON.stringify({
    Version: 3,
    Meta: { PhysicsDictionary: [{ Id: "PhysicsSetting1", Name: "尾巴" }, { Id: "PhysicsSetting2", Name: "\u0001Hair\u0002" }] },
    PhysicsSettings: [
      { Id: "PhysicsSetting1", Input: [{ Source: { Target: "Parameter", Id: "ParamBreath" } }],
        Output: [{ Destination: { Target: "Parameter", Id: "Swing" } }, { Destination: { Target: "Parameter", Id: "Swing" } }] },
      { Id: "PhysicsSetting2", Input: [], Output: [{ Destination: { Target: "Parameter", Id: "HairSway" } }] },
      { Id: "PhysicsSetting3", Output: [] },
    ],
  })));
  const bundle = new LocalModelBundle(input, "avatar.model3.json");
  assert.deepEqual(JSON.parse(JSON.stringify(bundle.physicsSettings())), [
    { name: "尾巴", outputs: ["Swing"], inputs: ["ParamBreath"] },
    { name: "Hair", outputs: ["HairSway"], inputs: [] },
  ]);
  const broken = files({ FileReferences: { Moc: "avatar.moc3", Textures: ["texture.png"], Physics: "avatar.physics3.json" } });
  broken.set("avatar.physics3.json", new TextEncoder().encode("{ not json"));
  assert.deepEqual(new LocalModelBundle(broken, "avatar.model3.json").physicsSettings(), []);
});
