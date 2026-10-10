// Writes the sound groups + colors (Kaalob, 2026-10-09: Ghost / Voice / Physical / Device / Object / Environment)
// into the overlay's config.json. Usage: node groups.js <config.json>
const fs = require("fs");
const path = process.argv[2];
const cfg = JSON.parse(fs.readFileSync(path, "utf8").replace(/^﻿/, ""));

// Kaalob's picks (2026-10-09): Voice yellow + outline, Physical white + outline, Device green, Object orange, Environment white
cfg.GroupColors = {
  Ghost: "#FF5082", Voice: "#FFE135", Physical: "#FFFFFF",
  Device: "#5BE35B", Object: "#FF9628", Environment: "#FFFFFF",
};
cfg.GroupOutline = ["Voice", "Physical"];
// Kaalob (2026-10-09): all sounds 40 %, ghosts 70 %, live captions 70 %
cfg.SoundOpacity = 0.4;
cfg.GroupOpacity = { Ghost: 0.7 };
cfg.CaptionOpacity = 0.7;
// Kaalob: no "Voice" sound rows, people talking is the yellow caption instead (ghost lines are pink captions)
cfg.HideGroups = ["Voice"];

// general sound detector categories
cfg.CategoryGroups = {
  ghost: "Ghost", voice: "Voice", move: "Physical", door: "Physical", object: "Physical",
  alert: "Device", music: "Object", env: "Environment", other: "Environment",
};

const groups = {
  Ghost: ["GHOST HUNTING", "HEARTBEAT", "GHOST ATTACK", "BANSHEE SCREAM", "SCREAM", "Laugh", "WHISPER", "GROWL",
          "GHOST VOICE", "Crying", "Humming", "EATING / CUTTING", "GUNSHOT"],
  Voice: ["Voice"],   // players talking: never ghost-colored
  Physical: ["Floor creak", "Footsteps", "Door close", "Door rattle", "Door open / creak", "KNOCK", "CABINETS BANGING",
             "Crash / clatter", "Scratching", "Item thrown / dropped", "BOOM", "Boom", "Bang", "Explosion"],
  Device: ["Switch / breaker", "PHONE RINGING", "CAR ALARM", "ALARM", "Radio / TV", "Microwave", "Camera", "Typing", "Blender"],
  Object: ["PIANO", "Candle", "Bell", "Toilet flush", "Water / tap", "Rocking chair / horse", "Cart squeak", "BALLOON POP",
           "Church bell", "Wind chime", "Chime", "Doorbell", "Whistle", "MUSIC BOX", "Faucet", "Sink", "Bathtub", "Clock", "Tick"],
  Environment: ["LIGHTS FLICKER"],
};
cfg.LabelGroups = {};
for (const [g, labels] of Object.entries(groups)) for (const l of labels) cfg.LabelGroups[l] = g;
// the trained model's labels sit in the "game" category; make sure every one of them has a group
const head = process.argv[3] && fs.existsSync(process.argv[3]) ? JSON.parse(fs.readFileSync(process.argv[3], "utf8")) : [];
const missing = head.filter(l => !l.startsWith("_") && !cfg.LabelGroups[l]);
if (missing.length) console.log("NO GROUP:", missing.join(", "));

fs.writeFileSync(path, JSON.stringify(cfg, null, 2));
console.log(`groups written: ${Object.keys(cfg.LabelGroups).length} labels, ${Object.keys(cfg.GroupColors).length} colors`);
