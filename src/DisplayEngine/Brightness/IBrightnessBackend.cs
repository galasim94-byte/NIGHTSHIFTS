namespace Nightshift.DisplayEngine.Brightness;

/// <summary>
/// Un backend de luminosité. « hardware » = rétroéclairage réel piloté,
/// « software » = atténuation perçue via gamma. L'UI doit distinguer les deux :
/// ne jamais laisser croire qu'on règle le rétroéclairage quand ce n'est pas le cas.
/// </summary>
public interface IBrightnessBackend : IDisposable
{
    string Name { get; }

    BrightnessKind Kind { get; }

    bool IsAvailable { get; }

    int? Read();

    bool Write(int percent);
}

public enum BrightnessKind
{
    Hardware,
    Software
}
