#!/usr/bin/env node
// Local-only download server for manual plugin installation tests.
// Usage: node tools/plugin-test-server.mjs [port]
// Serves artifacts/plugins over http://127.0.0.1:5765/ (the desktop default).

import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "artifacts", "plugins");
const port = Number(process.argv[2] ?? process.env.REMOTECLI_PLUGIN_PORT ?? 5765);

const types = {
  ".lspkg": "application/octet-stream",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8"
};

const server = createServer(async (request, response) => {
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.writeHead(405, { Allow: "GET, HEAD" }).end();
    return;
  }

  const name = decodeURIComponent(new URL(request.url ?? "/", "http://127.0.0.1").pathname).replace(/^\/+/, "");
  if (!name || name.includes("/") || name.includes("\\") || name.startsWith(".")) {
    response.writeHead(404).end("not found");
    return;
  }

  const file = path.join(root, name);
  try {
    const info = await stat(file);
    if (!info.isFile()) throw new Error("not a file");
    const body = await readFile(file);
    response.writeHead(200, {
      "Content-Type": types[path.extname(name).toLowerCase()] ?? "application/octet-stream",
      "Content-Length": body.length,
      "Cache-Control": "no-store"
    });
    if (request.method === "HEAD") response.end();
    else response.end(body);
    console.log(`${new Date().toISOString()} ${request.method} ${name} ${body.length} bytes`);
  } catch {
    response.writeHead(404).end("not found");
  }
});

server.listen(port, "127.0.0.1", () => {
  console.log(`Remote CLI plugin test server: http://127.0.0.1:${port}/ (root: ${root})`);
  console.log(`Install URL example: http://127.0.0.1:${port}/code-highlight.lspkg`);
});
