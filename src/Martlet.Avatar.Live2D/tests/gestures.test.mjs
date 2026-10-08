import assert from "node:assert/strict";
import test from "node:test";
import { BLUSH_LEVELS, GESTURES, GesturePlayer, HOLDABLE_GESTURES, HOLD_PARTS, drowse, flinchJolt, gestureFrame, isBlush, isGesture,
  isHoldable, sharePart, supportedGestures } from "../dist/index.js";

test("a model gets only the gestures whose standard parameters it has", () => {
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY"]), ["nod", "shake", "bow", "blush", "laugh", "chuckle", "sigh", "gasp",
    "cough", "clear_throat", "sniff", "shush", "inhale", "exhale", "mumble", "sneeze", "fear", "crying",
    "shy", "giggle", "flinch", "look_away", "think", "blush_deep", "blush_fierce"]);
  assert.deepEqual(supportedGestures(["ParamAngleX", "ParamAngleY", "ParamAngleZ", "ParamBodyAngleZ", "ParamEyeLSmile",
    "ParamEyeRSmile", "ParamCheek", "ParamBrowLY", "ParamBrowRY", "ParamEyeLOpen", "ParamEyeROpen", "ParamMouthForm",
    "ParamEyeBallX", "ParamEyeBallY", "ParamMouthOpenY"]), [...GESTURES]);
  assert.deepEqual(supportedGestures(["ParamEyeLSmile"]), [...BLUSH_LEVELS], "every model blushes at every level (drawn when it has no ParamCheek)");
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
  assert.deepEqual(supportedGestures(["ParamEyeLOpen"]), ["blush", "wink", "blush_deep", "blush_fierce"]);
  assert.deepEqual(supportedGestures(["ParamEyeLOpen", "ParamEyeROpen", "ParamMouthForm"]), ["blush", "wink", "pout", "drowsy",
    "blush_deep", "blush_fierce"]);
  assert.deepEqual(supportedGestures(["ParamEyeBallX", "ParamEyeBallY"]), ["blush", "eye_roll", "eyes_up", "blush_deep", "blush_fierce"]);
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
  assert.deepEqual([...HOLDABLE_GESTURES], ["pout", "shy", "look_away", "drowsy", "blush", "eyes_up", "mouth_open", "blush_deep",
    "blush_fierce"]);
  assert.ok(isHoldable("shy") && !isHoldable("wink"));
  const player = new GesturePlayer();
  assert.equal(player.advance(0.1), undefined);
  player.play("shy", true);
  assert.deepEqual(player.state, { held: ["shy"] });
  let frame;
  for (let i = 0; i < 300; i++) frame = player.advance(0.05);
  assert.ok(frame.look.y < -0.2 && frame.parameters.ParamEyeLSmile > 0.49, "still shy after 15 seconds");
  const a = player.advance(0.05).look.x;
  for (let i = 0; i < 40; i++) player.advance(0.05);
  assert.notEqual(player.advance(0.05).look.x, a, "a held pose stays alive");
  player.play("nod");
  assert.deepEqual(player.state, { playing: "nod", held: ["shy"] });
  for (let i = 0; i < 6; i++) frame = player.advance(0.05);
  assert.ok(frame.parameters.ParamEyeLSmile < 0.45, "the held pose eases back under the nod");
  for (let i = 0; i < 30; i++) frame = player.advance(0.05);
  assert.deepEqual(player.state, { held: ["shy"] });
  assert.ok(frame.parameters.ParamEyeLSmile > 0.49, "and resumes after it");
  player.play("wink", true);
  assert.deepEqual(player.state, { playing: "wink", held: ["shy"] }, "a gesture that can't be held plays once");
  player.play("drowsy", true);
  assert.deepEqual(player.state.held, ["drowsy"], "drowsy moves the eyes and head too, so shy lets go");
  for (let i = 0; i < 40; i++) frame = player.advance(0.05);
  assert.ok(!("ParamEyeLSmile" in frame.parameters) && frame.parameters.ParamEyeLOpen < -0.4, "crossfaded to drowsy");
  player.end("drowsy");
  assert.deepEqual(player.state, { held: [] });
  frame = player.advance(0.05);
  assert.ok(frame.parameters.ParamEyeLOpen < -0.3, "eases out rather than snapping back");
  for (let i = 0; i < 20; i++) frame = player.advance(0.05);
  assert.equal(frame, undefined);
  player.play("pout");
  assert.deepEqual(player.state, { playing: "pout", held: [] }, "without hold it plays once");
  for (let i = 0; i < 60; i++) frame = player.advance(0.05);
  assert.equal(frame, undefined);
});

test("every blush level moves the model's own blush fully, once or held, is the cheeks and replaces another without a dip", () => {
  assert.deepEqual([...BLUSH_LEVELS], ["blush", "blush_deep", "blush_fierce"]);
  assert.ok(BLUSH_LEVELS.every(isBlush) && !isBlush("shy"));
  assert.deepEqual(GESTURES.slice(-2), ["blush_deep", "blush_fierce"], "after the gestures before them");
  for (const level of BLUSH_LEVELS) {
    assert.ok(isHoldable(level), level);
    assert.deepEqual(HOLD_PARTS[level], ["cheeks"], level);
    assert.equal(gestureFrame(level, 2).parameters.ParamCheek, 1, level);
    assert.equal(gestureFrame(level, 4), undefined, `${level} plays 4 seconds`);
  }
  const player = new GesturePlayer();
  player.play("eyes_up", true);
  player.play("blush_deep", true);
  let frame;
  for (let i = 0; i < 40; i++) frame = player.advance(0.05);
  assert.deepEqual([player.state.held, frame.parameters.ParamCheek], [["eyes_up", "blush_deep"], 1], "a blush level layers with the eyes");
  // A new level lets the one before go (both are the cheeks), and the cheeks stay fully flushed through the crossfade.
  player.play("blush_fierce", true);
  assert.deepEqual(player.state, { held: ["eyes_up", "blush_fierce"] });
  for (let i = 0; i < 20; i++) {
    frame = player.advance(0.05);
    assert.ok(frame.parameters.ParamCheek > 0.99, String(frame.parameters.ParamCheek));
  }
});

