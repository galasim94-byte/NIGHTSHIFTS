#nullable disable
using System.Diagnostics;
using System.Text;
using Nightshift.Core.Color;
using Nightshift.DisplayEngine;

namespace Nightshift.EngineHarness;

/// <summary>
/// Harnais de validation du moteur sur le matériel réel.
///
///   list                       → écrans, HDR, backend de luminosité
///   set &lt;kelvin&gt; [luminosité]  → application immédiate (puis restauration)
///   fade &lt;de&gt; &lt;vers&gt; &lt;ms&gt;      → transition réelle, mesure de la durée
///   brightness &lt;pourcent&gt;      → règle la luminosité (matérielle si possible)
///   selftest                   → séquence complète avec vérifications, code de sortie 0/1
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Console redirigée : l'encodage par défaut suffit.
        }

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "selftest";

        try
        {
            return command switch
            {
                "list" => List(),
                "set" => Set(args),
                "fade" => Fade(args),
                "brightness" => Brightness(args),
                "selftest" => SelfTest(),
                "hdrdebug" => HdrDebug(),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("commandes: list | set <kelvin> [luminosite] | fade <de> <vers> <ms> | brightness <pct> | selftest");
        return 64;
    }

    private static int HdrDebug()
    {
        foreach (string line in Nightshift.DisplayEngine.Hdr.HdrProbe.Diagnose())
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        foreach (string line in Nightshift.DisplayEngine.Hdr.HdrProbe.DiagnoseRaw())
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static int List()
    {
        using var engine = new DisplayFilterEngine();
        engine.Initialize();

        Console.WriteLine("=== ÉCRANS ===");
        foreach (var display in engine.Displays)
        {
            Console.WriteLine($"  {display.DeviceName} {(display.IsPrimary ? "(principal)" : "(secondaire)")}");
            Console.WriteLine($"    adaptateur : {display.AdapterName}");
            Console.WriteLine($"    moniteur   : {display.MonitorName}");
            Console.WriteLine($"    identifiant: {display.MonitorId}");
        }

        Console.WriteLine("=== HDR / ADVANCED COLOR ===");
        if (engine.HdrState.Count == 0)
        {
            Console.WriteLine("  aucune sortie interrogée — diagnostic DXGI :");
            foreach (string line in Nightshift.DisplayEngine.Hdr.HdrProbe.Diagnose())
            {
                Console.WriteLine("    " + line);
            }
        }
        else
        {
            foreach (var hdr in engine.HdrState)
            {
                Console.WriteLine($"  {hdr.DeviceName} : {hdr.ColorSpaceName} hdr={hdr.IsHdr} max_lum={hdr.MaxLuminance:0}");
            }
        }

        Console.WriteLine("=== LUMINOSITÉ ===");
        Console.WriteLine($"  backend : {engine.Brightness.ActiveName}");
        Console.WriteLine($"  type    : {engine.Brightness.ActiveKind}");
        Console.WriteLine($"  niveau  : {engine.Brightness.Read()?.ToString() ?? "n/a"}");

        return 0;
    }

    private static int Set(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int kelvin))
        {
            return Usage();
        }

        double brightness = args.Length > 2 && double.TryParse(args[2], out double b) ? b : 100.0;

        using var engine = new DisplayFilterEngine();
        engine.Initialize();
        var statuses = engine.SetTarget(kelvin, brightness);

        Console.WriteLine($"Consigne : {kelvin} K, luminosité {brightness:0}%");
        PrintStatuses(statuses);
        Console.WriteLine("Appliquer pendant 3 s...");
        Thread.Sleep(3000);

        bool restored = engine.RestoreAll();
        Console.WriteLine($"Restauration : {(restored ? "exacte" : "ÉCHEC")}");
        return restored ? 0 : 1;
    }

    private static int Fade(string[] args)
    {
        if (args.Length < 4 ||
            !int.TryParse(args[1], out int from) ||
            !int.TryParse(args[2], out int to) ||
            !int.TryParse(args[3], out int milliseconds))
        {
            return Usage();
        }

        using var engine = new DisplayFilterEngine();
        engine.Initialize();

        engine.SetTarget(from, 100.0);
        Thread.Sleep(300);

        var stopwatch = Stopwatch.StartNew();
        engine.SetTarget(to, 100.0, milliseconds / 1000.0);

        int steps = 0;
        int lastKelvin = from;
        bool monotonic = true;

        while (engine.IsTransitionActive)
        {
            engine.Tick();
            steps++;

            if ((to < from && engine.CurrentKelvin > lastKelvin) || (to > from && engine.CurrentKelvin < lastKelvin))
            {
                monotonic = false;
            }

            lastKelvin = engine.CurrentKelvin;
            Thread.Sleep(20);
        }

        stopwatch.Stop();
        engine.Tick();

        Console.WriteLine($"Transition {from} K → {to} K demandée en {milliseconds} ms");
        Console.WriteLine($"  durée réelle : {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"  pas appliqués: {steps}");
        Console.WriteLine($"  monotone     : {monotonic}");
        Console.WriteLine($"  finale       : {engine.CurrentKelvin} K (attendu {to})");

        PrintStatuses(engine.LastApply);
        bool restored = engine.RestoreAll();
        Console.WriteLine($"Restauration : {(restored ? "exacte" : "ÉCHEC")}");

        return restored && monotonic && engine.CurrentKelvin == to ? 0 : 1;
    }

    private static int Brightness(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out int percent))
        {
            return Usage();
        }

        using var engine = new DisplayFilterEngine();
        engine.Initialize();

        Console.WriteLine($"Backend : {engine.Brightness.ActiveName} ({engine.Brightness.ActiveKind})");
        int? before = engine.Brightness.Read();
        Console.WriteLine($"Avant   : {before?.ToString() ?? "n/a"}");

        engine.SetBrightness(percent);
        Console.WriteLine($"Consigne: {percent}%");

        Thread.Sleep(400);
        int? after = engine.Brightness.Read();
        Console.WriteLine($"Après   : {after?.ToString() ?? "n/a"}");

        if (before is int original && engine.Brightness.HasHardware)
        {
            engine.Brightness.Write(original);
            Console.WriteLine($"Rétroéclairage restauré à {original}");
        }

        engine.RestoreAll();
        return 0;
    }

    private static int SelfTest()
    {
        Console.WriteLine("=== NIGHTSHIFT — SELFTEST MOTEUR ===");

        using var engine = new DisplayFilterEngine();
        engine.Initialize();

        Console.WriteLine($"écrans={engine.Displays.Count} backend={engine.Brightness.ActiveKind} hdr={engine.HasHdrActive}");
        Console.WriteLine();

        if (engine.HasHdrActive)
        {
            Console.WriteLine("ATTENTION : HDR actif — la LUT SDR sera ignorée sur ce(s) écran(s).");
            Console.WriteLine();
        }

        int failures = 0;
        int[] sequence = { 6500, 5000, 4000, 3500, 3000, 2700, 2200, 2000 };
        int previousBlue = int.MaxValue;
        int previousLuminance = int.MaxValue;
        bool blueMonotonic = true;
        bool luminanceMonotonic = true;

        foreach (int kelvin in sequence)
        {
            var statuses = engine.SetTarget(kelvin, 100.0);

            bool applied = statuses.Count > 0 && statuses.All(s => s.Ok && s.Verified && s.MaxDelta == 0.0);
            if (!applied)
            {
                failures++;
            }

            ushort[] ramp = RampBuilder.Build(kelvin, 100.0);
            int blue = ramp[767]; // canal B = 512..767
            int luminance = ramp[255] + ramp[511] + ramp[767];

            if (blue > previousBlue)
            {
                blueMonotonic = false;
            }

            if (luminance > previousLuminance)
            {
                luminanceMonotonic = false;
            }

            previousBlue = blue;
            previousLuminance = luminance;

            var samples = RampMath.Sample(ramp);
            Console.WriteLine(
                $"{kelvin,5} K  appliqué={(applied ? "oui" : "NON"),-4} R255={samples.R255,5} G255={samples.G255,5} B255={samples.B255,5}  {string.Join(" | ", statuses.Select(s => s.Describe()))}");
        }

        Console.WriteLine();
        Console.WriteLine($"bleu décroissant avec la température : {blueMonotonic}");
        Console.WriteLine($"luminance décroissante (pas de perte brutale) : {luminanceMonotonic}");

        if (!blueMonotonic || !luminanceMonotonic)
        {
            failures++;
        }

        bool restored = engine.RestoreAll();
        Console.WriteLine($"restauration exacte : {restored}");

        if (!restored)
        {
            failures++;
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "SELFTEST: PASS" : $"SELFTEST: FAIL ({failures} problème(s))");
        return failures == 0 ? 0 : 1;
    }

    private static void PrintStatuses(IReadOnlyList<DisplayApplyStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            Console.WriteLine("  aucun écran appliqué");
            return;
        }

        foreach (var status in statuses)
        {
            Console.WriteLine($"  {status.Device} ({status.MonitorName}) : {status.Describe()}");
        }
    }
}
