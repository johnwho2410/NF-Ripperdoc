using System.ComponentModel;
using System.Windows;

namespace GameDoctor.App;

/// <summary>
/// Einfache Ansicht (Standard) oder Experten-Ansicht. In der einfachen Ansicht stehen nur Erklärungen und Schritte,
/// die einem Spieler helfen – Adressen, Fehlercodes und Statistik erscheinen erst, wenn „Technische Details“ eingeschaltet ist.
/// </summary>
public sealed class ViewMode : INotifyPropertyChanged
{
    public static ViewMode Instance { get; } = new();

    private bool _expert = AppSettings.Load().ExpertView;

    public bool Expert
    {
        get => _expert;
        set
        {
            if (_expert == value) return;
            _expert = value;
            AppSettings.Update(s => s.ExpertView = value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expert)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpertVisibility)));
        }
    }

    public Visibility ExpertVisibility => _expert ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
