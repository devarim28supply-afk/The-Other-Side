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

$opencv = Get-ChildItem "$pkg\face" -Recurse -Filter OpenCvSharpExtern.dll -ErrorAction SilentlyContinue
$model = Join-Path $pkg 'face\models\face_detection_yunet_2023mar.onnx'
if (-not $opencv) { throw 'OpenCvSharp native library missing from the face window' }
if (-not (Test-Path $model)) { throw 'face model missing from the face window' }
if (-not (Select-String -Path "$pkg\face\www\index.html" -Pattern 'face.js?v=18' -SimpleMatch -Quiet)) {
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
