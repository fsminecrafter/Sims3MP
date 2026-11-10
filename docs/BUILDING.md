# Building and installing the client

How the parts fit together:

```
 The Sims 3 (each player)                              anywhere, no game needed
+--------------------------+   loopback   +------------------+  TCP + UDP  +----------------+
| Sims3MP script mod (C#)  |<------------>| sims3mp-bridge   |<----------->| sims3mp-server |
|  via Sims3MPNative.dll   |              | files, voice, net|             |                |
+--------------------------+              +------------------+             +----------------+
```

The game's script runtime has no sockets, threads or file access. A small
native shim (a MonoPatcher plugin) connects the mod to a bridge process on
the same PC, which does networking, file transfer and voice off the game's
thread. `SIMS3MP_BRIDGE_PORT` selects the bridge port per game instance
(used by `setup.ps1 -Test`).

## 1. Server, bridge and native shim

Needs Go 1.24+ and, for the shim, `i686-w64-mingw32-g++` (MSVC also works).

```bash
./scripts/build.sh
```

Copy the three files from `dist/client/plugins/` into
`<The Sims 3>\Game\Bin\MonoPatcher\plugins\`:

- `Sims3MPNative.dll` - the shim MonoPatcher loads into the game
- `sims3mp-bridge.exe` - started by the shim when the game runs
- `sims3mp-server.exe` - used by "Host on this PC"

MonoPatcher itself (its `.asi`, `wininet.dll` and script mod) must be
installed from its own download page; it may not be redistributed.

## 2. The script mod

A built `Sims3MP.package` is kept in `prebuilt/client/`. To rebuild it you
need the game's script assemblies (`mscorlib`, `System`, `ScriptCore`,
`SimIFace`, `UI`, `Sims3GameplaySystems`, `Sims3GameplayObjects`) copied
from `Game\Bin` into a folder **outside this repository** — they are EA's
files and must never be committed or shared — plus Mono's `mcs`:

```bash
GAME_DLLS=/path/to/game-dlls ./scripts/build-mod.sh
```

This compiles `mod/src/*.cs` and packs the result with `cmd/sims3mp-pack`.
`./scripts/build.sh --prebuilt` refreshes the other files in `prebuilt/`.

`mod/stubs/Sims3Stubs.cs` holds empty declarations of the game API the mod
uses, so CI can compile the mod without the game. Keep it in step with
`GameApi.cs`.

### If the package does not load

The packer writes the S3SA script resource and DBPF container from their
publicly described layouts; it was not verified against S3PE output. If the
game ignores the package, make it the usual way: in S3PE create a package,
add `build/Sims3MP.dll` as an `S3SA` resource, and import
`mod/Sims3MP.Main.xml` as `_XML` with instance FNV-64 of `Sims3MP.Main`.
Sending a known-good script mod package would let the packer be checked.

### What the game does and does not expose

Checked against the real assemblies:

| Wanted | Available to scripts | Used |
|---|---|---|
| Object colours/patterns | `ObjectDesigner.GetObjectDesignPreset` / `SetPresetInfo` | yes |
| Terrain height | `World.GetTerrainHeight` / `LotSetTerrainHeight` | yes, one-unit grid per lot |
| Roof | `World.LotGetAutoRoofStyle` / `LotSetAutoRoofStyle` / `LotDoAutoRoof` | style only |
| Walls, floors, coverings | nothing that creates or edits them | no |
| F9 key | UI reports only keys in its hotkey maps | read by the native shim (`MP_MenuKey`) |
| Save name | `GameStates.LoadFileName` | yes |

A relationship is published by the owner of the sim with the lower id when
both sims are played by people, so two owners never overwrite each other.

`setup.ps1` installs from `prebuilt/client/` (or `dist/client/`). Pushing a tag
`v*` builds a release zip that `setup.ps1` can download.

## Tests

```bash
go test -race ./...
```

```bash
./scripts/test-mod.sh
```

The second one needs Mono (`mcs`, `mono`).

## Protocol

`internal/proto/proto.go` is the reference; `mod/src/Net.cs` mirrors it.
TCP carries framed, ordered messages; UDP carries sim state and voice.

What is replicated, and how:

| Data | Mechanism |
|---|---|
| Sim positions | UDP, 30 Hz, owner of the household only |
| Interactions | transient ordered ops (not replayed to late joiners) |
| Funds + six needs | `CEntityData` slot 1, latest value wins, kept by the server |
| Skills, career, moodlets, relationships | `CEntityData` slots 2-5, one blob per sim, sent when changed |
| Objects (create/move/delete/design) | logged build ops, permission-checked per lot, replayed on join |
| Lot structure (walls, floors, roofs, terrain, coverings) | `CLotData`, one blob per lot, sent on leaving build mode and every 5 s while building; needs build permission |

The op log is stored in `ops.log` and reset when a new world is uploaded.
Clients send the last sequence number they applied, so a reconnect resumes
instead of replaying. Objects from the hosted save are referenced by their
id; objects created later are referenced by the sequence number of the op
that created them, because every client hands out the same local ids.
The default ports are 38920 (server, TCP+UDP) and 38921 (bridge, loopback).
