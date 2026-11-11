#nullable enable
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nightshift.Core.Color;
using Nightshift.DisplayEngine;

namespace Nightshift.UI.ViewModels;

/// <summary>Pastille de température préréglée.</summary>
public sealed class Preset : INotifyPropertyChanged
{
    private bool _isSelected;

    public Preset(string label, int kelvin, Action<int> select)
    {
        Label = label;
        Kelvin = kelvin;
        Command = new RelayCommand(() => select(kelvin));
    }

    public string Label { get; }

    public int Kelvin { get; }

    public ICommand Command { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _action;

    public RelayCommand(Action action) => _action = action;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _action();
}

/// <summary>
/// État de l'interface. Toute la vérité vient du moteur : l'interface n'invente
/// aucune valeur affichée, en particulier la température effective.
///
/// Le moteur est passif : c'est la pompe (33 ms) qui avance les transitions et
/// applique les changements groupés, et elle ne tourne que lorsqu'il y a quelque
/// chose à faire.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public const int MinimumTemperature = 1900;
    public const int MaximumTemperature = ColorTemperature.NeutralKelvin;
    public const int MinimumBrightness = 5;

    private const double EnableTransitionSeconds = 0.9;
    private const double DisableTransitionSeconds = 0.7;
    private const double DefaultTemperature = 3400;

    private static readonly NumberFormatInfo Grouped = new()
    {
        NumberGroupSeparator = "\u202F",
        NumberDecimalDigits = 0
    };

    private readonly DisplayFilterEngine _engine;
    private readonly DispatcherTimer _pump;
    private readonly DispatcherTimer _brightnessDelay;

    private double _temperature = DefaultTemperature;
    private double _brightness;
    private bool _enabled;
    private bool _pendingApply;
    private bool _pendingBrightness;
    private int _pendingBrightnessValue;
    private bool _suppressApply;
    private string? _cachedStatus;
    private string? _cachedEffective;
    private bool? _cachedStrengthNote;
    private bool _disposed;

