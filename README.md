# The Other Side

Deaf-accessibility tools for **The Other Side** (Steam app 2764750, Unreal Engine 5.4), the same
setup as the Phasmophobia overlay (PhasmoSound): see every game sound on screen, read live captions
of everything said, and talk through a computer voice.

The live copy that gets played with is `D:\Tools\tos-sound-overlay` (not in git: it holds the
game's own audio).

## What is here

| Folder | What it does |
|---|---|
| `tools/TosAudioExtract` | Pulls every sound out of `TheOtherSide-Windows.pak` (UE 5.4, Oodle, not encrypted). The game has unversioned properties and no `.usmap`, so SoundWaves are not parsed as objects: the Bink Audio (`ABEU`) first chunk is cut out of the `.uexp`, joined with the `.ubulk` stream and decoded by `vgmstream-cli` (D:\Tools\vgmstream). Result: 596 clips in `gameclips\` + `index.tsv`. The 22 weather loops (thunder, rain, wind) fail (they have a different chunk layout) and are not needed. |
| `captions/transcribe_lines.py` | Whisper `medium.en` transcript of every ghost voice line (Hans, McGavin, Orphan, Skia, ghost responses, whispers) -> `ghost_lines.json`. |
| `captions/captions_server.py` | The overlay's Whisper captioner plus ghost-line snapping: a caption that is close to a real ghost line is replaced by the exact line and tagged `GHOST (Orphan): ...`. One- and two-word lines (yes, no, run) are never tagged because players say them too. |
| `overlay/` | **Other Side Sound** (OtherSideSound.exe), a fork of the PhasmoSound overlay for this game: sound groups + colors (Ghost, Voice, Physical, Device, Object, Environment), outlines, opacity, partial-caption support, WhisperPython. `groups.js` writes the group settings into config.json. |
| `train/labels.json` | Path rules that name each game sound (GHOST VOICE, GHOST HUNTING, HEARTBEAT, Door close, Footsteps, PIANO, ...). `_background` / `_equipment` are learned but never shown. |
| `train/dump_clips.py`, `train/train_cnn.py` | Same log-mel CNN trainer as PhasmoTrain: writes `head.onnx` + `head_labels.json` for the overlay's `app\model\`. |

## Rebuild from scratch

```
cd tools\TosAudioExtract
dotnet run -c Release -- extract D:\Tools\tos-sound-overlay\gameclips
cd ..\..\captions
D:\Tools\kokoro-tts\.venv\Scripts\python.exe transcribe_lines.py D:/Tools/tos-sound-overlay/gameclips ghost_lines.raw.json medium.en
cd ..\train
D:\Tools\phasmo-sound-overlay\PhasmoTrain\.venv\Scripts\python.exe dump_clips.py D:/Tools/tos-sound-overlay/gameclips D:/Tools/tos-sound-overlay/train-data
D:\Tools\phasmo-sound-overlay\PhasmoTrain\.venv\Scripts\python.exe train_cnn.py D:/Tools/tos-sound-overlay/train-data D:/Tools/tos-sound-overlay/train-out 4000
```

Never commit the game's audio (`gameclips/`, `train-data/`): it belongs to the game's developer.
