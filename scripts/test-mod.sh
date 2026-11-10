#!/usr/bin/env bash
# End-to-end test of the script mod's logic (everything except GameApi.cs):
# real server + two real bridges + the C# Session/Menu code with a fake game.
# Needs Go and Mono (mcs, mono).
set -euo pipefail
cd "$(dirname "$0")/.."
T=$(mktemp -d)
trap 'kill $(jobs -p) 2>/dev/null || true; rm -rf "$T"' EXIT
go build -o "$T/server" ./cmd/sims3mp-server
go build -o "$T/bridge" ./cmd/sims3mp-bridge
mcs -nologo -out:"$T/harness.exe" mod/src/Net.cs mod/src/IGame.cs mod/src/Session.cs mod/src/Menu.cs mod/test/Harness.cs
for p in alice bob; do
  mkdir -p "$T/$p/Mods/Packages" && echo mod > "$T/$p/Mods/Packages/Sims3MP.package"
done
mkdir -p "$T/alice/Saves/Town.sims3" && echo world > "$T/alice/Saves/Town.sims3/Meta.data"
"$T/server" -addr 127.0.0.1:38990 -data "$T/data" -admin-password test-admin &
"$T/bridge" -local 127.0.0.1:38991 -ts3dir "$T/alice" -no-voice &
"$T/bridge" -local 127.0.0.1:38992 -ts3dir "$T/bob" -no-voice &
sleep 1
mono "$T/harness.exe" 127.0.0.1:38990 test-admin 38991 38992
