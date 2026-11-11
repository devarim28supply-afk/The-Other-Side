# Builds the friend package: D:\Tools\tos-reader-friend\OtherSideReader.zip
# Sounds on screen only (reader + overlay), .NET included, nothing to install, no game audio inside.
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\..\.."
$outRoot = 'D:\Tools\tos-reader-friend'
$pkg = Join-Path $outRoot 'OtherSideReader'

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null

Write-Host 'Publishing the reader...'
dotnet publish "$repo\reader\TosReader\TosReader.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:DebugType=none -o "$pkg\reader" -v q -nologo
if ($LASTEXITCODE) { throw 'reader publish failed' }

Write-Host 'Publishing the overlay...'
dotnet publish "$repo\overlay\OtherSideSound.csproj" -c Release -r win-x64 --self-contained `
    -p:DebugType=none -o "$pkg\overlay" -v q -nologo
if ($LASTEXITCODE) { throw 'overlay publish failed' }

New-Item -ItemType Directory -Force "$pkg\overlay\captions" | Out-Null
Copy-Item "$repo\captions\ghost_lines.json", "$repo\captions\evidence_clips.txt", "$repo\captions\captions_server.py" "$pkg\overlay\captions\"
# Portable CPU Whisper for the live subtitles (built once in captions-engine: uv python + faster-whisper + small.en).
# No game audio inside: the audio-evidence matcher (evidence.py + extracted clips) is left out, the server runs without it.
$eng = Join-Path $outRoot 'captions-engine'
if (-not (Test-Path "$eng\python\python.exe") -or -not (Test-Path "$eng\model\small.en\model.bin")) { throw 'captions-engine missing (python + model\small.en)' }
Copy-Item "$eng\python" "$pkg\overlay\captions\python" -Recurse -Force
Copy-Item "$eng\model" "$pkg\overlay\captions\model" -Recurse -Force
& node "$PSScriptRoot\friend-config.js" 'D:\Tools\tos-sound-overlay\app\config.json' "$pkg\overlay\config.json"
if ($LASTEXITCODE) { throw 'friend config failed' }

Copy-Item "$PSScriptRoot\Start Other Side Reader.cmd", "$PSScriptRoot\README.txt" $pkg

$zip = Join-Path $outRoot 'OtherSideReader.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip
Write-Host ("Done: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
