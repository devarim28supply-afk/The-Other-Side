// Writes the sound overlay config for the friend zip: Kaalo's settings with the
// things that only make sense on his PC turned off. Usage: node overlay-config.mjs <src> <dst>
import fs from "node:fs";
const [src, dst] = process.argv.slice(2);
const cfg = JSON.parse(fs.readFileSync(src, "utf8"));
Object.assign(cfg, {
  GameFeedPath: "../soundfeed.txt",
  // The overlay reads ghost_lines.json and evidence_clips.txt from this file's folder.
  WhisperScript: "captions_server.py",
  Device: "auto",
  Speak: false,
  UseKokoro: false,
  SetDefaultMic: false,
  WhisperCaptions: false,
  LiveCaptions: false,
});
fs.writeFileSync(dst, JSON.stringify(cfg, null, 2));
const back = JSON.parse(fs.readFileSync(dst, "utf8"));
if (back.SetDefaultMic || back.Speak || back.GameFeedPath !== "../soundfeed.txt") {
  console.error("overlay config is not the friend config");
  process.exit(1);
}
console.log("overlay config written");
