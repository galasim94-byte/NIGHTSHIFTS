#nullable disable
using System.Runtime.InteropServices;

namespace Nightshift.DisplayEngine.Native;

/// <summary>
/// Appels DXGI « bas niveau » (pointeurs de vtable) pour récupérer DXGI_OUTPUT_DESC1,
/// seule source publique fiable de l'état Advanced Color / HDR par sortie.
///
/// Indices de vtable vérifiés : IUnknown(3) + IDXGIObject(4) + IDXGIDeviceSubObject(1)
/// + IDXGIOutput(12) + IDXGIOutput1(4) + IDXGIOutput2(1) + IDXGIOutput3(1)
/// + IDXGIOutput4(1) + IDXGIOutput5(1) => GetDesc1 = slot 28.
/// </summary>
internal static class Dxgi
{
    internal static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    // IID exact de IDXGIOutput6 (dxgi1_6.h). Une valeur approximative renvoie E_NOINTERFACE.
    internal static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    private const int SlotEnumAdapters1 = 12;
    private const int SlotEnumOutputs = 7;

    // Mesuré sur la machine de référence : le slot 28 n'écrit que 4 octets (0x00000003),
    // soit les drapeaux de CheckHardwareCompositionSupport. GetDesc1 est donc en 27.
    // (La numérotation théorique donnait 28 : un décalage d'un cran sur ce runtime DXGI.)
    private const int SlotGetDesc1 = 27;

    /// <summary>
    /// Miroir blittable de DXGI_OUTPUT_DESC1. Le nom de périphérique est un tampon
    /// de caractères fixes (et non une chaîne managée) : la structure est écrite
    /// directement par DXGI dans la pile, une référence managée y serait corrompue.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public uint ColorSpace;
        public float RedX;
        public float RedY;
        public float GreenX;
        public float GreenY;
        public float BlueX;
        public float BlueY;
        public float WhiteX;
        public float WhiteY;
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;

        // Réserve : le runtime n'écrit jamais au-delà de sizeof(DXGI_OUTPUT_DESC1).
        public int Padding0;
        public int Padding1;
        public int Padding2;
        public int Padding3;
    }

    /// <summary>Miroir blittable de DXGI_OUTPUT_DESC (validité de l'adressage vtable).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OutputDesc
    {
        public fixed char DeviceName[32];
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public int Padding0;
        public int Padding1;
    }

    /// <summary>Slot 8 = IDXGIOutput::GetDesc (après IUnknown 3 + IDXGIObject 4 + GetDevice 1).</summary>
    internal static unsafe int GetDesc(IntPtr output, out OutputDesc desc, out string deviceName)
    {
        OutputDesc local = default;
        delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int> fn =
            (delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)(*(void***)output)[8];
        int hr = fn(output, &local);
        desc = local;
        deviceName = hr == 0 ? (Marshal.PtrToStringUni((IntPtr)local.DeviceName) ?? string.Empty) : string.Empty;
        return hr;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    internal static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    internal static unsafe int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
    {
        IntPtr result;
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int> fn =
            (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[SlotEnumAdapters1];
        int hr = fn(factory, index, &result);
        adapter = result;
        return hr;
    }

    internal static unsafe int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        IntPtr result;
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int> fn =
            (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)adapter)[SlotEnumOutputs];
        int hr = fn(adapter, index, &result);
        output = result;
        return hr;
    }

    /// <summary>
    /// Écrit la sortie du slot 28 dans un tampon brut : permet d'identifier sans
    /// ambiguïté la méthode réellement présente (GetDesc1 remplit un nom, une
    /// méthode à drapeaux unique n'écrit que 4 octets).
    /// </summary>
    internal static unsafe int RawSlot(IntPtr output6, int slot, byte[] buffer)
    {
        fixed (byte* p = buffer)
        {
            delegate* unmanaged[Stdcall]<IntPtr, void*, int> fn =
                (delegate* unmanaged[Stdcall]<IntPtr, void*, int>)(*(void***)output6)[slot];
            return fn(output6, p);
        }
    }

    internal static unsafe int GetDesc1(IntPtr output6, out OutputDesc1 desc, out string deviceName)
    {
        OutputDesc1 local = default;
        delegate* unmanaged[Stdcall]<IntPtr, OutputDesc1*, int> fn =
            (delegate* unmanaged[Stdcall]<IntPtr, OutputDesc1*, int>)(*(void***)output6)[SlotGetDesc1];
        int hr = fn(output6, &local);
        desc = local;
        deviceName = hr == 0
            ? (Marshal.PtrToStringUni((IntPtr)local.DeviceName) ?? string.Empty)
            : string.Empty;
        return hr;
    }
}
