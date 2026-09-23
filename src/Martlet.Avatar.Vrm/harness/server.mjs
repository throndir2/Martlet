import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
const files = new Map([
  ["/", ["index.html", "text/html; charset=utf-8"]],
  ["/app.js", ["app.js", "text/javascript; charset=utf-8"]],
  ["/style.css", ["style.css", "text/css; charset=utf-8"]],
]);
const server = createServer(async (request, response) => {
  const entry = files.get(request.url);
  if (request.method !== "GET" || !entry || request.headers.host !== "127.0.0.1:4178") {
    response.writeHead(404).end(); return;
  }
  try {
    const body = await readFile(new URL(`../public/${entry[0]}`, import.meta.url));
    response.writeHead(200, {
      "Content-Type": entry[1], "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff",
      "Content-Security-Policy": "default-src 'none'; script-src 'self'; style-src 'self'; img-src blob:; connect-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
      "Permissions-Policy": "microphone=(), camera=(), geolocation=()",
    }).end(body);
  } catch (error) {
    console.error(error);
    response.writeHead(500).end("Local harness file unavailable; run bundle first.");
  }
});
server.on("error", error => { console.error(error); process.exitCode = 1; });
server.listen(4178, "127.0.0.1", () => console.log("Offline VRM harness: http://127.0.0.1:4178"));
