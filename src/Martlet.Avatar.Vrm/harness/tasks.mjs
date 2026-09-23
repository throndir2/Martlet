import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { readdirSync } from "node:fs";
import { join } from "node:path";
import { build } from "esbuild";
const require = createRequire(import.meta.url);
const root = fileURLToPath(new URL("../", import.meta.url));
function run(args) {
  const result = spawnSync(process.execPath, args, { cwd: root, stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
const task = process.argv[2];
if (task === "build" || task === "test") run([require.resolve("typescript/bin/tsc"), "-p", "tsconfig.json"]);
if (task === "test") run(["--test", ...readdirSync(join(root, "dist", "test")).filter(name => name.endsWith(".test.js")).map(name => join(root, "dist", "test", name))]);
if (task === "bundle" || task === "dev")
  await build({ absWorkingDir: root, entryPoints: ["harness/main.ts"], bundle: true, format: "esm", outfile: "public/app.js", legalComments: "inline" });
if (task === "dev") await import("./server.mjs");
if (!["build", "test", "bundle", "dev"].includes(task)) throw new Error("Unknown task.");
