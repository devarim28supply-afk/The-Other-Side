// ghost_lines.raw.json (Whisper, prompted with the file name) -> ghost_lines.json (what the captioner snaps to).
// Whisper often just echoed the file-name prompt ("IAmHere 01"); those lines are rebuilt from the name instead.
// Only ghosts' words are kept: no grunts/laughs, no narrator voiceovers, no one- or two-word lines (players say them too).
const fs = require("fs");
const raw = JSON.parse(fs.readFileSync(__dirname + "/ghost_lines.raw.json", "utf8"));

const skip = /Grunt|Laugh|^Action |Crying|Psst|ThroatGrowl|Wind FX|Whisper|^Kill|^Die|GhostAudio Die|^No( \d+|\d)?$|^Yes$|^Death\d?$|^Doom$|^Here$|^Where$|^Stop$|^Strength$|^Almost$|^Alright$|^Welcome$|^Hide$|^Run( \d+)?$|^Murdered$|^Victoria$|^Beware$|^Hello( \d+)?$/;
const fix = { Shouldnt: "Shouldn't", Dont: "Don't", Cant: "Can't", Wasnt: "Wasn't", Youll: "You'll", Youre: "You're",
              Im: "I'm", Wouldnt: "Wouldn't", Whats: "What's", Lets: "Let's", Fine: "Find", Prays: "Prayers", To: "to" };
const human = h => h.replace(/ \d+$/, "").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/\bI([A-Z])/g, "I $1").split(" ")
                    .map(w => fix[w] ?? w).join(" ").replace(/^SKIA /, "Skia, ").replace(/^to /, "To ");
const echoed = (t, h) => /[a-z][A-Z]|\d/.test(t) || t.replace(/[.!?]/g, "").trim() === h.replace(/ \d+$/, "");
const misheard = { "Bye": "Behind you", "I'll never go with you": "You're never gonna leave here",
                   "Sound Of Horror Voice FX Ghost Look At Me": "Look at me", "Hurt You": "I don't want to hurt you",
                   "You're but Play Thing": "You're but a plaything", "Youll Die If. You Dont Leave": "You'll die if you don't leave",
                   "Dont Talk Here. Dont Talk Here": "Don't talk here", "No, No Dont Come To Close": "No, no, don't come too close" };

const out = [], seen = new Set();
for (const l of raw) {
  if (!["Hans", "McGavin", "Orphan", "Skia", "Ghost"].includes(l.speaker) || skip.test(l.name) || !l.text) continue;
  let t = echoed(l.text, l.name) ? human(l.name) : l.text.replace(/\s+/g, " ").trim();
  t = t.replace(/[.!]+$/, "").replace(/^Hmm[,.] /, "");
  t = misheard[t] ?? t;
  // apostrophes and sentence case for display (matching ignores case and punctuation)
  t = t.split(" ").map(w => fix[w.replace(/[,.?]/g, "")] && !/^(To|Fine|Prays)$/.test(w) ? w.replace(/^\w+/, fix[w.replace(/[,.?]/g, "")]) : w).join(" ");
  t = t.replace(/^SKIA /, "Skia, ").toLowerCase().replace(/\bi\b/g, "I").replace(/\b(i'm|i'll)\b/g, m => "I" + m.slice(1))
       .replace(/\b(skia|victoria|mcgavin|hans)\b/g, m => m[0].toUpperCase() + m.slice(1));
  t = t.charAt(0).toUpperCase() + t.slice(1);
  const key = l.speaker + "|" + t.toLowerCase();
  if (seen.has(key)) continue;
  seen.add(key);
  out.push({ speaker: l.speaker, text: t, file: l.file });
}
fs.writeFileSync(__dirname + "/ghost_lines.json", JSON.stringify(out, null, 1));
for (const o of out) console.log(o.speaker.padEnd(8), o.text);
console.log(out.length, "lines");
