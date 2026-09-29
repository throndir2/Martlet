import { build } from "../../Martlet.Avatar.Vrm/node_modules/esbuild/lib/main.js";
import { readFile, mkdir, copyFile, writeFile, rm, cp } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { ensureSdk, SDK_URL, SDK_SHA256, SDK_VERSION } from "../../Martlet.Avatar.Live2D/scripts/sdk.mjs";

const root = path.dirname(fileURLToPath(import.meta.url));
const output = path.join(root, "dist");
const repository = path.resolve(root, "../../..");
const evidence = process.argv[2] ? path.resolve(process.argv[2]) : path.resolve(root, "../obj");
await mkdir(output, { recursive: true });
const compiled = await build({ absWorkingDir: repository, entryPoints: [path.join(root, "app.js")], outfile: path.join(output, "app.js"),
  bundle: true, format: "esm", target: "es2022", legalComments: "external",
  external: ["./asset/sdk.js"], logLevel: "warning", metafile: true });
await copyFile(path.join(root, "index.html"), path.join(output, "index.html"));
const vrm = path.resolve(root, "../../Martlet.Avatar.Vrm");
const lock = JSON.parse(await readFile(path.join(vrm, "package-lock.json"), "utf8"));
const notices = [];
const licenseFiles = [];
for (const [name, metadata] of Object.entries(lock.packages)) {
  if (!name || metadata.dev) continue;
  let license;
  for (const file of ["LICENSE", "LICENSE.txt", "LICENSE.md"]) {
    try {
      const licenseFile = path.join(vrm, name, file);
      license = await readFile(licenseFile, "utf8"); licenseFiles.push(licenseFile); break;
    }
    catch (error) { if (error.code !== "ENOENT") throw error; }
  }
  if (!license) throw new Error(`Missing dependency license: ${name}`);
  notices.push(`${name} ${metadata.version}\n${license}`);
}
notices.push(await readFile(path.resolve(root, "../../Martlet.Avatar.Live2D/NOTICES.md"), "utf8"));
await writeFile(path.join(output, "THIRD-PARTY-NOTICES.txt"), notices.join("\n\n"));
const relative = file => path.relative(repository, file).replaceAll("\\", "/");
const record = async file => {
  const bytes = await readFile(file);
  const location = relative(file);
  if (location.startsWith("../")) throw new Error("Bundle evidence input escapes repository.");
  return { path: location, bytes: bytes.length, sha256: createHash("sha256").update(bytes).digest("hex") };
};
const inputs = [...new Set([...Object.keys(compiled.metafile.inputs).map(file => path.resolve(repository, file)),
  ...licenseFiles, fileURLToPath(import.meta.url), path.join(root, "index.html"), path.join(vrm, "package-lock.json"),
  path.resolve(root, "../../Martlet.Avatar.Live2D/package-lock.json"), path.resolve(root, "../../Martlet.Avatar.Live2D/NOTICES.md")])];
const outputs = [...Object.keys(compiled.metafile.outputs).map(file => path.resolve(repository, file)),
  path.join(output, "index.html"), path.join(output, "THIRD-PARTY-NOTICES.txt")];
const packages = Object.entries(lock.packages).filter(([name, metadata]) => name && !metadata.dev)
  .map(([name, metadata]) => ({ name: name.replace(/^node_modules\//, ""), version: metadata.version,
    integrity: metadata.integrity, inputs: inputs.map(relative).filter(file => file.startsWith(relative(path.join(vrm, name)) + "/")).sort() }));
await mkdir(evidence, { recursive: true });
await writeFile(path.join(evidence, "avatar-esbuild-metafile.json"), JSON.stringify(compiled.metafile, null, 2) + "\n");
await writeFile(path.join(evidence, "avatar-bundle-receipt.json"), JSON.stringify({
  version: 1, inputs: (await Promise.all(inputs.map(record))).sort((a, b) => a.path.localeCompare(b.path, "en")),
  outputs: (await Promise.all(outputs.map(record))).sort((a, b) => a.path.localeCompare(b.path, "en")), packages
}, null, 2) + "\n");

// Bundled Live2D runtime and default character, built from the pinned official SDK download (not committed).
const live2dRoot = path.resolve(root, "../../Martlet.Avatar.Live2D");
const live2dOutput = path.join(root, "live2d-dist");
await rm(live2dOutput, { recursive: true, force: true });
const sdkRoot = await ensureSdk({ required: process.env.MARTLET_REQUIRE_LIVE2D === "1" });
if (sdkRoot) {
  await mkdir(path.join(live2dOutput, "sdk"), { recursive: true });
  await build({ absWorkingDir: live2dRoot, entryPoints: [path.join(live2dRoot, "runtime", "sdk-bundle.ts")],
    outfile: path.join(live2dOutput, "sdk", "sdk.js"), bundle: true, format: "esm", platform: "browser",
    target: "es2022", minify: true, legalComments: "inline", logLevel: "warning" });
  await copyFile(path.join(sdkRoot, "Core", "live2dcubismcore.min.js"), path.join(live2dOutput, "sdk", "core.js"));
  await cp(path.join(sdkRoot, "Samples", "Resources", "Hiyori"), path.join(live2dOutput, "models", "Hiyori"), { recursive: true });
  const framework = await readFile(path.join(sdkRoot, "Framework", "LICENSE.md"), "utf8");
  const core = await readFile(path.join(sdkRoot, "Core", "LICENSE.md"), "utf8");
  await writeFile(path.join(live2dOutput, "LIVE2D-NOTICES.txt"), [
    "Live2D Cubism components bundled with Martlet",
    `Source: official Cubism SDK for Web ${SDK_VERSION} (${SDK_URL}, SHA-256 ${SDK_SHA256}).`,
    "",
    "Live2D Cubism Core (live2d/sdk/core.js): unmodified redistributable file, Copyright (c) Live2D Inc.,",
    "used under the Live2D Proprietary Software License Agreement. You may not modify, reverse engineer,",
    "extract or redistribute Cubism Core separately from Martlet. Live2D Inc. retains all rights.",
    "",
    "Cubism Web Framework (compiled into live2d/sdk/sdk.js): Copyright (c) Live2D Inc.,",
    "used under the Live2D Open Software License.",
    "",
    "Hiyori Momose sample model (live2d/models/Hiyori): Live2D Original Character, used under the",
    "Live2D Free Material License Agreement and Terms of Use for Live2D Cubism Sample Data",
    "(https://www.live2d.com/eula/live2d-sample-model-terms_en.html). The character design is unmodified.",
    "This content uses sample data owned and copyrighted by Live2D Inc. The sample data are utilized in",
    "accordance with terms and conditions set by Live2D Inc. This content itself is created at the author's",
    "sole discretion.",
    "",
    "Live2D and Cubism are trademarks or registered trademarks of Live2D Inc.",
    "",
    "----- Framework/LICENSE.md -----", framework,
    "----- Core/LICENSE.md -----", core,
  ].join("\n"));
}
