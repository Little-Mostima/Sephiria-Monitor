# One-click install / upgrade of Sephiria Monitor (plugin + MCP server).
# Usage:
#   .\install.ps1                       # install from the prebuilt dist\ folder next to this script
#   .\install.ps1 -Build                # build from ..\SephiriaMonitor first, then install
#   .\install.ps1 -McpDir D:\tools\mcp  # also copy server.mjs/selfcheck.mjs there
#   .\install.ps1 -StopGame             # stop Sephiria before replacing the dll (it locks the file)
param(
    [switch]$Build,
    [switch]$StopGame,
    [string]$GameDir = $env:SEPHIRIA_GAME_DIR,
    [string]$McpDir = "",
    [string]$Dotnet = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo = $here   # install.ps1 sits at the repo root; sources are "$repo\SephiriaMonitor"

function Find-GameDir {
    if ($GameDir -and (Test-Path (Join-Path $GameDir "Sephiria.exe"))) { return (Resolve-Path $GameDir).Path }
    $guess = "D:\steam\steamapps\common\Sephiria"
    if (Test-Path (Join-Path $guess "Sephiria.exe")) { return $guess }
    throw "Game dir not found. Pass -GameDir <path to Sephiria>."
}

function Find-Dotnet {
    if ($Dotnet -and (Test-Path $Dotnet)) { return $Dotnet }
    foreach ($c in @($env:SEPHIRIA_DOTNET, "D:\AscensionMod\_work\dotnet\dotnet.exe", "$env:USERPROFILE\.dotnet\dotnet.exe",
                     "C:\Program Files\dotnet\dotnet.exe")) {
        if ($c -and (Test-Path $c)) {
            $sdks = & $c --list-sdks 2>$null
            if ($sdks) { return $c }
        }
    }
    throw "No dotnet SDK found. Install one (winget install Microsoft.DotNet.SDK.8) or pass -Dotnet <dotnet.exe>."
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
}
if (-not (Test-Path $dll)) { throw "SephiriaMonitor.dll not found at $dll (use -Build, or put the prebuilt dll next to this script)." }

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

$serverSrc = Join-Path $repo "SurveillanceMod\server.mjs"
if ((Test-Path $serverSrc) -and $McpDir) {
    New-Item -ItemType Directory -Force -Path $McpDir | Out-Null
    Copy-Item $serverSrc (Join-Path $McpDir "server.mjs") -Force
    $sc = Join-Path $repo "SurveillanceMod\selfcheck.mjs"
    if (Test-Path $sc) { Copy-Item $sc (Join-Path $McpDir "selfcheck.mjs") -Force }
    Write-Host "mcp    : server.mjs -> $McpDir"
}

Write-Host ""
Write-Host "Next:"
Write-Host "  1) start the game, load a save (the plugin loads AddOns / DBs only when MainWorld appears)"
Write-Host "  2) verify:  Invoke-RestMethod http://127.0.0.1:47901/health"
Write-Host "  3) DSH MCP entry (cordis.patch.yml):"
Write-Host "       command: node"
Write-Host "       args: [`"$McpDir\server.mjs`"]"
