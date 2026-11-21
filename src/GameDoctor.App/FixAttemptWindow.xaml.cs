using System.Windows;
using System.Windows.Controls;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Community;

namespace GameDoctor.App;

/// <summary>Dialog „Lösung ausprobieren“: legt einen <see cref="FixAttempt"/> für eine Absturzstelle an.</summary>
public partial class FixAttemptWindow : Window
{
    private readonly CrashCase _crash;
    private readonly List<(RadioButton Button, FixSuggestion Suggestion)> _suggestions = [];

    public FixAttempt? Created { get; private set; }

    public FixAttemptWindow(CrashCase crash, AnalysisResult result, ModInventory inventory, ModuleClassifier classifier)
    {
        InitializeComponent();
        _crash = crash;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        int same = result.Crashes.Count(c => c.Signature == crash.Signature);
        TargetText.Text = $"Absturzstelle {crash.Signature} · {same}× im gewählten Zeitraum · zuletzt am {crash.Report.Time:dd.MM. HH:mm}";

        foreach (var s in FixSuggestions.For(crash, result, inventory, classifier))
        {
            var rb = new RadioButton
            {
                GroupName = "fix",
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = s.Label, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = s.Reason, Style = (Style)FindResource("Muted") },
                    },
                },
            };
            _suggestions.Add((rb, s));
            SuggestionsPanel.Children.Add(rb);
        }
        if (_suggestions.Count > 0) _suggestions[0].Button.IsChecked = true;
        else { NoSuggestions.Visibility = Visibility.Visible; RbOtherMod.IsChecked = true; }

        ModCombo.ItemsSource = inventory.Mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        _inventory = inventory;
    }

    private readonly ModInventory _inventory;

    private void ModCombo_GotFocus(object sender, RoutedEventArgs e) => RbOtherMod.IsChecked = true;

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        var build = _crash.Report.InternalVersion?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        FixAttempt? attempt = null;

        var chosen = _suggestions.FirstOrDefault(x => x.Button.IsChecked == true).Suggestion;
        if (chosen is not null)
        {
            attempt = new FixAttempt
            {
                Action = chosen.Action,
                TargetLabel = chosen.TargetLabel,
                NexusModId = chosen.NexusModId,
                ModuleFile = chosen.ModuleFile,
                TargetFiles = chosen.TargetFiles.ToList(),
            };
        }
        else if (RbOtherMod.IsChecked == true)
        {
            if (ModCombo.SelectedItem is not InstalledMod mod)
            {
                ErrorText.Text = "Bitte eine Mod aus der Liste wählen.";
                return;
            }
            var action = ActionCombo.SelectedIndex == 1 ? FixAction.UpdateMod : FixAction.DisableMod;
            attempt = new FixAttempt
            {
                Action = action,
                TargetLabel = mod.DisplayName,
                NexusModId = mod.NexusId,
                TargetFiles = _inventory.FilesOf(mod.NexusId).ToList(),
            };
        }
        else if (RbSetting.IsChecked == true)
        {
            if (note is null)
            {
                ErrorText.Text = "Bitte kurz in der Notiz festhalten, was du geändert hast.";
                return;
            }
            attempt = new FixAttempt { Action = FixAction.ChangeSetting, TargetLabel = note.Length > 60 ? note[..60] + " …" : note };
        }

        if (attempt is null)
        {
            ErrorText.Text = "Bitte eine Möglichkeit auswählen.";
            return;
        }

        Created = attempt with { Signatures = [_crash.Signature], GameBuild = build, Note = note };
        DialogResult = true;
    }
}
