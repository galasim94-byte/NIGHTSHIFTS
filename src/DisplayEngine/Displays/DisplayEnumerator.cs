#nullable disable
using System.Runtime.InteropServices;
using Nightshift.DisplayEngine.Native;

namespace Nightshift.DisplayEngine.Displays;

/// <summary>Un écran actif, identifié par son nom de périphérique (\\.\DISPLAYn).</summary>
public sealed record DisplayInfo(
    string DeviceName,
    string AdapterName,
    string MonitorName,
    string MonitorId,
    bool IsPrimary)
{
    /// <summary>Clé stable pour la persistance des snapshots de ramp.</summary>
    public string StableKey => string.IsNullOrEmpty(MonitorId) ? DeviceName : MonitorId;
}

public static class DisplayEnumerator
{
    /// <summary>Écrans rattachés au bureau, hors pilotes miroir.</summary>
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var displays = new List<DisplayInfo>();

        for (uint i = 0; ; i++)
        {
            var device = new DISPLAY_DEVICE();
            device.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

            if (!User32.EnumDisplayDevices(null, i, ref device, 0))
            {
                break;
            }

            bool attached = (device.StateFlags & User32.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            bool mirror = (device.StateFlags & User32.DISPLAY_DEVICE_MIRRORING_DRIVER) != 0;

            if (!attached || mirror)
            {
                continue;
            }

            string monitorName = string.Empty;
            string monitorId = string.Empty;

            var monitor = new DISPLAY_DEVICE();
            monitor.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (User32.EnumDisplayDevices(device.DeviceName, 0, ref monitor, 0))
            {
                monitorName = monitor.DeviceString ?? string.Empty;
                monitorId = monitor.DeviceID ?? string.Empty;
            }

            displays.Add(new DisplayInfo(
                device.DeviceName,
                device.DeviceString ?? string.Empty,
                monitorName,
                monitorId,
                (device.StateFlags & User32.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0));
        }

        return displays;
    }

    /// <summary>Handles HMONITOR des moniteurs du bureau.</summary>
    public static IReadOnlyList<IntPtr> EnumerateMonitors()
    {
        var monitors = new List<IntPtr>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            monitors.Add(hMonitor);
            return true;
        }

        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return monitors;
    }
}
