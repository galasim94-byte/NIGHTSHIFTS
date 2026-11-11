#nullable disable
using Nightshift.DisplayEngine.Displays;
using Nightshift.DisplayEngine.Native;

namespace Nightshift.DisplayEngine.Brightness;

/// <summary>
/// Luminosité matérielle des moniteurs externes via DDC/CI (VCP 0x10).
///
/// Mesuré indisponible sur la dalle interne de la machine de référence
/// (ERROR_GRAPHICS_I2C_ERROR_TRANSMITTING_DATA). Implémenté pour les moniteurs
/// externes, mais non validable sur cette configuration — à tester sur écran externe.
/// </summary>
public sealed class DdcBrightnessBackend : IBrightnessBackend
{
    private readonly List<(PHYSICAL_MONITOR[] Monitors, uint Count)> _groups = new();
    private bool _disposed;

    private DdcBrightnessBackend()
    {
    }

    public string Name => "DDC/CI (rétroéclairage du moniteur externe)";

    public BrightnessKind Kind => BrightnessKind.Hardware;

    public bool IsAvailable => _groups.Count > 0;

    public static DdcBrightnessBackend TryCreate()
    {
        var backend = new DdcBrightnessBackend();

        try
        {
            foreach (IntPtr hMonitor in DisplayEnumerator.EnumerateMonitors())
            {
                if (!Dxva2.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                {
                    continue;
                }

                var array = new PHYSICAL_MONITOR[count];
                if (!Dxva2.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, array))
                {
                    continue;
                }

                bool responds = false;
                foreach (PHYSICAL_MONITOR monitor in array)
                {
                    if (Dxva2.GetVCPFeatureAndVCPFeatureReply(
                            monitor.hPhysicalMonitor,
                            Dxva2.VCP_BRIGHTNESS,
                            out _,
                            out _,
                            out uint maximum) && maximum > 0)
                    {
                        responds = true;
                    }
                }

                if (responds)
                {
                    backend._groups.Add((array, count));
                }
                else
                {
                    Dxva2.DestroyPhysicalMonitors(count, array);
                }
            }
        }
        catch
        {
            backend.Dispose();
            return null;
        }

        return backend._groups.Count > 0 ? backend : null;
    }

    public int? Read()
    {
        foreach (var (monitors, _) in _groups)
        {
            foreach (PHYSICAL_MONITOR monitor in monitors)
            {
                if (Dxva2.GetVCPFeatureAndVCPFeatureReply(
                        monitor.hPhysicalMonitor,
                        Dxva2.VCP_BRIGHTNESS,
                        out _,
                        out uint current,
                        out uint maximum) && maximum > 0)
                {
                    return (int)Math.Round(current * 100.0 / maximum);
                }
            }
        }

        return null;
    }

    public bool Write(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        bool anySuccess = false;

        foreach (var (monitors, _) in _groups)
        {
            foreach (PHYSICAL_MONITOR monitor in monitors)
            {
                if (!Dxva2.GetVCPFeatureAndVCPFeatureReply(
                        monitor.hPhysicalMonitor,
                        Dxva2.VCP_BRIGHTNESS,
                        out _,
                        out _,
                        out uint maximum) || maximum == 0)
                {
                    continue;
                }

                uint target = (uint)Math.Round(maximum * clamped / 100.0);
                if (Dxva2.SetVCPFeature(monitor.hPhysicalMonitor, Dxva2.VCP_BRIGHTNESS, target))
                {
                    anySuccess = true;
                }
            }
        }

        return anySuccess;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (monitors, count) in _groups)
        {
            try
            {
                Dxva2.DestroyPhysicalMonitors(count, monitors);
            }
            catch
            {
                // Libération best-effort.
            }
        }

        _groups.Clear();
    }
}
