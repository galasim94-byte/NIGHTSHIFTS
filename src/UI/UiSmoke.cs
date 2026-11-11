using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Nightshift.Core.Color;
using Nightshift.UI.ViewModels;

namespace Nightshift.UI;

/// <summary>
/// Vérification de l'interface sur la machine réelle : charge le XAML, ouvre la
/// fenêtre, pilote l'interrupteur, la température et la luminosité, puis restaure.
///
/// Deux choses sont prouvées ici que la compilation ne prouve pas : aucune erreur
/// de liaison de données, et le moteur reçoit bien les ordres de l'interface.
/// </summary>
internal static class UiSmoke
{
    public static async void Run()
    {
        // Sans cela, la fermeture de la fenêtre arrête le processus avant l'écriture
        // du journal : le code de sortie ne prouverait plus rien.
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var log = new StringBuilder();
        var failures = new List<string>();
        var bindings = new BindingErrorListener();

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        int? initialBrightness = null;

        try
        {
            var viewModel = new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            Application.Current.MainWindow = window;

            initialBrightness = viewModel.HardwareBrightnessLevel;

            window.Show();
            await Task.Delay(600);
            window.UpdateLayout();

            log.AppendLine($"fenêtre      : {window.ActualWidth:0}×{window.ActualHeight:0} px");
            if (window.ActualHeight < 420)
            {
                failures.Add("fenêtre trop petite : le contenu n'est pas rendu");
            }

            viewModel.IsEnabled = true;
            await Task.Delay(1300);
            log.AppendLine(
                $"activé       : consigne {viewModel.Temperature:0} K · moteur {viewModel.EngineKelvin} K "
                + $"· effectif {viewModel.EngineEffectiveKelvin} K · intensité {viewModel.EngineStrength:P0} "
                + $"· écriture ok={viewModel.LastApplyOk}");

            if (viewModel.EngineKelvin != (int)viewModel.Temperature)
            {
                failures.Add("le moteur n'a pas la température de l'interface");
            }

            if (!viewModel.LastApplyOk)
            {
                failures.Add("écriture gamma refusée");
            }

            viewModel.Temperature = 2700;
            await Task.Delay(400);
            log.AppendLine(
                $"2700 K       : moteur {viewModel.EngineKelvin} K · effectif {viewModel.EngineEffectiveKelvin} K "
                + $"· intensité {viewModel.EngineStrength:P0}");

            if (viewModel.EngineKelvin != 2700)
            {
                failures.Add("2700 K non appliqué");
            }

            if (viewModel.EngineEffectiveKelvin < 2700 || viewModel.EngineEffectiveKelvin > 5200)
            {
                failures.Add($"température effective incohérente ({viewModel.EngineEffectiveKelvin} K)");
            }

            viewModel.Temperature = 3800;
            await Task.Delay(300);
            log.AppendLine(
                $"3800 K       : moteur {viewModel.EngineKelvin} K · effectif {viewModel.EngineEffectiveKelvin} K");

            viewModel.Brightness = 40;
            await Task.Delay(800);
            log.AppendLine(
                $"luminosité   : consigne 40 % · matériel={viewModel.HasHardwareBrightness} "
                + $"· niveau lu {viewModel.HardwareBrightnessLevel?.ToString() ?? "n/a"}");

            viewModel.IsEnabled = false;
            await Task.Delay(1200);
            bool restored = viewModel.VerifyRestore();

            log.AppendLine($"désactivé    : moteur {viewModel.EngineKelvin} K · restauration exacte={restored}");

            if (!restored)
            {
                failures.Add("restauration non exacte");
            }

            if (viewModel.EngineKelvin != ColorTemperature.NeutralKelvin)
            {
                failures.Add("retour au neutre incomplet");
            }

            // La luminosité matérielle doit être rendue à son niveau d'origine.
            if (initialBrightness is int level)
            {
                viewModel.Brightness = level;
                await Task.Delay(700);
                log.AppendLine($"luminosité   : restaurée à {level} % · niveau lu {viewModel.HardwareBrightnessLevel?.ToString() ?? "n/a"}");
            }

            window.Close();
            await Task.Delay(250);
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
            log.AppendLine(ex.ToString());
        }

        if (bindings.Messages.Count == 0)
        {
            log.AppendLine("liaisons     : 0 erreur");
        }
        else
        {
            failures.Add($"{bindings.Messages.Count} erreur(s) de liaison de données");

            foreach (string message in bindings.Messages.Take(5))
            {
                log.AppendLine("  liaison: " + message);
            }
        }

        log.AppendLine();
        log.AppendLine(failures.Count == 0
            ? "UI SMOKE: PASS"
            : "UI SMOKE: FAIL — " + string.Join(" ; ", failures));

        string text = log.ToString();
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nightshift");
        string path = Path.Combine(directory, "ui-smoke.log");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, text, Encoding.UTF8);
            Console.WriteLine("journal: " + path);
        }
        catch (Exception ex)
        {
            // Le fichier est la seule sortie fiable d'une application de bureau.
            failures.Add("journal impossible : " + ex.Message);
            text += Environment.NewLine + "journal impossible : " + ex.Message;
        }

        Console.WriteLine(text);

        Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
    }

    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void Write(string? message)
        {
            // Les erreurs de liaison arrivent par WriteLine.
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Messages.Add(message);
            }
        }
    }
}
