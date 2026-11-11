using System.Windows;
using Microsoft.Win32;
using Nightshift.UI.ViewModels;

namespace Nightshift.UI;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            UiSmoke.Run();
            return;
        }

        _viewModel = new MainViewModel();

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        window.Show();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Rebranchement d'un écran : le moteur ré-énumère et conserve les snapshots.
        Dispatcher.BeginInvoke(() => _viewModel?.RefreshDisplays());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // Filet de sécurité : Dispose restaure les tables d'origine (idempotent).
        _viewModel?.Dispose();
        base.OnExit(e);
    }
}