    private static readonly Brush StatusIdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Brush StatusErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x73)));

    public MainViewModel()
    {
        _engine = new DisplayFilterEngine();
        _engine.Initialize();

        _brightness = _engine.Brightness.Read() ?? 100;

        Presets = new[]
        {
            new Preset("Jour", 6500, SelectPreset),
            new Preset("Équilibré", 5000, SelectPreset),
            new Preset("Soir", 3800, SelectPreset),
            new Preset("Nuit", 2700, SelectPreset)
        };

        _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _pump.Tick += Pump;

        _brightnessDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _brightnessDelay.Tick += (_, _) =>
        {
            _brightnessDelay.Stop();
            if (!_pendingBrightness)
            {
                return;
            }

            _pendingBrightness = false;
            ApplyBrightness(_pendingBrightnessValue);
        };

        UpdatePresetSelection();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------------------------------------------------------------- état

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            _pendingApply = false;

            if (value)
            {
                _engine.SetTarget((int)_temperature, SoftwareBrightness, EnableTransitionSeconds);
            }
            else
            {
                _engine.SetTarget(ColorTemperature.NeutralKelvin, SoftwareBrightness, DisableTransitionSeconds);
            }

            EnsurePump();

            Notify(nameof(IsEnabled), nameof(StatusText), nameof(EffectiveText),
                nameof(ShowStrengthNote), nameof(StrengthNote));
            RefreshEngineState();
        }
    }

    public string StatusText => _enabled
        ? "Filtre appliqué à cet écran"
        : "Écran non modifié";

    // --------------------------------------------------------- température

    public double Temperature
    {
        get => _temperature;
        set
        {
            double level = Math.Clamp(Math.Round(value), MinimumTemperature, MaximumTemperature);
            if (Math.Abs(level - _temperature) < 0.5)
            {
                return;
            }

            _temperature = level;

            Notify(nameof(Temperature), nameof(TemperatureText), nameof(WarmthName));
            UpdatePresetSelection();

            if (_enabled && !_suppressApply)
            {
                // Pas de transition pendant un glissement : le rendu doit suivre le doigt.
                _pendingApply = true;
                EnsurePump();
            }

            Notify(nameof(EffectiveText));
        }
    }

    public string TemperatureText => FormatNumber(_temperature);

    public string WarmthName => _temperature switch
    {
        < 2400 => "NUIT PROFONDE",
        < 3200 => "NUIT",
        < 4300 => "SOIR",
        < 5400 => "ÉQUILIBRÉ",
        _ => "JOUR"
    };

    public IReadOnlyList<Preset> Presets { get; }

    public string EffectiveText
    {
        get
        {
            if (!_enabled)
            {
                return HasSoftwareDimming
                    ? $"Écran en neutre · atténué à {FormatNumber(_brightness)} %"
                    : "Écran en neutre — aucune modification";
            }

            int effective = _engine.EffectiveKelvin;
            double strength = _engine.EffectiveStrengthMin;

            return strength >= 0.995
                ? $"Effectif à l'écran : {FormatNumber(effective)} K — pleine intensité"
                : $"Effectif à l'écran : {FormatNumber(effective)} K — intensité limitée à {FormatNumber(strength * 100)} %";
        }
    }

    public bool ShowStrengthNote => _enabled && _engine.EffectiveStrengthMin < 0.995;

    public string StrengthNote =>
        "Windows refuse les tables trop éloignées de la sienne. La teinte est appliquée à "
        + "l'intensité maximale qu'il autorise, sans modifier aucun réglage du système.";

    // ---------------------------------------------------------- luminosité

    public double Brightness
    {
        get => _brightness;
        set
        {
            double level = Math.Clamp(Math.Round(value), MinimumBrightness, 100);
            if (Math.Abs(level - _brightness) < 0.5)
            {
                return;
            }

            _brightness = level;
            Notify(nameof(Brightness), nameof(BrightnessText), nameof(EffectiveText));

            _pendingBrightnessValue = (int)level;
            _pendingBrightness = true;
            _brightnessDelay.Stop();
            _brightnessDelay.Start();
        }
    }

    public string BrightnessText => $"{FormatNumber(_brightness)} %";

    public string BrightnessHint => _engine.Brightness.HasHardware
        ? "Rétroéclairage de l'écran piloté directement."
        : "Aucun rétroéclairage pilotable ici : l'atténuation passe par le gamma et reste perceptible.";

    // -------------------------------------------------------------- statut

    public string MonitorSummary
    {
        get
        {
            int count = _engine.Displays.Count;
            string screens = count switch
            {
                0 => "aucun écran détecté",
                1 => "1 écran",
                _ => $"{count} écrans"
            };

            return $"{screens} · {(_engine.HasHdrActive ? "HDR" : "SDR")}";
        }
    }

    public bool ShowHdrWarning => _engine.HasHdrActive;

    public string HdrWarning =>
        "HDR actif : Windows ignore la table SDR sur cet écran, le filtre n'a aucun effet visible. "
        + "Désactive le HDR dans les paramètres d'affichage pour l'utiliser.";

    public string EngineStatus => _engine.LastError ?? "Moteur prêt · restauration automatique à la fermeture";

    public Brush EngineStatusBrush => _engine.LastError is null ? StatusIdleBrush : StatusErrorBrush;

    // -------------------------------------------------------------- pompe

    private void Pump(object? sender, EventArgs e)
    {
        if (_engine.IsTransitionActive)
        {
            _engine.Tick();
        }
        else if (_pendingApply)
        {
            _pendingApply = false;
            _engine.SetTarget((int)_temperature, SoftwareBrightness, 0.0);
        }

        RefreshEngineState();

        if (!_engine.IsTransitionActive && !_pendingApply)
        {
            _pump.Stop();
        }
    }

    private void EnsurePump()
    {
        if (!_pump.IsEnabled)
        {
            _pump.Start();
        }
    }

    private void ApplyBrightness(int percent)
    {
        if (_engine.Brightness.HasHardware)
        {
            _engine.SetBrightness(percent);
        }
        else
        {
            // Atténuation logicielle : elle agit même filtre éteint, à teinte neutre.
            _engine.SetTarget(_enabled ? (int)_temperature : ColorTemperature.NeutralKelvin, percent, 0.0);
        }

        RefreshEngineState();
    }

    private void SelectPreset(int kelvin)
    {
        _suppressApply = true;
        Temperature = kelvin;
        _suppressApply = false;

        if (!_enabled)
        {
            return;
        }

        // Depuis une pastille, une transition douce est agréable et gratuite.
        _pendingApply = false;
        _engine.SetTarget(kelvin, SoftwareBrightness, 0.55);
        EnsurePump();
        RefreshEngineState();
    }

    private void UpdatePresetSelection()
    {
        foreach (Preset preset in Presets)
        {
            preset.IsSelected = Math.Abs(preset.Kelvin - _temperature) < 0.5;
        }
    }

    /// <summary>
    /// Ne notifie que ce qui a changé : pendant une transition, la pompe tourne à
    /// 30 Hz et il est inutile de faire réévaluer des liaisons identiques.
    /// </summary>
    private void RefreshEngineState()
    {
        string effective = EffectiveText;
        if (effective != _cachedEffective)
        {
            _cachedEffective = effective;
            Notify(nameof(EffectiveText));
        }

        bool note = ShowStrengthNote;
        if (note != _cachedStrengthNote)
        {
            _cachedStrengthNote = note;
            Notify(nameof(ShowStrengthNote));
        }

        string status = EngineStatus;
        if (status != _cachedStatus)
        {
            _cachedStatus = status;
            Notify(nameof(EngineStatus), nameof(EngineStatusBrush));
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    // --------------------------------------------------------- utilitaires

    private double SoftwareBrightness => _engine.Brightness.HasHardware ? 100.0 : _brightness;

    private bool HasSoftwareDimming => !_engine.Brightness.HasHardware && _brightness < 99.5;

    private static string FormatNumber(double value) => value.ToString("#,0", Grouped);

    private void Notify(params string[] names)
    {
        foreach (string name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Ré-énumère les écrans (branchement, changement de résolution).</summary>
    public void RefreshDisplays()
    {
        _engine.Refresh();
        Notify(nameof(MonitorSummary), nameof(ShowHdrWarning), nameof(BrightnessHint));
        RefreshEngineState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pump.Stop();
        _brightnessDelay.Stop();
        _engine.Dispose();
    }

    // ------------------------------------------------- accès de vérification

    internal DisplayFilterEngine Engine => _engine;

    internal int EngineKelvin => _engine.CurrentKelvin;

    internal int EngineEffectiveKelvin => _engine.EffectiveKelvin;

    internal double EngineStrength => _engine.EffectiveStrengthMin;

    internal bool LastApplyOk =>
        _engine.LastApply.Count > 0 && _engine.LastApply.All(s => s.Ok && s.Verified);

    internal int? HardwareBrightnessLevel => _engine.Brightness.Read();

    internal bool HasHardwareBrightness => _engine.Brightness.HasHardware;

    internal bool VerifyRestore() => _engine.RestoreAll();
}
