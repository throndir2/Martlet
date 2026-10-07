// Martlet's overlay emotes: anime "manpu" symbols (a sweat drop, an anger vein, hearts, sparkles, tears, gloom lines, ?, !, Zzz,
// music notes) drawn over any Live2D or VRM character on the overlay layer (../overlay.js). Each one draws in face units: the
// face anchor's centre is the origin, its width is 100 units and its roll turns everything with the head, so a symbol keeps
// its place and size however the character is zoomed or moved. Colours are fixed (not the app theme), with dark outlines,
// so they read on any desktop.

/** Every overlay emote, in the order the C# side lists them (CharacterActionInventory.AllGestures). */
export const MANPU_NAMES = Object.freeze(["sweat", "anger", "hearts", "sparkles", "tears", "gloom", "question", "exclaim", "sleepy",
  "music"]);

const TAU = Math.PI * 2;
const clamp = (x, low = 0, high = 1) => Math.max(low, Math.min(high, x));
const smooth = x => { const c = clamp(x); return c * c * (3 - 2 * c); };

/** 0 to 1 with a springy overshoot (about 1.1 at the top), for symbols popping in. */
export function easeOutBack(x) {
  const c = clamp(x) - 1, s = 1.70158;
  return 1 + (s + 1) * c * c * c + s * c * c;
}

/** Where `point` (overlay pixels) is in the anchor's face units. */
export function localPoint(anchor, point) {
  const scale = anchor.width / 100, dx = point.x - anchor.x, dy = point.y - anchor.y;
  const cos = Math.cos(anchor.angle || 0), sin = Math.sin(anchor.angle || 0);
  return { x: (dx * cos + dy * sin) / scale, y: (-dx * sin + dy * cos) / scale };
}

/** The top of the head in face units (above the centre, so negative), from the anchor's `top` or a typical anime head. */
export function headTop(anchor) {
  return anchor.top ? clamp(localPoint(anchor, anchor.top).y, -130, -40) : -75;
}

/** The time into particle `index`'s current life when particles start `spacing` seconds apart and repeat every `period`
 *  seconds, or undefined before it first appears. */
export function particleTime(t, index, spacing, period) {
  const local = t - index * spacing;
  return local < 0 ? undefined : local % period;
}

/** Draws `draw` in face units: origin at the face centre, 100 units across the face, turned with the head. */
function inFace(ctx, anchor, draw) {
  const scale = anchor.width / 100;
  ctx.translate(anchor.x, anchor.y);
  ctx.rotate(anchor.angle || 0);
  ctx.scale(scale, scale);
  ctx.lineJoin = "round"; ctx.lineCap = "round";
  draw();
}

function piece(ctx, alpha, x, y, scale, rotation, draw) {
  if (!(alpha > 0.001) || !(scale > 0.001)) return;
  ctx.save();
  ctx.globalAlpha *= clamp(alpha);
  ctx.translate(x, y);
  if (rotation) ctx.rotate(rotation);
  ctx.scale(scale, scale);
  draw();
  ctx.restore();
}

function outlined(ctx, fill, stroke, width) {
  ctx.fillStyle = fill; ctx.fill();
  ctx.strokeStyle = stroke; ctx.lineWidth = width; ctx.stroke();
}

// A stroke drawn twice, dark and wide under the colour, so a line has an outline.
function doubleStroke(ctx, colour, outline, width, border = 1.4) {
  ctx.strokeStyle = outline; ctx.lineWidth = width + 2 * border; ctx.stroke();
  ctx.strokeStyle = colour; ctx.lineWidth = width; ctx.stroke();
}

function gradient(ctx, y0, y1, top, bottom) {
  if (typeof ctx.createLinearGradient !== "function") return bottom;
  const g = ctx.createLinearGradient(0, y0, 0, y1);
  g.addColorStop(0, top); g.addColorStop(1, bottom);
  return g;
}

function shine(ctx, x, y, rx, ry, rotation = -0.5) {
  ctx.beginPath(); ctx.ellipse(x, y, rx, ry, rotation, 0, TAU);
  ctx.fillStyle = "rgba(255,255,255,0.85)"; ctx.fill();
}

