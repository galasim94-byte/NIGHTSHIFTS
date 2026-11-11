namespace Nightshift.Core.Color;

/// <summary>
/// Estime la température réellement présente à l'écran, à partir de la table
/// gamma qui a été écrite — jamais à partir de la consigne.
///
/// Pourquoi c'est nécessaire : Windows refuse les tables trop éloignées de la
/// sienne. Quand c'est le cas, le moteur applique un mélange entre la table
/// d'origine et la cible ; une consigne de 2700 K peut alors produire un écran
/// plus proche de 4100 K. Afficher « 2700 K » serait un mensonge.
///
/// Méthode : on mesure le rapport appliqué/origine sur le code maximal de chaque
/// canal (le seul point où la table vaut exactement le multiplicateur encodé),
/// on le ramène en lumière linéaire, puis on cherche la température dont le point
/// blanc normalisé est le plus proche. Aucune donnée de capteur : c'est une
/// équivalence colorimétrique, et elle est annoncée comme telle.
/// </summary>
public static class EffectiveTemperature
{
    private const int CoarseStep = 10;

    /// <summary>
    /// Température équivalente (K) de la table <paramref name="applied"/>
    /// relativement à la table <paramref name="original"/> du pilote.
    /// </summary>
    public static int Estimate(ReadOnlySpan<ushort> applied, ReadOnlySpan<ushort> original)
    {
        if (applied.Length != RampBuilder.RampLength)
        {
            throw new ArgumentException("Table appliquée invalide.", nameof(applied));
        }

        if (original.Length != RampBuilder.RampLength)
        {
            throw new ArgumentException("Table d'origine invalide.", nameof(original));
        }

        Span<double> multipliers = stackalloc double[3];

        for (int channel = 0; channel < 3; channel++)
        {
            // Code maximal : la table y vaut multiplier^(1/gamma) × 65535, donc le
            // rapport à la table d'origine isole exactement le multiplicateur.
            int index = (channel * 256) + 255;
            double source = original[index];

            if (source <= 0.0)
            {
                return ColorTemperature.NeutralKelvin;
            }

            double encoded = Math.Clamp(applied[index] / source, 0.0, 1.0);
            multipliers[channel] = Math.Pow(encoded, RampBuilder.DisplayGamma);
        }

        double max = Math.Max(multipliers[0], Math.Max(multipliers[1], multipliers[2]));
        if (max <= 1e-6)
        {
            return ColorTemperature.NeutralKelvin;
        }

        for (int channel = 0; channel < 3; channel++)
        {
            multipliers[channel] /= max;
        }

        // Écart au blanc pur inférieur à 0,2 % : la table est neutre à l'œil.
        double spread = Math.Max(
            Math.Abs(multipliers[0] - 1.0),
            Math.Max(Math.Abs(multipliers[1] - 1.0), Math.Abs(multipliers[2] - 1.0)));

        if (spread < 0.002)
        {
            return ColorTemperature.NeutralKelvin;
        }

        int best = ColorTemperature.NeutralKelvin;
        double bestError = double.MaxValue;

        for (int kelvin = ColorTemperature.MinimumKelvin; kelvin <= ColorTemperature.NeutralKelvin; kelvin += CoarseStep)
        {
            double error = Error(kelvin, multipliers[0], multipliers[1], multipliers[2]);
            if (error < bestError)
            {
                bestError = error;
                best = kelvin;
            }
        }

        // Affinage au kelvin autour du meilleur candidat grossier.
        int from = Math.Max(ColorTemperature.MinimumKelvin, best - CoarseStep);
        int to = Math.Min(ColorTemperature.NeutralKelvin, best + CoarseStep);

        for (int kelvin = from; kelvin <= to; kelvin++)
        {
            double error = Error(kelvin, multipliers[0], multipliers[1], multipliers[2]);
            if (error < bestError)
            {
                bestError = error;
                best = kelvin;
            }
        }

        return best;
    }

    /// <summary>Écart quadratique entre un point blanc théorique et le mesuré.</summary>
    private static double Error(int kelvin, double r, double g, double b)
    {
        WhitePoint white = ColorTemperature.WhitePointFor(kelvin);
        double dr = white.R - r;
        double dg = white.G - g;
        double db = white.B - b;
        return (dr * dr) + (dg * dg) + (db * db);
    }
}
