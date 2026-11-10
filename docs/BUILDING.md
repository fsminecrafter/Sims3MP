# Building and installing the client

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
The default ports are 38920 (server, TCP+UDP) and 38921 (bridge, loopback).
