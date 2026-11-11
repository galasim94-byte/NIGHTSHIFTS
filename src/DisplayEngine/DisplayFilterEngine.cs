#nullable disable
using Nightshift.Core.Color;
using Nightshift.Core.Transitions;
using Nightshift.DisplayEngine.Brightness;
using Nightshift.DisplayEngine.Displays;
using Nightshift.DisplayEngine.Gamma;
using Nightshift.DisplayEngine.Hdr;
using Nightshift.DisplayEngine.Storage;

namespace Nightshift.DisplayEngine;

/// <summary>État d'application d'un écran après une écriture.</summary>
public sealed record DisplayApplyStatus(
    string Device,
    string MonitorName,
    bool Ok,
    bool Verified,
    double MaxDelta,
    int Win32Error,
    double Strength)
{
    public string Describe()
    {
        string state = Ok
            ? (Verified ? $"OK delta={MaxDelta:0}" : "OK (relecture impossible)")
            : $"ÉCHEC win32={Win32Error}";

        if (Ok && Strength is > 0.0 and < 0.999)
        {
            state += $" intensité={Strength * 100:0}%";
        }

        return state;
    }
}

/// <summary>
/// Orchestrateur : un contrôleur gamma par écran, transitions, luminosité,
/// et restauration garantie.
///
/// Passif par construction : rien ne tourne en tâche de fond. L'appelant (UI ou
/// harnais) appelle <see cref="Tick"/> à la fréquence nécessaire, ce qui rend
/// le moteur entièrement testable et sans consommation résiduelle.
/// </summary>
public sealed class DisplayFilterEngine : IDisposable
{
    private readonly Dictionary<string, GdiGammaController> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransitionEngine _transition;
    private readonly RampSnapshotStore _snapshots;
    private readonly Func<double> _clock;
    private bool _disposed;
    private double _fromBrightness;
    private bool _brightnessDirty;
    private bool _pendingMarked;
    private readonly Dictionary<string, double> _strength = new(StringComparer.OrdinalIgnoreCase);

    public DisplayFilterEngine(RampSnapshotStore snapshots = null, Func<double> clockSeconds = null)
    {
        _snapshots = snapshots ?? new RampSnapshotStore();
        _clock = clockSeconds ?? (() => Environment.TickCount64 / 1000.0);
        _transition = new TransitionEngine(_clock);
        Brightness = new BrightnessService();
    }

    public IReadOnlyList<DisplayInfo> Displays { get; private set; } = Array.Empty<DisplayInfo>();

    public IReadOnlyList<HdrStatus> HdrState { get; private set; } = Array.Empty<HdrStatus>();

    public BrightnessService Brightness { get; }

    public int CurrentKelvin { get; private set; } = ColorTemperature.NeutralKelvin;

    public double CurrentBrightness { get; private set; } = 100.0;

    public int TargetKelvin { get; private set; } = ColorTemperature.NeutralKelvin;

    public double TargetBrightness { get; private set; } = 100.0;

    public bool IsTransitionActive => _transition.IsActive;

    /// <summary>Intensité réellement obtenue par écran (1.0 = pleine puissance).</summary>
    public IReadOnlyDictionary<string, double> EffectiveStrength => _strength;

    /// <summary>Intensité la plus faible parmi les écrans traités (1.0 = pleine puissance).</summary>
    public double EffectiveStrengthMin =>
        LastApply.Count == 0 ? 1.0 : LastApply.Min(s => s.Strength);

    /// <summary>
    /// Température réellement présente à l'écran, déduite de la table qui a été
    /// écrite. Peut être plus froide que la consigne quand le plafond du pilote
    /// impose une intensité réduite. Entre plusieurs écrans, on retient la valeur
    /// la plus défavorable : c'est celle qui détermine le résultat visible.
    /// </summary>
    public int EffectiveKelvin { get; private set; } = ColorTemperature.NeutralKelvin;

    public IReadOnlyList<DisplayApplyStatus> LastApply { get; private set; } = Array.Empty<DisplayApplyStatus>();

    public string LastError { get; private set; }

    /// <summary>Écran(s) actuellement en HDR : la LUT SDR y est inopérante.</summary>
    public IReadOnlyList<string> HdrDisplays =>
        HdrState.Where(h => h.IsHdr).Select(h => h.DeviceName).ToArray();

