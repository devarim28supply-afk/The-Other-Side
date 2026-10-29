// Writes one room pass for the friend zip. The account secret is read on this
// PC and is not written to the output file.
import fs from "node:fs";
import { AccessToken } from "livekit-server-sdk";

const out = process.argv[2];
const identity = process.argv[3] || "tos-friend";
const who = process.argv[4] || "Friend";
if (!out) process.exit(2);

const files = [
  "D:/Projects/CamDisplay/.env.local",
  "D:/Projects/Digital-House-App/.env.local",
];
const env = {};
for (const file of files) {
  if (!fs.existsSync(file)) continue;
  for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
    const m = line.match(/^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/);
    if (!m || env[m[1]]) continue;
    env[m[1]] = m[2].replace(/^["']|["']$/g, "");
  }
}
const key = env.LIVEKIT_API_KEY;
const secret = env.LIVEKIT_API_SECRET;
const url = env.LIVEKIT_URL;
if (!key || !secret || !url) process.exit(3);

const at = new AccessToken(key, secret, { identity, name: who, ttl: "720h" });
at.addGrant({
  roomJoin: true,
  room: "the-other-side",
  canPublish: true,
  canSubscribe: true,
  canPublishData: true,
  canUpdateOwnMetadata: true,
});
fs.writeFileSync(out, JSON.stringify({ url, token: await at.toJwt() }));
console.log("pass written");