test("held gestures that move different parts layer; one that shares a part lets the other go", () => {
  assert.deepEqual(HOLD_PARTS.eyes_up, ["eyes"]);
  assert.deepEqual(HOLD_PARTS.mouth_open, ["mouth"]);
  assert.deepEqual(HOLD_PARTS.blush, ["cheeks"]);
  for (const name of HOLDABLE_GESTURES) assert.ok(HOLD_PARTS[name].length > 0, name);
  assert.ok(!sharePart("eyes_up", "mouth_open") && !sharePart("eyes_up", "blush") && !sharePart("pout", "blush"));
  assert.ok(sharePart("eyes_up", "shy") && sharePart("mouth_open", "pout") && sharePart("look_away", "drowsy"));
  const player = new GesturePlayer();
  for (const name of ["eyes_up", "mouth_open", "blush"]) player.play(name, true);
  assert.deepEqual(player.state, { held: ["eyes_up", "mouth_open", "blush"] }, "the eyes, the mouth and the cheeks stay on together");
  let frame;
  for (let i = 0; i < 40; i++) frame = player.advance(0.05);
  assert.ok(frame.parameters.ParamEyeBallY > 0.75 && frame.parameters.ParamMouthOpenY > 0.5 && frame.parameters.ParamCheek > 0.99,
    "all three show at full strength");
  assert.deepEqual(frame.look, { x: 0, y: 0 }, "the head doesn't move");
  player.play("pout", true);
  assert.deepEqual(player.state, { held: ["eyes_up", "blush", "pout"] }, "pout moves the mouth too, so mouth_open lets go");
  for (let i = 0; i < 40; i++) frame = player.advance(0.05);
  assert.ok(!frame.parameters.ParamMouthOpenY && frame.parameters.ParamMouthForm < -0.99 && frame.parameters.ParamEyeBallY > 0.75);
  player.end("pout");
  assert.deepEqual(player.state, { held: ["eyes_up", "blush"] }, "ending one leaves the others on");
  player.play("look_away", true);
  assert.deepEqual(player.state, { held: ["blush", "look_away"] }, "look_away turns the eyes, so eyes_up lets go");
  player.play("look_away", true);
  assert.deepEqual(player.state, { held: ["blush", "look_away"] }, "holding it again changes nothing");
  player.play("blush");
  assert.deepEqual(player.state, { playing: "blush", held: ["blush", "look_away"] }, "played once while held, it stays held");
});

test("raised eyes stay up whatever the look, and the look comes back when they are let go", () => {
  const player = new GesturePlayer();
  player.play("eyes_up", true);
  let frame;
  // The look adds itself to the eyeballs (ParamEyeBallX += look.x, ParamEyeBallY += look.y); held eyes take it out again.
  for (const look of [{ x: 0.6, y: -0.8 }, { x: -0.5, y: 0.9 }, { x: 0, y: 0 }]) {
    for (let i = 0; i < 40; i++) frame = player.advance(0.05, look);
    assert.ok(look.y + frame.parameters.ParamEyeBallY > 0.75, `the eyes stay up: ${JSON.stringify(look)}`);
    assert.ok(Math.abs(look.x + frame.parameters.ParamEyeBallX) < 0.16, `and don't follow the look sideways: ${JSON.stringify(look)}`);
    assert.equal(frame.eyes, 1);
    assert.deepEqual(frame.look, { x: 0, y: 0 }, "the head still follows the look");
  }
  player.end("eyes_up");
  for (let i = 0; i < 20; i++) frame = player.advance(0.05, { x: 0.6, y: -0.8 });
  assert.equal(frame, undefined, "let go, the eyes follow the look again");
  assert.ok(gestureFrame("eyes_up", 1.5).parameters.ParamEyeBallY > 0.75 && gestureFrame("eyes_up", 2.99).parameters.ParamEyeBallY < 0.05,
    "without hold the eyes go up for a few seconds");
});

test("a held open mouth stays open and breathes; the adapter gives the mouth to the voice while it speaks", () => {
  const player = new GesturePlayer();
  player.play("mouth_open", true);
  let frame, least = 1, most = 0;
  for (let i = 0; i < 40; i++) frame = player.advance(0.05);
  for (let i = 0; i < 60; i++) {
    frame = player.advance(0.05);
    least = Math.min(least, frame.parameters.ParamMouthOpenY);
    most = Math.max(most, frame.parameters.ParamMouthOpenY);
  }
  assert.ok(least > 0.5 && most < 0.7 && most - least > 0.05, `open, gently alive: ${least} to ${most}`);
  assert.ok(gestureFrame("mouth_open", 1.3).parameters.ParamMouthOpenY > 0.5 && gestureFrame("mouth_open", 2.6) === undefined,
    "without hold the mouth opens for a few seconds");
});
