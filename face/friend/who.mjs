import fs from "node:fs";
import { RoomServiceClient } from "livekit-server-sdk";

const files = ["D:/Projects/CamDisplay/.env.local", "D:/Projects/Digital-House-App/.env.local"];
const env = {};
for (const file of files) {
  if (!fs.existsSync(file)) continue;
  for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/);
    if (!m || env[m[1]]) continue;
    env[m[1]] = m[2].replace(/^["']|["']$/g, "");
  }
}
const host = env.LIVEKIT_URL.replace(/^wss:/, "https:");
const svc = new RoomServiceClient(host, env.LIVEKIT_API_KEY, env.LIVEKIT_API_SECRET);
const people = await svc.listParticipants("the-other-side");
if (people.length === 0) console.log("nobody in the room");
for (const p of people) {
  console.log(`${p.identity} name=${p.name || ""} attrs=${JSON.stringify(p.attributes || {})} meta=${JSON.stringify(p.metadata || "")} perm=${JSON.stringify(p.permission || {})}`);
}
