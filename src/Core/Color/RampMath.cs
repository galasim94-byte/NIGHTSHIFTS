namespace Nightshift.Core.Color;

/// <summary>
/// Opérations de comparaison sur les tables gamma.
///
/// Important (mesuré Phase 1) : la table « neutre » d'un pilote n'est PAS forcément
/// l'identité i×256 ; sur la machine de référence c'est i×257. On ne compare donc
/// jamais à une identité codée en dur, mais à une table de référence capturée.
/// </summary>
public static class RampMath
{
    public readonly record struct ChannelSamples(ushort R0, ushort R128, ushort R255, ushort G0, ushort G128, ushort G255, ushort B0, ushort B128, ushort B255);

    public static double MaxDelta(ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Les deux tables doivent avoir la même longueur.", nameof(b));
        }

        double max = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double delta = Math.Abs((int)a[i] - b[i]);
            if (delta > max)
            {
                max = delta;
            }
        }

        return max;
    }

    /// <summary>Vrai si la table est égale à la référence à la tolérance près.</summary>
    public static bool IsCloseTo(ReadOnlySpan<ushort> candidate, ReadOnlySpan<ushort> reference, double tolerance = 0.0)
        => MaxDelta(candidate, reference) <= tolerance;

    /// <summary>Vrai si les trois canaux sont identiques (aucune teinte appliquée).</summary>
    public static bool IsNeutral(ReadOnlySpan<ushort> ramp)
    {
        for (int i = 0; i < 256; i++)
        {
            if (ramp[i] != ramp[256 + i] || ramp[256 + i] != ramp[512 + i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Vrai si chaque canal est croissant (une table filtre valide l'est toujours).</summary>
    public static bool IsMonotonic(ReadOnlySpan<ushort> ramp)
    {
        for (int channel = 0; channel < 3; channel++)
        {
            int offset = channel * 256;
            for (int i = 1; i < 256; i++)
            {
                if (ramp[offset + i] < ramp[offset + i - 1])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Valeurs témoins, pour les rapports et les journaux.</summary>
    public static ChannelSamples Sample(ReadOnlySpan<ushort> ramp) => new(
        ramp[0], ramp[128], ramp[255],
        ramp[256], ramp[384], ramp[511],
        ramp[512], ramp[640], ramp[767]);
}
