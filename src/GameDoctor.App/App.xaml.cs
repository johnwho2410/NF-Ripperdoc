using System.Windows;

namespace GameDoctor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Gespeicherten Skin laden, bevor das erste Fenster erscheint.
        ThemeManager.ApplySaved();
        base.OnStartup(e);
    }
}
