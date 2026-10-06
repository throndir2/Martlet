// Downloads the official Cubism SDK for Web 5-r.4 zip from Live2D, verifies its pinned SHA-256
// and extracts it into the ignored vendor directory. Nothing from the SDK is committed to git.
import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { access, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const SDK_VERSION = "5-r.4";
export const SDK_URL = `https://cubism.live2d.com/sdk-web/bin/CubismSdkForWeb-${SDK_VERSION}.zip`;
export const SDK_SHA256 = "d78904d908bd232b800219e01732e4ea2f0562b5e9f35a2670742a1c16d22942";
export const vendorRoot = fileURLToPath(new URL("../vendor/", import.meta.url));
export const sdkRoot = path.join(vendorRoot, `CubismSdkForWeb-${SDK_VERSION}`);

const exists = file => access(file).then(() => true, () => false);

function extract(zip, destination) {
  // Windows ships bsdtar (zip-capable) in System32; Git's GNU tar earlier on PATH cannot read zip files.
  if (process.platform === "win32") {
    execFileSync(path.join(process.env.SystemRoot ?? "C:\\Windows", "System32", "tar.exe"), ["-xf", zip, "-C", destination], { stdio: "inherit" });
    return;
  }
  // macOS tar is bsdtar; Linux's GNU tar can't read zip files, so fall back to unzip or Python's zipfile there.
  const tools = [["tar", ["-xf", zip, "-C", destination]], ["unzip", ["-q", "-o", zip, "-d", destination]],
    ["python3", ["-m", "zipfile", "-e", zip, destination]]];
  for (const [index, [tool, args]] of tools.entries()) {
    try { execFileSync(tool, args, { stdio: index === tools.length - 1 ? "inherit" : "ignore" }); return; }
    catch (error) { if (index === tools.length - 1) throw error; }
  }
}

/** Returns the extracted SDK root, or null when unavailable and not required. */
export async function ensureSdk({ required = false } = {}) {
  const marker = path.join(sdkRoot, ".martlet-verified");
  if (await exists(marker) && (await readFile(marker, "utf8")).trim() === SDK_SHA256) return sdkRoot;
  try {
    if (process.env.MARTLET_LIVE2D_OFFLINE === "1") throw new Error("MARTLET_LIVE2D_OFFLINE=1 disables the SDK download.");
    await mkdir(vendorRoot, { recursive: true });
    const zip = path.join(vendorRoot, `CubismSdkForWeb-${SDK_VERSION}.zip`);
    let bytes = await exists(zip) ? await readFile(zip) : undefined;
    if (!bytes || createHash("sha256").update(bytes).digest("hex") !== SDK_SHA256) {
      console.log(`Downloading Live2D Cubism SDK for Web ${SDK_VERSION} from ${SDK_URL}`);
      const response = await fetch(SDK_URL);
      if (!response.ok) throw new Error(`Live2D SDK download failed: HTTP ${response.status}.`);
      bytes = Buffer.from(await response.arrayBuffer());
      const actual = createHash("sha256").update(bytes).digest("hex");
      if (actual !== SDK_SHA256) throw new Error(`Live2D SDK SHA-256 mismatch: expected ${SDK_SHA256}, got ${actual}.`);
      await writeFile(zip, bytes);
    }
    await rm(sdkRoot, { recursive: true, force: true });
    extract(zip, vendorRoot);
    for (const file of ["Core/live2dcubismcore.min.js", "Framework/src/live2dcubismframework.ts",
      "Samples/Resources/Hiyori/Hiyori.model3.json"]) {
      if (!await exists(path.join(sdkRoot, file))) throw new Error(`Live2D SDK archive is missing ${file}.`);
    }
    await writeFile(marker, SDK_SHA256 + "\n");
    await rm(zip, { force: true });
    return sdkRoot;
  } catch (error) {
    if (required) throw error;
    console.warn(`Live2D SDK unavailable; building without the bundled Live2D runtime and Hiyori. ${error.message}`);
    return null;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  console.log(await ensureSdk({ required: true }));
}
