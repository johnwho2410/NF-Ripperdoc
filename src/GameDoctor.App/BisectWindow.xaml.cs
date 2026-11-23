using System.ComponentModel;
using System.Windows;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Community;
using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Localization;

namespace GameDoctor.App;

/// <summary>
/// Assistent „Welche Mod ist es?“: grenzt die Mod, die Abstürze auslöst, durch schrittweises Halbieren ein.
/// GameDoctor ändert nichts an den Mods – der Spieler schaltet sie selbst um; das Fenster führt durch die Schritte und merkt sich den Stand.
/// </summary>
public partial class BisectWindow : Window
{
    private enum Mode { Setup, RoundNotStarted, Watching, WatchingDecided, Done }

    private sealed class CandidateVm(BisectMod mod) : INotifyPropertyChanged
    {
        private bool _checked = true;
        public BisectMod Mod { get; } = mod;
        public string Display => Mod.Suspect ? Mod.Name + "  " + L.T("(in Abstürzen aufgefallen)") : Mod.Name;
        public bool IsChecked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private AnalysisResult _result;
    private ModInventory _inventory;
    private readonly Func<Task<AnalysisResult?>> _reanalyze;
    private BisectSession? _session;
    private BisectEvaluation? _eval;
    private List<CandidateVm> _candidates = [];
    private Mode _mode;

    /// <summary>Wurde der Stand verändert (neue Runde, Ergebnis …)? Dann sollte die Hauptansicht ihre Hinweise neu berechnen.</summary>
    public bool Changed { get; private set; }

    public BisectWindow(AnalysisResult result, ModInventory inventory, Func<Task<AnalysisResult?>> reanalyze)
    {
        InitializeComponent();
        _result = result;
        _inventory = inventory;
        _reanalyze = reanalyze;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _session = BisectStore.Load();
        Render();
    }

    // ------------------------------------------------------------------ Anzeige

    private void Render()
    {
        if (_session is null) ShowSetup();
        else if (_session.Done) ShowDone();
        else ShowRound();
    }

    private void SetButtons(Mode mode, string primary, bool primaryEnabled = true, string? secondary = null, string? tertiary = null, bool cancel = false)
    {
        _mode = mode;
        PrimaryActionButton.Content = primary;
        PrimaryActionButton.IsEnabled = primaryEnabled;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        TertiaryButton.Content = tertiary;
        TertiaryButton.Visibility = tertiary is null ? Visibility.Collapsed : Visibility.Visible;
        CancelSearchButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSetup()
    {
        SetupPanel.Visibility = Visibility.Visible;
        RoundPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        IntroText.Text = L.T("Wenn das Spiel abstürzt und keine einzelne Mod klar auffällt, hilft nur Eingrenzen. GameDoctor führt dich dabei.");

        var counts = BisectPlanner.SuspectCounts(_result.Crashes, _inventory);
        _candidates = BisectPlanner.Candidates(_inventory, counts)
            .OrderByDescending(m => m.Suspect).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new CandidateVm(m)).ToList();
        CandidateList.ItemsSource = _candidates;
        SelectFlaggedButton.IsEnabled = _candidates.Any(c => c.Mod.Suspect);
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        if (_mode != Mode.Setup && _session is not null) return;
        int n = _candidates.Count(c => c.IsChecked);
        EstimateText.Text = n < 2
            ? L.T("Wähle mindestens zwei Mods aus.")
            : L.F("{0} Mods ausgewählt – das dauert voraussichtlich etwa {1} Runden (je eine Spielsitzung von ein bis zwei Stunden).", n, BisectSession.EstimateRounds(n));
        SetButtons(Mode.Setup, L.T("Suche starten"), primaryEnabled: n >= 2);
    }

