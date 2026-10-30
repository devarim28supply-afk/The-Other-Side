# Face cameras — handoff

Read this before editing. The goal is not done. Do not tell Kaalo it is fixed until both players have looked at each other in a round.

Two people play The Other Side together (Steam app 2764750, UE 5.4, shipping exe `TheOtherSide-Win64-Shipping.exe`). Kaalo is WokeFire. Nathan is Badwolves. Each person must see the other person's live webcam on that person's face. Their own camera is published so the friend can see it, and it must not be drawn on their own screen. The game already has proximity voice. This is video only.

Private, non-commercial, both players opt in. Do not edit paks or any other game files. Do not reveal ghost type, ghost position, or unfound evidence. Do not spoof the game. SoundSpy and the sound overlay stay as they are.

## Where this stopped

Kaalo said to stop after the last bug report. Do not restart TosFace, do not kill the game, and do not rebuild the zip unless he asks. Re-check processes before you trust the pids below. They were true on the night of 2026-10-28.

Two bugs are open:

1. The webcam picture is not on. A screen capture showed a black diamond on Badwolves' face, nameplate `96 Badwolves`. The diamond was in the right place while he faced the camera. The inside was solid black. Both people were in LiveKit room `the-other-side` and neither had published a video track: `tos-woke` (name WokeFire, no attributes, 0 tracks) and `tos-friend` (name Friend, attribute `tosId=257`, `tosName=Badwolves`, 0 tracks). Kaalo's WebView was connected to LiveKit and to `127.0.0.1:5192`. Health returned ok. `openCamera` retries for about 40 seconds and then gives up. The "camera busy" text is on the hidden panel, so nobody sees it. `share.log` had no new `face` lines after 18:50, which matches no video frames being posted. Nathan's missing track is the picture that belongs on that diamond. Kaalo's missing track is what Nathan would see.

2. The overlay does not cover two screens. It stays on The Other Side's screen only. `ShareHex.OnEnum` keeps the one largest visible game window and projects into that client rect. The game window at that moment was `-2560,-370`, 2560x1440, which is the left monitor. The diamond window was inside it, about `-1252,-145`, 424x590. This was not investigated further. Kaalo stopped the work there.

What is already true from that same look:

- The running game had loaded `FacePlate loaded build 39` at 21:17. Self id was 257, then a few seconds later 258. The pose file then had `M 258` and the other plate `257`, which matched Nathan's `tosId`.
- Kaalo's own camera was not drawn over his view. The black diamond was the other player.
- Turning so the picture matches the mesh, and the back of the head covering it, was not re-checked after the diamond was seen face-on.
- The Hex and Diamond button window is hidden in `PanelForm.SetVisibleCore` in the working tree. That change is local until this handoff commit. Parking the form at `(-32000,-32000)` was not enough: Windows put it back on Nathan's one-monitor desktop. A desktop zip was rebuilt after the hide. If Nathan still sees the buttons, he is on an older zip. The buttons do not control the shape.

Earlier, before this look: Nathan had seen a flat screen diamond on Kaalo, and Kaalo had seen no shape because his faces window was not running. That part is superseded by the capture above.

## Done when

- Looking at the other player shows their webcam on their face.
- The outline and the picture are the face plate from `hexArrays` in `ue4ss/FacePlate/Scripts/main.lua`, projected with that head bone. Yaw, pitch, and roll change the shape the way that mesh would.
- Right Arrow still morphs that same card between diamond and hex. The morph is on the face plane, then projected. It is not a new shape drawn flat to the screen.
- When the face points away from the camera, the picture is gone. The back of the head covers it. Do not draw the card on the back of the skull. The Lua mesh is double-sided; the overlay must not copy that.
- Neither screen shows that person's own camera. A first-person plate on your own head fills the view. That bug already shipped once.
- If the other player is not in front of the camera, the picture stays off. No leftover window.
- Diamond face tracking still feels like the confirmed M2A behavior when the other person is facing the camera: the diamond roams, then the picture pans, no vertical move, no zoom. Losing the face holds, then recenters. It must not hide the friend's picture.

## Controls that already work

Right Arrow, `GetAsyncKeyState(0x27)`, works while the game is focused. The mouse does not affect it.

- Tap (key up before 1 second): hex for 20 seconds, then diamond. A tap during those 20 seconds starts them over.
- Hold at least 1 second: hex locks. The lock fires at the 1 second mark while the key is still down.
- Tap while locked: diamond.

