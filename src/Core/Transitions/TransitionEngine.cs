namespace Nightshift.Core.Transitions;

public enum Easing
{
    Linear,
    SmoothStep,
    SmootherStep
}

public static class EasingFunctions
{
    /// <summary>
    /// Smootherstep (Ken Perlin) : dérivée première ET seconde nulles aux extrémités,
    /// donc aucun à-coup perceptible au début ni à la fin d'une transition.
    /// </summary>
    public static double Apply(Easing easing, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return easing switch
        {
            Easing.Linear => t,
            Easing.SmoothStep => t * t * (3.0 - 2.0 * t),
            _ => t * t * t * (t * (t * 6.0 - 15.0) + 10.0)
        };
    }
}

/// <summary>
/// Interpolation temporelle entre deux températures.
///
/// Déterministe : l'horloge est injectée (secondes), ce qui permet de tester
/// l'intégralité d'une transition en microsecondes, sans attendre ni dépendre
/// d'un timer. Aucun dépassement (overshoot) n'est possible : la courbe reste
/// monotone entre les bornes.
/// </summary>
public sealed class TransitionEngine
{
    private readonly Func<double> _clock;
    private double _startSeconds;

    public TransitionEngine(Func<double>? clockSeconds = null)
    {
        _clock = clockSeconds ?? (() => Environment.TickCount64 / 1000.0);
    }

    public bool IsActive { get; private set; }

    public int FromKelvin { get; private set; }

    public int ToKelvin { get; private set; }

    public double DurationSeconds { get; private set; }

    public double Progress { get; private set; }

    public Easing Easing { get; set; } = Easing.SmootherStep;

    public void Start(int fromKelvin, int toKelvin, double durationSeconds)
    {
        FromKelvin = fromKelvin;
        ToKelvin = toKelvin;
        DurationSeconds = Math.Max(0.0, durationSeconds);
        _startSeconds = _clock();
        Progress = 0.0;
        IsActive = DurationSeconds > 0.0 && fromKelvin != toKelvin;

        if (!IsActive)
        {
            Progress = 1.0;
        }
    }

    /// <summary>Valeur courante ; <paramref name="finished"/> indique la fin de la transition.</summary>
    public int Sample(out bool finished)
    {
        if (!IsActive)
        {
            finished = true;
            Progress = 1.0;
            return ToKelvin;
        }

        double elapsed = _clock() - _startSeconds;
        Progress = Math.Clamp(elapsed / DurationSeconds, 0.0, 1.0);
        finished = Progress >= 1.0;

        int value = (int)Math.Round(FromKelvin + (ToKelvin - FromKelvin) * EasingFunctions.Apply(Easing, Progress));

        if (finished)
        {
            IsActive = false;
            return ToKelvin;
        }

        return value;
    }

    public void Cancel()
    {
        IsActive = false;
    }
}
