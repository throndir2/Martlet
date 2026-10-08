// Martlet's touch zones on the showing character (Companion › Touch › Touch zones › Show the zones on the character): each area
// Martlet gave ("zoneview") where its parts are now. An area that follows Live2D drawables is the box around them as they are
// drawn now (a tail's area swings with the tail); one that follows VRM bones or nodes keeps its size around where they are now;
// any other stays where its box is with the character framed whole, moved by the view's zoom and pan. They are drawn on a canvas
// of their own over the character, which never takes a click, and read for Martlet's MCP (character_zones) drawn or not.

let zones = { draw: false, areas: [] };
let canvas, context, painted = false;

/** Draws on `target`, a canvas laid exactly over the avatar canvas. */
export function attachZones(target) { canvas = target; context = target?.getContext?.("2d") ?? undefined; }

const finite = value => typeof value === "number" && Number.isFinite(value);
const names = list => Array.isArray(list) && list.length <= 256 && list.every(name => typeof name === "string" && name.length > 0 && name.length <= 256);
const valid = area => area !== null && typeof area === "object" && typeof area.zone === "string" && typeof area.name === "string" &&
  Number.isInteger(area.area) && /^#[0-9a-f]{6}$/i.test(area.color ?? "") && ["left", "top", "right", "bottom"].every(key => finite(area[key])) &&
  area.right > area.left && area.bottom > area.top && names(area.drawables) && names(area.bones) && names(area.nodes);

/** Takes the zones Martlet gives (`{draw, areas}`); anything else clears them. Returns how many areas it took. */
export function setZoneView(data) {
  const areas = Array.isArray(data?.areas) ? data.areas.slice(0, 512).filter(valid) : [];
  zones = { draw: data?.draw === true && areas.length > 0, areas };
  return areas.length;
}

/** Whether the zones are drawn over the character now. */
export const zonesDrawn = () => zones.draw;

/** How many areas Martlet gave. */
export const zoneAreas = () => zones.areas.length;

/**
 * Where each area is now, as fractions of the page (+y down): `drawables()` gives each Live2D drawable's bounds now by ID,
 * `points()` each VRM bone's and node's place now by name (both fractions of the page, asked for only when an area needs them)
 * and `frame` the view's {zoom, x, y, frame}. Each box says what placed it (`from`: "drawables", "bones" or "box").
 */
export function zoneBoxes({ drawables, points, frame }) {
  let bounds, places;
  const zoom = finite(frame?.zoom) && frame.zoom > 0 ? frame.zoom : 1;
  const panX = finite(frame?.x) ? frame.x * (finite(frame?.frame) ? frame.frame : 1) : 0, panY = finite(frame?.y) ? frame.y : 0;
  // A point with the character framed whole, where it is in the view now (the renderers draw fitted * zoom + pan).
  const framed = (x, y) => [((2 * x - 1) * zoom + panX + 1) / 2, (1 - ((1 - 2 * y) * zoom + panY)) / 2];
  return zones.areas.map(area => {
    const at = { zone: area.zone, name: area.name, area: area.area, color: area.color };
    if (area.drawables.length > 0) {
      bounds ??= drawables?.() ?? new Map();
      const found = area.drawables.map(id => bounds.get(id)).filter(Boolean);
      if (found.length > 0)
        return { ...at, from: "drawables", left: Math.min(...found.map(d => d.left)), top: Math.min(...found.map(d => d.top)),
          right: Math.max(...found.map(d => d.right)), bottom: Math.max(...found.map(d => d.bottom)) };
    }
    const [left, top] = framed(area.left, area.top), [right, bottom] = framed(area.right, area.bottom);
    if (area.bones.length + area.nodes.length > 0) {
      places ??= points?.() ?? new Map();
      const found = [...area.nodes, ...area.bones].map(name => places.get(name)).filter(Boolean);
      if (found.length > 0) {
        const x = found.reduce((sum, p) => sum + p.x, 0) / found.length, y = found.reduce((sum, p) => sum + p.y, 0) / found.length;
        const halfWidth = (right - left) / 2, halfHeight = (bottom - top) / 2;
        return { ...at, from: "bones", left: x - halfWidth, top: y - halfHeight, right: x + halfWidth, bottom: y + halfHeight };
      }
    }
    return { ...at, from: "box", left, top, right, bottom };
  });
}

/** Sizes the zones' canvas to its avatar canvas, clears it and, while the zones are drawn, draws `boxes` (see zoneBoxes): each
 *  area's outline in its zone's color, the zone's name on its first area. */
export function renderZones(boxes) {
  if (!canvas || !context) return;
  if (!zones.draw && !painted) return;
  const ratio = Math.min(2048 / Math.max(1, canvas.clientWidth, canvas.clientHeight), globalThis.devicePixelRatio || 1);
  const width = Math.min(2048, Math.max(1, Math.round(canvas.clientWidth * ratio)));
  const height = Math.min(2048, Math.max(1, Math.round(canvas.clientHeight * ratio)));
  if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
  context.setTransform(1, 0, 0, 1, 0, 0);
  context.clearRect(0, 0, width, height);
  painted = zones.draw;
  if (!painted) return;
  context.lineWidth = Math.max(1, 1.5 * ratio);
  for (const box of boxes) {
    const x = box.left * width, y = box.top * height, w = (box.right - box.left) * width, h = (box.bottom - box.top) * height;
    context.strokeStyle = box.color;
    context.fillStyle = box.color + "22";
    context.fillRect(x, y, w, h);
    context.strokeRect(x, y, w, h);
  }
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  context.font = "11px 'Segoe UI', sans-serif";
  context.textBaseline = "top";
  for (const box of boxes.filter(b => b.area === 0)) {
    const x = box.left * canvas.clientWidth + 2, y = box.top * canvas.clientHeight + 1;
    const text = box.name, measured = context.measureText(text).width;
    context.fillStyle = box.color + "cc";
    context.fillRect(x - 2, y - 1, measured + 4, 14);
    context.fillStyle = "#ffffff";
    context.fillText(text, x, y);
  }
}
