#!/usr/bin/env bash
# Builds everything that can be built without the game into dist/.
# Needs Go; the native shim additionally needs i686-w64-mingw32-g++.
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf dist && mkdir -p dist/server dist/client/plugins

# Dedicated server for the usual hosts (no game needed on any of them).
for target in linux/amd64 linux/arm64 windows/amd64 darwin/arm64; do
  os=${target%/*} arch=${target#*/} ext=""
  [ "$os" = windows ] && ext=.exe
  CGO_ENABLED=0 GOOS=$os GOARCH=$arch go build -trimpath -ldflags "-s -w" \
    -o "dist/server/sims3mp-server-$os-$arch$ext" ./cmd/sims3mp-server
done

# Client side: goes into <game>\Game\Bin\MonoPatcher\plugins\.
# 64-bit processes next to the 32-bit game; -H=windowsgui keeps the bridge windowless.
CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath -ldflags "-s -w -H=windowsgui" \
  -o dist/client/plugins/sims3mp-bridge.exe ./cmd/sims3mp-bridge
CGO_ENABLED=0 GOOS=windows GOARCH=amd64 go build -trimpath -ldflags "-s -w" \
  -o dist/client/plugins/sims3mp-server.exe ./cmd/sims3mp-server
if command -v i686-w64-mingw32-g++ >/dev/null; then
  i686-w64-mingw32-g++ -O2 -Wall -shared -static -o dist/client/plugins/Sims3MPNative.dll \
    native/Sims3MPNative.cpp -lws2_32 -Wl,--kill-at
else
  echo "warning: i686-w64-mingw32-g++ not found, Sims3MPNative.dll not built" >&2
fi
# The in-game part needs the game's own DLLs to compile (scripts/build-mod.sh),
# so the last built package is kept in prebuilt/ and shipped from there.
if [ -f prebuilt/client/Sims3MP.package ]; then cp prebuilt/client/Sims3MP.package dist/client/; fi
if [ "${1:-}" = "--prebuilt" ]; then
  mkdir -p prebuilt/client/plugins && cp dist/client/plugins/* prebuilt/client/plugins/
fi
ls -l dist/server dist/client/plugins
