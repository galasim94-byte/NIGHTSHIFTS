#nullable disable
using System.Runtime.InteropServices;

namespace Nightshift.DisplayEngine.Native;

internal static class Gdi32
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDCW")]
    internal static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] lpRamp);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);
}
