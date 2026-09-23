import { createServer } from "node:http";
import { readFile } from "node:fs/promises";

const files = new Map([
  ["/", ["index.html", "text/html; charset=utf-8"]],
  ["/app.js", ["app.js", "text/javascript"]],
  ["/sdk.js", ["sdk.js", "text/javascript"]],
  ["/core.js", ["core.js", "text/javascript"]],
]);
const origin = "http://127.0.0.1:4184";
const server = createServer(async (request, response) => {
  if (request.headers.host !== "127.0.0.1:4184" || request.method !== "GET") {
    response.writeHead(403).end("Local GET requests only.");
    return;
  }
  const file = files.get(request.url);
  if (!file) { response.writeHead(404).end("Not found."); return; }
  try {
    const data = await readFile(new URL(`../dev-dist/${file[0]}`, import.meta.url));
    response.writeHead(200, {
      "Content-Type": file[1], "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
      "Content-Security-Policy": "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'none'; img-src blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'",
      "Permissions-Policy": "camera=(), microphone=(), geolocation=(), autoplay=()",
    }).end(data);
  } catch (error) {
    console.error("DEV_ASSET_MISSING", error.message);
    response.writeHead(503, { "Content-Type": "text/plain" }).end("MISSING_LOCAL_BUILD: run build:dev after supplying the licensed SDK.");
  }
});
server.on("error", error => { console.error(error); process.exitCode = 1; });
server.listen(4184, "127.0.0.1", () => console.log(`Local development only: ${origin}; Ctrl+C to stop.`));
