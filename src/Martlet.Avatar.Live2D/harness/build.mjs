import { copyFile, mkdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { ensureSdk } from "../scripts/sdk.mjs";

const root = fileURLToPath(new URL("../", import.meta.url));
const sdkRoot = await ensureSdk({ required: true });
await mkdir(new URL("../dev-dist/", import.meta.url), { recursive: true });
await build({
  absWorkingDir: root,
  entryPoints: { app: "harness/app.ts", sdk: "harness/sdk-local.ts" },
  outdir: "dev-dist", bundle: true, platform: "browser", format: "esm", target: "es2022",
  sourcemap: false, logLevel: "warning",
});
await copyFile(path.join(sdkRoot, "Core", "live2dcubismcore.min.js"), new URL("../dev-dist/core.js", import.meta.url));
await copyFile(new URL("index.html", import.meta.url), new URL("../dev-dist/index.html", import.meta.url));
console.log("Built local-only dev-dist from the pinned official SDK download. Contains licensed SDK code; do not publish it separately.");