function dropPath(ctx, r) {
  ctx.beginPath();
  ctx.moveTo(0, -1.7 * r);
  ctx.bezierCurveTo(0.3 * r, -1.0 * r, r, -0.4 * r, r, 0.15 * r);
  ctx.arc(0, 0.15 * r, r, 0, Math.PI);
  ctx.bezierCurveTo(-r, -0.4 * r, -0.3 * r, -1.0 * r, 0, -1.7 * r);
  ctx.closePath();
}

function drop(ctx, r, light, dark, outline) {
  dropPath(ctx, r);
  outlined(ctx, gradient(ctx, -1.7 * r, 1.15 * r, light, dark), outline, 0.22 * r);
  shine(ctx, -0.38 * r, -0.05 * r, 0.2 * r, 0.42 * r, 0.35);
}

function heartPath(ctx, r) {
  ctx.beginPath();
  ctx.moveTo(0, r * 0.95);
  ctx.bezierCurveTo(-r * 0.25, r * 0.7, -r * 1.05, r * 0.15, -r * 1.0, -r * 0.35);
  ctx.bezierCurveTo(-r * 0.95, -r * 0.95, -r * 0.2, -r * 1.05, 0, -r * 0.45);
  ctx.bezierCurveTo(r * 0.2, -r * 1.05, r * 0.95, -r * 0.95, r * 1.0, -r * 0.35);
  ctx.bezierCurveTo(r * 1.05, r * 0.15, r * 0.25, r * 0.7, 0, r * 0.95);
  ctx.closePath();
}

function starPath(ctx, r) {
  const k = r * 0.16;
  ctx.beginPath();
  ctx.moveTo(0, -r);
  ctx.quadraticCurveTo(k, -k, r, 0);
  ctx.quadraticCurveTo(k, k, 0, r);
  ctx.quadraticCurveTo(-k, k, -r, 0);
  ctx.quadraticCurveTo(-k, -k, 0, -r);
  ctx.closePath();
}

function glyph(ctx, text, size, fill, outline) {
  ctx.font = `900 ${size}px "Segoe UI Black", "Arial Black", "Segoe UI", sans-serif`;
  ctx.textAlign = "center"; ctx.textBaseline = "middle";
  ctx.strokeStyle = outline; ctx.lineWidth = size * 0.16; ctx.strokeText(text, 0, 0);
  ctx.fillStyle = fill; ctx.fillText(text, 0, 0);
}

// A particle's fade over its life: in over `fadeIn` seconds, out over the last `fadeOut`.
const life = (time, length, fadeIn = 0.2, fadeOut = 0.5) => Math.min(smooth(time / fadeIn), smooth((length - time) / fadeOut));

function sweat(ctx, anchor, t) {
  const top = headTop(anchor);
  const slide = 7 * smooth((t - 0.2) / 1.6) + 1.2 * Math.sin(TAU * t / 1.4);
  piece(ctx, 1, 58, 0.48 * top + slide, easeOutBack(t / 0.35), 0.12, () => drop(ctx, 10, "#eefaff", "#6fc3f2", "#1f5f95"));
  piece(ctx, smooth((t - 0.35) / 0.3), 73, 0.48 * top + 22 + 0.6 * slide, easeOutBack((t - 0.35) / 0.35) * 0.45, 0.2,
    () => drop(ctx, 10, "#eefaff", "#6fc3f2", "#1f5f95"));
}

function anger(ctx, anchor, t) {
  const top = headTop(anchor);
  const throb = 1 + 0.13 * Math.max(0, Math.sin(TAU * t / 0.45)) ** 2;
  piece(ctx, 1, 36, 0.62 * top, easeOutBack(t / 0.3) * throb, 0.15, () => {
    for (let quarter = 0; quarter < 4; quarter++) {
      ctx.save();
      ctx.rotate(quarter * Math.PI / 2);
      ctx.beginPath();
      ctx.moveTo(3.2, 12.5);
      ctx.quadraticCurveTo(3.6, 3.6, 12.5, 3.2);
      doubleStroke(ctx, "#ef3340", "#5e0a12", 3.6, 1.3);
      ctx.restore();
    }
  });
}

