// Lautstaerke einer einzelnen App (wie im Windows-Lautstaerkemixer) ueber die Core-Audio-Schnittstelle.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace PerfTray
{
    static class AppVolume
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints();
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            void GetAudioSessionControl();
            void GetSimpleAudioVolume();
            void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            void GetCount(out int count);
            void GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl (nur Platzhalter, damit die Reihenfolge stimmt)
            void GetState(); void GetDisplayName(); void SetDisplayName(); void GetIconPath(); void SetIconPath();
            void GetGroupingParam(); void SetGroupingParam(); void RegisterAudioSessionNotification(); void UnregisterAudioSessionNotification();
            // IAudioSessionControl2
            void GetSessionIdentifier(); void GetSessionInstanceIdentifier();
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ISimpleAudioVolume
        {
            void SetMasterVolume(float level, ref Guid context);
            void GetMasterVolume(out float level);
            void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        // Alle Audio-Sitzungen eines Prozessnamens (ein Browser hat oft mehrere)
        static List<ISimpleAudioVolume> Find(string processName)
        {
            var result = new List<ISimpleAudioVolume>();
            if (string.IsNullOrEmpty(processName)) return result;
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice device;
            enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out device);
            var iid = typeof(IAudioSessionManager2).GUID;
            object o;
            device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o);
            IAudioSessionEnumerator sessions;
            ((IAudioSessionManager2)o).GetSessionEnumerator(out sessions);
            int count;
            sessions.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 ctl;
                sessions.GetSession(i, out ctl);
                uint pid;
                if (ctl.GetProcessId(out pid) < 0 || pid == 0) continue;
                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { continue; }
                if (name.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    result.Add((ISimpleAudioVolume)ctl);
            }
            return result;
        }

        // Lautstaerke aendern (delta z. B. +0.05); gibt die neue Lautstaerke 0..1 zurueck, null = App hat keinen Ton
        public static float? Change(string processName, float delta)
        {
            try
            {
                var list = Find(processName);
                if (list.Count == 0) return null;
                float level;
                list[0].GetMasterVolume(out level);
                level = Math.Max(0f, Math.Min(1f, level + delta));
                var ctx = Guid.Empty;
                foreach (var v in list) v.SetMasterVolume(level, ref ctx);
                return level;
            }
            catch { return null; }
        }

        public static float? Get(string processName)
        {
            try
            {
                var list = Find(processName);
                if (list.Count == 0) return null;
                float level;
                list[0].GetMasterVolume(out level);
                return level;
            }
            catch { return null; }
        }

        public static bool? IsMuted(string processName)
        {
            try
            {
                var list = Find(processName);
                if (list.Count == 0) return null;
                bool muted;
                list[0].GetMute(out muted);
                return muted;
            }
            catch { return null; }
        }

        public static void SetMute(string processName, bool mute)
        {
            try
            {
                var ctx = Guid.Empty;
                foreach (var v in Find(processName)) v.SetMute(mute, ref ctx);
            }
            catch { }
        }
    }
}
