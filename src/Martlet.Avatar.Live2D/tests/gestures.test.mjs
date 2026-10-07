import assert from "node:assert/strict";
import test from "node:test";
import { GESTURES, gestureFrame, isGesture, supportedGestures } from "../dist/index.js";

test("a model gets only the gestures whose standard parameters it has", () => {
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY"]), ["nod", "shake", "bow", "blush", "laugh", "chuckle", "sigh", "gasp",
    "cough", "clear_throat", "sniff", "shush", "inhale", "exhale", "mumble", "sneeze", "fear", "crying"]);
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY", "ParamAngleZ", "ParamBodyAngleZ", "ParamEyeLSmile",
    "ParamEyeRSmile", "ParamCheek", "ParamBrowLY", "ParamBrowRY"]), [...GESTURES]);
  assert.deepEqual(supportedGestures(["ParamEyeLSmile"]), ["blush"], "every model blushes (drawn when it has no ParamCheek)");
  assert.equal(isGesture("wave"), false);
  assert.equal(isGesture("laugh"), true);
});

test("gestures fade in, move their parameters and end", () => {
  assert.equal(gestureFrame("blush", 0).parameters.ParamCheek, 0);
  assert.equal(gestureFrame("blush", 2).parameters.ParamCheek, 1);
  assert.ok(gestureFrame("tilt", 0.9).parameters.ParamAngleZ > 15);
  assert.ok(gestureFrame("smile", 1.5).parameters.ParamEyeLSmile > 0.99);
  assert.ok(gestureFrame("bow", 1).look.y < -0.8);
  assert.ok(Math.abs(gestureFrame("sway", 0.3).parameters.ParamBodyAngleZ) > 1);
  assert.ok(gestureFrame("nod", 0.275).look.y < -0.3);
  for (const name of GESTURES) assert.equal(gestureFrame(name, 10), undefined, name);
});

test("voice emotes move the head and face and settle back", () => {
  const laugh = gestureFrame("laugh", 1);
  assert.ok(laugh.parameters.ParamEyeLSmile > 0.99 && laugh.parameters.ParamMouthForm > 0.99);
  assert.ok(gestureFrame("gasp", 0.5).look.y > 0.3 && gestureFrame("gasp", 0.5).parameters.ParamBrowLY > 0.7);
  assert.ok(gestureFrame("sigh", 1.2).look.y < -0.4 && gestureFrame("sigh", 1.2).parameters.ParamEyeLOpen < -0.3);
  assert.ok(gestureFrame("cough", 0.1).look.y < -0.4);
  assert.ok(gestureFrame("sneeze", 0.6).look.y > 0.3 && gestureFrame("sneeze", 0.85).look.y < -0.7);
  assert.ok(gestureFrame("angry", 1.2).parameters.ParamBrowLY < -0.7);
  assert.ok(gestureFrame("crying", 1.5).look.y < -0.4 && gestureFrame("crying", 1.5).parameters.ParamEyeLOpen < -0.6);
  assert.ok(Math.abs(gestureFrame("hum", 0.65 + 0.325).parameters.ParamAngleZ) > 6);
  assert.ok(gestureFrame("dramatic", 1.2).parameters.ParamAngleZ < -14);
  for (const name of GESTURES) {
    const start = gestureFrame(name, 0);
    assert.ok(Math.abs(start.look.x) < 1e-9 && Math.abs(start.look.y) < 1e-9, name);
    for (const value of Object.values(start.parameters)) assert.ok(Math.abs(value) < 1e-9, name);
  }
});
