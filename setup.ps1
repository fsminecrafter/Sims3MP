<#
.SYNOPSIS
  Installs, tests or removes Sims3MP (multiplayer for The Sims 3).

.EXAMPLE
  .\setup.ps1              Install into your game.
  .\setup.ps1 -Test        Start two copies of the game on this PC to try multiplayer alone.
  .\setup.ps1 -Server      Run a dedicated server on this PC (the game is not needed for this).
  .\setup.ps1 -Uninstall   Remove Sims3MP again.
  .\setup.ps1 -GameDir "D:\Games\The Sims 3"   Use this if the game is not found automatically.
#>
param(
    [string]$GameDir,
    [switch]$Test,
    [switch]$Server,
    [switch]$Uninstall,
    [int]$Port = 38920
)

$ErrorActionPreference = 'Stop'
$Repo = 'fsminecrafter/Sims3MP'
$PluginFiles = 'Sims3MPNative.dll', 'sims3mp-bridge.exe', 'sims3mp-server.exe'

function Say($text)  { Write-Host $text }
function Good($text) { Write-Host "  OK   $text" -ForegroundColor Green }
function Warn($text) { Write-Host "  !    $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host "`nERROR: $text" -ForegroundColor Red; exit 1 }

function Find-GameExe($dir) {
    foreach ($exe in 'TS3W.exe', 'TS3.exe') {
        $p = Join-Path $dir "Game\Bin\$exe"
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Find-GameDir {
    if ($GameDir) {
        if (Find-GameExe $GameDir) { return $GameDir }
        Fail "No game found in '$GameDir' (expected Game\Bin\TS3W.exe inside it)."
    }
    $candidates = @()
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Sims\The Sims 3', 'HKLM:\SOFTWARE\Sims\The Sims 3') {
        try { $candidates += (Get-ItemProperty -Path $key -ErrorAction Stop).'Install Dir' } catch { }
    }
    $pf86 = ${env:ProgramFiles(x86)}
    $candidates += "$pf86\Steam\steamapps\common\The Sims 3",
                   "$env:ProgramFiles\EA Games\The Sims 3",
                   "$pf86\Electronic Arts\The Sims 3",
                   "$pf86\Origin Games\The Sims 3",
                   "$env:ProgramFiles\Origin Games\The Sims 3"
    foreach ($c in $candidates) {
        if ($c -and (Find-GameExe $c)) { return $c.TrimEnd('\') }
    }
    Say "Could not find The Sims 3 automatically."
    $answer = Read-Host "Paste the folder the game is installed in (the one that contains 'Game')"
    $answer = $answer.Trim('"', ' ')
    if ($answer -and (Find-GameExe $answer)) { return $answer }
    Fail "No game found there. Run again with: .\setup.ps1 -GameDir ""<folder>"""
}

# The game's user folder: Documents\Electronic Arts\The Sims 3 (name differs in some languages).
function Find-UserDir {
    $ea = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Electronic Arts'
    $default = Join-Path $ea 'The Sims 3'
    if (Test-Path $default) { return $default }
    if (Test-Path $ea) {
        $found = Get-ChildItem $ea -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'Saves') } | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    Fail "Could not find the game's Documents folder. Start The Sims 3 once, close it, and run setup again."
}

# Returns a folder that contains the client files, getting them the easiest way available.
function Get-ClientFiles {
    $local = Join-Path $PSScriptRoot 'dist\client'
    $have = { param($d) -not ($PluginFiles | Where-Object { -not (Test-Path (Join-Path $d "plugins\$_")) }) }
    if (& $have $PSScriptRoot) { Good "Using the files next to setup.ps1"; return $PSScriptRoot }  # unpacked release
    if (& $have $local) { Good "Using files from dist\client"; return $local }

    Say "Downloading the latest release..."
    $tmp = Join-Path $env:TEMP "sims3mp-setup"
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $tmp -ItemType Directory | Out-Null
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $zip = Join-Path $tmp 'client.zip'
        Invoke-WebRequest "https://github.com/$Repo/releases/latest/download/sims3mp-client.zip" -OutFile $zip -UseBasicParsing
        Expand-Archive $zip -DestinationPath $tmp -Force
        if (& $have $tmp) { Good "Downloaded"; return $tmp }
    } catch {
        Warn "No downloadable release was found ($($_.Exception.Message))"
    }

    if (Get-Command go -ErrorAction SilentlyContinue) {
        Say "Building from source with Go..."
        $out = Join-Path $local 'plugins'
        New-Item $out -ItemType Directory -Force | Out-Null
        Push-Location $PSScriptRoot
        try {
            $env:CGO_ENABLED = '0'; $env:GOOS = 'windows'; $env:GOARCH = 'amd64'
            & go build -trimpath -ldflags '-s -w -H=windowsgui' -o (Join-Path $out 'sims3mp-bridge.exe') ./cmd/sims3mp-bridge
            if ($LASTEXITCODE -ne 0) { Fail "Building the bridge failed." }
            & go build -trimpath -ldflags '-s -w' -o (Join-Path $out 'sims3mp-server.exe') ./cmd/sims3mp-server
            if ($LASTEXITCODE -ne 0) { Fail "Building the server failed." }
            $gxx = Get-Command i686-w64-mingw32-g++ -ErrorAction SilentlyContinue
            if ($gxx) {
                & $gxx.Source -O2 -shared -static -o (Join-Path $out 'Sims3MPNative.dll') native/Sims3MPNative.cpp -lws2_32 '-Wl,--kill-at'
            }
        } finally { Pop-Location }
        if (& $have $local) { Good "Built"; return $local }
        Fail "Built the server and bridge, but Sims3MPNative.dll needs a 32-bit C++ compiler (i686-w64-mingw32-g++). See docs\BUILDING.md."
    }
    Fail "Could not get the Sims3MP files. Download a release from https://github.com/$Repo/releases and unpack it next to setup.ps1, or see docs\BUILDING.md to build them."
}

function Copy-Checked($from, $to) {
    try { Copy-Item $from $to -Force }
    catch [UnauthorizedAccessException] {
        Fail "Windows refused to write to '$to'. Right-click PowerShell, choose 'Run as administrator' and run setup again. (Close the game first.)"
    }
    catch [System.IO.IOException] {
        Fail "Could not write '$to'. Close The Sims 3 (and any running sims3mp-bridge) and run setup again."
    }
}

# ---------------------------------------------------------------------------

Say ""
Say "Sims3MP setup"
Say "-------------"

if ($Server) {
    $exe = Join-Path (Get-ClientFiles) 'plugins\sims3mp-server.exe'
    $data = Join-Path $PSScriptRoot 'sims3mp-data'
    Say "Starting a dedicated server on port $Port (TCP and UDP). Data is kept in $data"
    Say "The admin password is printed below. Press Ctrl+C to stop."
    & $exe -addr ":$Port" -data $data
    exit $LASTEXITCODE
}

$game    = Find-GameDir
$gameExe = Find-GameExe $game
$user    = Find-UserDir
$plugins = Join-Path $game 'Game\Bin\MonoPatcher\plugins'
$mods    = Join-Path $user 'Mods\Packages'
Good "Game:      $game"
Good "Documents: $user"

if ($Uninstall) {
    Get-Process sims3mp-bridge -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($f in $PluginFiles + 'ts3dir.txt') { Remove-Item (Join-Path $plugins $f) -Force -ErrorAction SilentlyContinue }
    Remove-Item (Join-Path $mods 'Sims3MP.package') -Force -ErrorAction SilentlyContinue
    Good "Sims3MP was removed. Your saves and other mods were not touched."
    exit 0
}

if ($Test) {
    $missing = $PluginFiles | Where-Object { -not (Test-Path (Join-Path $plugins $_)) }
    if ($missing) { Fail "Sims3MP is not installed yet. Run .\setup.ps1 first." }
    if (Get-Process TS3W, TS3 -ErrorAction SilentlyContinue) { Fail "Close The Sims 3 first: test mode starts the game itself." }
    Get-Process sims3mp-bridge -ErrorAction SilentlyContinue | Stop-Process -Force

    # Each copy of the game gets its own bridge by using a different port.
    Say ""
    Say "Starting game 1 (the HOST)..."
    $env:SIMS3MP_BRIDGE_PORT = '38921'
    $host1 = Start-Process $gameExe -ArgumentList '-w' -WorkingDirectory (Split-Path $gameExe) -PassThru
    Say "Waiting 30 seconds before starting the second copy..."
    Start-Sleep -Seconds 30
    Say "Starting game 2 (the GUEST)..."
    $env:SIMS3MP_BRIDGE_PORT = '38931'
    $guest = Start-Process $gameExe -ArgumentList '-w' -WorkingDirectory (Split-Path $gameExe) -PassThru
    Remove-Item Env:\SIMS3MP_BRIDGE_PORT
    Start-Sleep -Seconds 15
    if ($host1.HasExited -or $guest.HasExited) {
        Warn "One of the two copies closed again. The game may refuse to run twice on this PC;"
        Warn "in that case test with a second PC, or a second Windows user/virtual machine."
    }
    Say ""
    Say "Both copies share your saves and mods. Now do this:"
    Say ""
    Say "  In game 1 (HOST)"
    Say "    1. Load the save you want to play and SAVE it once."
    Say "    2. Press F9 > 'Host on this PC' > name: Host > port: $Port"
    Say "    3. Press F9 > 'Copy world and start hosting' > type the save's name"
    Say ""
    Say "  In game 2 (GUEST)"
    Say "    4. Load any save, press F9 > 'Join a server' > name: Guest > address: 127.0.0.1:$Port"
    Say "    5. When it says the world is ready: go to the main menu and load the save it names"
    Say "    6. Press F9 > 'Enter the session'"
    Say ""
    Say "Logs: $user\Sims3MP\bridge.log (host), bridge-38931.log (guest), server.log"
    exit 0
}

# ---- install --------------------------------------------------------------

if (Get-Process TS3W, TS3 -ErrorAction SilentlyContinue) { Fail "Close The Sims 3 before installing." }
Get-Process sims3mp-bridge -ErrorAction SilentlyContinue | Stop-Process -Force

$src = Get-ClientFiles
try { New-Item $plugins -ItemType Directory -Force | Out-Null }
catch { Fail "Windows refused to create '$plugins'. Run PowerShell as administrator and try again." }
foreach ($f in $PluginFiles) { Copy-Checked (Join-Path $src "plugins\$f") (Join-Path $plugins $f) }
Set-Content -Path (Join-Path $plugins 'ts3dir.txt') -Value $user -Encoding UTF8
Good "Installed the bridge, server and native part into $plugins"

$ready = $true
$package = Join-Path $src 'Sims3MP.package'
if (Test-Path $package) {
    New-Item $mods -ItemType Directory -Force | Out-Null
    Copy-Checked $package (Join-Path $mods 'Sims3MP.package')
    Good "Installed Sims3MP.package into $mods"
} else {
    $ready = $false
    Warn "Sims3MP.package (the in-game part) is not available yet: it has to be"
    Warn "compiled against the game's own files first. See docs\BUILDING.md, step 2."
}
if (-not (Test-Path (Join-Path $game 'Game\Bin\MonoPatcher.asi'))) {
    $ready = $false
    Warn "MonoPatcher is not installed. Sims3MP needs it. Get it here and follow its"
    Warn "instructions, then you are done:  https://modthesims.info/d/687815"
}
if (-not (Test-Path (Join-Path $user 'Mods\Resource.cfg'))) {
    $ready = $false
    Warn "Your game is not set up for mods yet (no Mods\Resource.cfg in the Documents folder)."
    Warn "Search for 'Sims 3 mods framework setup' and install it first."
}

Say ""
if ($ready) {
    Say "Done! Start The Sims 3, load a save and press F9." 
    Say "To try it alone with two copies of the game:  .\setup.ps1 -Test"
} else {
    Say "Almost there: fix the points marked with '!' above, then you can play."
}