    private void ShowRound()
    {
        var s = _session!;
        SetupPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        RoundPanel.Visibility = Visibility.Visible;
        IntroText.Text = L.T("GameDoctor merkt sich den Stand. Du kannst das Fenster jederzeit schließen und später weitermachen.");

        RoundTitle.Text = s.Current!.Confirming
            ? L.F("Runde {0} – Bestätigung", s.Round)
            : L.F("Runde {0} – noch {1} verdächtige Mods", s.Round, s.Suspects.Count);
        RoundSubtitle.Text = L.F("Insgesamt voraussichtlich noch etwa {0} Runden.", BisectSession.EstimateRounds(s.Suspects.Count));

        HistoryList.ItemsSource = s.History.Select(h => L.F("Runde {0}: {1} Mods ausgeschaltet → {2}", h.Number, h.Disabled.Count,
            h.Outcome == BisectOutcome.Crashed ? L.T("weiter abgestürzt (Ursache bei den anderen Mods)") : L.T("stabil (Ursache bei den ausgeschalteten Mods)"))).ToList();
        HistoryTitle.Visibility = HistoryList.Visibility = s.History.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (s.Current.StartedAt is null)
        {
            InstructionsPanel.Visibility = Visibility.Visible;
            WatchPanel.Visibility = Visibility.Collapsed;
            InstructionsList.ItemsSource = BisectPlanner.Instructions(s, _inventory);
            CheckText.Text = "";
            SetButtons(Mode.RoundNotStarted, L.T("Runde starten"), secondary: L.T("Umstellung prüfen"), cancel: true);
        }
        else
        {
            InstructionsPanel.Visibility = Visibility.Collapsed;
            WatchPanel.Visibility = Visibility.Visible;
            Evaluate();
        }
    }

    private void Evaluate()
    {
        var ledger = PlaySessionLedger.Load();
        ledger.Harvest(_result.Install, _result.Crashes);
        _eval = BisectEvaluator.Evaluate(_session!, _result.Crashes, ledger);
        WatchProgress.Value = _eval.Progress * 100;
        WatchText.Text = _eval.Text;
        switch (_eval.Status)
        {
            case BisectStatus.Crashed:
                WatchHeadline.Text = L.T("Das Spiel ist abgestürzt – die Ursache liegt bei den Mods, die an geblieben sind.");
                SetButtons(Mode.WatchingDecided, L.T("Ergebnis übernehmen"), cancel: true);
                break;
            case BisectStatus.Stable:
                WatchHeadline.Text = L.T("Das Spiel lief stabil – die Ursache liegt bei den Mods, die aus waren.");
                SetButtons(Mode.WatchingDecided, L.T("Ergebnis übernehmen"), cancel: true);
                break;
            default:
                WatchHeadline.Text = L.T("Die Runde läuft.");
                SetButtons(Mode.Watching, L.T("Aktualisieren"), secondary: L.T("Kein Absturz – genug gespielt"), tertiary: L.T("Es ist abgestürzt"), cancel: true);
                break;
        }
    }

    private void ShowDone()
    {
        var s = _session!;
        SetupPanel.Visibility = Visibility.Collapsed;
        RoundPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        IntroText.Text = L.T("Die Suche ist beendet.");
        DoneHeadline.Text = s.Culprit is not null ? L.F("Gefunden: „{0}“", s.Culprit) : L.T("Keine einzelne Mod gefunden");
        DoneText.Text = s.Conclusion ?? "";
        DoneNext.Text = s.Culprit is not null
            ? L.T("Was jetzt: Schau auf der Mod-Seite nach einem Update oder bekannten Problemen. Hilft das nicht, lass die Mod weg. Mit „Als Lösungsversuch eintragen“ beobachtet GameDoctor weiter, ob die Abstürze ausbleiben.")
            : L.T("Was jetzt: Prüfe die anderen Hinweise in GameDoctor (Grafiktreiber, Auslagerungsdatei, Hardware) und teste einzelne Mods, die dir verdächtig vorkommen.");
        SetButtons(Mode.Done, L.T("Neue Suche"), secondary: s.Culprit is not null ? L.T("Als Lösungsversuch eintragen") : null);
    }

    // ------------------------------------------------------------------ Eingaben

