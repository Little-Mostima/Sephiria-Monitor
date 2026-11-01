# One-click install / upgrade of Sephiria Monitor.
#
# The plugin IS the MCP server: while the game runs it serves JSON-RPC on
# http://127.0.0.1:47901/mcp and read-only data on the same port as plain GET.
#
# Usage:
#   .\install.ps1                       # install dist\SephiriaMonitor.dll (or bin\Release)
#   .\install.ps1 -Build                # build SephiriaMonitor\ first, then install
#   .\install.ps1 -StopGame             # stop Sephiria first (the dll is locked while it runs)
#   .\install.ps1 -GameDir "E:\Steam\steamapps\common\Sephiria"
param(
    [switch]$Build,
    [switch]$StopGame,
    [string]$GameDir = $env:SEPHIRIA_GAME_DIR,
    [string]$Dotnet = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = $here   # install.ps1 sits at the repo root; sources are "$repo\SephiriaMonitor"

function Find-GameDir {
    if ($GameDir -and (Test-Path (Join-Path $GameDir "Sephiria.exe"))) { return (Resolve-Path $GameDir).Path }
    foreach ($guess in @("D:\steam\steamapps\common\Sephiria", "C:\Program Files (x86)\Steam\steamapps\common\Sephiria",
                         "D:\Steam\steamapps\common\Sephiria")) {
        if (Test-Path (Join-Path $guess "Sephiria.exe")) { return $guess }
    }
    throw "Game dir not found. Pass -GameDir <path to Sephiria>."
}

function Find-Dotnet {
    if ($Dotnet -and (Test-Path $Dotnet)) { return $Dotnet }
    foreach ($c in @($env:SEPHIRIA_DOTNET, "$env:USERPROFILE\.dotnet\dotnet.exe", "C:\Program Files\dotnet\dotnet.exe")) {
        if ($c -and (Test-Path $c)) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks) { return $c }
        }
    }
    throw "No dotnet SDK found (a runtime-only install cannot build). Install .NET SDK 8 or pass -Dotnet <dotnet.exe>."
}

Write-Host "== Sephiria Monitor installer =="
$game = Find-GameDir
Write-Host "game   : $game"

$pluginDir = Join-Path $game "BepInEx\plugins\SephiriaMonitor"
$dll = Join-Path $here "SephiriaMonitor.dll"
if (-not (Test-Path $dll)) { $dll = Join-Path $here "dist\SephiriaMonitor.dll" }
if (-not (Test-Path $dll)) { $dll = Join-Path $repo "SephiriaMonitor\bin\Release\SephiriaMonitor.dll" }

if ($Build) {
    $dn = Find-Dotnet
    Write-Host "dotnet : $dn"
    $env:SEPHIRIA_GAME_DIR = $game
    & $dn build (Join-Path $repo "SephiriaMonitor") -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    $dll = Join-Path $repo "SephiriaMonitor\bin\Release\SephiriaMonitor.dll"
}
if (-not (Test-Path $dll)) { throw "SephiriaMonitor.dll not found (use -Build, or put the prebuilt dll next to this script)." }
if (-not (Test-Path (Join-Path $game "BepInEx\core\BepInEx.dll"))) { throw "BepInEx is missing in $game (install BepInEx 5.4.x x64 first)." }

if ($StopGame) {
    $p = Get-Process Sephiria -ErrorAction SilentlyContinue
    if ($p) { Write-Host "stopping game pid $($p.Id)"; Stop-Process -Id $p.Id -Force; Start-Sleep -Seconds 6 }
}

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
$target = Join-Path $pluginDir "SephiriaMonitor.dll"
Copy-Item $dll $target -Force
$sha = (Get-FileHash $target -Algorithm SHA256).Hash.Substring(0, 16)
Write-Host ("plugin : {0} ({1:N0} B, sha256 {2}...)" -f $target, (Get-Item $target).Length, $sha)

# The hook catalogue ships with the plugin: the embedded MCP server reads it next to the dll.
$hooks = Join-Path $repo "SephiriaMonitor\hooks.json"
if (Test-Path $hooks) {
    Copy-Item $hooks (Join-Path $pluginDir "hooks.json") -Force
    Write-Host ("hooks  : {0:N0} B" -f (Get-Item (Join-Path $pluginDir "hooks.json")).Length)
}

Write-Host ""
Write-Host "Next:"
Write-Host "  1) start the game and load a save (DBs and AddOns load when MainWorld appears)"
Write-Host "  2) health:  Invoke-RestMethod http://127.0.0.1:47901/health"
Write-Host "  3) MCP:     point the client at http://127.0.0.1:47901/mcp"
Write-Host "              (stdio-only clients: node `"$repo\tools\mcp-stdio.mjs`")"
Write-Host "  4) checks:  node `"$repo\tools\selfcheck.mjs`""
