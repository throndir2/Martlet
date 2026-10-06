import assert from "node:assert/strict";
import test from "node:test";
import { GESTURES, gestureFrame, isGesture, supportedGestures } from "../dist/index.js";

test("a model gets only the gestures whose standard parameters it has", () => {
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY"]), ["nod", "shake", "bow"]);
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY", "ParamAngleZ", "ParamBodyAngleZ", "ParamEyeLSmile",
    "ParamEyeRSmile", "ParamCheek", "ParamBrowLY", "ParamBrowRY"]), [...GESTURES]);
  assert.deepEqual(supportedGestures(["ParamEyeLSmile"]), []);
  assert.equal(isGesture("wave"), false);
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
