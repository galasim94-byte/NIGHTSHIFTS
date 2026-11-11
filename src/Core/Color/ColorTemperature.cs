namespace Nightshift.Core.Color;

/// <summary>
/// Point blanc d'un corps noir (CCT) exprimé comme multiplicateurs RGB normalisés.
/// Le canal le plus fort vaut 1.0 : la normalisation conserve la luminance maximale
/// disponible et évite le « gris » que produirait une simple réduction des canaux.
/// </summary>
public readonly record struct WhitePoint(double R, double G, double B)
{
    public static readonly WhitePoint Neutral = new(1.0, 1.0, 1.0);

    public double this[int channel] => channel switch
    {
        0 => R,
        1 => G,
        2 => B,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public bool IsNeutral => Math.Abs(R - 1.0) < 1e-9 && Math.Abs(G - 1.0) < 1e-9 && Math.Abs(B - 1.0) < 1e-9;
}

/// <summary>
/// Conversion température de couleur → point blanc sRGB.
///
/// Méthode : lieu de Planck en coordonnées CIE 1960 UCS (approximation de Kim et al.,
/// valable de 1667 K à 25000 K), puis conversion XYZ → sRGB linéaire via la matrice
/// sRGB/D65. On obtient directement les multiplicateurs RGB à appliquer pour qu'un
/// écran sRGB reproduise ce blanc, sans adaptation chromatique supplémentaire
/// (la matrice D65→sRGB est déjà l'adaptation).
///
/// Aucune dépendance Win32 : testable sans écran.
/// </summary>
public static class ColorTemperature
{
    public const int NeutralKelvin = 6500;
    public const int MinimumKelvin = 1000;
    public const int MaximumKelvin = 10000;

    /// <summary>Point blanc normalisé (canal max = 1) pour la température demandée.</summary>
    public static WhitePoint WhitePointFor(int kelvin)
    {
        kelvin = Math.Clamp(kelvin, MinimumKelvin, MaximumKelvin);

        // Un écran sRGB ne peut pas être rendu plus froid que son blanc natif :
        // à 6500 K et au-delà, on renvoie le blanc neutre exact (aucun filtre).
        if (kelvin >= NeutralKelvin)
        {
            return WhitePoint.Neutral;
        }

        double t = kelvin;
        double x = PlanckianX(t);
        double y = PlanckianY(x, t);

        if (y <= 0.0)
        {
            return WhitePoint.Neutral;
        }

        // XYZ d'un blanc de luminance Y = 1 pour cette chromaticité.
        double bigX = x / y;
        double bigY = 1.0;
        double bigZ = (1.0 - x - y) / y;

        // Matrice XYZ → sRGB linéaire (primaires sRGB, blanc D65).
        double r = 3.2404542 * bigX - 1.5371385 * bigY - 0.4985314 * bigZ;
        double g = -0.9692660 * bigX + 1.8760108 * bigY + 0.0415560 * bigZ;
        double b = 0.0556434 * bigX - 0.2040259 * bigY + 1.0572252 * bigZ;

        // Un blanc plus chaud peut produire une composante négative hors gamut :
        // on borne à 0 puis on renormalise pour rester dans [0,1].
        r = Math.Max(0.0, r);
        g = Math.Max(0.0, g);
        b = Math.Max(0.0, b);

        double max = Math.Max(r, Math.Max(g, b));
        if (max <= 0.0)
        {
            return WhitePoint.Neutral;
        }

        return new WhitePoint(r / max, g / max, b / max);
    }

    /// <summary>Coordonnée x du lieu de Planck (Kim et al.).</summary>
    private static double PlanckianX(double kelvin)
    {
        if (kelvin <= 4000.0)
        {
            return -0.2661239e9 / (kelvin * kelvin * kelvin)
                 - 0.2343589e6 / (kelvin * kelvin)
                 + 0.8776956e3 / kelvin
                 + 0.179910;
        }

        return -3.0258469e9 / (kelvin * kelvin * kelvin)
             + 2.1070379e6 / (kelvin * kelvin)
             + 0.2226347e3 / kelvin
             + 0.240390;
    }

    /// <summary>Coordonnée y du lieu de Planck, fonction de x et de la température.</summary>
    private static double PlanckianY(double x, double kelvin)
    {
        double x2 = x * x;
        double x3 = x2 * x;

        if (kelvin <= 2222.0)
        {
            return -1.1063814 * x3 - 1.34811020 * x2 + 2.18555832 * x - 0.20219683;
        }

        if (kelvin <= 4000.0)
        {
            return -0.9549476 * x3 - 1.37418593 * x2 + 2.09137015 * x - 0.16748867;
        }

        return 3.0817580 * x3 - 5.87338670 * x2 + 3.75112997 * x - 0.37001483;
    }
}
