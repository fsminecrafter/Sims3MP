#!/usr/bin/env bash
# Compiles the script mod against the real game assemblies and packs it.
# Usage: GAME_DLLS=/path/to/folder/with/the/game/dlls ./scripts/build-mod.sh
# The folder needs mscorlib, System, ScriptCore, SimIFace, UI,
# Sims3GameplaySystems and Sims3GameplayObjects (.dll), copied from the game.
# Those files belong to EA: never commit or redistribute them.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${GAME_DLLS:?set GAME_DLLS to the folder containing the game DLLs}"
refs=()
for n in mscorlib System ScriptCore SimIFace Sims3GameplaySystems Sims3GameplayObjects UI; do
  [ -f "$GAME_DLLS/$n.dll" ] || { echo "missing $GAME_DLLS/$n.dll" >&2; exit 1; }
  refs+=("-r:$GAME_DLLS/$n.dll")
done
mkdir -p build prebuilt/client
mcs -nologo -nostdlib -noconfig -optimize+ -target:library -out:build/Sims3MP.dll "${refs[@]}" mod/src/*.cs
go run ./cmd/sims3mp-pack -dll build/Sims3MP.dll -out prebuilt/client/Sims3MP.package
