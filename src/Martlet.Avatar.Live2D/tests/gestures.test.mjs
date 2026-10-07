import assert from "node:assert/strict";
import test from "node:test";
import { GESTURES, GesturePlayer, HOLDABLE_GESTURES, drowse, flinchJolt, gestureFrame, isGesture, isHoldable,
  supportedGestures } from "../dist/index.js";

test("a model gets only the gestures whose standard parameters it has", () => {
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY"]), ["nod", "shake", "bow", "laugh", "chuckle", "sigh", "gasp",
    "cough", "clear_throat", "sniff", "shush", "inhale", "exhale", "mumble", "sneeze", "fear", "crying",
    "shy", "giggle", "flinch", "look_away", "think"]);
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY", "ParamAngleZ", "ParamBodyAngleZ", "ParamEyeLSmile",
    "ParamEyeRSmile", "ParamCheek", "ParamBrowLY", "ParamBrowRY", "ParamEyeLOpen", "ParamEyeROpen", "ParamMouthForm",
    "ParamEyeBallX", "ParamEyeBallY"]), [...GESTURES]);
  assert.deepEqual(supportedGestures(["ParamEyeLSmile"]), []);
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

test("touch and mood gestures need their standard parameters and move the face naturally", () => {
  assert.deepEqual(supportedGestures(["ParamEyeLOpen"]), ["wink"]);
  assert.deepEqual(supportedGestures(["ParamEyeLOpen", "ParamEyeROpen", "ParamMouthForm"]), ["wink", "pout", "drowsy"]);
  assert.deepEqual(supportedGestures(["ParamEyeBallX", "ParamEyeBallY"]), ["eye_roll"]);
  const wink = gestureFrame("wink", 0.5);
  assert.ok(wink.parameters.ParamEyeLOpen < -0.99 && !("ParamEyeROpen" in wink.parameters), "only the left eye closes");
  assert.ok(gestureFrame("pout", 1.3).parameters.ParamMouthForm < -0.99 && gestureFrame("pout", 1.3).parameters.ParamCheekPuff > 0.7);
  const shy = gestureFrame("shy", 1);
  assert.ok(shy.look.y < -0.3 && shy.look.x < -0.2 && shy.parameters.ParamEyeLSmile > 0.4, "looks down and away, smiling");
  assert.ok(Math.abs(gestureFrame("giggle", 0.45).look.y - gestureFrame("giggle", 0.54).look.y) > 0.1, "giggles bounce");
  assert.ok(gestureFrame("flinch", 0.08).look.y > 0.29 && gestureFrame("flinch", 0.08).parameters.ParamBodyAngleY > 4.9, "jerks back");
  assert.ok(gestureFrame("flinch", 1.2).look.y < 0.05, "then settles");
  assert.ok(flinchJolt(0) === 0 && flinchJolt(0.08) === 1 && flinchJolt(0.5) < 0.35);
  const lean = gestureFrame("lean_in", 1.4);
  assert.ok(lean.parameters.ParamAngleZ > 9 && lean.parameters.ParamBodyAngleY < -3.9 && lean.parameters.ParamEyeLOpen < -0.5);
  assert.ok(gestureFrame("look_away", 1.2).look.x > 0.6);
  const think = gestureFrame("think", 1.4);
  assert.ok(think.look.y > 0.39 && think.look.x < -0.25);
  const roll = [0.3, 0.75, 1.3].map(t => gestureFrame("eye_roll", t).parameters);
  assert.ok(roll[0].ParamEyeBallX < -0.5 && roll[1].ParamEyeBallY > 0.8 && roll[2].ParamEyeBallX > 0.5, "the eyes roll over the top");
  assert.ok(drowse(0) === 0 && drowse(4.8) > 0.99 && drowse(5.4) < 0.01, "drowsy nods off slowly and catches itself");
  assert.ok(gestureFrame("drowsy", 3).parameters.ParamEyeLOpen < -0.6);
});

test("holdable gestures stay until ended and gestures played meanwhile play on top", () => {
  assert.deepEqual([...HOLDABLE_GESTURES], ["pout", "shy", "look_away", "drowsy"]);
  assert.ok(isHoldable("shy") && !isHoldable("wink"));
  const player = new GesturePlayer();
  assert.equal(player.advance(0.1), undefined);
  player.play("shy", true);
  assert.deepEqual(player.state, { held: "shy" });
  let frame;
  for (let i = 0; i < 300; i++) frame = player.advance(0.05);
  assert.ok(frame.look.y < -0.2 && frame.parameters.ParamEyeLSmile > 0.49, "still shy after 15 seconds");
  const a = player.advance(0.05).look.x;
  for (let i = 0; i < 40; i++) player.advance(0.05);
  assert.notEqual(player.advance(0.05).look.x, a, "a held pose stays alive");
  player.play("nod");
  assert.deepEqual(player.state, { playing: "nod", held: "shy" });
  for (let i = 0; i < 6; i++) frame = player.advance(0.05);
  assert.ok(frame.parameters.ParamEyeLSmile < 0.45, "the held pose eases back under the nod");
  for (let i = 0; i < 30; i++) frame = player.advance(0.05);
  assert.deepEqual(player.state, { held: "shy" });
  assert.ok(frame.parameters.ParamEyeLSmile > 0.49, "and resumes after it");
  player.play("wink", true);
  assert.deepEqual(player.state, { playing: "wink", held: "shy" }, "a gesture that can't be held plays once");
  player.play("drowsy", true);
  assert.deepEqual(player.state.held, "drowsy", "a new held gesture replaces the last");
  for (let i = 0; i < 40; i++) frame = player.advance(0.05);
  assert.ok(!("ParamEyeLSmile" in frame.parameters) && frame.parameters.ParamEyeLOpen < -0.4, "crossfaded to drowsy");
  player.end("drowsy");
  assert.deepEqual(player.state, {});
  frame = player.advance(0.05);
  assert.ok(frame.parameters.ParamEyeLOpen < -0.3, "eases out rather than snapping back");
  for (let i = 0; i < 20; i++) frame = player.advance(0.05);
  assert.equal(frame, undefined);
  player.play("pout");
  assert.deepEqual(player.state, { playing: "pout" }, "without hold it plays once");
  for (let i = 0; i < 60; i++) frame = player.advance(0.05);
  assert.equal(frame, undefined);
});
