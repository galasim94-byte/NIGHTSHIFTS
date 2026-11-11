// NIGHTSHIFT — Sonde de faisabilité technique (Phase 1)
// Compilée avec le compilateur .NET Framework x64 (csc.exe) pour interroger les API Win32 réelles.
// Usage :
//   NightshiftProbe.exe            -> lecture seule (aucune modification de l'écran)
//   NightshiftProbe.exe --tint     -> applique 4500 K puis 2700 K pendant ~1,2 s et restaure
//   NightshiftProbe.exe --restore <dir> -> restaure les ramps sauvegardées

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace NightshiftPhase1Probe
{
    internal static class Program
    {
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
        private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;
        private const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x00000008;

        private const byte VCP_BRIGHTNESS = 0x10;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] lpRamp);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, out uint pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

        private static void Line(string s) { Console.WriteLine(s); }

        private static string Err() { return "err=" + Marshal.GetLastWin32Error(); }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 1 && args[0] == "--restore")
                {
                    return RestoreAll(args.Length >= 2 ? args[1] : "ramps");
                }

                bool tint = Array.IndexOf(args, "--tint") >= 0;
                string rampDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ramps");
                Directory.CreateDirectory(rampDir);

                Line("=== NIGHTSHIFT - SONDE PHASE 1 ===");
                Line("OS=" + Environment.OSVersion.VersionString + " build=" + Environment.OSVersion.Version.Build);
                Line("64bit=" + Environment.Is64BitProcess + " clr=" + Environment.Version);
                Line("mode=" + (tint ? "READ+WRITE(tint)" : "READ_ONLY"));
                Line("");

                // ---------- 1. Adaptateurs et ramps ----------
                Line("[1] ADAPTATEURS / GAMMA RAMPS");
                var devices = new List<DisplayPath>();
                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE();
                    dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;

                    bool attached = (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                    bool mirror = (dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;
                    if (!attached || mirror) continue;

                    var path = new DisplayPath();
                    path.DeviceName = dd.DeviceName;
                    path.AdapterName = dd.DeviceString;
                    path.Primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;

                    // Moniteur rattaché
                    var mon = new DISPLAY_DEVICE();
                    mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0))
                    {
                        path.MonitorName = mon.DeviceString;
                        path.MonitorId = mon.DeviceID;
                    }

                    IntPtr hdc = CreateDC(null, dd.DeviceName, null, IntPtr.Zero);
                    if (hdc == IntPtr.Zero)
                    {
                        path.DcOk = false;
                        Line("  device=" + dd.DeviceName + " CreateDC FAILED " + Err());
                        devices.Add(path);
                        continue;
                    }
                    path.DcOk = true;

                    var ramp = new ushort[768];
                    path.RampRead = GetDeviceGammaRamp(hdc, ramp);
                    path.RampReadError = Marshal.GetLastWin32Error();
                    if (path.RampRead)
                    {
                        path.OriginalRamp = (ushort[])ramp.Clone();
                        path.IsIdentity = IsIdentity(ramp);
                        // détection d'un filtre déjà actif (Windows Night Light, f.lux, pilote)
                        path.IsMonotonic = IsMonotonic(ramp);
                        SaveRamp(rampDir, dd.DeviceName, ramp);
                    }

                    Line("  device=" + dd.DeviceName + " primary=" + path.Primary);
                    Line("    adapter=" + path.AdapterName);
                    Line("    monitor=" + path.MonitorName);
                    Line("    monitor_id=" + path.MonitorId);
                    Line("    GetDeviceGammaRamp=" + path.RampRead + (path.RampRead ? "" : " " + Err()));
                    if (path.RampRead)
                    {
                        Line("    ramp_identity=" + path.IsIdentity + " ramp_monotonic=" + path.IsMonotonic);
                        Line(string.Format(CultureInfo.InvariantCulture,
                            "    samples R[0,128,255]={0},{1},{2} G={3},{4},{5} B={6},{7},{8}",
                            ramp[0], ramp[128], ramp[255], ramp[256], ramp[384], ramp[511], ramp[512], ramp[640], ramp[767]));
                    }
                    DeleteDC(hdc);
                    devices.Add(path);
                }
                Line("  count_displays=" + devices.Count);
                Line("");

                // ---------- 2. Test d'écriture réel ----------
                Line("[2] TEST D'ECRITURE (SetDeviceGammaRamp)");
                if (!tint)
                {
                    Line("  skipped (mode lecture seule, relancer avec --tint)");
                }
                else
                {
                    foreach (var d in devices)
                    {
                        if (!d.RampRead) { Line("  device=" + d.DeviceName + " skip: ramp illisible"); continue; }
                        IntPtr hdc = CreateDC(null, d.DeviceName, null, IntPtr.Zero);
                        if (hdc == IntPtr.Zero) { Line("  device=" + d.DeviceName + " CreateDC FAILED"); continue; }
                        try
                        {
                            foreach (int kelvin in new[] { 4500, 2700 })
                            {
                                var target = BuildRamp(kelvin);
                                bool ok = SetDeviceGammaRamp(hdc, target);
                                int lastErr = Marshal.GetLastWin32Error();
                                Thread.Sleep(1200);
                                var readback = new ushort[768];
                                bool readOk = GetDeviceGammaRamp(hdc, readback);
                                double deltaMax = readOk ? MaxDelta(target, readback) : -1;
                                Line("  device=" + d.DeviceName + " kelvin=" + kelvin +
                                     " Set=" + ok + (ok ? "" : " " + Err()) +
                                     " readback_ok=" + readOk +
                                     " max_delta=" + deltaMax.ToString("0", CultureInfo.InvariantCulture) +
                                     " applied_visually=yes(observer)");
                            }
                        }
                        finally
                        {
                            bool restored = SetDeviceGammaRamp(hdc, d.OriginalRamp);
                            int rerr = Marshal.GetLastWin32Error();
                            var check = new ushort[768];
                            bool readOk = GetDeviceGammaRamp(hdc, check);
                            bool exact = readOk && MaxDelta(check, d.OriginalRamp) == 0;
                            Line("  device=" + d.DeviceName + " RESTORE Set=" + restored +
                                 (restored ? "" : " " + Err()) + " restored_exactly=" + exact);
                            DeleteDC(hdc);
                        }
                    }
                }
                Line("");

                // ---------- 3. DDC/CI luminosité ----------
                Line("[3] ECRANS PHYSIQUES / DDC-CI (luminosite materielle, lecture seule)");
                var monitors = new List<IntPtr>();
                MonitorEnumProc cb = delegate (IntPtr hMon, IntPtr hdcMon, ref RECT rc, IntPtr data)
                {
                    monitors.Add(hMon);
                    return true;
                };
                GC.KeepAlive(cb);
                if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero))
                {
                    Line("  EnumDisplayMonitors FAILED " + Err());
                }
                int idx = 0;
                foreach (IntPtr hMon in monitors)
                {
                    idx++;
                    uint count;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0)
                    {
                        Line("  hmonitor#" + idx + " physical_monitors=0 " + Err());
                        continue;
                    }
                    var phys = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, phys))
                    {
                        Line("  hmonitor#" + idx + " GetPhysicalMonitors FAILED " + Err());
                        continue;
                    }
                    try
                    {
                        for (int p = 0; p < phys.Length; p++)
                        {
                            uint type, cur, max;
                            bool vcpOk = GetVCPFeatureAndVCPFeatureReply(phys[p].hPhysicalMonitor, VCP_BRIGHTNESS, out type, out cur, out max);
                            int vcpErr = Marshal.GetLastWin32Error();
                            Line("  hmonitor#" + idx + " name=\"" + phys[p].szPhysicalMonitorDescription + "\"" +
                                 " ddc_brightness=" + vcpOk +
                                 (vcpOk ? " current=" + cur + " max=" + max : " " + "err=" + vcpErr));
                        }
                    }
                    finally
                    {
                        DestroyPhysicalMonitors(count, phys);
                    }
                }
                Line("  count_monitors=" + monitors.Count);
                Line("");
                Line("=== FIN SONDE ===");
                return 0;
            }
            catch (Exception ex)
            {
                Line("PROBE_EXCEPTION: " + ex);
                return 2;
            }
        }

        private sealed class DisplayPath
        {
            public string DeviceName;
            public string AdapterName;
            public string MonitorName;
            public string MonitorId;
            public bool Primary;
            public bool DcOk;
            public bool RampRead;
            public int RampReadError;
            public bool IsIdentity;
            public bool IsMonotonic;
            public ushort[] OriginalRamp;
        }

        private static int RestoreAll(string dir)
        {
            Line("RESTORE from " + dir);
            foreach (string file in Directory.GetFiles(dir, "*.ramp"))
            {
                string device = Path.GetFileNameWithoutExtension(file).Replace('_', '\\');
                if (!device.StartsWith("\\\\.\\")) device = "\\\\.\\" + device.TrimStart('.', '\\');
                byte[] bytes = File.ReadAllBytes(file);
                if (bytes.Length != 1536) { Line("  skip " + file); continue; }
                var ramp = new ushort[768];
                for (int i = 0; i < 768; i++) ramp[i] = BitConverter.ToUInt16(bytes, i * 2);
                IntPtr hdc = CreateDC(null, device, null, IntPtr.Zero);
                if (hdc == IntPtr.Zero) { Line("  " + device + " CreateDC FAILED"); continue; }
                bool ok = SetDeviceGammaRamp(hdc, ramp);
                Line("  " + device + " restored=" + ok);
                DeleteDC(hdc);
            }
            return 0;
        }

        private static void SaveRamp(string dir, string device, ushort[] ramp)
        {
            string safe = device.Replace("\\", "").Replace(".", "").Replace("/", "");
            var bytes = new byte[1536];
            for (int i = 0; i < 768; i++)
            {
                ushort v = ramp[i];
                bytes[i * 2] = (byte)(v & 0xFF);
                bytes[i * 2 + 1] = (byte)(v >> 8);
            }
            File.WriteAllBytes(Path.Combine(dir, safe + ".ramp"), bytes);
        }

        private static bool IsIdentity(ushort[] ramp)
        {
            for (int i = 0; i < 256; i++)
            {
                if (ramp[i] != i * 256 || ramp[256 + i] != i * 256 || ramp[512 + i] != i * 256) return false;
            }
            return true;
        }

        private static bool IsMonotonic(ushort[] ramp)
        {
            for (int c = 0; c < 3; c++)
            {
                for (int i = 1; i < 256; i++)
                {
                    if (ramp[c * 256 + i] < ramp[c * 256 + i - 1]) return false;
                }
            }
            return true;
        }

        private static double MaxDelta(ushort[] a, ushort[] b)
        {
            double max = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = Math.Abs((int)a[i] - b[i]);
                if (d > max) max = d;
            }
            return max;
        }

        // ---------- Modèle couleur (prototype, sera déplacé dans src/Core en Phase 2) ----------
        private const double DisplayGamma = 2.2;

        private static double[] WhitePoint(int kelvin)
        {
            double t = kelvin / 100.0;
            double r, g, b;
            if (t <= 66)
            {
                r = 255.0;
                g = 99.4708025861 * Math.Log(t) - 161.1195681661;
                b = t <= 19 ? 0.0 : 138.5177312231 * Math.Log(t - 10.0) - 305.0447927307;
            }
            else
            {
                r = 329.698727446 * Math.Pow(t - 60.0, -0.1332047592);
                g = 288.1221695283 * Math.Pow(t - 60.0, -0.0755148492);
                b = 255.0;
            }
            r = Clamp01(r / 255.0);
            g = Clamp01(g / 255.0);
            b = Clamp01(b / 255.0);
            double m = Math.Max(r, Math.Max(g, b));
            if (m <= 0.0) m = 1.0;
            return new[] { r / m, g / m, b / m };
        }

        private static ushort[] BuildRamp(int kelvin)
        {
            double[] white = WhitePoint(kelvin);
            var ramp = new ushort[768];
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < 256; i++)
                {
                    double v = i / 255.0;
                    double linear = Math.Pow(v, DisplayGamma) * white[c];
                    double srgb = Math.Pow(Clamp01(linear), 1.0 / DisplayGamma);
                    ramp[c * 256 + i] = (ushort)Math.Round(Clamp01(srgb) * 65535.0);
                }
            }
            return ramp;
        }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
    }
}