    private void Candidate_Changed(object sender, RoutedEventArgs e) => UpdateEstimate();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var c in _candidates) c.IsChecked = true; UpdateEstimate(); }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var c in _candidates) c.IsChecked = false; UpdateEstimate(); }
    private void SelectFlagged_Click(object sender, RoutedEventArgs e) { foreach (var c in _candidates) c.IsChecked = c.Mod.Suspect; UpdateEstimate(); }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Setup:
                _session = BisectPlanner.Start(_candidates.Where(c => c.IsChecked).Select(c => c.Mod).ToList());
                SaveSession();
                Render();
                break;

            case Mode.RoundNotStarted:
                if (CheckApplied(out var problems) && problems.Count > 0 &&
                    MessageBox.Show(this, L.T("GameDoctor sieht noch Abweichungen:") + "\n\n" + string.Join("\n", problems) + "\n\n" + L.T("Trotzdem starten?"),
                        L.T("Runde starten"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                BisectPlanner.BeginRound(_session!);
                SaveSession();
                Render();
                break;

            case Mode.Watching:
                PrimaryActionButton.IsEnabled = false;
                if (await _reanalyze() is { } fresh) { _result = fresh; _inventory = fresh.Inventory; }
                Evaluate();
                break;

            case Mode.WatchingDecided:
                ApplyOutcome(_eval!.Status == BisectStatus.Crashed ? BisectOutcome.Crashed : BisectOutcome.Stable);
                break;

            case Mode.Done:
                BisectStore.Clear();
                _session = null;
                Changed = true;
                Render();
                break;
        }
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.RoundNotStarted:
                if (!CheckApplied(out var problems))
                    CheckText.Text = L.T("GameDoctor kann hier nicht prüfen, ob die Mods umgestellt sind (keine Dateiliste von deinem Mod-Manager). Achte selbst darauf.");
                else
                    CheckText.Text = problems.Count == 0 ? L.T("✔ Die Umstellung stimmt.") : string.Join("\n", problems);
                break;

            case Mode.Watching:
                if (_eval is { Status: BisectStatus.Waiting } &&
                    MessageBox.Show(this, L.T("GameDoctor meint, die Runde war noch zu kurz für ein belastbares Ergebnis. Trotzdem als stabil werten?"),
                        L.T("Kein Absturz"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                ApplyOutcome(BisectOutcome.Stable);
                break;

            case Mode.Done:
                CreateFixAttempt();
                break;
        }
    }

    private void Tertiary_Click(object sender, RoutedEventArgs e) => ApplyOutcome(BisectOutcome.Crashed);

    private void CancelSearch_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, L.T("Die Suche wirklich abbrechen? Der bisherige Stand geht verloren."), L.T("Suche abbrechen"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BisectStore.Clear();
        _session = null;
        Changed = true;
        Render();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ Logik

    private void ApplyOutcome(BisectOutcome outcome)
    {
        BisectPlanner.Finish(_session!, outcome, _eval?.PlayTime ?? TimeSpan.Zero);
        SaveSession();
        Render();
    }

    private void SaveSession()
    {
        BisectStore.Save(_session!);
        Changed = true;
    }

    /// <summary>Vergleicht den Spielordner mit der Anleitung der Runde. false = nicht prüfbar.</summary>
    private bool CheckApplied(out List<string> problems)
    {
        problems = [];
        if (_result.Install is null || _session?.Current is null) return false;
        var installed = FixEvaluator.IndexModFiles(_result.Install, _inventory.ExtraRoots);
        var (stillActive, stillOff) = BisectPlanner.CheckApplied(_session, installed);
        if (stillActive.Count > 0)
            problems.Add(L.F("Noch an, sollten aus sein: {0}", string.Join(", ", stillActive.Take(6)) + (stillActive.Count > 6 ? " …" : "")));
        if (stillOff.Count > 0)
            problems.Add(L.F("Noch aus, sollten wieder an sein: {0}", string.Join(", ", stillOff.Take(6)) + (stillOff.Count > 6 ? " …" : "")));
        return true;
    }

    private void CreateFixAttempt()
    {
        if (_session?.Culprit is not { } name) return;
        var mod = _session.Pool.FirstOrDefault(m => m.Name == name);
        var signatures = _result.Crashes.Where(c => c.Signature != "?").GroupBy(c => c.Signature).Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToList();
        FixAttemptStore.Upsert(new FixAttempt
        {
            Action = FixAction.DisableMod,
            TargetLabel = name,
            NexusModId = mod?.NexusId,
            TargetFiles = mod?.Files ?? [],
            Signatures = signatures,
            Note = L.T("Ergebnis der Mod-Suche"),
        });
        Changed = true;
        MessageBox.Show(this, L.T("Eingetragen. Du findest den Versuch jetzt im Tab „Lösungen“."), L.T("Als Lösungsversuch eintragen"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
