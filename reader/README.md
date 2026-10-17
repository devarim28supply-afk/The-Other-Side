# Other Side Reader (outside the game)

Reads the running game's memory from a separate program, **read-only**, and writes the same
`soundfeed.txt` lines as the in-game SoundSpy mod. Nothing is injected: no DLL, no hooks, nothing
written to the game's memory or folder. The process handle is opened with `PROCESS_VM_READ` +
`PROCESS_QUERY_INFORMATION` only, so Windows refuses any write.

The in-game SoundSpy mod and the original Other Side Sound overlay are separate and stay as they are.

## Install / run

- `install-tos-reader.cmd` builds the reader into `D:\Tools\tos-reader\bin`, copies the overlay to
  `D:\Tools\tos-reader\overlay` (its `GameFeedPath` points at `D:\Tools\tos-reader\soundfeed.txt`) and
  makes the desktop shortcut **Other Side Reader**. Run it again to refresh the overlay copy after
  changing the original overlay's settings.
- The shortcut runs `start-tos-reader.cmd`: the reader (minimized console; waits for the game) + the overlay copy.
- `uninstall-tos-reader.cmd` stops the reader and removes the shortcut.

## How it finds things (UE 5.4)

| What | How |
|---|---|
| Object table (GUObjectArray) | Scanned in the exe image by shape: chunked array with Num <= Max and matching chunk counts. |
| Name table (FNamePool) | Scanned by shape: block 0 starts with `None`, then `ByteProperty`. |
| Field offsets | The game's own reflection: UStruct `ChildProperties` (0x50) -> FField `Next` 0x18, `Name` 0x20, FProperty `Offset_Internal` 0x44, FBoolProperty byte offset / mask at 0x71 / 0x73. |
| Listener | LocalPlayer.PlayerController -> PlayerCameraManager.CameraCachePrivate.POV (Location, Rotation.Yaw). |
| Every playing sound | The audio device's active-sound list (`TArray<FActiveSound*>`), found once per game start (~20 s, three passes over the heap): a playing AudioComponent's USoundBase -> records holding it at +0x18 -> pointer lists holding those records -> the TArray header. Record: sound +0x18, owner actor object index +0x40, position +0x250. |

Modes for exploring: `TosReader scan [words]`, `props <Class>`, `audio`, `hunt [sound]`, `lists <addr...>`,
`owner <addr>`, `recs <listHeader>`, `raw <Class> [prop]`.
