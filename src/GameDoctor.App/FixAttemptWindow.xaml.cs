using GameDoctor.Core.Localization;
using System.Windows;
using System.Windows.Controls;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Community;
using GameDoctor.Core.Games.Cyberpunk;

namespace GameDoctor.App;

/// <summary>
/// Dialog „Lösung ausprobieren“: legt einen <see cref="FixAttempt"/> für eine Absturzstelle an.
/// GameDoctor ändert dabei nichts an den Mods – der Dialog zeigt, wie der Spieler die Änderung selbst vornimmt.
/// </summary>
public partial class FixAttemptWindow : Window
{
    private readonly CrashCase _crash;
    private readonly ModInventory _inventory;
    private readonly CyberpunkInstall? _install;
    private readonly List<(RadioButton Button, FixSuggestion Suggestion)> _suggestions = [];
    private readonly bool _ready;

    public FixAttempt? Created { get; private set; }

    /// <param name="focusMods">Mods, um die es dem Spieler gerade geht (z. B. Konflikt-Mods): werden zuerst vorgeschlagen.</param>
    public FixAttemptWindow(CrashCase crash, AnalysisResult result, ModInventory inventory, ModuleClassifier classifier, IReadOnlyList<string>? focusMods = null)
    {
        InitializeComponent();
        _crash = crash;
        _inventory = inventory;
        _install = result.Install;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        int same = result.Crashes.Count(c => c.Signature == crash.Signature);
        TargetText.Text = L.F("Absturzstelle {0} · {1}× im gewählten Zeitraum · zuletzt am {2:g}", crash.Signature, same, crash.Report.Time);

        bool focus = focusMods is { Count: > 0 };
        if (focus)
            TargetText.Text += "\n" + L.F("Es geht um einen Mod-Konflikt zwischen {0}. Schalte testweise eine der Mods aus und spiele weiter.",
                string.Join(" und ", focusMods!.Take(4).Select(m => $"„{m}“")));

        foreach (var s in FixSuggestions.For(crash, result, inventory, classifier, focus))
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
            rb.Checked += Selection_Changed;
            _suggestions.Add((rb, s));
            SuggestionsPanel.Children.Add(rb);
        }

        ModCombo.ItemsSource = inventory.Mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

        _ready = true;
        if (_suggestions.Count > 0) _suggestions[0].Button.IsChecked = true;
        else { NoSuggestions.Visibility = Visibility.Visible; RbOtherMod.IsChecked = true; }
        UpdateInstructions();
    }

    private void ModCombo_GotFocus(object sender, RoutedEventArgs e) => RbOtherMod.IsChecked = true;

    private void Selection_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateInstructions();
    }

    /// <summary>Anleitung passend zur aktuellen Auswahl und zum erkannten Mod-Manager.</summary>
    private void UpdateInstructions()
    {
        var draft = BuildAttempt(validate: false);
        var manager = _inventory.Manager ?? L.T("kein Mod-Manager erkannt – Anleitung für manuell installierte Mods");
        if (draft is null)
        {
            InstructionsTitle.Text = L.T("So geht’s");
            InstructionsList.ItemsSource = new[] { L.T("Wähle oben aus, was du ausprobieren möchtest.") };
            return;
        }
        InstructionsTitle.Text = draft.Action == FixAction.ChangeSetting ? L.T("So geht’s") : L.F("So geht’s ({0})", manager);
        var steps = FixInstructions.For(draft.Action, draft.TargetLabel, draft.NexusModId, draft.TargetFiles, _inventory, _install);
        InstructionsList.ItemsSource = Numbered(steps);
    }

    /// <summary>Schritte nummerieren; eingerückte Unterpunkte („   • …“) behalten ihre Aufzählungszeichen.</summary>
    internal static List<string> Numbered(IEnumerable<string> steps)
    {
        int n = 0;
        return steps.Select(s => s.StartsWith("   ", StringComparison.Ordinal) ? s : $"{++n}. {s}").ToList();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var attempt = BuildAttempt(validate: true);
        if (attempt is null) return;

        var note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        var build = _crash.Report.InternalVersion?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        Created = attempt with { Signatures = [_crash.Signature], GameBuild = build, Note = note };
        DialogResult = true;
    }

    /// <summary>Baut den Versuch aus der aktuellen Auswahl. Bei <paramref name="validate"/> werden Fehler angezeigt.</summary>
    private FixAttempt? BuildAttempt(bool validate)
    {
        if (validate) ErrorText.Text = "";
        var note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();

        var chosen = _suggestions.FirstOrDefault(x => x.Button.IsChecked == true).Suggestion;
        if (chosen is not null)
            return new FixAttempt
            {
                Action = chosen.Action,
                TargetLabel = chosen.TargetLabel,
                NexusModId = chosen.NexusModId,
                ModuleFile = chosen.ModuleFile,
                TargetFiles = chosen.TargetFiles.ToList(),
            };

        if (RbOtherMod.IsChecked == true)
        {
            if (ModCombo.SelectedItem is not InstalledMod mod)
            {
                if (validate) ErrorText.Text = L.T("Bitte eine Mod aus der Liste wählen.");
                return null;
            }
            return new FixAttempt
            {
                Action = ActionCombo.SelectedIndex == 1 ? FixAction.UpdateMod : FixAction.DisableMod,
                TargetLabel = mod.DisplayName,
                NexusModId = mod.NexusId,
                TargetFiles = _inventory.FilesOf(mod.NexusId).ToList(),
            };
        }

        if (RbSetting.IsChecked == true)
        {
            if (note is null && validate)
            {
                ErrorText.Text = L.T("Bitte kurz in der Notiz festhalten, was du geändert hast.");
                return null;
            }
            var label = note ?? L.T("Einstellung geändert");
            return new FixAttempt { Action = FixAction.ChangeSetting, TargetLabel = label.Length > 60 ? label[..60] + " …" : label };
        }

        if (validate) ErrorText.Text = L.T("Bitte eine Möglichkeit auswählen.");
        return null;
    }
}
