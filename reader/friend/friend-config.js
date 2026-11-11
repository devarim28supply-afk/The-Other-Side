// Makes the friend package's overlay config from Kaalob's live config: same look (groups, colors, ring),
// the sounds on screen plus live subtitles (offline CPU Whisper). No voice, no mic changes, no hotkeys that the game uses.
// node friend-config.js <source config.json> <target config.json>
const fs = require('fs');
const [src, dst] = process.argv.slice(2);
const cfg = JSON.parse(fs.readFileSync(src, 'utf8').replace(/^﻿/, ''));

Object.assign(cfg, {
  GameFeedPath: '..\\soundfeed.txt',                 // written by reader\TosReader.exe next to this folder
  WhisperScript: 'captions\\captions_server.py',     // only its folder is used: captions\ghost_lines.json + evidence_clips.txt
  Device: 'auto',
  // Live subtitles of what the game plays (ghost voice, spirit box, teammates): portable CPU Whisper that
  // make-friend-zip.ps1 puts in overlay\captions (python\ + model\small.en). Paths are relative to overlay\.
  WhisperCaptions: true,
  WhisperPython: 'captions\\python\\python.exe',
  WhisperModel: 'model\\small.en',   // relative to the captioner's own folder (overlay\captions)
  WhisperDevice: 'cpu',
  OwnVoiceCaptions: false,
  Speak: false,
  UseKokoro: false,
  SetDefaultMic: false,
  HelperEnabled: false,
  SummaryEnabled: false,
  MouseWheelMenu: false,
  WheelMiddleClick: false,
  SayHomeKey: false,
  SayF6: false,
  SayMouseButton: 0,
  HuntKey: false,
  IntroLine: '',
  IntroLine2: '',
  KokoroPython: '', KokoroScript: '', EdgeTtsPython: '',
  MicDevice: '', SpeakDevice: '', OwnVoiceDevice: '',
});

fs.writeFileSync(dst, JSON.stringify(cfg, null, 2));
console.log('friend config written: ' + dst);
