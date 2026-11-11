using System.Runtime.InteropServices;

namespace Nightshift.UI.Theme;

/// <summary>
/// Réglages DWM minimaux : cadre sombre et coins arrondis.
///
/// La fenêtre n'utilise aucun cadre système (chrome entièrement dessiné), mais
/// DWM reste responsable des coins, de l'ombre et du menu de la barre des tâches.
/// </summary>
internal static class WindowTheme
{
    private const int UseImmersiveDarkMode = 20;
    private const int WindowCornerPreference = 33;
    private const int CornerRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref enabled, sizeof(int));

        int round = CornerRound;
        DwmSetWindowAttribute(hwnd, WindowCornerPreference, ref round, sizeof(int));
    }
}
