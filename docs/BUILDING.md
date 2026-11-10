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

This step needs the game and has not been done yet by anyone.

1. Create a C# class library targeting .NET Framework 2.0 with "do not
   reference mscorlib" enabled, as for any Sims 3 script mod.
2. Reference the game's `mscorlib.dll`, `System.dll`, `ScriptCore.dll`,
   `SimIFace.dll`, `Sims3GameplaySystems.dll`, `Sims3GameplayObjects.dll`
   and `UI.dll` (from `Game\Bin`, extracted from the game's packages).
3. Add every file in `mod/src/`. Do **not** add `mod/stubs/`.
4. Fix `GameApi.cs` until it compiles: it is the only file that uses game
   types, and `mod/stubs/Sims3Stubs.cs` lists each member it assumes.
5. With S3PE create `Sims3MP.package` containing
   - the assembly as an `S3SA` resource, and
   - `mod/Sims3MP.Main.xml` as an `_XML` resource whose instance is the
     FNV-64 hash of `Sims3MP.Main`.
6. Put the package in `Documents\Electronic Arts\The Sims 3\Mods\Packages`.

Things most likely to need attention in `GameApi.cs`:

- the per-frame hook (`WindowBase.Tick`) and the F9 key event
- reading and setting the game speed
- detecting build/buy mode and the lot being edited
- `Sim.IsSleeping`, autonomy on/off for sims played by other people
- `SaveName` (currently returns "", so players confirm via the F9 menu)
- funds and motives (`Household.FamilyFunds`, `Sim.Motives`)
- listing a lot's objects, and creating one from a catalog resource key

To hand the result to players, put `Sims3MP.package` next to the `plugins`
folder in `dist/client/`; `setup.ps1` installs it from there. Pushing a tag
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
| Objects (create/move/delete) | logged build ops, permission-checked per lot, replayed on join |

The op log is stored in `ops.log` and reset when a new world is uploaded.
Clients send the last sequence number they applied, so a reconnect resumes
instead of replaying. Objects from the hosted save are referenced by their
id; objects created later are referenced by the sequence number of the op
that created them, because every client hands out the same local ids.
The default ports are 38920 (server, TCP+UDP) and 38921 (bridge, loopback).