const HEARTS = [{ x: -52, size: 10 }, { x: 54, size: 12 }, { x: -36, size: 8 }, { x: 42, size: 9 }, { x: -60, size: 7.5 }];
function hearts(ctx, anchor, t) {
  const top = headTop(anchor), spacing = 0.36, period = HEARTS.length * spacing;
  HEARTS.forEach((heart, i) => {
    const time = particleTime(t, i, spacing, period);
    if (time === undefined) return;
    const rise = 46 * smooth(time / period) + 6 * time;
    piece(ctx, life(time, period, 0.15, 0.55), heart.x + 5 * Math.sin(TAU * time / 1.1 + i), 0.35 * top - rise,
      easeOutBack(time / 0.3), 0.25 * Math.sin(TAU * time / 1.6 + i), () => {
        heartPath(ctx, heart.size);
        outlined(ctx, gradient(ctx, -heart.size, heart.size, "#ffa6c9", "#ff3f86"), "#9c1250", heart.size * 0.2);
        shine(ctx, -heart.size * 0.48, -heart.size * 0.42, heart.size * 0.2, heart.size * 0.12, -0.7);
      });
  });
}

const SPARKLES = [{ x: -64, y: -38, r: 12 }, { x: 62, y: -60, r: 15 }, { x: -46, y: -90, r: 9 }, { x: 68, y: 6, r: 10 },
  { x: -68, y: 12, r: 8 }, { x: 34, y: -98, r: 10 }];
function sparkles(ctx, anchor, t) {
  const stretch = (headTop(anchor) - 5) / -80;
  SPARKLES.forEach((spark, i) => {
    const start = i * 0.11, twinkle = 0.85 + 0.15 * Math.sin(TAU * t / 0.7 + i * 1.9), y = spark.y < 0 ? spark.y * stretch : spark.y;
    piece(ctx, smooth((t - start) / 0.15), spark.x, y, easeOutBack((t - start) / 0.3) * twinkle, 0.3 * Math.sin(TAU * t / 2.4 + i),
      () => {
        starPath(ctx, spark.r);
        outlined(ctx, "#fff8cc", "#d99a00", spark.r * 0.14);
        ctx.beginPath(); ctx.arc(0, 0, spark.r * 0.18, 0, TAU); ctx.fillStyle = "#ffffff"; ctx.fill();
      });
    piece(ctx, smooth((t - start - 0.2) / 0.2) * (0.5 + 0.5 * Math.sin(TAU * t / 0.9 + i)), spark.x + spark.r * 1.3,
      y - spark.r * 1.1, 1, 0, () => {
        ctx.beginPath(); ctx.arc(0, 0, spark.r * 0.22, 0, TAU);
        outlined(ctx, "#fff8cc", "#d99a00", spark.r * 0.08);
      });
  });
}

function tears(ctx, anchor, t) {
  if (!anchor.eyeLeft || !anchor.eyeRight) return;
  const eyes = [localPoint(anchor, anchor.eyeLeft), localPoint(anchor, anchor.eyeRight)];
  const gap = Math.max(12, Math.abs(eyes[1].x - eyes[0].x));
  eyes.forEach((eye, side) => {
    const out = eye.x < (eyes[0].x + eyes[1].x) / 2 ? -1 : 1, x = eye.x + out * 0.14 * gap, y = eye.y + 0.24 * gap;
    // The wet streak down the cheek, growing in.
    const length = 0.75 * gap * smooth((t - 0.1) / 0.6);
    if (length > 0.5) {
      ctx.save();
      ctx.beginPath(); ctx.moveTo(x, y); ctx.quadraticCurveTo(x + out * 2, y + length * 0.5, x + out * 1.2, y + length);
      ctx.strokeStyle = "rgba(31,95,149,0.6)"; ctx.lineWidth = 4.6; ctx.stroke();
      ctx.strokeStyle = "rgba(176,226,255,0.95)"; ctx.lineWidth = 3; ctx.stroke();
      ctx.restore();
    }
    // A glistening pool on the lower lid.
    piece(ctx, smooth(t / 0.3), x - out * 0.1 * gap, y - 1, 1, 0, () => {
      ctx.beginPath(); ctx.ellipse(0, 0, 0.16 * gap, 0.05 * gap + 0.8, 0, 0, TAU);
      outlined(ctx, "rgba(190,232,255,0.9)", "rgba(31,95,149,0.8)", 0.9);
    });
    for (let i = 0; i < 3; i++) {
      const time = particleTime(t - 0.35, i, 0.45, 1.35);
      if (time === undefined) continue;
      const fall = 0.95 * gap * smooth(time / 1.1);
      piece(ctx, life(time, 1.35, 0.12, 0.4), x + out * 1.5 * smooth(time / 1.1), y + 3 + fall,
        easeOutBack(time / 0.25) * 0.6, 0, () => drop(ctx, 0.11 * gap, "#f2fbff", "#7cc9f5", "#1f5f95"));
    }
  });
}