Default settled shape is diamond. Morph is 400 ms, ease-out cubic `cubic-bezier(0, 0, 0.2, 1)`. There is no auto ping-pong and no right-edge pin. Do not bring those back. The Hex and Diamond button window must not be shown. Parking it at `(-32000, -32000)` is not enough: Windows puts that tool window back on a one-monitor desktop, which is why Nathan still saw the buttons. `PanelForm.SetVisibleCore` keeps it hidden. Right Arrow is the only shape control. Do not show that form. Quit by ending `TosFace.exe` in Task Manager. There is no close button.

## Pieces

| Piece | Path |
| --- | --- |
| Lua source | `ue4ss/FacePlate/Scripts/main.lua` |
| Lua the game loads | `D:\Steam\steamapps\common\The Other Side\TheOtherSide\Binaries\Win64\ue4ss\Mods\FacePlate\Scripts\main.lua` |
| Lua log | `...\ue4ss\Mods\FacePlate\faceplate.log` |
| Pose file | `...\ue4ss\Mods\FacePlate\plate.txt` |
| Overlay source | `face/host/` (`TosFace.csproj`, net10.0-windows) |
| Overlay Kaalo runs | `face/host/bin/TosFace.exe`, started by scheduled task `TosFaceOnce` |
| Page source | `face/public/` (`face.js?v=15`) |
| Page the exe serves | `face/host/bin/www/` |
| Friend package script | `face/friend/make-friend-zip.ps1` |
| Desktop zip | `C:\Users\Kaalo\OneDrive\Desktop\OtherSideFaces.zip` |
| Face model | `face/host/models/face_detection_yunet_2023mar.onnx` (232589 bytes). Gitignored. Fallback `D:\Projects\M2A\assets\models\face_detection_yunet_2023mar.onnx` |
| M2A spec for shape and tracking | `D:\Projects\M2A\lib\features\webcam\` (`overlay_geometry.dart`, `shape_design.dart`, `shape_controller.dart`, `diamond_tracking.dart`) |

`main.lua` on disk logs `FacePlate loaded build 42`. UE4SS hot reload is off (`EnableHotReloadSystem = 0`). The game keeps the script it loaded at process start. Read the first `loaded build` line in `faceplate.log` and the process start time before assuming build 39 is live. A later launch the same night loaded build 39. Copy Lua to the Steam path after every Lua edit, and also to `C:\Users\Kaalo\OneDrive\Desktop\FacePlate-main.lua`. Do not tell Kaalo a new script is live while the old process is running.

Build 39 writes `M {selfPlayerId}` into `plate.txt`. Build 38 does not. The overlay skips the `M` id. With no `M` line it skips any plate whose average depth is under 50 cm, which is your own head in first person. A friend standing closer than 50 cm is also skipped until the game restarts into build 39. Restarting TosFace does not reload Lua. Restarting the game does.

## Pose file

Written about every 16 ms after an 8 second boot. Seqlock, padded to 2048 bytes. Read it with shared read/write. A torn read (`S` seq != `E` seq) must not hide the overlay.

```
S {seq}
C pitch yaw roll x y z fov
P {id} loc(3) rot(3) scale(3) localVerts(18)
M {selfId}
E {seq}
```

`P` has 29 tokens. Verts are component-local, 6 points. Head bone: +X is up, +Z is the character's right, +Y is out of the face. The live hex is `y = 18`, scale `1.35`, corners `(11,-8) (11,8) (0,14) (-11,8) (-11,-8) (0,-14)`. World size is about 30 cm tall and 38 cm wide before the actor scale. Rejected placements: `y = -18` (back of the head) and `y = 40` (a card in front of the eyes, facing the eyes). `ShareHex.TryPlate` still remaps a `y ≈ 40` card onto the face hex so an old running game sits on the face.

The mesh is hidden with `SetHiddenInGame(true, true)`. The live path does not call `refreshMedia` or `attachMedia`. `makeMirror` is never called. Leave that dead code uncalled. Do not put a webcam texture back on the in-game mesh. The picture is only the screen overlay.

TosFace finds `plate.txt` by the game exe path: `{exe dir}\ue4ss\Mods\FacePlate\plate.txt`, cached 2 seconds, fallback `D:\Tools\tos-faceplates\plate.txt`. Nathan does not have a D: drive. The pose file beside the mod is the one that matters. FaceTrack and jpeg dumps still write under `D:\Tools\tos-faceplates` and can fail silently on his PC.

## Overlay

`ShareHexForm` is layered, color-keyed, and click-through. It no longer draws the outline. `OverlayForm` is the webcam window. It must not have `WS_EX_LAYERED` or `WS_EX_TRANSPARENT`. A layered video window hides the picture. Its extended style is `TOOLWINDOW | NOACTIVATE`. `SetWindowRgn` clips the form and the WebView2 to the projected plate. Park it at -32000 when there is no plate.

Projection uses the component transform and the camera from `plate.txt`. UE rotation basis matches `FRotator`: local X/Y/Z are those axes. Horizontal FOV comes from the `C` line. Viewport is the largest visible client of `TheOtherSide-Win64-Shipping`. A vertex closer than 5 cm is rejected. Do not call `ProjectWorldLocationToScreen`. Do not move an unattached mesh with `K2_SetWorldLocation` every tick. That crashed. The plate is created with `AddComponentByClass` and attached to the `head` socket.

Current draw path. The diamond outline was seen on Badwolves' face. The picture inside it was black, and a turned head was not re-checked:

- Morph diamond and hex in the face plane (`ShareHex.TryPlate`). Diamond height matches the hex. Diamond width is height / 1.45. Vertex order is top-left, top-right, right, bottom-right, bottom-left, left. Diamond is the degenerate set top, top, right, bottom, bottom, left.
- Project those 6 points. The window is their screen bounding box, not a 16:9 card.
- Hide the plate when the outward normal (+Y, the component Y axis) points away from the camera: `(camera - faceCenter) · Y <= 0`. Own-face check on 2026-10-28 gave about -11 cm, which is correct because the first-person camera sits behind the plate. If a friend who is looking at you disappears, this sign is backwards.
- UVs map the unmirrored video onto that plane. Local +Z (their right) is u = 0. Local +X (up) is v = 0. `panFrac` and the diamond roam are shifts in that plane, multiplied by `1 - hexBlend`, so a full hex shows the whole frame and does not roam.
- `face/public/face.js` draws the remote video on a WebGL canvas with those points. The local camera is published and kept in `#sink`. It is not a `video.fill` on the stage. `POST /track?mir=0` means FaceTrack must not flip X. A missing `mir` query still flips X, for old pages.
- The OS region and the canvas both have to match. A DPI mismatch shows the right outline with the picture shifted. Check that before inventing a new projection.

