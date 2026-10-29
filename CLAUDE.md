# Face cameras — handoff

Read this before editing. The goal is not done. Do not tell Kaalo it is fixed until both players have looked at each other in a round.

Two people play The Other Side together (Steam app 2764750, UE 5.4, shipping exe `TheOtherSide-Win64-Shipping.exe`). Kaalo is WokeFire. Nathan is Badwolves. Each person must see the other person's live webcam on that person's face. Their own camera is published so the friend can see it, and it must not be drawn on their own screen. The game already has proximity voice. This is video only.

Private, non-commercial, both players opt in. Do not edit paks or any other game files. Do not reveal ghost type, ghost position, or unfound evidence. Do not spoof the game. SoundSpy and the sound overlay stay as they are.

## What the user last saw

Nathan could see a diamond on Kaalo's face. Kaalo saw no shape on Nathan, because Kaalo's faces window was not running. After it was started, Kaalo said the diamond does not match the real face plate: when a character turns, the picture must turn with the head the way the in-game mesh does, and the back of the head must cover it.

A mesh-matched overlay was then written and pushed as `ebc2268`. It was not seen on a second player. At the last check `plate.txt` contained only the local plate, so the window was parked off screen. Treat that change as unproven.

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

Default settled shape is diamond. Morph is 400 ms, ease-out cubic `cubic-bezier(0, 0, 0.2, 1)`. There is no auto ping-pong and no right-edge pin. Do not bring those back. The Hex and Diamond buttons are parked at `(-32000, -32000)` with `ShowInTaskbar` false. Do not bring that window back. Quit by ending `TosFace.exe` in Task Manager. There is no close button.

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

`main.lua` on disk logs `FacePlate loaded build 39`. UE4SS hot reload is off (`EnableHotReloadSystem = 0`). The game keeps the script it loaded at process start. Read the first `loaded build` line in `faceplate.log` and the process start time before assuming build 39 is live. On the evening of 2026-10-28 the running game had loaded build 38. Copy Lua to the Steam path after every Lua edit, and also to `C:\Users\Kaalo\OneDrive\Desktop\FacePlate-main.lua`. Do not tell Kaalo a new script is live while the old process is running.

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

Current draw path, unverified in game:

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
