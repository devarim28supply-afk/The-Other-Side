# Installs the in-game face plate into The Other Side on THIS pc.
# The game must be closed. It does not change a running game.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bundle = Join-Path $here "ue4ss-install"

if (Get-Process "TheOtherSide-Win64-Shipping" -ErrorAction SilentlyContinue) {
    Write-Host "Quit The Other Side completely, then run Install FacePlate again."
    exit 1
}

function SteamLibraries {
    $paths = New-Object System.Collections.Generic.List[string]
    $reg = Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
    if ($reg -and $reg.SteamPath) { $paths.Add($reg.SteamPath) }
    $vdf = $null
    if ($reg -and $reg.SteamPath) { $vdf = Join-Path $reg.SteamPath "steamapps\libraryfolders.vdf" }
    if ($vdf -and (Test-Path $vdf)) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $paths.Add(($Matches[1] -replace '\\\\', '\'))
            }
        }
    }
    return $paths | Select-Object -Unique
}

$win = $null
foreach ($lib in SteamLibraries) {
    $candidate = Join-Path $lib "steamapps\common\The Other Side\TheOtherSide\Binaries\Win64"
    if (Test-Path (Join-Path $candidate "TheOtherSide-Win64-Shipping.exe")) { $win = $candidate; break }
}
if (-not $win) {
    Write-Host "The Other Side was not found in Steam. Install the game, then run this again."
    exit 1
}

Write-Host "Game: $win"
$ue = Join-Path $win "ue4ss"
$haveLoader = (Test-Path (Join-Path $win "dwmapi.dll")) -and (Test-Path (Join-Path $ue "UE4SS.dll"))
if (-not $haveLoader) {
    if (-not (Test-Path (Join-Path $bundle "dwmapi.dll"))) {
        Write-Host "The mod loader files are missing from this folder."
        exit 1
    }
    Copy-Item (Join-Path $bundle "dwmapi.dll") (Join-Path $win "dwmapi.dll") -Force
    New-Item -ItemType Directory -Force $ue | Out-Null
    Copy-Item (Join-Path $bundle "ue4ss\*") $ue -Recurse -Force
    Write-Host "Installed the mod loader."
}

$plateSrc = Join-Path $here "game-mod\FacePlate"
$plateDst = Join-Path $ue "Mods\FacePlate"
New-Item -ItemType Directory -Force (Join-Path $plateDst "Scripts") | Out-Null
Copy-Item (Join-Path $plateSrc "Scripts\main.lua") (Join-Path $plateDst "Scripts\main.lua") -Force

$helpers = Join-Path $ue "Mods\shared\UEHelpers\UEHelpers.lua"
if (-not (Test-Path $helpers)) {
    $sharedSrc = Join-Path $bundle "ue4ss\Mods\shared"
    if (Test-Path $sharedSrc) {
        New-Item -ItemType Directory -Force (Join-Path $ue "Mods\shared") | Out-Null
        Copy-Item $sharedSrc (Join-Path $ue "Mods\shared") -Recurse -Force
    }
}

$modsTxt = Join-Path $ue "Mods\mods.txt"
if (-not (Test-Path $modsTxt)) {
    Copy-Item (Join-Path $bundle "ue4ss\Mods\mods.txt") $modsTxt -Force
} else {
    $text = Get-Content $modsTxt -Raw
    if ($text -notmatch '(?m)^FacePlate\s*:') {
        $text = $text -replace '(?m)^;\s*Built-in keybinds', "FacePlate : 1`r`n`r`n; Built-in keybinds"
        if ($text -notmatch 'FacePlate') { $text = $text.TrimEnd() + "`r`nFacePlate : 1`r`n" }
        Set-Content -Path $modsTxt -Value $text -Encoding ASCII
    }
}

Write-Host "FacePlate is installed."
Write-Host "Start The Other Side, then run Start Other Side Faces.cmd."
