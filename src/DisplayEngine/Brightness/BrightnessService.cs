#nullable disable
namespace Nightshift.DisplayEngine.Brightness;

/// <summary>
/// Choisit le meilleur backend réellement disponible, dans l'ordre :
/// 1. WMI (dalle interne), 2. DDC/CI (moniteur externe), 3. aucun → atténuation gamma.
///
/// Aucune supposition : chaque backend est sondé, et l'absence de matériel est
/// exposée à l'interface au lieu d'être masquée.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private IBrightnessBackend _backend;
    private bool _disposed;

    /// <summary>Backend actif ; null = luminosité logicielle (gamma).</summary>
    public IBrightnessBackend Backend => _backend;

    public bool HasHardware => _backend is not null && _backend.IsAvailable;

    public string ActiveName => HasHardware ? _backend.Name : "Atténuation logicielle (gamma)";

    public BrightnessKind ActiveKind => HasHardware ? BrightnessKind.Hardware : BrightnessKind.Software;

    public void Refresh()
    {
        if (_backend is not null)
        {
            return;
        }

        _backend = WmiBrightnessBackend.TryCreate() as IBrightnessBackend
                   ?? DdcBrightnessBackend.TryCreate();
    }

    public int? Read() => HasHardware ? _backend.Read() : null;

    public bool Write(int percent)
    {
        if (!HasHardware)
        {
            return false;
        }

        bool ok = _backend.Write(percent);
        if (!ok)
        {
            _backend.Dispose();
            _backend = null;
        }

        return ok;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend?.Dispose();
        _backend = null;
    }
}