`Silhouette()` in `ShareHex.cs` is the old screen-facing diamond. Nothing calls it. Do not wire it back. That shape is what Kaalo rejected.

WebView2: camera Allow, microphone Deny. Do not open a second capture while TosFace or the game already has the camera. The camera's friendly name on this PC is W4DS. Do not print `camera.url`.

One other player is the normal case. `videoFor` uses LiveKit participant attribute `tosId`, then falls back to the single remote video. `tosId` comes from the reader feed's self id. The watched plate id is the Lua PlayerId. They should match. The fallback covers one friend if the reader is down. The pose does not come from the reader. The reader is still required for `tosId`.

## Face tracking

YuNet `face_detection_yunet_2023mar.onnx`. Score floor 0.7, detect 320x240, grab width 640. Feed 1280x720. `trackSmoothing` 0.11, deadzone 22 px, diamond roam ±45 px, `diamondRatio` 1.45. Lost face holds 10 seconds, then clears the offset. `ShareHex` no longer hides the window when `DiamondHidden` is set. Do not blank the friend because tracking missed.

Samples are the raw remote frame, not the CSS or WebGL transform. The displayed remote picture is not mirrored. The published local track is the raw camera. Do not mirror it before publish.

OpenCvSharp 4.13. `FaceDetectorYN.Create` without backend/target. `Cv2.ImDecode` is BGR. Qualify `OpenCvSharp.Size`. `System.Text` is not an implicit using. Known build warnings only: WindowsBase MSB3277, unused `OverlayForm.follow` and `regionKey`, WFO0003.

## Run and build

PowerShell 5.1 has no `&&`. `$PID` is reserved. Do not start TosFace or the sound overlay with `Start-Process`. The shell kills that process when the command ends, and a `Win32_Process` create from this shell lands on session 0, which is the wrong desktop.

Restart Kaalo's overlay:

