using System.Windows;

namespace GameDoctor.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Sprache vor allem anderen setzen: gespeichert, sonst nach der Windows-Sprache.
        var lang = AppSettings.Load().Language;
        if (lang is null)
        {
            lang = GameDoctor.Core.Localization.L.FromSystem();
            AppSettings.Update(s => s.Language = lang);
        }
        GameDoctor.Core.Localization.L.SetLanguage(lang);

        // Gespeicherten Skin laden, bevor das erste Fenster erscheint.
        ThemeManager.ApplySaved();
        base.OnStartup(e);
    }
}
