using System;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PhasmoSound;

/// Makes the virtual mic the Windows default recording device (all roles), because the game's
/// voice engine (Vivox) follows the Windows default communications mic, not the in-game dropdown.
public static class DefaultMic
{
    public static void Ensure(string namePart, int volumePercent, int formatRate = 0, string? speakDevice = null, string? gameOutput = null)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            var pc0 = (IPolicyConfig)new PolicyConfigClient();
            // The VB-CABLE installer makes CABLE Input the default OUTPUT. Then the game plays into the mic cable
            // (other players hear the game, the overlay hears nothing). Keep the default output on the game's device.
            if (!string.IsNullOrEmpty(speakDevice) && !string.IsNullOrEmpty(gameOutput) && !gameOutput.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var cur = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                if (cur.FriendlyName.Contains(speakDevice, StringComparison.OrdinalIgnoreCase))
                {
                    var gameDev = e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                                .FirstOrDefault(d => d.FriendlyName.Contains(gameOutput, StringComparison.OrdinalIgnoreCase));
                    if (gameDev != null)
                    {
                        foreach (var r in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications }) Marshal.ThrowExceptionForHR(pc0.SetDefaultEndpoint(gameDev.ID, r));
                        Log.Write($"default output was {cur.FriendlyName} (the mic cable!) -> moved to {gameDev.FriendlyName}");
                    }
                }
            }
            var dev = e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                       .FirstOrDefault(d => d.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase));
            if (dev == null) { Log.Write("default mic: device not found " + namePart); return; }
            bool changed = false;
            var pc = (IPolicyConfig)new PolicyConfigClient();
            if (formatRate > 0)
            {
                // 48 kHz 16-bit mono: the format Unity/Vivox voice chat handles cleanly
                int hr = SetFormat(pc, dev.ID, formatRate, 16, 1);
                Log.Write($"mic format {dev.FriendlyName} -> {formatRate} Hz 16-bit mono: hr=0x{hr:X8}");
            }
            foreach (var (role, erole) in new[] { (Role.Console, ERole.eConsole), (Role.Multimedia, ERole.eMultimedia), (Role.Communications, ERole.eCommunications) })
            {
                if (e.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID == dev.ID) continue;
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(dev.ID, erole));
                changed = true;
            }
            if (dev.AudioEndpointVolume.Mute) { dev.AudioEndpointVolume.Mute = false; changed = true; }
            float want = Math.Clamp(volumePercent, 0, 100) / 100f;
            if (Math.Abs(dev.AudioEndpointVolume.MasterVolumeLevelScalar - want) > 0.02f) { dev.AudioEndpointVolume.MasterVolumeLevelScalar = want; changed = true; }
            Log.Write($"default mic: {dev.FriendlyName} {(changed ? "set as default, level " + volumePercent + "%" : "already default")}");

            // Voice chat plays other players through the default COMMUNICATIONS output. Keep it on the same
            // device as normal output, so Live Captions and the overlay's listener hear them too.
            var normalOut = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            var commOut = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            if (normalOut.ID != commOut.ID)
            {
                Marshal.ThrowExceptionForHR(pc.SetDefaultEndpoint(normalOut.ID, ERole.eCommunications));
                Log.Write($"communications output moved from {commOut.FriendlyName} to {normalOut.FriendlyName}");
            }
        }
        catch (Exception ex) { Log.Write("default mic: " + ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    static int SetFormat(IPolicyConfig pc, string id, int rate, int bits, int channels)
    {
        var f = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = (ushort)channels, nSamplesPerSec = (uint)rate, wBitsPerSample = (ushort)bits,
            nBlockAlign = (ushort)(channels * bits / 8), nAvgBytesPerSec = (uint)(rate * channels * bits / 8), cbSize = 0,
        };
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
        try { Marshal.StructureToPtr(f, p, false); return pc.SetDeviceFormat(id, p, p); }
        catch (Exception ex) { Log.Write("mic format: " + ex.Message); return -1; }
        finally { Marshal.FreeHGlobal(p); }
    }

    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(string dev, out IntPtr fmt);
        int GetDeviceFormat(string dev, int def, out IntPtr fmt);
        int ResetDeviceFormat(string dev);
        int SetDeviceFormat(string dev, IntPtr a, IntPtr b);
        int GetProcessingPeriod(string dev, int def, out long a, out long b);
        int SetProcessingPeriod(string dev, ref long p);
        int GetShareMode(string dev, out IntPtr mode);
        int SetShareMode(string dev, IntPtr mode);
        int GetPropertyValue(string dev, int a, IntPtr key, out IntPtr val);
        int SetPropertyValue(string dev, int a, IntPtr key, IntPtr val);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string dev, ERole role);
        int SetEndpointVisibility(string dev, int visible);
    }
}
