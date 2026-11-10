# Sims3MP

Multiplayer for The Sims 3: a dedicated server that does not need the game,
and a client mod that keeps every player's game in step with it.

> **Status: early foundation, not yet playable.** The server, the client
> bridge, voice chat and the mod's session logic are implemented and tested
> automatically. The part that talks to the game itself (`mod/src/GameApi.cs`)
> was written without access to the game and has **never been run inside The
> Sims 3**. See [What works and what does not](#what-works-and-what-does-not).

## How it fits together

```
 The Sims 3 (each player)                              anywhere, no game needed
┌──────────────────────────┐   loopback   ┌──────────────────┐  TCP + UDP  ┌─────────────────┐
│ Sims3MP script mod (C#)  │◄────────────►│ sims3mp-bridge   │◄───────────►│ sims3mp-server  │
│  via Sims3MPNative.dll   │              │ files, voice, net│             │                 │
└──────────────────────────┘              └──────────────────┘             └─────────────────┘
```

The game's script runtime has no sockets, threads or file access, so the mod
cannot network by itself. A 200-line native shim (loaded through
[MonoPatcher](https://modthesims.info/d/687815)) connects the mod to a bridge
process on the same PC, and the bridge does the real work. Heavy operations
(uploading a save, installing mods, mixing voice) therefore never run on the
game's thread or inside its 32-bit address space.

| Part | Folder | Language |
|---|---|---|
| Dedicated server | `cmd/sims3mp-server`, `internal/server` | Go |
| Client bridge | `cmd/sims3mp-bridge`, `internal/bridge`, `internal/voice` | Go |
| Native shim | `native/` | C++ (32-bit DLL) |
| Script mod | `mod/` | C# |

## Hosting flow

1. The admin creates a world, a sim and picks a lot as usual, then **saves**.
2. **F9** opens the Sims3MP menu (or type `sims3mp` in the cheat console).
3. *Host on this PC* starts a server locally, or *Connect to a server as
   admin* uses a dedicated one started with `sims3mp-server -admin-password ...`.
4. *Copy world and start hosting* uploads the admin's mods and save. The
   server holds every game paused while it takes the world in, then resumes.
5. Other players press F9 > *Join a server*. Missing mods are installed
   automatically (the player is asked to restart the game), the world is
   downloaded as a save, and after loading it they enter the session.

## Rules the server enforces

- **Time** runs at 1x, and at 3x while every player's sims are asleep. The
  admin can pin any speed (or pause) with the normal speed buttons or F9.
- **Households**: each player plays their own; a household can only be
  claimed by one player.
- **Lots**: moving in makes you the owner. Only the owner, the builders they
  appoint (F9 > *Lots and builders*) and the admin can build there. Everyone
  can see every lot.
- **Admin-only mods**: NRaas interactions are hidden for non-admins, and
  admin operations are rejected by the server for everyone else.
- **Voice**: proximity chat. Volume falls with distance and pans left/right;
  beyond 40 units the server does not even send the audio.

## Running a dedicated server

```bash
./scripts/build.sh
```

```bash
./dist/server/sims3mp-server-linux-amd64 -addr :38920 -data ./sims3mp-data -admin-password "choose-one"
```

Open port 38920 for both TCP and UDP. `-password` adds a join password.

## What works and what does not

Verified by automated tests in this repository (`go test ./...` and
`scripts/test-mod.sh`, which runs the mod's real session and menu code with
a simulated game against a real server and bridges):

- hosting, world upload and download, automatic mod install + restart prompt
- player identity, admin rights, kick, persistence across server restarts
- clock rules (1x / 3x-when-all-asleep / admin override / pause on init)
- lot ownership, builders, build-mode lockout
- household ownership, sim position relay over UDP, interaction relay
- voice codec, voice-activity gate, distance attenuation, stereo pan, range culling
- server relay time on loopback is about 0.1 ms, so latency is your network's

Not verified, or not done yet:

- **`mod/src/GameApi.cs` has never run in the game.** It is compiled only
  against `mod/stubs/Sims3Stubs.cs`, a hand-written guess at the game API.
  Expect wrong names and signatures on the first real build.
- The native shim and the Windows audio code compile but are untested.
- **Build/buy changes are not replicated.** Objects, walls and purchases
  made after the world was hosted stay local until the admin hosts again
  (which makes everyone reload).
- Sim sync is "run the same interaction on every client + correct the
  position when it drifts". The Sims 3 simulation is not deterministic, so
  needs, funds, relationships and outcomes will drift between players.
  Making that authoritative is the main remaining work.
- 60 fps / under 20 ms is a design goal (nothing blocks the game thread,
  realtime data uses UDP), not a measured result.
- The game is not paused by the mod while a modal dialog is open on the
  admin's PC; if the game pauses itself there, everyone pauses.
- The menu is a numbered text prompt, not a proper window.
- Windows only (MonoPatcher's native plugins do not exist on macOS).

## Security notes

- Joining a server installs the `.package` mods its admin uploaded. Script
  mods are code: only join servers you trust.
- Player names are bound to a key stored in
  `Documents\Electronic Arts\The Sims 3\Sims3MP\identity.key`.
- Traffic is not encrypted.

See [docs/BUILDING.md](docs/BUILDING.md) to build and install the client.
