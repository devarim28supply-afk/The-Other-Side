# Builds D:\Tools\tos-reader-friend\OtherSideFaces.zip
# Reader + face window, .NET included. The LiveKit account secret stays on this PC.
# The zip gets one room pass for the friend (room the-other-side).
$ErrorActionPreference = 'Stop'
$face = Resolve-Path "$PSScriptRoot\.."
$repo = Resolve-Path "$face\.."
$outRoot = 'D:\Tools\tos-reader-friend'
$pkg = Join-Path $outRoot 'OtherSideFaces'

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force "$pkg\face\www\livekit" | Out-Null

Write-Host 'Publishing the reader...'
dotnet publish "$repo\reader\TosReader\TosReader.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -o "$pkg\reader" -v q -nologo
if ($LASTEXITCODE) { throw 'reader publish failed' }

Write-Host 'Publishing the face window...'
dotnet publish "$face\host\TosFace.csproj" -c Release -r win-x64 --self-contained `
    -p:DebugType=none -o "$pkg\face" -v q -nologo
if ($LASTEXITCODE) { throw 'face publish failed' }

Copy-Item "$face\public\*" "$pkg\face\www\" -Force
Copy-Item "$face\node_modules\livekit-client\dist\livekit-client.esm.mjs" "$pkg\face\www\livekit\"
New-Item -ItemType Directory -Force "$pkg\game-mod\FacePlate\Scripts" | Out-Null
Copy-Item "$repo\ue4ss\FacePlate\Scripts\main.lua" "$pkg\game-mod\FacePlate\Scripts\main.lua"
Copy-Item "$PSScriptRoot\Start Other Side Faces.cmd", "$PSScriptRoot\README.txt", "$PSScriptRoot\How to install.txt", "$PSScriptRoot\Install FacePlate.cmd", "$PSScriptRoot\Install FacePlate.ps1" $pkg

# The sound overlay (the visible part of the sound reader). Kaalo's working copy,
# without his private data, with a config that is safe on another PC: no speech,
# no change to the default microphone, the default output device, and the feed the
# reader in this package writes (..\soundfeed.txt, one level above overlay\).
$overlaySrc = 'D:\Tools\tos-reader\overlay'
if (-not (Test-Path "$overlaySrc\OtherSideSound.exe")) { throw 'sound overlay not found' }
Write-Host 'Copying the sound overlay...'
Copy-Item $overlaySrc "$pkg\overlay" -Recurse -Force
foreach ($drop in 'samples', 'helper', 'phrases\cache', 'runtimes\android', 'runtimes\ios', 'runtimes\linux-arm64',
    'runtimes\linux-x64', 'runtimes\osx-arm64', 'runtimes\osx-x64', 'runtimes\win-arm64', 'runtimes\win-x86') {
    Remove-Item "$pkg\overlay\$drop" -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item "$pkg\overlay\log.txt", "$pkg\overlay\*.pdb", "$pkg\overlay\*.lib" -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$pkg\overlay\samples", "$pkg\overlay\phrases\cache" | Out-Null
foreach ($data in 'ghost_lines.json', 'evidence_clips.txt') {
    $from = "D:\Tools\tos-sound-overlay\captions\$data"
    if (-not (Test-Path $from)) { throw "sound overlay data missing: $data" }
    Copy-Item $from "$pkg\overlay\$data"
}
node "$PSScriptRoot\overlay-config.mjs" "$overlaySrc\config.json" "$pkg\overlay\config.json"
if ($LASTEXITCODE) { throw 'overlay config failed' }

$opencv = Get-ChildItem "$pkg\face" -Recurse -Filter OpenCvSharpExtern.dll -ErrorAction SilentlyContinue
$model = Join-Path $pkg 'face\models\face_detection_yunet_2023mar.onnx'
if (-not $opencv) { throw 'OpenCvSharp native library missing from the face window' }
if (-not (Test-Path $model)) { throw 'face model missing from the face window' }
if (-not (Select-String -Path "$pkg\face\www\index.html" -Pattern 'face.js?v=23' -SimpleMatch -Quiet)) {
    throw 'face page is not the current script'
}

Write-Host 'Writing the room pass...'
Set-Location $face
$pass = Join-Path $pkg 'face\friend.json'
node "$PSScriptRoot\mint-pass.mjs" $pass
if ($LASTEXITCODE) { throw 'room pass failed' }

$secretHit = Get-ChildItem $pkg -Recurse -File -Include *.json,*.txt,*.js,*.mjs,*.html,*.css,*.cmd |
    Select-String -Pattern 'LIVEKIT_API_SECRET' -SimpleMatch
if ($secretHit) { throw 'refusing to zip: account secret would be included' }

$zip = Join-Path $outRoot 'OtherSideFaces.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip
$desk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'OtherSideFaces.zip'
Copy-Item $zip $desk -Force
Write-Host ("Done: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host "Desktop copy: $desk"
