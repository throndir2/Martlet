/** Where a Live2D model's face is, for Martlet's own drawings over it (a blush glow, manga symbols). Live2D models carry no
 *  face landmarks, so it is estimated: from an authored head/face hit area's mesh, from meshes whose IDs name the face or
 *  cheeks, or from the shape of the top of the model; a hint (from vision) overrides the estimate. */

export interface Point { readonly x: number; readonly y: number }

/** A face in model units (y up): the middle of the eye line, the face's width and its roll (radians, counterclockwise). */
export interface Face { readonly x: number; readonly y: number; readonly width: number; readonly roll: number }

/** The features Martlet draws around, in the same units as the face (y up). */
export interface FaceFeatures {
  readonly x: number; readonly y: number; readonly width: number; readonly roll: number;
  readonly cheekLeft: Point; readonly cheekRight: Point; readonly eyeLeft: Point; readonly eyeRight: Point;
  readonly mouth: Point; readonly top: Point;
}

/** A face found by vision, as fractions of the model's canvas (0,0 top left, y down; width of the canvas width). */
export interface FaceHint {
  readonly x: number; readonly y: number; readonly width: number;
  readonly cheekLeft?: Point; readonly cheekRight?: Point;
}

export interface Box { readonly minX: number; readonly maxX: number; readonly minY: number; readonly maxY: number }

export const HEAD_AREA = /head|face|頭|顔|头|脸|臉|atama|kao/i;
export const FACE_MESH = /face|顔|脸|臉|kao/i;
export const CHEEK_MESH = /cheek|頬|ほほ|腮|hoho/i;

/** How the face is found: the meshes to measure (and how to read their box), or a fixed face to move with the angles. */
export type FaceSource =
  | { readonly kind: "head" | "skin" | "cheeks"; readonly drawables: readonly number[] }
  | { readonly kind: "fixed"; readonly face: Face };

/** Picks how to find the face from the hit areas (`{ id, name }`, the id an ArtMesh's) and the drawables' IDs. */
export function faceSource(hitAreas: readonly { readonly id: string; readonly name: string }[], drawableIds: readonly string[],
  layout: () => Face | undefined): FaceSource | undefined {
  const index = new Map(drawableIds.map((id, i) => [id, i]));
  const head = hitAreas.filter(h => HEAD_AREA.test(h.name) || HEAD_AREA.test(h.id))
    .map(h => index.get(h.id)).filter((i): i is number => i !== undefined);
  if (head.length) return { kind: "head", drawables: head };
  const skin = drawableIds.flatMap((id, i) => FACE_MESH.test(id) ? [i] : []);
  if (skin.length) return { kind: "skin", drawables: skin };
  const cheeks = drawableIds.flatMap((id, i) => CHEEK_MESH.test(id) ? [i] : []);
  if (cheeks.length) return { kind: "cheeks", drawables: cheeks };
  const face = layout();
  return face ? { kind: "fixed", face } : undefined;
}

/** The box of the vertices (interleaved x, y). */
export function bounds(meshes: Iterable<ArrayLike<number>>): Box | undefined {
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
  for (const vertices of meshes)
    for (let v = 0; v + 1 < vertices.length; v += 2) {
      const x = vertices[v]!, y = vertices[v + 1]!;
      if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
    }
  return Number.isFinite(minX) && maxX > minX && maxY > minY ? { minX, maxX, minY, maxY } : undefined;
}

/** The face from the box of the meshes a source names. */
export function faceFromBox(kind: "head" | "skin" | "cheeks", box: Box, roll = 0): Face {
  const width = box.maxX - box.minX, height = box.maxY - box.minY, x = (box.minX + box.maxX) / 2;
  switch (kind) {
    // A head area takes in the hair: the face is narrower, the eyes a little below its middle.
    case "head": return { x, y: box.minY + 0.42 * height, width: 0.72 * width, roll };
    // The face's skin runs from the chin to under the fringe: the eyes sit a little above its middle.
    case "skin": return { x, y: box.minY + 0.55 * height, width, roll };
    // Both cheeks: the eyes are above them and the face wider.
    case "cheeks": return { x, y: (box.minY + box.maxY) / 2 + 0.25 * width, width: 1.3 * width, roll };
  }
}