function gloom(ctx, anchor, t) {
  const top = headTop(anchor), bottom = -6, shade = smooth(t / 0.6);
  ctx.save();
  ctx.globalAlpha *= 0.55 * shade;
  ctx.beginPath(); ctx.ellipse(0, (top + bottom) / 2, 52, (bottom - top) / 2 + 6, 0, 0, TAU);
  ctx.fillStyle = gradient(ctx, top, bottom + 6, "rgba(38,24,72,0.85)", "rgba(38,24,72,0)");
  ctx.fill();
  ctx.restore();
  for (let i = 0; i < 9; i++) {
    const x = -40 + i * 10, start = top + 6 + 4 * Math.abs(Math.sin(i * 2.3));
    const end = start + (bottom - start) * (0.55 + 0.4 * Math.abs(Math.sin(i * 1.7 + 0.4)));
    const grow = smooth((t - 0.06 * i) / 0.55), y = start + (end - start) * grow;
    if (y - start < 0.5) continue;
    ctx.save();
    ctx.beginPath(); ctx.moveTo(x, start); ctx.lineTo(x + 0.6 * Math.sin(TAU * t / 2.5 + i), y);
    ctx.strokeStyle = gradient(ctx, start, end, "rgba(30,18,58,0.95)", "rgba(52,36,96,0.25)");
    ctx.lineWidth = i % 3 === 1 ? 2.6 : 1.8;
    ctx.stroke();
    ctx.restore();
  }
}

function question(ctx, anchor, t) {
  const top = headTop(anchor);
  piece(ctx, 1, 58, 0.72 * top + 1.5 * Math.sin(TAU * t / 1.3), easeOutBack(t / 0.35), 0.18 + 0.1 * Math.sin(TAU * t / 1.1),
    () => glyph(ctx, "?", 34, "#5ab4ff", "#123a63"));
  piece(ctx, smooth((t - 0.3) / 0.2), 75, 0.72 * top + 14 + Math.sin(TAU * t / 1.5), easeOutBack((t - 0.3) / 0.35) * 0.55, 0.45,
    () => glyph(ctx, "?", 34, "#5ab4ff", "#123a63"));
}

function exclaim(ctx, anchor, t) {
  const top = headTop(anchor), hop = -6 * Math.sin(Math.PI * clamp(t / 0.3)), cx = 56, cy = 0.72 * top;
  piece(ctx, 1, cx, cy + hop, easeOutBack(t / 0.25), 0.12, () => glyph(ctx, "!", 38, "#ffcf3a", "#7a1b05"));
  // Short burst lines around it as it pops.
  const burst = smooth((t - 0.05) / 0.2) * (1 - smooth((t - 0.9) / 0.4));
  if (burst > 0.01) {
    ctx.save();
    ctx.globalAlpha *= burst;
    const r0 = 20, r1 = 20 + 9 * smooth((t - 0.05) / 0.25);
    for (const angle of [-2.6, -1.9, -0.9, -0.2]) {
      ctx.beginPath();
      ctx.moveTo(cx + r0 * Math.cos(angle), cy + r0 * Math.sin(angle));
      ctx.lineTo(cx + r1 * Math.cos(angle), cy + r1 * Math.sin(angle));
      doubleStroke(ctx, "#ffcf3a", "#7a1b05", 2.2, 0.9);
    }
    ctx.restore();
  }
}

function sleepy(ctx, anchor, t) {
  const top = headTop(anchor), spacing = 0.7, period = 2.1;
  for (let i = 0; i < 3; i++) {
    const time = particleTime(t, i, spacing, period);
    if (time === undefined) continue;
    const p = time / period;
    piece(ctx, life(time, period, 0.25, 0.6), 40 + 30 * p + 3 * Math.sin(TAU * time / 1.2), 0.45 * top - 38 * p,
      (0.7 + 0.7 * p) * easeOutBack(time / 0.35), -0.25 + 0.15 * Math.sin(TAU * time / 1.6),
      () => glyph(ctx, "Z", 20, "#d6defe", "#28356e"));
  }
}

