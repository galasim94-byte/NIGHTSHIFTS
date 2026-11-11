#nullable disable
using System.Runtime.InteropServices;
using Nightshift.DisplayEngine.Native;

namespace Nightshift.DisplayEngine.Hdr;

public sealed record HdrStatus(
    string DeviceName,
    uint ColorSpace,
    bool IsHdr,
    bool AttachedToDesktop,
    float MaxLuminance)
{
    /// <summary>Espace couleur lisible, pour les journaux et le diagnostic.</summary>
    public string ColorSpaceName => ColorSpace switch
    {
        0 => "sRGB (SDR)",
        1 => "scRGB linéaire (HDR)",
        5 => "YCbCr601 (SDR)",
        12 => "HDR10 / PQ P2020",
        13 => "YCbCr PQ P2020",
        14 => "scRGB PQ P2020",
        17 => "RGB P2020 (SDR)",
        18 => "HLG P2020",
        19 => "YCbCr HLG P2020",
        _ => "espace couleur #" + ColorSpace
    };
}

/// <summary>
/// Interroge DXGI 1.6 pour savoir si une sortie est en mode Advanced Color / HDR.
///
/// Pourquoi c'est indispensable : en HDR, Windows n'applique pas (ou court-circuite)
/// la LUT SDR utilisée par les gamma ramps. Sans cette détection, l'application
/// prétendrait filtrer alors que rien ne se passe.
/// </summary>
public static class HdrProbe
{
    public static IReadOnlyList<HdrStatus> Query()
    {
        var results = new List<HdrStatus>();

        Guid iid = Dxgi.IidFactory1;
        if (Dxgi.CreateDXGIFactory1(ref iid, out IntPtr factory) != 0 || factory == IntPtr.Zero)
        {
            return results;
        }

        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                if (Dxgi.EnumAdapters1(factory, adapterIndex, out IntPtr adapter) != 0)
                {
                    break;
                }

                try
                {
                    for (uint outputIndex = 0; ; outputIndex++)
                    {
                        if (Dxgi.EnumOutputs(adapter, outputIndex, out IntPtr output) != 0)
                        {
                            break;
                        }

                        try
                        {
                            Guid outputIid = Dxgi.IidOutput6;
                            if (Marshal.QueryInterface(output, ref outputIid, out IntPtr output6) != 0 || output6 == IntPtr.Zero)
                            {
                                continue;
                            }

                            try
                            {
                                if (Dxgi.GetDesc1(output6, out Dxgi.OutputDesc1 desc, out string deviceName) == 0)
                                {
                                    results.Add(new HdrStatus(
                                        deviceName,
                                        desc.ColorSpace,
                                        IsHdrColorSpace(desc.ColorSpace),
                                        desc.AttachedToDesktop != 0,
                                        desc.MaxLuminance));
                                }
                            }
                            finally
                            {
                                Marshal.Release(output6);
                            }
                        }
                        finally
                        {
                            Marshal.Release(output);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        return results;
    }

    /// <summary>
    /// Trace chaque appel DXGI avec son code de retour : indispensable pour savoir
    /// si « aucune sortie HDR » signifie « tout est SDR » ou « la sonde a échoué ».
    /// </summary>
    public static IReadOnlyList<string> Diagnose()
    {
        var log = new List<string>();

        try
        {
            Guid iid = Dxgi.IidFactory1;
            int hr = Dxgi.CreateDXGIFactory1(ref iid, out IntPtr factory);
            log.Add($"CreateDXGIFactory1 hr=0x{hr:X8} ptr=0x{factory:X}");

            if (hr != 0 || factory == IntPtr.Zero)
            {
                return log;
            }

            try
            {
                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    int adapterResult = Dxgi.EnumAdapters1(factory, adapterIndex, out IntPtr adapter);
                    log.Add($"EnumAdapters1[{adapterIndex}] hr=0x{adapterResult:X8}");

                    if (adapterResult != 0)
                    {
                        break;
                    }

                    try
                    {
                        for (uint outputIndex = 0; ; outputIndex++)
                        {
                            int outputResult = Dxgi.EnumOutputs(adapter, outputIndex, out IntPtr output);
                            log.Add($"  EnumOutputs[{outputIndex}] hr=0x{outputResult:X8}");

                            if (outputResult != 0)
                            {
                                break;
                            }

                            try
                            {
                                // 1. Valide l'adressage de vtable avec GetDesc (slot 8, interface de base).
                                int baseDesc = Dxgi.GetDesc(output, out _, out string baseName);
                                log.Add($"    GetDesc hr=0x{baseDesc:X8} name='{baseName}'");

                                // 2. Teste les IID candidats : un succès sur Output1 prouve que
                                //    la mécanique QueryInterface est bonne et isole un IID erroné.
                                foreach ((string name, Guid candidate) in InterfaceCandidates)
                                {
                                    Guid tryIid = candidate;
                                    int qi = Marshal.QueryInterface(output, ref tryIid, out IntPtr probe);
                                    log.Add($"    QueryInterface({name}) hr=0x{qi:X8}");
                                    if (probe != IntPtr.Zero)
                                    {
                                        Marshal.Release(probe);
                                    }
                                }

                                Guid outputIid = Dxgi.IidOutput6;
                                int queryResult = Marshal.QueryInterface(output, ref outputIid, out IntPtr output6);

                                if (queryResult == 0 && output6 != IntPtr.Zero)
                                {
                                    try
                                    {
                                        int descResult = Dxgi.GetDesc1(output6, out Dxgi.OutputDesc1 desc, out string name);
                                        log.Add($"    GetDesc1 hr=0x{descResult:X8} name='{name}' colorSpace={desc.ColorSpace} attached={desc.AttachedToDesktop} maxLum={desc.MaxLuminance:0}");
                                    }
                                    finally
                                    {
                                        Marshal.Release(output6);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.Release(output);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex)
        {
            log.Add("EXCEPTION: " + ex.GetType().Name + " " + ex.Message);
        }

        return log;
    }

    /// <summary>
    /// Vidage brut du slot 28 : lève toute ambiguïté sur la méthode appelée.
    /// </summary>
    public static IReadOnlyList<string> DiagnoseRaw()
    {
        var log = new List<string>();

        try
        {
            Guid iid = Dxgi.IidFactory1;
            if (Dxgi.CreateDXGIFactory1(ref iid, out IntPtr factory) != 0 || factory == IntPtr.Zero)
            {
                log.Add("factory indisponible");
                return log;
            }

            try
            {
                if (Dxgi.EnumAdapters1(factory, 0, out IntPtr adapter) != 0)
                {
                    log.Add("aucun adaptateur");
                    return log;
                }

                try
                {
                    if (Dxgi.EnumOutputs(adapter, 0, out IntPtr output) != 0)
                    {
                        log.Add("aucune sortie");
                        return log;
                    }

                    try
                    {
                        Guid outputIid = Dxgi.IidOutput6;
                        if (Marshal.QueryInterface(output, ref outputIid, out IntPtr output6) != 0 || output6 == IntPtr.Zero)
                        {
                            log.Add("IDXGIOutput6 indisponible");
                            return log;
                        }

                        try
                        {
                            var buffer = new byte[256];
                            int hr = Dxgi.RawSlot(output6, 27, buffer);
                            log.Add($"slot27 hr=0x{hr:X8}");

                            var hex = new System.Text.StringBuilder();
                            for (int i = 0; i < 32; i++)
                            {
                                hex.Append(buffer[i].ToString("X2"));
                                if ((i & 15) == 15)
                                {
                                    hex.Append(' ');
                                }
                            }

                            log.Add("octets 0..31 : " + hex);
                            log.Add("utf16 0..31 : '" + System.Text.Encoding.Unicode.GetString(buffer, 0, 64).TrimEnd('\0') + "'");
                            log.Add("uint32@64/68/72/76 : " + BitConverter.ToUInt32(buffer, 64) + "/" + BitConverter.ToUInt32(buffer, 68) + "/" + BitConverter.ToUInt32(buffer, 72) + "/" + BitConverter.ToUInt32(buffer, 76));
                            log.Add("float@96/100/104 : " + BitConverter.ToSingle(buffer, 96) + "/" + BitConverter.ToSingle(buffer, 100) + "/" + BitConverter.ToSingle(buffer, 104));
                        }
                        finally
                        {
                            Marshal.Release(output6);
                        }
                    }
                    finally
                    {
                        Marshal.Release(output);
                    }
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex)
        {
            log.Add("EXCEPTION: " + ex.GetType().Name + " " + ex.Message);
        }

        return log;
    }

    /// <summary>IID candidats des interfaces DXGI de sortie (du plus ancien au plus récent).</summary>
    private static readonly (string Name, Guid Id)[] InterfaceCandidates =
    {
        ("IDXGIOutput1", new Guid("00cddea8-939b-4b83-a340-a685226666cc")),
        ("IDXGIOutput2", new Guid("595e39d1-2724-4663-99b1-da969de28364")),
        ("IDXGIOutput5", new Guid("80a07424-ab52-42eb-833c-0c42fd282d98")),
        ("IDXGIOutput6", new Guid("068346e8-aaec-4b84-add7-137f513f77a1")),
    };

    /// <summary>Vrai pour les espaces couleur HDR / Advanced Color.</summary>
    private static bool IsHdrColorSpace(uint colorSpace)
        => colorSpace is 1 or 12 or 13 or 14 or 16 or 19;
}
