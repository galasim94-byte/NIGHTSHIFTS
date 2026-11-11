using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Nightshift.UI.Theme;
using Nightshift.UI.ViewModels;

namespace Nightshift.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
        => WindowTheme.Apply(new WindowInteropHelper(this).Handle);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Bouton relâché avant le début du glissement : sans effet.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Restaure l'écran pendant que la fenêtre est encore vivante :
        // même si la fermeture est annulée plus tard, la table d'origine est déjà là.
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosing(e);
    }
}
