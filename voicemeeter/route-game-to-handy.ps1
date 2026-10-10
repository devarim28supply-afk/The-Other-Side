# Sends all PC sound (the game) through Voicemeeter so Handy can transcribe it:
#   Windows default output -> "Voicemeeter Input" -> A1 = LG ULTRAGEAR (you + the sound overlay)
#                                                -> B1 = "Voicemeeter Out B1" (Handy's microphone)
# Built 2026-10-10 for Kaalob. Run again any time; it is safe to repeat.

$ErrorActionPreference = 'Stop'
$vmDir   = 'C:\Program Files (x86)\VB\Voicemeeter'
$monitor = 'LG ULTRAGEAR (NVIDIA High Definition Audio)'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class VMR {
    [DllImport(@"$vmDir\VoicemeeterRemote64.dll")] public static extern int VBVMR_Login();
    [DllImport(@"$vmDir\VoicemeeterRemote64.dll")] public static extern int VBVMR_Logout();
    [DllImport(@"$vmDir\VoicemeeterRemote64.dll")] public static extern int VBVMR_IsParametersDirty();
    [DllImport(@"$vmDir\VoicemeeterRemote64.dll", CharSet = CharSet.Ansi)] public static extern int VBVMR_SetParameters(string script);
    [DllImport(@"$vmDir\VoicemeeterRemote64.dll", CharSet = CharSet.Ansi)] public static extern int VBVMR_GetParameterFloat(string name, out float value);
    [DllImport(@"$vmDir\VoicemeeterRemote64.dll", CharSet = CharSet.Ansi)] public static extern int VBVMR_GetVoicemeeterType(out int type);
}
"@

# 1. Voicemeeter must be running
if (-not (Get-Process voicemeeter_x64, voicemeeter -ErrorAction SilentlyContinue)) {
    Start-Process "$vmDir\voicemeeter_x64.exe"
    Start-Sleep 4
}
$r = [VMR]::VBVMR_Login()
Write-Host "login: $r (0 = ok, 1 = ok but Voicemeeter was not running)"
Start-Sleep 1
$type = 0; [void][VMR]::VBVMR_GetVoicemeeterType([ref]$type)
Write-Host "voicemeeter type: $type (1 = Voicemeeter, 2 = Banana, 3 = Potato)"

# 2. A1 = the monitor; the virtual input (Strip 2 on standard Voicemeeter) goes to A1 and B1; hardware strips stay out
$script = @(
    # MME, not WDM: Voicemeeter opens WDM outputs exclusively, and then the sound overlay cannot listen
    # to the monitor any more (0x8889000A device in use). MME shares it. MME names stop at 31 characters.
    "Bus[0].device.mme = `"$($monitor.Substring(0, [Math]::Min(31, $monitor.Length)))`";",
    "Strip[2].A1 = 1;", "Strip[2].B1 = 1;", "Strip[2].Mute = 0;", "Strip[2].Gain = 0;",
    "Strip[0].A1 = 0;", "Strip[0].B1 = 0;", "Strip[1].A1 = 0;", "Strip[1].B1 = 0;",
    "Bus[0].Mute = 0;", "Bus[1].Mute = 0;", "Bus[0].Gain = 0;", "Bus[1].Gain = 0;"
) -join ' '
$r = [VMR]::VBVMR_SetParameters($script)
Write-Host "set parameters: $r (0 = ok)"
Start-Sleep 1
for ($i = 0; $i -lt 10; $i++) { [void][VMR]::VBVMR_IsParametersDirty(); Start-Sleep -Milliseconds 100 }
foreach ($p in 'Strip[2].A1', 'Strip[2].B1', 'Bus[0].Mute', 'Bus[1].Mute') {
    $v = 0.0; [void][VMR]::VBVMR_GetParameterFloat($p, [ref]$v); Write-Host "$p = $v"
}
[void][VMR]::VBVMR_Logout()

# 3. Windows plays everything into Voicemeeter
& 'D:\Tools\phasmo-sound-overlay\SetDefaultMic\bin\Release\net10.0\SetDefaultMic.exe' 'CABLE Output' 70 --out 'Voicemeeter Input'
