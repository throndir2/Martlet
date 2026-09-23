import { createHash } from "node:crypto";
import { readFile, writeFile, mkdir, lstat, readdir } from "node:fs/promises";
import path from "node:path";
import { gunzipSync } from "node:zlib";

const [repository, payload, publishDirectory, receiptDirectory] = process.argv.slice(2);
if (!receiptDirectory) throw new Error("Expected repository, payload, publish and browser receipt directories.");
const hash = (bytes, algorithm = "sha256") => createHash(algorithm).update(bytes).digest("hex");
const ordinal = (a, b) => a < b ? -1 : a > b ? 1 : 0;
const vrm = "src\\Martlet.Avatar.Vrm";
const web = "src\\Martlet.Avatar.RendererHost\\web";
const inputMap = new Map();
const utf8 = new TextDecoder("utf-8", { fatal: true });
const windows = value => value.replaceAll("/", "\\");
function relative(value) {
  if (typeof value !== "string" || !value || value.length > 1024 || /[<>:"/|?*\x00-\x1f\x7f-\x9f]/u.test(value))
    throw new Error("Invalid browser evidence path.");
  for (const segment of value.split("\\")) {
    if (!segment || segment === "." || segment === ".." || /[. ]$/u.test(segment) ||
        /^(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)/iu.test(segment))
      throw new Error("Unsafe browser evidence path.");
  }
  return value;
}
async function regular(file) {
  let current = file;
  while (true) {
    const stat = await lstat(current);
    if (stat.isSymbolicLink()) throw new Error("Reparse/symlink browser input is not supported.");
    const parent = path.dirname(current);
    if (parent === current) break;
    current = parent;
  }
  if (!(await lstat(file)).isFile()) throw new Error("Browser input must be a regular file.");
}
async function record(root, name) {
  relative(name);
  const file = path.join(root, name);
  await regular(file);
  const bytes = await readFile(file);
  return { path: name, bytes: bytes.length, sha256: hash(bytes) };
}
async function readExisting(file) {
  try { await regular(file); return await readFile(file); }
  catch (error) { if (error.code === "ENOENT") return null; throw error; }
}
async function writeOrVerify(file, bytes) {
  const existing = await readExisting(file);
  if (existing !== null) {
    if (!existing.equals(bytes)) throw new Error("Retained browser evidence differs; use fresh publish output.");
  } else await writeFile(file, bytes, { flag: "wx" });
}
function checkRecord(actual, expected) {
  if (!expected || actual.path !== windows(expected.path) || actual.bytes !== expected.bytes || actual.sha256 !== expected.sha256)
    throw new Error(`Browser build receipt differs from actual input/output: ${actual.path}`);
}
async function addInput(name, role, owner = null, entry = null) {
  relative(name);
  const observed = await record(repository, name);
  let existing = inputMap.get(name.toLowerCase());
  if (existing) {
    if (existing.path !== name || existing.package !== owner || existing.entry !== entry ||
        existing.bytes !== observed.bytes || existing.sha256 !== observed.sha256) throw new Error("Ambiguous or changed browser input.");
  } else {
    existing = { ...observed, package: owner, entry, roles: [] };
    inputMap.set(name.toLowerCase(), existing);
  }
  if (!existing.roles.includes(role)) existing.roles.push(role);
  existing.roles.sort(ordinal);
  return existing;
}
function tarFiles(compressed) {
  const tar = gunzipSync(compressed, { maxOutputLength: 256 * 1024 * 1024 });
  const files = new Map();
  const names = new Set();
  let offset = 0, pendingPath = null, ended = false;
  const text = bytes => utf8.decode(bytes).split("\0")[0];
  while (offset + 512 <= tar.length) {
    const header = tar.subarray(offset, offset + 512);
    offset += 512;
    if (header.every(value => value === 0)) { ended = true; break; }
    const number = bytes => {
      const value = text(bytes).trim();
      if (!/^[0-7]+$/u.test(value)) throw new Error("Unsupported npm tar numeric encoding.");
      const result = Number.parseInt(value, 8);
      if (!Number.isSafeInteger(result)) throw new Error("Oversized npm tar entry.");
      return result;
    };
    const checksum = number(header.subarray(148, 156));
    const actual = header.reduce((sum, value, index) => sum + (index >= 148 && index < 156 ? 32 : value), 0);
    if (actual !== checksum) throw new Error("Invalid npm tar header checksum.");
    const size = number(header.subarray(124, 136));
    if (size > 64 * 1024 * 1024 || offset + size > tar.length) throw new Error("Truncated or oversized npm tar entry.");
    const bytes = tar.subarray(offset, offset + size);
    offset += Math.ceil(size / 512) * 512;
    const type = header[156];
    if (type === 120) {
      if (pendingPath !== null || size > 16384) throw new Error("Invalid npm tar extended header.");
      let cursor = 0;
      while (cursor < bytes.length) {
        const space = bytes.indexOf(32, cursor);
        if (space < 0) throw new Error("Invalid npm tar extended field.");
        const length = Number(utf8.decode(bytes.subarray(cursor, space)));
        if (!Number.isSafeInteger(length) || length <= space - cursor + 2 || cursor + length > bytes.length || bytes[cursor + length - 1] !== 10)
          throw new Error("Invalid npm tar extended length.");
        const value = utf8.decode(bytes.subarray(space + 1, cursor + length - 1));
        const equal = value.indexOf("=");
        if (equal < 1) throw new Error("Invalid npm tar extended key.");
        const key = value.slice(0, equal);
        if (key === "path") pendingPath = value.slice(equal + 1);
        else if (!["mtime", "atime", "ctime", "uid", "gid", "uname", "gname"].includes(key))
          throw new Error("Unsupported npm tar extended metadata.");
        cursor += length;
      }
      continue;
    }
    let name = text(header.subarray(0, 100));
    const prefix = text(header.subarray(345, 500));
    if (prefix) name = `${prefix}/${name}`;
    if (pendingPath !== null) { name = pendingPath; pendingPath = null; }
    if (type === 53 && name.endsWith("/")) name = name.slice(0, -1);
    relative(windows(name));
    if (name !== "package" && !name.startsWith("package/")) throw new Error("Npm archive entry escapes its package.");
    if (names.has(name.toLowerCase()) || names.size >= 16384) throw new Error("Duplicate or oversized npm tar inventory.");
    names.add(name.toLowerCase());
    if (type === 0 || type === 48) files.set(name, bytes);
    else if (type !== 53 || size !== 0) throw new Error("Unsupported npm tar link or entry type.");
  }
  if (!ended || pendingPath !== null || tar.subarray(offset).some(value => value !== 0)) throw new Error("Incomplete npm tar terminator.");
  return files;
}

const lockPaths = ["src\\Martlet.Avatar.Live2D\\package-lock.json", `${vrm}\\package-lock.json`];
const lockFiles = [];
for (const name of lockPaths) { lockFiles.push(await record(repository, name)); await addInput(name, "lock"); }
const lock = JSON.parse(await readFile(path.join(repository, lockPaths[1]), "utf8"));
if (lock.lockfileVersion !== 3 || !lock.packages) throw new Error("Expected reviewed npm lockfile version 3.");
const pins = JSON.parse(await readFile(path.join(repository, "packaging\\windows\\toolchain.json"), "utf8"));
if (process.versions.node !== pins.browserTools.node) throw new Error("Incorrect observed Node version.");
const selected = Object.keys(lock.packages).filter(key => key && (!lock.packages[key].dev ||
  key === "node_modules/esbuild" || key === "node_modules/@esbuild/win32-x64")).sort(ordinal);
if (!selected.length || selected.length > 256) throw new Error("Invalid npm package count.");
const receipts = JSON.parse(await readFile(path.join(receiptDirectory, "avatar-bundle-receipt.json"), "utf8"));
const metafileBytes = await readFile(path.join(receiptDirectory, "avatar-esbuild-metafile.json"));
const metafile = JSON.parse(utf8.decode(metafileBytes));
if (receipts.version !== 1 || !Array.isArray(receipts.inputs) || !Array.isArray(receipts.outputs) || !Array.isArray(receipts.packages))
  throw new Error("Unsupported browser build receipt.");
const packages = [], archives = new Map();
const cache = path.join(publishDirectory, "npm-archives");
await mkdir(cache, { recursive: true });
for (const key of selected) {
  relative(windows(key));
  const metadata = lock.packages[key];
  const name = key.slice("node_modules/".length);
  if (!/^node_modules\/(?:@[^/]+\/)?[^/]+$/u.test(key)) throw new Error("Unreviewed nested npm resolution.");
  if (!/^sha512-[A-Za-z0-9+/]{86}==$/u.test(metadata.integrity)) throw new Error("Expected a single pinned npm SHA512 integrity.");
  const expected = Buffer.from(metadata.integrity.slice(7), "base64");
  const url = new URL(metadata.resolved);
  if (url.protocol !== "https:" || url.hostname !== "registry.npmjs.org" || url.username || url.password || url.search || url.hash)
    throw new Error("Unreviewed npm package source.");
  const archivePath = path.join(cache, `${hash(Buffer.from(key))}.tgz`);
  let bytes = await readExisting(archivePath);
  if (bytes === null) {
    const response = await fetch(url, { redirect: "error", signal: AbortSignal.timeout(120000) });
    if (!response.ok) throw new Error(`Cannot acquire pinned npm archive: HTTP ${response.status}`);
    const chunks = []; let length = 0;
    for await (const chunk of response.body) {
      length += chunk.length;
      if (length > 64 * 1024 * 1024) throw new Error("Npm archive exceeds bound.");
      chunks.push(chunk);
    }
    bytes = Buffer.concat(chunks);
  }
  if (bytes.length > 64 * 1024 * 1024) throw new Error("Npm archive exceeds bound.");
  if (!createHash("sha512").update(bytes).digest().equals(expected)) throw new Error("Npm archive integrity failure.");
  const entries = tarFiles(bytes);
  const manifest = JSON.parse(utf8.decode(entries.get("package/package.json") ?? Buffer.alloc(0)));
  if (manifest.name !== name || manifest.version !== metadata.version) throw new Error("Npm archive identity differs from lock.");
  await writeOrVerify(archivePath, bytes);
  archives.set(key, entries);
  const dependencies = new Set();
  for (const group of ["dependencies", "peerDependencies", "optionalDependencies"]) {
    for (const dependency of Object.keys(metadata[group] ?? {})) {
      const resolved = `node_modules/${dependency}`;
      if (!selected.includes(resolved)) {
        if (group === "optionalDependencies" && metadata.dev) continue;
        throw new Error("Unresolved npm dependency or peer edge.");
      }
      dependencies.add(resolved);
    }
  }
  const packageRecord = { key, name, version: metadata.version, scope: metadata.dev ? "build" : "runtime",
    integrity: metadata.integrity, archiveSha512: hash(bytes, "sha512"), dependencies: [...dependencies].sort(ordinal), notices: [] };
  packages.push(packageRecord);
  await addInput(`${vrm}\\${windows(key)}\\package.json`, "package-metadata", key, "package/package.json");
  if (!metadata.dev) {
    const receipt = receipts.packages.filter(item => item.name === name && item.version === metadata.version && item.integrity === metadata.integrity);
    if (receipt.length !== 1) throw new Error("Runtime package missing or duplicated in build receipt.");
    const license = ["LICENSE", "LICENSE.txt", "LICENSE.md"].find(candidate => entries.has(`package/${candidate}`));
    if (!license) throw new Error("Complete runtime package license missing from archive.");
    const location = `${vrm}\\${windows(key)}\\${license}`;
    await addInput(location, "notice", key, `package/${license}`);
    packageRecord.notices.push(location);
  }
}
function owner(name) {
  const matches = selected.filter(key => name.startsWith(`${vrm}\\${windows(key)}\\`));
  if (matches.length > 1) throw new Error("Ambiguous npm input owner.");
  if (!matches.length) return [null, null];
  return [matches[0], `package/${name.slice(vrm.length + 1 + windows(matches[0]).length + 1).replaceAll("\\", "/")}`];
}
const bundleInputs = Object.keys(metafile.inputs).map(windows).sort(ordinal);
for (const name of bundleInputs) await addInput(name, "bundle-source", ...owner(name));
await addInput(`${web}\\build.mjs`, "build-script");
await addInput(`${web}\\index.html`, "static");
await addInput("src\\Martlet.Avatar.Live2D\\NOTICES.md", "notice");
for (const name of ["src\\Martlet.Avatar.Live2D\\package.json", `${vrm}\\package.json`]) await addInput(name, "package-metadata");
for (const name of [`${vrm}\\node_modules\\esbuild\\lib\\main.js`, `${vrm}\\node_modules\\@esbuild\\win32-x64\\esbuild.exe`])
  await addInput(name, "build-script", ...owner(name));
for (const input of inputMap.values()) {
  if (input.package !== null) {
    const bytes = archives.get(input.package)?.get(input.entry);
    if (!bytes || bytes.length !== input.bytes || hash(bytes) !== input.sha256) throw new Error(`Installed npm input differs from pinned archive: ${input.path}`);
  } else if (input.path.includes("\\node_modules\\")) throw new Error("Unowned materialized npm input.");
}
const seenReceiptInputs = new Set();
for (const entry of receipts.inputs) {
  const name = windows(entry.path);
  if (seenReceiptInputs.has(name.toLowerCase())) throw new Error("Duplicate browser receipt input.");
  seenReceiptInputs.add(name.toLowerCase());
  checkRecord(inputMap.get(name.toLowerCase()), entry);
}
for (const name of [...bundleInputs, `${web}\\build.mjs`, `${web}\\index.html`, ...lockPaths,
  ...packages.flatMap(item => item.notices), "src\\Martlet.Avatar.Live2D\\NOTICES.md"])
  if (!seenReceiptInputs.has(name.toLowerCase())) throw new Error("Missing actual browser input in build receipt.");
if (receipts.packages.length !== packages.filter(item => item.scope === "runtime").length) throw new Error("Unexpected browser runtime receipt package.");
const notices = [];
for (const key of Object.keys(lock.packages).filter(key => key && !lock.packages[key].dev)) {
  const packageRecord = packages.find(item => item.key === key);
  notices.push(`${key} ${packageRecord.version}\n${await readFile(path.join(repository, packageRecord.notices[0]), "utf8")}`);
}
notices.push(await readFile(path.join(repository, "src\\Martlet.Avatar.Live2D\\NOTICES.md"), "utf8"));
const outputs = [];
const names = ["THIRD-PARTY-NOTICES.txt", "app.js", "app.js.LEGAL.txt", "index.html"];
const actualNames = (await readdir(path.join(payload, "Desktop\\AvatarRenderer\\web"))).sort(ordinal);
if (JSON.stringify(actualNames) !== JSON.stringify(names)) throw new Error("Unexpected browser payload file inventory.");
for (const name of names) {
  const inputPath = `${web}\\dist\\${name}`;
  const built = await record(repository, inputPath);
  const matches = receipts.outputs.filter(item => windows(item.path) === inputPath);
  if (matches.length !== 1) throw new Error("Missing or duplicate browser receipt output.");
  checkRecord(built, matches[0]);
  const output = await record(payload, `Desktop\\AvatarRenderer\\web\\${name}`);
  if (output.bytes !== built.bytes || output.sha256 !== built.sha256) throw new Error("Published browser output differs from build receipt.");
  const kind = name === "app.js" ? "bundle" : name === "app.js.LEGAL.txt" ? "legal" : name === "index.html" ? "static" : "notices";
  const inputs = kind === "static" ? [`${web}\\index.html`] : kind === "notices"
    ? [...packages.flatMap(item => item.notices), "src\\Martlet.Avatar.Live2D\\NOTICES.md"].sort(ordinal) : bundleInputs;
  if (kind === "notices" && built.sha256 !== hash(Buffer.from(notices.join("\n\n")))) throw new Error("Incomplete or modified combined runtime notices.");
  if (kind === "static" && built.sha256 !== inputMap.get(`${web}\\index.html`.toLowerCase()).sha256) throw new Error("Static browser source differs.");
  outputs.push({ ...output, kind, inputs });
}
if (receipts.outputs.length !== 4) throw new Error("Unexpected browser receipt output count.");
const npmRoot = path.join(path.dirname(process.execPath), "node_modules\\npm");
const npm = JSON.parse(await readFile(path.join(npmRoot, "package.json"), "utf8"));
if (npm.version !== pins.browserTools.npm) throw new Error("Incorrect npm tool version.");
const esbuild = packages.find(item => item.key === "node_modules/esbuild");
if (esbuild.version !== pins.browserTools.esbuild) throw new Error("Incorrect esbuild tool version.");
const tools = [
  { name: "node", version: process.versions.node, files: [await record(path.dirname(process.execPath), path.basename(process.execPath))] },
  { name: "npm", version: npm.version, files: [await record(npmRoot, "bin\\npm-cli.js"), await record(npmRoot, "package.json")] },
  { name: "esbuild", version: esbuild.version, files: [await record(path.join(repository, vrm, "node_modules\\@esbuild\\win32-x64"), "esbuild.exe")] }
];
const evidence = { schemaVersion: 1, context: "AvatarRenderer",
  recipe: { script: `${web}\\build.mjs`, entryPoints: [`${web}\\app.js`], metafileSha256: hash(metafileBytes),
    receiptSha256: hash(await readFile(path.join(receiptDirectory, "avatar-bundle-receipt.json"))) },
  lockFiles, tools, packages, inputs: [...inputMap.values()].sort((a, b) => ordinal(a.path, b.path)), outputs };
await writeOrVerify(path.join(publishDirectory, "browser-evidence.json"), Buffer.from(JSON.stringify(evidence, null, 2) + "\n"));