    public bool HasHdrActive => HdrState.Any(h => h.IsHdr);

    public bool AnyDisplayOpen => _controllers.Count > 0;

    /// <summary>
    /// Démarrage : récupère une éventuelle restauration en attente (crash précédent),
    /// puis énumère les écrans et capture leurs tables d'origine.
    /// </summary>
    public void Initialize()
    {
        bool recover = _snapshots.HasPendingRestore;

        Refresh();

        if (recover)
        {
            foreach (DisplayInfo display in Displays)
            {
                ushort[] saved = _snapshots.Load(display.StableKey);
                if (saved is null || !_controllers.TryGetValue(display.DeviceName, out GdiGammaController controller))
                {
                    continue;
                }

                controller.Apply(saved);
                controller.CaptureOriginal(out _);
                _snapshots.Save(display.StableKey, controller.OriginalRamp);
            }

            _snapshots.SetPendingRestore(false);
            _pendingMarked = false;
        }

        Brightness.Refresh();
    }

    /// <summary>Ré-énumère les écrans (changement de topologie, branchement, résolution).</summary>
    public void Refresh()
    {
        IReadOnlyList<DisplayInfo> displays = DisplayEnumerator.Enumerate();
        var present = new HashSet<string>(displays.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);

        foreach (string device in _controllers.Keys.Where(k => !present.Contains(k)).ToArray())
        {
            _controllers[device].Dispose();
            _controllers.Remove(device);
        }

        foreach (DisplayInfo display in displays)
        {
            if (_controllers.ContainsKey(display.DeviceName))
            {
                continue;
            }

            var controller = new GdiGammaController(display.DeviceName);
            int error = 0;
            if (!controller.IsOpen || !controller.CaptureOriginal(out error))
            {
                LastError = $"Écran {display.DeviceName} : table gamma illisible (win32={error})";
                controller.Dispose();
                continue;
            }

            _controllers[display.DeviceName] = controller;

            // Snapshot conservé seulement s'il n'existe pas déjà : il doit rester
            // la table d'origine du pilote, jamais une table filtrée.
            if (_snapshots.Load(display.StableKey) is null)
            {
                _snapshots.Save(display.StableKey, controller.OriginalRamp);
            }

            DisplayInfo _ = display;
        }

        Displays = displays;
        HdrState = HdrProbe.Query();
    }

    /// <summary>
    /// Change la cible. <paramref name="transitionSeconds"/> = 0 → application immédiate.
    /// </summary>
    public IReadOnlyList<DisplayApplyStatus> SetTarget(int kelvin, double brightnessPercent = 100.0, double transitionSeconds = 0.0)
    {
        int previousKelvin = TargetKelvin;

        TargetKelvin = Math.Clamp(kelvin, ColorTemperature.MinimumKelvin, ColorTemperature.NeutralKelvin);
        TargetBrightness = Math.Clamp(brightnessPercent, 0.0, 100.0);

        // Une cible plus froide (kelvin plus élevé) s'écarte toujours moins de la table
        // neutre : l'intensité mémorisée n'a plus de raison de s'appliquer. Si la pleine
        // intensité est encore refusée, ApplyCompliant redescend aussitôt, sans que rien
        // ne change à l'écran. Sans cette remise à zéro, passer de 2700 K à 3800 K
        // conserverait 70 % et l'écran resterait plus chaud que demandé.
        if (TargetKelvin >= previousKelvin || TargetKelvin >= 3500)
        {
            _strength.Clear();
        }

        if (transitionSeconds <= 0.0)
        {
            _transition.Cancel();
            CurrentKelvin = TargetKelvin;
            CurrentBrightness = TargetBrightness;
            return ApplyNow();
        }

        _fromBrightness = CurrentBrightness;
        _transition.Start(CurrentKelvin, TargetKelvin, transitionSeconds);
        return LastApply;
    }