1. `dotnet build face\host\TosFace.csproj -c Release -o face\host\bin\build-out`
2. Copy `TosFace.exe`, `TosFace.dll`, `TosFace.pdb`, `TosFace.deps.json`, and `TosFace.runtimeconfig.json` onto `face\host\bin\`. Do not wipe `www`, `friend.json`, `WebView2Loader.dll`, or `runtimes`.
3. Copy `face\public\*` onto `face\host\bin\www\`. Bump the `face.js?v=` query in `index.html` and the same check in `make-friend-zip.ps1` whenever `face.js` changes.
4. `taskkill` only `TosFace.exe` in session 7. Never kill `TheOtherSide-Win64-Shipping.exe` unless Kaalo agrees.
5. `schtasks /Run /TN TosFaceOnce`

Health, with the room pass present: `http://127.0.0.1:5192/api/health` returns `{"ok":true,"room":"the-other-side"}`. Do not print the pass. `face/host/bin/friend.json` is the local room pass. It is gitignored because it lives under `bin/`. Never commit it, never paste it, never log it.

Nathan's update is a new `OtherSideFaces.zip` from `face/friend/make-friend-zip.ps1`. The script mints a room pass from the LiveKit secret in the local env files and refuses to zip if `LIVEKIT_API_SECRET` appears in the package. Do not print `friend.json`. He can replace the folder and restart `Start Other Side Faces.cmd` while the game is open. That picks up TosFace and the page. `Install FacePlate.cmd` refuses to run while the game is open. Run it only after the game is fully quit, and only to copy Lua. Do not drag the zip into Steam. Do not have him run an older installer. The zip does not include a UE4SS loader. He already has one.

## Do not

- Commit `friend.json`, `.env`, the account secret, or a room token.
- Commit the unrelated dirty files `overlay/GameFeed.cs`, `overlay/RadarElement.cs`, `reader/TosReader/Feed.cs`, `reader/TosReader/Program.cs`. Leave them untouched.
- Revive the in-game jpeg plate, ImgMedia, vidcap, or the mirror mesh.
- Add a second camera, a screen-facing silhouette, an auto shape cycle, or a right-edge pin.
- Fight an unkillable session-0 process.
- Claim the picture matches the mesh until Kaalo and Nathan have both looked.

## Camera and lag notes (2026-10-28)

- Black picture cause on Kaalo's PC: OBS (`obs64.exe`) held the webcam, so `getUserMedia` failed with `NotReadableError`. Close OBS or turn on "Deactivate when not showing" for its camera source. The page now reports the real error; TosFace writes it to `%LOCALAPPDATA%\TosFace\face.log`.
- `ShareHex.Lead` moves the projected plate ahead by `lead.txt` milliseconds (beside the exe, default 12, 0 = off) using the `T` line speed. Untested in game; tune from how the lock feels.

## In-game picture (build 41-42, 2026-10-29) - supersedes the overlay sections above

Kaalo asked to drop the screen overlay: the webcam is now drawn by the engine on the hex mesh attached to the head bone, so it cannot lag or drift.

- TosFace no longer builds `ShareHexForm` or places any window. It only joins LiveKit, publishes the local camera, and POSTs the other player's frame to `/frame?id=remote`, which `FriendHost.SaveFrame` writes as `remote.jpg` beside `main.lua` (`ShareHexForm.PlateDir()`, so it works without a D: drive).
- Lua `refreshImage` reads `remote.jpg` about every 100 ms and loads it with `KismetRenderingLibrary:ImportFileAsTexture2D`, then sets it as `SlateUI` on the plate's `Widget3DPassThrough_Opaque` instance. The plate is hidden until the first frame and again after 3 s with no new frame. The self plate stays hidden. A few `import texture failed` lines are a frame read mid-replace and are harmless.
- The pose file (`plate.txt`) is off (`POSE_FILE = false`). The `PlayerTick` hook never registers (class not loaded at script start), which is why the old pose writer ran on a free 16 ms timer.
- Build 42 adds the diamond/hex morph in Lua (`faceArrays`, `tickShape`), rules from M2A `overlay_geometry.dart` / `shape_controller.dart`: default diamond, 400 ms ease-out, picture UVs come from each vertex's place on the face plane. Right Arrow is read with `PlayerController:IsInputKeyDown` (fallback: `RegisterKeyBind`, tap only). The key changes plates on the machine where it is pressed.
- Not done: diamond face-tracking pan/roam on the mesh. Open: Kaalo reported the picture "faces the other side of the hex" (build 41); cause not confirmed. The mesh has both windings, so the back of the plate shows a mirrored picture. Ask for lettered answers before changing UVs or winding.
- Camera: another program holding the webcam (OBS on both PCs) gives `NotReadableError`; TosFace stops retrying after 40 tries.

## Build 43-46 (2026-10-28/29)

