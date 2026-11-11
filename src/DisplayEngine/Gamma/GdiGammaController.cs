#nullable disable
using System.Runtime.InteropServices;
using Nightshift.Core.Color;
using Nightshift.DisplayEngine.Native;

namespace Nightshift.DisplayEngine.Gamma;

/// <summary>Résultat d'une écriture de gamma ramp, avec vérification par relecture.</summary>
public readonly record struct GammaApplyResult(bool Ok, bool Verified, double MaxDelta, int Win32Error)
{
    public string Describe() => Ok
        ? (Verified ? $"OK (delta={MaxDelta:0})" : "OK (relecture impossible)")
        : $"ÉCHEC (win32={Win32Error})";
}

/// <summary>
/// Un contrôleur par écran : conserve la DC d'affichage, capture la table d'origine
/// AVANT toute modification, applique les tables et restaure à l'identique.
///
/// La DC reste ouverte pendant la vie du contrôleur : cela évite de recréer un
/// contexte graphique à chaque frame de transition (~20 Hz).
/// </summary>
public sealed class GdiGammaController : IDisposable
{
    private IntPtr _hdc;
    private bool _disposed;

    public GdiGammaController(string deviceName)
    {
        DeviceName = deviceName;
        _hdc = Gdi32.CreateDC(null, deviceName, null, IntPtr.Zero);
    }

    public string DeviceName { get; }

    public bool IsOpen => _hdc != IntPtr.Zero;

    /// <summary>Table présente à l'ouverture, avant toute action de NIGHTSHIFT.</summary>
    public ushort[] OriginalRamp { get; private set; }

    public bool CaptureOriginal(out int win32Error)
    {
        win32Error = 0;
        if (!IsOpen)
        {
            win32Error = -1;
            return false;
        }

        var ramp = new ushort[RampBuilder.RampLength];
        if (!Gdi32.GetDeviceGammaRamp(_hdc, ramp))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        OriginalRamp = ramp;
        return true;
    }

    public GammaApplyResult Apply(ushort[] ramp)
    {
        if (!IsOpen)
        {
            return new GammaApplyResult(false, false, -1, -1);
        }

        if (!Gdi32.SetDeviceGammaRamp(_hdc, ramp))
        {
            return new GammaApplyResult(false, false, -1, Marshal.GetLastWin32Error());
        }

        // Vérification systématique : un pilote peut accepter l'appel et ignorer la table.
        var readback = new ushort[RampBuilder.RampLength];
        if (!Gdi32.GetDeviceGammaRamp(_hdc, readback))
        {
            return new GammaApplyResult(true, false, -1, 0);
        }

        return new GammaApplyResult(true, true, RampMath.MaxDelta(ramp, readback), 0);
    }

    public GammaApplyResult ApplyKelvin(int kelvin, double brightnessPercent)
        => Apply(RampBuilder.Build(kelvin, brightnessPercent));

    /// <summary>
    /// Applique la table cible en respectant le plafond imposé par Windows/pilote.
    ///
    /// Mesuré en Phase 2 : au-delà d'un certain écart à la table neutre, le pilote
    /// refuse l'écriture (SetDeviceGammaRamp retourne FALSE). On descend donc par
    /// paliers jusqu'à la plus forte intensité acceptée. Les refus ne changent rien
    /// à l'écran : aucune image intermédiaire n'est visible, il n'y a pas de scintillement.
    /// </summary>
    public GammaApplyResult ApplyCompliant(ushort[] target, out double strength)
    {
        strength = 1.0;

        if (OriginalRamp is null)
        {
            return Apply(target);
        }

        double[] ladder = { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.2 };
        int lastError = 0;

        foreach (double alpha in ladder)
        {
            ushort[] candidate = alpha >= 1.0
                ? target
                : RampBuilder.Blend(OriginalRamp, target, alpha);

            GammaApplyResult result = Apply(candidate);

            if (result.Ok)
            {
                strength = alpha;
                return result;
            }

            lastError = result.Win32Error;
        }

        strength = 0.0;
        return new GammaApplyResult(false, false, -1, lastError);
    }

    /// <summary>Restaure la table d'origine ; le delta doit être 0.</summary>
    public GammaApplyResult Restore()
    {
        if (OriginalRamp is null)
        {
            return new GammaApplyResult(false, false, -1, -1);
        }

        return Apply(OriginalRamp);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hdc != IntPtr.Zero)
        {
            if (OriginalRamp is not null)
            {
                Gdi32.SetDeviceGammaRamp(_hdc, OriginalRamp);
            }

            Gdi32.DeleteDC(_hdc);
            _hdc = IntPtr.Zero;
        }
    }
}
