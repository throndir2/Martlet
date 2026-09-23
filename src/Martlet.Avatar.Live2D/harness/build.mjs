import { access, copyFile, mkdir } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { FRAMEWORK_REVISION } from "../dist/sdk.js";

const root = fileURLToPath(new URL("../", import.meta.url));
const core = new URL("../local-sdk/Core/live2dcubismcore.min.js", import.meta.url);
const framework = fileURLToPath(new URL("../local-sdk/Framework/", import.meta.url));
try {
  await access(core);
  await access(new URL("../local-sdk/Framework/src/live2dcubismframework.ts", import.meta.url));
} catch (error) {
  throw new Error("MISSING_LOCAL_SDK: Supply your lawfully obtained SDK 5-r.4 Core script in local-sdk/Core and the pinned official Framework checkout in local-sdk/Framework. No downloads or license acceptance are automated.", { cause: error });
}
const revision = execFileSync("git", ["-C", framework, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
const dirty = execFileSync("git", ["-C", framework, "status", "--porcelain", "--untracked-files=normal"], { encoding: "utf8" }).trim();
if (revision !== FRAMEWORK_REVISION || dirty) {
  throw new Error(`SDK_VERSION_MISMATCH: local-sdk/Framework must be an unmodified checkout of ${FRAMEWORK_REVISION}.`);
}
await mkdir(new URL("../dev-dist/", import.meta.url), { recursive: true });
await build({
  absWorkingDir: root,
  entryPoints: { app: "harness/app.ts", sdk: "harness/sdk-local.ts" },
  outdir: "dev-dist", bundle: true, platform: "browser", format: "esm", target: "es2022",
  sourcemap: false, logLevel: "warning",
});
await copyFile(core, new URL("../dev-dist/core.js", import.meta.url));
await copyFile(new URL("index.html", import.meta.url), new URL("../dev-dist/index.html", import.meta.url));
console.log("Built local-only dev-dist. Contains licensed SDK code; do not publish or redistribute.");
