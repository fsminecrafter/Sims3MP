# Sims3MP — multiplayer for The Sims 3

Play the same Sims 3 town with your friends. Everyone controls their own
household, on their own PC, in the same world.

> **Heads up — this is an untested first build.** Everything compiles against
> the real game and the logic is tested outside it, but nobody has run it
> inside The Sims 3 yet. Expect bugs; keep a backup of your saves.
> See [Where the project stands](#where-the-project-stands).

## What you need

- The Sims 3 on Windows (the server itself runs anywhere and does not need the game)
- Your game already set up for mods
- [MonoPatcher](https://modthesims.info/d/687815) installed

## Install

1. Download this project and unpack it (it includes ready-made files in `prebuilt\`).
2. Close The Sims 3.
3. Right-click `setup.ps1` and choose **Run with PowerShell**.

That's it. The script finds your game, copies the files, and tells you if
anything is missing. If Windows blocks the script, open PowerShell in the
folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
```

| Command | What it does |
|---|---|
| `.\setup.ps1` | Install |
| `.\setup.ps1 -Test` | Start **two copies of the game** on this PC so you can try multiplayer alone |
| `.\setup.ps1 -Server` | Run a dedicated server on this PC |
| `.\setup.ps1 -Uninstall` | Remove Sims3MP (saves and other mods are untouched) |
| `.\setup.ps1 -GameDir "D:\Games\The Sims 3"` | Use if the game is not found automatically |

## How to play

**The host (admin)**

1. Make your world, your sim, and pick a home as usual. **Save the game.**
2. Press **F9**. Choose **Host on this PC**.
3. Press **F9** again. Choose **Copy world and start hosting**.
4. Give your friends your IP address. Forward port `38920` (TCP and UDP) on your router.

**Everyone else**

1. Start the game and load any save. Press **F9** and choose **Join a server**.
2. If the server uses mods you don't have, they are installed for you and you are asked to restart the game.
3. The world downloads as a save. Load it from the main menu, press **F9**, choose **Enter the session**.
4. Press **F9 > Choose household** to pick who you play.

If F9 does nothing, open the cheat console (Ctrl+Shift+C) and type `sims3mp`.
If neither works, the mod did not load: see *Not working or not proven yet* below.

## The rules

| | |
|---|---|
| **Time** | Normal speed. It jumps to 3x only while *everyone* is asleep. The admin can change it any time. |
| **Households** | Everyone plays their own. Two players can't control the same one. |
| **Money and needs** | Follow the player who owns the household, and are restored if you reload. |
| **Skills, careers, moodlets, relationships** | Same: the owner's game decides, everyone else's copy follows. |
| **Lots** | Move in and the lot is yours. Only you, builders you appoint (F9 > Lots and builders), and the admin can build there. |
| **Build/buy** | Objects you place, move, recolour or delete show up for everyone. So do terrain changes and the roof style. **Walls and floors do not** (see below). |
| **Mods like NRaas** | Allowed. Only the admin gets their menus. |
| **Voice chat** | Built in. People get quieter as your camera moves away from them. |

## Trying it on one PC

```powershell
.\setup.ps1 -Test
```

This opens the game twice in windowed mode — one host, one guest — and prints
the exact steps for each window. Both copies share your saves and mods. If
the game refuses to open twice on your PC, use a second PC instead.

## Running a server without the game

On Windows: `.\setup.ps1 -Server`. On Linux or macOS, build once and run it:

```bash
./scripts/build.sh
```

```bash
./dist/server/sims3mp-server-linux-amd64 -admin-password "pick-one"
```

Then the admin uses **F9 > Connect to a server as admin** with that password.

## Where the project stands

Tested automatically (`go test ./...` and `scripts/test-mod.sh`, which runs
the mod's real logic against a real server with a simulated game):

- hosting, joining, world download, automatic mod install
- time rules, admin rights, lot ownership and builders
- households, sim movement and actions being passed between players
- money, needs, skills, careers, moodlets and relationships, including after a reload
- placing, moving, recolouring and deleting objects, including two people building at once
- sending a lot's walls/floors/roofs/terrain to everyone when its builder leaves build mode
- voice volume by distance

Not working or not proven yet:

- **Nothing has been run inside The Sims 3 yet.** The in-game code
  (`mod/src/GameApi.cs`) now compiles against the real game files, so every
  game function it calls exists — but whether each one *behaves* as
  expected is unknown until someone plays it.
- **`Sims3MP.package` might not load.** It is written by this project's own
  packer instead of S3PE and has not been compared with a real one. If you
  see no "Sims3MP loaded" message after loading a save, build the package
  with S3PE instead ([docs/BUILDING.md](docs/BUILDING.md)).
- `setup.ps1`, test mode, the native DLL and the Windows audio code have not
  been run on a real setup beyond the first steps of `setup.ps1`.
- Whether The Sims 3 allows two copies to run at once is unconfirmed.
- **Walls, floors, wallpaper and floor coverings do not sync, and cannot
  with this approach.** The game gives script mods no function to create or
  change them; only the interactive build tools can. What does sync:
  objects and their colours/patterns, terrain height, and the automatic
  roof style. Until this is solved another way, the host should use *Copy
  world and start hosting* again after anyone changes walls or floors.
- Roofs drawn by hand are not synced, only the auto-roof style.
- Not synced at all: inventories, wishes, opportunities, traits, pregnancy
  and ageing, the family tree, and anything town-wide such as story progression.
- If the host reloads their own save, they should use *Copy world and start
  hosting* again so everyone gets the same world.
- 60 fps and under 20 ms is what the design aims for; it has not been measured in the game.
- The F9 menu is a simple numbered list.

## Good to know

- Joining a server installs the mods its admin uses. Mods are code — only join servers you trust.
- Voice and game traffic are not encrypted.
- Logs are in `Documents\Electronic Arts\The Sims 3\Sims3MP\`.

Developers: see [docs/BUILDING.md](docs/BUILDING.md).
