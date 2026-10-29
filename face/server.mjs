// Local face server. Tokens come from the LiveKit account already used by
// CamDisplay and Digital House. The room is only "the-other-side", so a game
// session does not publish into the house or the 3D room.
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { AccessToken } from "livekit-server-sdk";

const PORT = 5191;
const ROOM = "the-other-side";
const FEED = process.env.TOS_FEED || "D:\\Tools\\tos-reader\\soundfeed.txt";
const PUBLIC = path.join(import.meta.dirname, "public");
const LIVEKIT_DIST = path.join(import.meta.dirname, "node_modules", "livekit-client", "dist");

const ENV_FILES = [
  path.join(import.meta.dirname, ".env.local"),
  "D:\\Projects\\CamDisplay\\.env.local",
  "D:\\Projects\\Digital-House-App\\.env.local",
];

function loadEnv() {
  const env = {};
  for (const file of ENV_FILES) {
    if (!fs.existsSync(file)) continue;
    for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
      const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/);
      if (!m || env[m[1]]) continue;
      env[m[1]] = m[2].replace(/^["']|["']$/g, "");
    }
  }
  return env;
}

const env = loadEnv();
const livekit = {
  key: env.LIVEKIT_API_KEY || "",
  secret: env.LIVEKIT_API_SECRET || "",
  url: env.LIVEKIT_URL || "",
};

const clients = new Set();
let feedPos = 0;
let carry = "";

function sendLine(line) {
  if (!line) return;
  const packet = `data: ${line}\n\n`;
  for (const res of clients) res.write(packet);
}

function readFeed() {
  let st;
  try { st = fs.statSync(FEED); } catch { return; }
  if (st.size < feedPos) { feedPos = 0; carry = ""; }
  if (st.size === feedPos) return;
  const fd = fs.openSync(FEED, "r");
  try {
    const len = st.size - feedPos;
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, feedPos);
    feedPos = st.size;
    const text = carry + buf.toString("utf8");
    const lines = text.split(/\n/);
    carry = lines.pop() ?? "";
    for (const line of lines) sendLine(line.replace(/\r$/, ""));
  } finally {
    fs.closeSync(fd);
  }
}

setInterval(readFeed, 50);

function contentType(file) {
  if (file.endsWith(".html")) return "text/html; charset=utf-8";
  if (file.endsWith(".js") || file.endsWith(".mjs")) return "text/javascript; charset=utf-8";
  if (file.endsWith(".css")) return "text/css; charset=utf-8";
  return "application/octet-stream";
}

function safeFile(root, urlPath) {
  const file = path.normalize(path.join(root, urlPath));
  if (!file.startsWith(root)) return null;
  return file;
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url || "/", "http://127.0.0.1");
  if (url.pathname === "/api/health") {
    const ok = Boolean(livekit.key && livekit.secret && livekit.url);
    res.writeHead(ok ? 200 : 500, { "content-type": "application/json" });
    res.end(JSON.stringify({ ok, room: ROOM }));
    return;
  }
  if (url.pathname === "/api/token") {
    if (!livekit.key || !livekit.secret || !livekit.url) {
      res.writeHead(500, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: "LiveKit is not configured in CamDisplay or Digital House .env.local" }));
      return;
    }
    const identity = url.searchParams.get("identity") || "";
    const name = url.searchParams.get("name") || "Other Side";
    if (!/^[\w-]{8,80}$/.test(identity)) {
      res.writeHead(400, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: "identity is required" }));
      return;
    }
    const at = new AccessToken(livekit.key, livekit.secret, { identity, name, ttl: "6h" });
    at.addGrant({
      roomJoin: true,
      room: ROOM,
      canPublish: true,
      canSubscribe: true,
      canPublishData: true,
      canUpdateOwnMetadata: true,
    });
    res.writeHead(200, { "content-type": "application/json", "cache-control": "no-store" });
    res.end(JSON.stringify({ token: await at.toJwt(), url: livekit.url }));
    console.log("token minted");
    return;
  }
  if (url.pathname === "/feed") {
    res.writeHead(200, {
      "content-type": "text/event-stream",
      "cache-control": "no-cache",
      connection: "keep-alive",
    });
    res.write(": ok\n\n");
    clients.add(res);
    req.on("close", () => clients.delete(res));
    return;
  }
  if (url.pathname.startsWith("/livekit/")) {
    const file = safeFile(LIVEKIT_DIST, url.pathname.slice("/livekit/".length));
    if (!file || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
      res.writeHead(404);
      res.end();
      return;
    }
    res.writeHead(200, { "content-type": contentType(file) });
    fs.createReadStream(file).pipe(res);
    return;
  }
  const rel = url.pathname === "/" ? "index.html" : url.pathname.slice(1);
  const file = safeFile(PUBLIC, rel);
  if (!file || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.writeHead(404);
    res.end();
    return;
  }
  res.writeHead(200, { "content-type": contentType(file), "cache-control": "no-store" });
  fs.createReadStream(file).pipe(res);
});

server.on("error", (err) => {
  if (err.code === "EADDRINUSE") {
    console.log("face server already running");
    process.exit(0);
  }
  console.error(err.message);
  process.exit(1);
});

server.listen(PORT, "127.0.0.1", () => {
  console.log(`face server http://127.0.0.1:${PORT} room ${ROOM}`);
  console.log(livekit.url ? "LiveKit account found" : "LiveKit account missing");
});