const NOTES = [{ x: -48, colour: "#ff7ab8", pair: false }, { x: 50, colour: "#6fd0ff", pair: true },
  { x: -36, colour: "#ffd25a", pair: true }, { x: 44, colour: "#8ff07a", pair: false }];
function noteHead(ctx, x, y, colour) {
  ctx.beginPath(); ctx.ellipse(x, y, 4.3, 3.1, -0.4, 0, TAU);
  outlined(ctx, colour, "#262640", 1.3);
}
function music(ctx, anchor, t) {
  const top = headTop(anchor), spacing = 0.5, period = NOTES.length * spacing;
  NOTES.forEach((note, i) => {
    const time = particleTime(t, i, spacing, period);
    if (time === undefined) return;
    const p = time / period;
    piece(ctx, life(time, period, 0.15, 0.5), note.x + 6 * Math.sin(TAU * time / 1.2 + i), 0.3 * top - 44 * p,
      1.35 * easeOutBack(time / 0.3), 0.25 * Math.sin(TAU * time / 1.4 + i), () => {
        ctx.beginPath();
        if (note.pair) {
          ctx.moveTo(3.6, 0); ctx.lineTo(3.6, -16); ctx.lineTo(15.6, -19); ctx.lineTo(15.6, -3);
        } else {
          ctx.moveTo(3.6, 0); ctx.lineTo(3.6, -16); ctx.bezierCurveTo(6, -12, 11, -11, 9, -4);
        }
        doubleStroke(ctx, note.colour, "#262640", 1.8, 1.1);
        noteHead(ctx, 0, 0, note.colour);
        if (note.pair) noteHead(ctx, 12, -3, note.colour);
      });
  });
}

/** Each overlay emote: how long it plays (seconds; a held one plays until it is ended), its fades, and what it draws
 *  `t` seconds in. Drawings stay sensible for any `t`, so a held one keeps going (looping ones keep looping). */
export const MANPU = Object.freeze({
  sweat: { duration: 2.8, fadeIn: 0.15, fadeOut: 0.5, draw: sweat },
  anger: { duration: 2.2, fadeIn: 0.12, fadeOut: 0.4, draw: anger },
  hearts: { duration: 3.4, fadeIn: 0.2, fadeOut: 0.6, draw: hearts },
  sparkles: { duration: 2.4, fadeIn: 0.15, fadeOut: 0.5, draw: sparkles },
  tears: { duration: 3.2, fadeIn: 0.25, fadeOut: 0.6, draw: tears },
  gloom: { duration: 3.6, fadeIn: 0.4, fadeOut: 0.7, draw: gloom },
  question: { duration: 2.2, fadeIn: 0.1, fadeOut: 0.4, draw: question },
  exclaim: { duration: 1.6, fadeIn: 0.06, fadeOut: 0.35, draw: exclaim },
  sleepy: { duration: 3.6, fadeIn: 0.2, fadeOut: 0.6, draw: sleepy },
  music: { duration: 3.2, fadeIn: 0.2, fadeOut: 0.6, draw: music },
});

/** Draws overlay emote `name` `t` seconds in; `weight` (0 to 1) fades it in and out as a whole. */
export function drawManpu(name, ctx, anchor, t, weight) {
  const effect = MANPU[name];
  if (!effect || !anchor || !(anchor.width > 0) || !(weight > 0)) return;
  ctx.save();
  ctx.globalAlpha = clamp(weight);
  inFace(ctx, anchor, () => effect.draw(ctx, anchor, Math.max(0, t)));
  ctx.restore();
}

/** Registers every overlay emote with the overlay layer's `registerOverlay`. */
export function registerManpu(registerOverlay) {
  for (const name of MANPU_NAMES) {
    const { duration, fadeIn, fadeOut } = MANPU[name];
    registerOverlay(name, { duration, fadeIn, fadeOut, draw: (ctx, anchor, t, weight) => drawManpu(name, ctx, anchor, t, weight) });
  }
}