/** The face estimated from the visible meshes: the width of the top of the model (hair and head) gives the head's size. */
export function faceFromLayout(meshes: readonly ArrayLike<number>[]): Face | undefined {
  const box = bounds(meshes);
  if (!box) return undefined;
  const height = box.maxY - box.minY;
  const xs: number[] = [];
  for (const vertices of meshes)
    for (let v = 0; v + 1 < vertices.length; v += 2) {
      const y = vertices[v + 1]!;
      if (y <= box.maxY - 0.04 * height && y >= box.maxY - 0.14 * height) xs.push(vertices[v]!);
    }
  if (xs.length < 4) return undefined;
  xs.sort((a, b) => a - b);
  const low = xs[Math.floor(xs.length * 0.1)]!, high = xs[Math.ceil(xs.length * 0.9) - 1]!;
  const head = Math.min(high - low, box.maxX - box.minX, 0.6 * height);
  if (!(head > 0)) return undefined;
  // Tuned on the bundled Hiyori and a VTuber-style bust: the middle 80% of the band's vertices spans about the face's
  // width, and the eyes sit about that far below the top of the hair.
  return { x: (low + high) / 2, y: box.maxY - 0.92 * head, width: head, roll: 0 };
}

/** A vision hint as a face in model units, for a model canvas `canvasWidth` by `canvasHeight` centered on the origin. */
export function faceFromHint(hint: FaceHint, canvasWidth: number, canvasHeight: number): Face | undefined {
  if (![hint.x, hint.y, hint.width].every(Number.isFinite) || !(hint.width > 0)) return undefined;
  return { x: (hint.x - 0.5) * canvasWidth, y: (0.5 - hint.y) * canvasHeight, width: hint.width * canvasWidth, roll: 0 };
}

/** How far the head really rolls for ParamAngleZ (degrees), in radians, counterclockwise. The parameter's ±30 is a range,
 *  not an angle: artists roll the head mesh far less (about 10° on Hiyori), so it is scaled down and clamped. */
export function headRoll(angleZ: number): number {
  const degrees = Math.max(-MAX_ROLL_DEGREES, Math.min(MAX_ROLL_DEGREES, ROLL_SCALE * angleZ));
  return Number.isFinite(degrees) ? degrees * Math.PI / 180 : 0;
}
const ROLL_SCALE = 0.35, MAX_ROLL_DEGREES = 12;

/** A fixed face moved by the head's angles (degrees; Cubism's ParamAngleX turns right, Y up, Z rolls counterclockwise). */
export function turnFace(face: Face, angleX: number, angleY: number, angleZ: number): Face {
  const roll = headRoll(angleZ);
  // The head turns about the neck, below the face.
  const pivot = { x: face.x, y: face.y - 0.9 * face.width };
  const x = face.x + 0.12 * face.width * (angleX / 30), y = face.y + 0.1 * face.width * (angleY / 30);
  const dx = x - pivot.x, dy = y - pivot.y, c = Math.cos(roll), s = Math.sin(roll);
  return { x: pivot.x + dx * c - dy * s, y: pivot.y + dx * s + dy * c, width: face.width, roll: face.roll + roll };
}

/** The eyes, cheeks, mouth and top of the head around `face` (left and right as the viewer sees them). */
export function faceFeatures(face: Face): FaceFeatures {
  const c = Math.cos(face.roll), s = Math.sin(face.roll), w = face.width;
  const at = (dx: number, dy: number): Point => ({ x: face.x + (dx * c - dy * s) * w, y: face.y + (dx * s + dy * c) * w });
  return { ...face, eyeLeft: at(-0.2, 0), eyeRight: at(0.2, 0), cheekLeft: at(-0.27, -0.2), cheekRight: at(0.27, -0.2),
    mouth: at(0, -0.36), top: at(0, 0.62) };
}
