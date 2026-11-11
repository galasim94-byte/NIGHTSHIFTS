namespace Nightshift.Core.Color;

/// <summary>
/// Construit la table gamma (3 × 256 entrées 16 bits) réellement envoyée à l'écran.
///
/// Le calcul se fait en lumière linéaire (gamma d'affichage 2.2) puis est dé-gammafié :
/// c'est la seule façon d'obtenir une teinte correcte sans écraser les tons clairs.
/// Température et luminosité perçue sont fusionnées dans la MÊME table : une seule
/// écriture par écran, donc aucun cumul de pertes de quantification.
/// </summary>
public static class RampBuilder
{
    public const double DisplayGamma = 2.2;
    public const int RampLength = 3 * 256;

    /// <summary>
    /// Construit la LUT pour une température (K) et un niveau de luminosité logicielle (%).
    /// </summary>
    /// <param name="kelvin">Température de couleur cible.</param>
    /// <param name="brightnessPercent">100 = pas d'atténuation logicielle.</param>
    public static ushort[] Build(int kelvin, double brightnessPercent = 100.0)
    {
        return Build(kelvin, brightnessPercent, DisplayGamma);
    }

    public static ushort[] Build(int kelvin, double brightnessPercent, double gamma)
    {
        WhitePoint white = ColorTemperature.WhitePointFor(kelvin);
        double dim = Math.Clamp(brightnessPercent, 0.0, 100.0) / 100.0;

        // Atténuation appliquée en linéaire par dim^gamma : la sortie code-valeur
        // est alors proportionnelle à dim, ce qui correspond à la perception.
        double dimLinear = dim <= 0.0 ? 0.0 : Math.Pow(dim, gamma);

        var ramp = new ushort[RampLength];

        for (int channel = 0; channel < 3; channel++)
        {
            double multiplier = white[channel];
            int offset = channel * 256;

            for (int i = 0; i < 256; i++)
            {
                double code = i / 255.0;
                double linear = Math.Pow(code, gamma) * multiplier * dimLinear;
                double encoded = Math.Pow(Math.Clamp(linear, 0.0, 1.0), 1.0 / gamma);
                ramp[offset + i] = (ushort)Math.Round(Math.Clamp(encoded, 0.0, 1.0) * 65535.0);
            }
        }

        return ramp;
    }

    /// <summary>Table neutre (aucun filtre, aucune atténuation).</summary>
    public static ushort[] Neutral() => Build(ColorTemperature.NeutralKelvin, 100.0);

    /// <summary>
    /// Interpole une table entre une référence neutre et une cible.
    ///
    /// Windows rejette les tables qui s'écartent trop de la table du pilote
    /// (protection anti-malware). Réduire <paramref name="strength"/> est la seule
    /// façon conforme d'appliquer un filtre plus fort : aucun réglage système n'est modifié.
    /// </summary>
    public static ushort[] Blend(ReadOnlySpan<ushort> neutral, ReadOnlySpan<ushort> target, double strength)
    {
        if (neutral.Length != target.Length)
        {
            throw new ArgumentException("Les deux tables doivent avoir la même longueur.", nameof(target));
        }

        strength = Math.Clamp(strength, 0.0, 1.0);
        var result = new ushort[neutral.Length];

        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (ushort)Math.Round(neutral[i] + ((int)target[i] - neutral[i]) * strength);
        }

        return result;
    }
}