- Each player controls ONLY their own shape. Right Arrow (Lua `tickShape`, `IsInputKeyDown`) writes `shape_self.txt` beside `main.lua`; TosFace's page polls `/api/shape` and publishes it as LiveKit participant attribute `shape`; the other TosFace POSTs it to `/shape?v=` which writes `shape_remote.txt`; Lua `readRemoteShape` morphs the plate you look at. `face.js?v=18`.
- `face/host/ShapeHud.cs`: small yellow shape in the middle of the game window when YOUR shape changes (shows the shape you leave, morphs to the new one, fades). Own shape only, click-through.
- Plate facing (confirmed by Kaalo): the mesh has two coplanar windings. Only the engine-front side shows the picture, so both together gave a black plate from the front. Default is winding A only ("sa"), unmirrored, `faceArrays`. `facing.txt` beside `main.lua` overrides it live (read every second): `mu` mirror left/right, `mv` flip up/down, `sa`/`sb` one winding, `nd` normals down. No file = `sa`.
- Build 46: plate up a little (`PLATE_UP`), diamond unchanged, hex grows down to the stomach (`HEX_X`, `HEX_TALL`); the full frame is stretched over the taller hex and UVs blend with the morph. Not yet confirmed in game.
- Nathan's PC has a Claude session (Remote Control, "Nathan's PC: The Other Side Session"). Send it exact old/new Lua text to apply itself instead of re-sending files; TosFace/page changes still need the zip.
- Sound reader: `TosReader.exe` plus the overlay `OtherSideSound.exe` must both run. On Kaalo's PC scheduled tasks `TosReaderOnce` and `TosSoundOverlayOnce` start them in session 7 (a plain `start` from the shell is killed). Neither was running when the sound overlay was "invisible".

## Build 47-50 (2026-10-29)

- Hex keeps its shape: it is the old hex scaled up evenly (`HEX_SCALE = HEX_TALL / 22`, `HEX_HALFW`), 39 tall to reach the stomach, wider as it gets taller, so the picture is not stretched. Diamond unchanged. Plate floats `plateOut` (default 14, was 18) in front of the face; `facing.txt` word `out12` etc. tunes it live.
- Face tracking on the diamond (M2A `diamond_tracking.dart`): TosFace tracks the OTHER player's face in their camera frames (`face.js` samples the remote video, `FaceTrack.Publish` writes `track.txt` "dx pan" in 1280-wide feed px beside `main.lua`); Lua `trackedDiamond` slides the diamond window over fixed footage (UV follows the moved vertex) and pans the footage, clamped inside the frame. Hex ignores it (blend).
- The black back of the plate is hidden: `gateBehind` hides the whole plate when `(camera - face centre) . plate +Y <= 0`; `applyVisible` combines it with the 3 s no-frame hide.
- Sound reader: the friend zip now ships the sound overlay (`overlay\`, trimmed copy of `D:\Tools\tos-reader\overlay`) with a safe config from `face/friend/overlay-config.mjs` (feed `../soundfeed.txt`, Device auto, Speak/SetDefaultMic/UseKokoro/Whisper off). `Start Other Side Faces.cmd` starts reader + face + overlay. PowerShell 5.1 cannot parse the overlay config (keys `BOOM`/`Boom` differ only by case), so edit it with node.
- When editing Lua or JSON through shell strings, backslashes get eaten (`"\track.txt"` became a tab). Use the editor tool, and check `grep -n '"[^"]*\[a-z]'`.

## Build 51-52: Up and Left Arrow cameras

- **Up Arrow (build 51):** both players' hex cameras slide in from the right edge, upper right, stacked (mine on top, theirs below), 5 s, then out. Lua writes `slide_self.txt` (new number per press); the page publishes it as LiveKit attribute `slide`; the other host writes `slide_remote.txt` (`/slide?v=`). `face/host/SlideHud.cs` slides on either file changing. Needs `local.jpg` (`/frame?id=local`, my own camera).
- **Left Arrow (build 52):** my own hex camera, long and wide (96% of the screen width, aspect 1.9), 6 s, my screen only. Lua writes `big_self.txt`; `face/host/BigHud.cs` draws it; pressing again closes early.
- Camera pictures are cropped to the hex shape, never stretched. `face.js?v=22`, `local.jpg` is 640x360.
- Friend zip now carries `overlay\ghost_lines.json` and `overlay\evidence_clips.txt`, with `WhisperScript` set to `captions_server.py` (relative), so Nathan's overlay loads the ghost lines.
- Never run `Get-Process dotnet | Stop-Process` here: it took the reader and sound overlay down with it. Restart them with `TosReaderOnce` and `TosSoundOverlayOnce`.
