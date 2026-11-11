#nullable disable
using System.Management;

namespace Nightshift.DisplayEngine.Brightness;

/// <summary>
/// Luminosité matérielle des dalles internes via WMI (root\WMI).
/// Mesuré disponible sur la machine de référence (dalle LEN9080, 50/100).
/// </summary>
public sealed class WmiBrightnessBackend : IBrightnessBackend
{
    private readonly string _instanceName;

    private WmiBrightnessBackend(string instanceName)
    {
        _instanceName = instanceName;
    }

    public string Name => "WMI (rétroéclairage de la dalle interne)";

    public BrightnessKind Kind => BrightnessKind.Hardware;

    public bool IsAvailable { get; private set; } = true;

    public static WmiBrightnessBackend TryCreate()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName FROM WmiMonitorBrightnessMethods");

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (item["InstanceName"] is string name && !string.IsNullOrWhiteSpace(name))
                    {
                        return new WmiBrightnessBackend(name);
                    }
                }
            }
        }
        catch
        {
            // WMI absent ou restreint : pas un backend disponible.
        }

        return null;
    }

    public int? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness");

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (item["InstanceName"] is string name &&
                        string.Equals(name, _instanceName, StringComparison.OrdinalIgnoreCase) &&
                        item["CurrentBrightness"] is not null)
                    {
                        return Convert.ToInt32(item["CurrentBrightness"]);
                    }
                }
            }
        }
        catch
        {
            IsAvailable = false;
        }

        return null;
    }

    public bool Write(int percent)
    {
        int value = Math.Clamp(percent, 0, 100);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT InstanceName FROM WmiMonitorBrightnessMethods");

            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    if (item["InstanceName"] is string name &&
                        string.Equals(name, _instanceName, StringComparison.OrdinalIgnoreCase))
                    {
                        item.InvokeMethod("WmiSetBrightness", new object[] { 0u, (byte)value });
                        return true;
                    }
                }
            }
        }
        catch
        {
            IsAvailable = false;
        }

        return false;
    }

    public void Dispose()
    {
    }
}
