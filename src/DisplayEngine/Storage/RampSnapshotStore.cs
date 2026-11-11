#nullable disable
using System.Text;
using Nightshift.Core.Color;

namespace Nightshift.DisplayEngine.Storage;

/// <summary>
/// Snapshot disque des tables d'origine, plus le drapeau « restauration en attente ».
///
/// Rôle : si NIGHTSHIFT meurt brutalement (kill, exception fatale), le prochain
/// démarrage peut restaurer l'écran AVANT toute autre action. C'est la barrière
/// n°1 et n°5 de la stratégie de restauration décrite dans l'architecture.
/// </summary>
public sealed class RampSnapshotStore
{
    private const string PendingFileName = "restore.pending";

    public RampSnapshotStore(string baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nightshift",
            "ramps");

        Directory.CreateDirectory(BaseDirectory);
    }

    public string BaseDirectory { get; }

    public bool HasPendingRestore => File.Exists(Path.Combine(BaseDirectory, PendingFileName));

    public void SetPendingRestore(bool pending)
    {
        string path = Path.Combine(BaseDirectory, PendingFileName);

        if (pending)
        {
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Save(string stableKey, ushort[] ramp)
    {
        if (ramp is null || ramp.Length != RampBuilder.RampLength)
        {
            return;
        }

        var bytes = new byte[RampBuilder.RampLength * 2];
        for (int i = 0; i < ramp.Length; i++)
        {
            bytes[i * 2] = (byte)(ramp[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)(ramp[i] >> 8);
        }

        WriteAtomic(PathFor(stableKey), bytes);
    }

    public ushort[] Load(string stableKey)
    {
        string path = PathFor(stableKey);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length != RampBuilder.RampLength * 2)
        {
            return null;
        }

        var ramp = new ushort[RampBuilder.RampLength];
        for (int i = 0; i < ramp.Length; i++)
        {
            ramp[i] = (ushort)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8));
        }

        return ramp;
    }

    private string PathFor(string stableKey)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(stableKey.Length);
        foreach (char c in stableKey)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return Path.Combine(BaseDirectory, builder.ToString() + ".ramp");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);

        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