    /// <summary>Avance la transition si elle est active. Renvoie le dernier état appliqué.</summary>
    public IReadOnlyList<DisplayApplyStatus> Tick()
    {
        if (!_transition.IsActive)
        {
            if (_brightnessDirty)
            {
                _brightnessDirty = false;
                return ApplyNow();
            }

            return LastApply;
        }

        int kelvin = _transition.Sample(out bool finished);
        double eased = EasingFunctions.Apply(_transition.Easing, _transition.Progress);

        CurrentKelvin = kelvin;
        CurrentBrightness = _fromBrightness + (TargetBrightness - _fromBrightness) * eased;

        IReadOnlyList<DisplayApplyStatus> result = ApplyNow();

        if (finished)
        {
            CurrentKelvin = TargetKelvin;
            CurrentBrightness = TargetBrightness;
            result = ApplyNow();
        }

        return result;
    }

    /// <summary>Luminosité : matérielle si possible, sinon fusionnée dans la LUT gamma.</summary>
    public void SetBrightness(int percent, bool immediate = true)
    {
        percent = Math.Clamp(percent, 0, 100);

        if (Brightness.HasHardware && Brightness.Write(percent))
        {
            CurrentBrightness = 100.0;
            TargetBrightness = 100.0;
            _brightnessDirty = true;
        }
        else
        {
            CurrentBrightness = percent;
            TargetBrightness = percent;
            _brightnessDirty = true;
        }

        if (immediate)
        {
            _brightnessDirty = false;
            ApplyNow();
        }
    }

    public IReadOnlyList<DisplayApplyStatus> ApplyNow()
    {
        var statuses = new List<DisplayApplyStatus>(_controllers.Count);
        int kelvin = Math.Clamp((int)Math.Round((double)CurrentKelvin), ColorTemperature.MinimumKelvin, ColorTemperature.NeutralKelvin);
        ushort[] ramp = RampBuilder.Build(kelvin, CurrentBrightness);
        int effectiveWorst = -1;

        foreach (DisplayInfo display in Displays)
        {
            if (!_controllers.TryGetValue(display.DeviceName, out GdiGammaController controller))
            {
                continue;
            }

            double strength = _strength.TryGetValue(display.DeviceName, out double known) ? known : 1.0;
            ushort[] displayRamp = strength >= 1.0
                ? ramp
                : RampBuilder.Blend(controller.OriginalRamp, ramp, strength);

            GammaApplyResult result = controller.Apply(displayRamp);

            if (!result.Ok)
            {
                // Plafond du pilote atteint : on cherche la plus forte intensité acceptée.
                result = controller.ApplyCompliant(ramp, out double found);
                strength = found;
                _strength[display.DeviceName] = found;

                // ApplyCompliant a écrit la table mélangée à cette intensité : on
                // reconstruit exactement ce qui est à l'écran pour l'estimation.
                displayRamp = strength >= 1.0
                    ? ramp
                    : RampBuilder.Blend(controller.OriginalRamp, ramp, strength);
            }

            if (result.Ok)
            {
                int effective = EffectiveTemperature.Estimate(displayRamp, controller.OriginalRamp);
                effectiveWorst = Math.Max(effectiveWorst, effective);
            }

            statuses.Add(new DisplayApplyStatus(
                display.DeviceName,
                display.MonitorName,
                result.Ok,
                result.Verified,
                result.MaxDelta,
                result.Win32Error,
                strength));

            if (!result.Ok)
            {
                LastError = $"Écran {display.DeviceName} : écriture refusée (win32={result.Win32Error})";
            }
        }

        // Marqueur de crash écrit une seule fois par session, pas à chaque frame.
        if (!_pendingMarked && statuses.Any(s => s.Ok))
        {
            _snapshots.SetPendingRestore(true);
            _pendingMarked = true;
        }

        if (effectiveWorst >= 0)
        {
            EffectiveKelvin = effectiveWorst;
        }

        LastApply = statuses;
        return statuses;
    }

    /// <summary>Restaure toutes les tables d'origine et lève le drapeau de restauration.</summary>
    public bool RestoreAll()
    {
        bool allOk = true;

        foreach (GdiGammaController controller in _controllers.Values)
        {
            GammaApplyResult result = controller.Restore();
            allOk &= result.Ok && result.Verified && result.MaxDelta == 0.0;
        }

        if (allOk)
        {
            _snapshots.SetPendingRestore(false);
            _pendingMarked = false;
        }

        return allOk;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RestoreAll();

        foreach (GdiGammaController controller in _controllers.Values)
        {
            controller.Dispose();
        }

        _controllers.Clear();
        Brightness.Dispose();
    }
}
