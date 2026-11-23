using GameDoctor.Core.Localization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Community;
using GameDoctor.Core.Games.Cyberpunk;
using GameDoctor.Core.Performance;
using GameDoctor.Core.Report;
using GameDoctor.Core.Symbols;
using Microsoft.Win32;

namespace GameDoctor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        InitPerformance();
        Loaded += async (_, _) =>
        {
            _loaded = true;
            await RunAnalysisAsync();
        };
    }

    private async Task RunAnalysisAsync()
    {
        if (_vm.IsBusy) return;
        _vm.IsBusy = true;
        _vm.Status = L.T("Starte Analyse …");
        try
        {
            var options = new CrashAnalyzerOptions { Days = _vm.Days };
            // Fortschritt kommt aus dem Hintergrund-Thread und wird auf den UI-Thread übertragen
            var progress = new Progress<string>(msg => _vm.Status = msg);
            var result = await Task.Run(() => CrashAnalyzer.Run(options, ((IProgress<string>)progress).Report));
            _vm.Load(result);
            await RefreshFixesAsync(result);
            _vm.Status = L.F("Analyse abgeschlossen: {0} Abstürze, {1:t}", result.Crashes.Count, DateTime.Now);
        }
        catch (Exception ex)
        {
            _vm.Status = L.F("Analyse fehlgeschlagen: {0}", ex.Message);
            MessageBox.Show(this, ex.ToString(), L.T("Analyse fehlgeschlagen"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await RunAnalysisAsync();

    private async void Days_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && int.TryParse(tag, out var days))
        {
            _vm.Days = days;
            if (_loaded) await RunAnalysisAsync();
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverviewPage is null) return; // während InitializeComponent
        OverviewPage.Visibility = Tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        CrashPage.Visibility = Tabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        PerfPage.Visibility = Tabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        EnvPage.Visibility = Tabs.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        FixPage.Visibility = Tabs.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ Datenquellen

    private async void SetGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Cyberpunk-2077-Spielordner auswählen (der Ordner, in dem „bin“ und „archive“ liegen)") };
        if (dlg.ShowDialog(this) != true) return;

        var folder = NormalizeGameFolder(dlg.FolderName);
        if (folder is null)
        {
            MessageBox.Show(this, L.F("In „{0}“ wurde kein Cyberpunk 2077 gefunden (bin\\x64\\Cyberpunk2077.exe fehlt).", dlg.FolderName) + "\n\n" +
                L.T("Bitte den Hauptordner des Spiels wählen – bei Steam z. B. …\\steamapps\\common\\Cyberpunk 2077."),
                L.T("Kein Spielordner"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var ps = GameDoctor.Core.Platform.PathSettings.Load();
        ps.GamePath = folder;
        ps.Save();
        await RunAnalysisAsync();
    }

    /// <summary>Nimmt auch versehentlich gewählte Unterordner an (bin oder bin\x64).</summary>
    private static string? NormalizeGameFolder(string folder)
    {
        for (var dir = folder; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (CyberpunkInstall.IsGameFolder(dir)) return dir;
            if (dir.Length <= 3) break;
            if (!folder.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || folder.Length - dir.Length > 12) break;
        }
        return null;
    }

    private async void SetMo2_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Mod-Organizer-2-Instanz auswählen (Ordner mit ModOrganizer.ini)") };
        if (dlg.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(dlg.FolderName, "ModOrganizer.ini")))
        {
            MessageBox.Show(this, L.T("In diesem Ordner liegt keine ModOrganizer.ini.\n\nBei einer portablen Installation ist das der Ordner von ModOrganizer.exe, sonst %LOCALAPPDATA%\\ModOrganizer\\<Instanzname>."), L.T("Keine MO2-Instanz"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var ps = GameDoctor.Core.Platform.PathSettings.Load();
        ps.Mo2InstancePath = dlg.FolderName;
        ps.Save();
        await RunAnalysisAsync();
    }

    private async void ResetPaths_Click(object sender, RoutedEventArgs e)
    {
        var ps = GameDoctor.Core.Platform.PathSettings.Load();
        ps.GamePath = null;
        ps.Mo2InstancePath = null;
        ps.Save();
        await RunAnalysisAsync();
    }

    private async void SearchGame_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                L.T("GameDoctor durchsucht jetzt die lokalen Laufwerke nach Cyberpunk 2077 (höchstens 60 Sekunden).\n\nDabei werden nur Ordnernamen geprüft – keine Dateien geöffnet, nichts übertragen. Windows- und Systemordner werden übersprungen."),
                L.T("Nach dem Spiel suchen"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;

        _vm.IsBusy = true;
        List<string> hits;
        try
        {
            var progress = new Progress<string>(d => _vm.Status = L.F("Durchsuche {0}", d));
            hits = await Task.Run(() => CyberpunkInstall.SearchDrives(TimeSpan.FromSeconds(60), default, ((IProgress<string>)progress).Report));
        }
        finally
        {
            _vm.IsBusy = false;
        }

        if (hits.Count == 0)
        {
            _vm.Status = L.T("Keine Cyberpunk-Installation gefunden.");
            MessageBox.Show(this, L.T("Keine Installation gefunden. Bitte den Spielordner manuell festlegen."), L.T("Nichts gefunden"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var hit in hits)
        {
            var answer = MessageBox.Show(this, L.F("Gefunden:\n{0}\n\nDiesen Ordner verwenden?", hit), L.T("Cyberpunk gefunden"),
                hits.Count > 1 ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer != MessageBoxResult.Yes) continue;
            var ps = GameDoctor.Core.Platform.PathSettings.Load();
            ps.GamePath = hit;
            ps.Save();
            await RunAnalysisAsync();
            return;
        }
    }

    // ------------------------------------------------------------------ Lösungsversuche

    private ModInventory _inventory = new();

    /// <summary>Sitzungsprotokoll fortschreiben und alle Lösungsversuche neu bewerten.</summary>
    /// <param name="redetectMods">Mod-Liste frisch einlesen – nötig, nachdem der Spieler in Vortex/MO2 etwas geändert hat.</param>
    private async Task RefreshFixesAsync(AnalysisResult result, bool redetectMods = false)
    {
        var (vms, steps) = await Task.Run(() =>
        {
            _inventory = redetectMods && result.Install is not null ? ModInventory.Detect(result.Install) : result.Inventory;
            var ledger = PlaySessionLedger.Load();
            ledger.Harvest(result.Install, result.Crashes);
            try { ledger.Save(); } catch { /* nicht kritisch */ }
            var evaluator = new FixEvaluator(result.Crashes, ledger, result.Install, _inventory.ExtraRoots);
            var evaluated = FixAttemptStore.Load().Select(a => (Attempt: a, Eval: evaluator.Evaluate(a))).ToList();
            var viewModels = evaluated.Select(x => new FixAttemptViewModel(x.Attempt, x.Eval, _inventory, result.Install)).ToList();
            return (viewModels, NextStepAdvisor.Build(result, evaluated, BisectStore.Load()));
        });
        _vm.SetFixes(vms);
        _vm.SetNextSteps(steps);
    }

    private async void OpenBisect_Click(object sender, RoutedEventArgs e) => await OpenBisectAsync();

    /// <summary>Öffnet den Assistenten „Welche Mod ist es?“.</summary>
    private async Task OpenBisectAsync()
    {
        if (_vm.Result is not { } result) return;
        var win = new BisectWindow(result, _inventory, async () => { await RunAnalysisAsync(); return _vm.Result; }) { Owner = this };
        win.ShowDialog();
        if (win.Changed && _vm.Result is { } current) await RefreshFixesAsync(current);
    }

    /// <summary>Knopf in der Karte „Dein nächster Schritt“.</summary>
    private async void NextStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NextStepViewModel vm } || _vm.Result is not { } result) return;
        switch (vm.Step.Action)
        {
            case NextStepAction.TryFix when vm.Step.Crash is { } crash:
            {
                var dlg = new FixAttemptWindow(crash, result, _inventory, result.Classifier) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Created is null) return;
                FixAttemptStore.Upsert(dlg.Created);
                await RefreshFixesAsync(result);
                Tabs.SelectedIndex = 4;
                break;
            }
            case NextStepAction.OpenFixes:
                Tabs.SelectedIndex = 4;
                break;
            case NextStepAction.OpenBisect:
                await OpenBisectAsync();
                break;
        }
    }

    private async void FixRecheck_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is not { } result) return;
        _vm.Status = L.T("Prüfe Lösungsversuche …");
        await RefreshFixesAsync(result, redetectMods: true);
        _vm.Status = L.F("Lösungsversuche geprüft ({0:t}).", DateTime.Now);
    }

    private async void NewFix_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is not { } result || _vm.SelectedCrash?.Case is not { } crash) return;
        var dlg = new FixAttemptWindow(crash, result, _inventory, result.Classifier) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Created is null) return;
        FixAttemptStore.Upsert(dlg.Created);
        await RefreshFixesAsync(result);
        Tabs.SelectedIndex = 4;
        _vm.Status = dlg.Created.Action == FixAction.DisableMod
            ? L.F("Beobachtung gestartet: {0}. Jetzt die Mod selbst deaktivieren und dann „Erneut prüfen“ klicken.", dlg.Created.TargetLabel)
            : L.F("Beobachtung gestartet: {0}. Spiel jetzt ganz normal weiter – GameDoctor wertet bei jeder Analyse aus.", dlg.Created.TargetLabel);
    }

    private async void FixHelped_Click(object sender, RoutedEventArgs e) => await SetVerdict(sender, UserVerdict.Helped);
    private async void FixNotHelped_Click(object sender, RoutedEventArgs e) => await SetVerdict(sender, UserVerdict.NotHelped);

    private async Task SetVerdict(object sender, UserVerdict verdict)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || _vm.Result is not { } result) return;
        var attempt = FixAttemptStore.Load().FirstOrDefault(a => a.Id == id);
        if (attempt is null) return;
        // Ein zweiter Klick auf dieselbe Einschätzung nimmt sie zurück
        var newVerdict = attempt.Verdict == verdict ? UserVerdict.None : verdict;
        FixAttemptStore.Upsert(attempt with { Verdict = newVerdict, VerdictAt = newVerdict == UserVerdict.None ? null : DateTime.Now });
        await RefreshFixesAsync(result);
    }

    private async void FixRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id } || _vm.Result is not { } result) return;
        if (MessageBox.Show(this, L.T("Diesen Lösungsversuch entfernen?"), L.T("Entfernen"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        FixAttemptStore.Remove(id);
        await RefreshFixesAsync(result);
    }

    // ------------------------------------------------------------------ Performance

    private PerformanceRecorder? _recorder;
    private OverlayController _overlay = null!;

    private void InitPerformance()
    {
        // ---- Ingame-Overlay
        _overlay = new OverlayController(this)
        {
            // Das Overlay braucht Live-Werte: läuft keine Aufnahme, wird eine gestartet.
            EnsureLiveData = () =>
            {
                if (OverlayController.WindowModeProblem(CyberpunkSettings.TryLoad()?.WindowMode) is { } problem)
                {
                    var answer = MessageBox.Show(this, problem + "\n\n" + L.T("Overlay trotzdem einblenden?"), L.T("Ingame-Overlay"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return false;
                }
                if (_recorder is null) StartRecording();
                return true;
            },
        };
        _overlay.VisibilityChanged += visible => _vm.Perf.OverlayVisible = visible;
        _vm.Perf.OverlayCorner = _overlay.Corner;
        Closed += (_, _) => _overlay.Dispose();

        _vm.Perf.PresentMonAvailable = PresentMonTool.Find() is not null;
        _vm.Perf.Hint = L.T("Starte die Aufnahme, wechsle ins Spiel und spiele 2–5 Minuten. Danach hier auf „Aufnahme beenden“.");
        Closing += (_, _) =>
        {
            // Läuft beim Schließen noch eine Aufnahme, wird sie wenigstens gespeichert
            if (_recorder is not null)
            {
                var rec = _recorder.Stop();
                if (rec.Frames.Count > 0) try { RecordingStore.Save(rec); } catch { /* egal */ }
            }
        };

        // Letzte Aufnahme anzeigen, damit das Ergebnis nach einem Neustart nicht weg ist
        if (RecordingStore.Latest() is { } latest)
        {
            try { ShowRecording(RecordingStore.Load(latest), L.F("Letzte Aufnahme geladen ({0:g}).", File.GetLastWriteTime(latest))); }
            catch { /* defekte Datei ignorieren */ }
        }
    }

    private PerformanceRecording? _shownRecording;

    private void ShowRecording(PerformanceRecording recording, string hint)
    {
        _shownRecording = recording;
        var report = PerformanceAnalyzer.Analyze(recording, CyberpunkSettings.TryLoad());
        _vm.Perf.ShowReport(report);
        _vm.Perf.Hint = hint;
    }

    private void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Aufnahme öffnen"),
            Filter = "GameDoctor-Aufnahme (*.json.gz)|*.json.gz",
            InitialDirectory = Directory.Exists(RecordingStore.Dir) ? RecordingStore.Dir : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ShowRecording(RecordingStore.Load(dlg.FileName), L.F("Aufnahme geladen: {0}", Path.GetFileName(dlg.FileName)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, L.T("Aufnahme konnte nicht geladen werden"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartRecording()
    {
        var perf = _vm.Perf;
        // Entwickler-Schalter: anderes Programm messen (z. B. zum Testen ohne Spiel)
        var process = Environment.GetEnvironmentVariable("GAMEDOCTOR_PERF_PROCESS") ?? "Cyberpunk2077.exe";
        _recorder = new PerformanceRecorder(process);
        _recorder.LiveUpdated += s => Dispatcher.BeginInvoke(() =>
        {
            perf.ShowLive(s);
            _overlay.OnLive(s);
        });
        _recorder.Start();
        perf.IsRecording = true;
        perf.Hint = L.T("Aufnahme läuft …");
        _vm.Status = L.T("Performance-Aufnahme gestartet");
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        var perf = _vm.Perf;
        if (_recorder is null)
        {
            StartRecording();
            return;
        }

        var recording = _recorder.Stop();
        _recorder.Dispose();
        _recorder = null;
        _overlay.Hide(); // ohne Messung hätte das Overlay nur veraltete Werte
        perf.IsRecording = false;
        string saved = "";
        if (recording.Frames.Count > 0 || recording.Sensors.Count > 0)
        {
            try { saved = " " + L.F("Gespeichert unter {0}", RecordingStore.Save(recording)); }
            catch (Exception ex) { saved = " " + L.F("(Speichern fehlgeschlagen: {0})", ex.Message); }
        }
        ShowRecording(recording, L.F("Aufnahme beendet: {0} min.", recording.Duration.ToString(@"mm\:ss")));
        _vm.Status = perf.Hint + saved;
    }

    private void Overlay_Click(object sender, RoutedEventArgs e) => _overlay.Toggle();

    private void OverlayCorner_Checked(object sender, RoutedEventArgs e)
    {
        if (_overlay is null || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<OverlayCorner>(tag, out var corner)) return;
        _overlay.Corner = corner;
        _vm.Perf.OverlayCorner = corner;
    }

    private async void DownloadPresentMon_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            L.F("PresentMon (Intel, Open Source, MIT-Lizenz, ca. 1 MB) wird vom offiziellen GitHub-Release heruntergeladen:\n\n{0}\n\nDie Datei wird per Prüfsumme kontrolliert und unter\n{1}\nabgelegt. Fortfahren?",
                PresentMonTool.DownloadUrl, PresentMonTool.ToolsDir),
            L.T("PresentMon herunterladen"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _vm.Status = L.T("Lade PresentMon herunter …");
            await PresentMonTool.DownloadAsync();
            _vm.Perf.PresentMonAvailable = true;
            _vm.Status = L.T("PresentMon installiert.");
        }
        catch (Exception ex)
        {
            _vm.Status = L.F("Download fehlgeschlagen: {0}", ex.Message);
            MessageBox.Show(this, ex.Message, L.T("Download fehlgeschlagen"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Lädt die optionalen Funktionsnamen (Community-Projekt cp2077-symbols) – nur nach ausdrücklicher Zustimmung.</summary>
    private async void DownloadSymbols_Click(object sender, RoutedEventArgs e)
    {
        var root = _vm.Result?.Install?.Root;
        if (root is null)
        {
            MessageBox.Show(this, L.T("Cyberpunk 2077 wurde nicht gefunden. Bitte zuerst den Spielordner festlegen."), L.T("Funktionsnamen laden"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            L.F("Mit den Funktionsnamen kann GameDoctor Absturzstellen benennen und einem Teilsystem des Spiels zuordnen (z. B. „Sinne“ oder „Waffen“).\n\nDie Namen stammen vom Community-Projekt cp2077-symbols (MIT-Lizenz, inoffiziell, für Patch 2.5):\n{0}\n\nHerunterladen werden ca. 59 MB von codeberg.org. Die Datei wird per Prüfsumme kontrolliert, lokal in\n{1}\nabgelegt und mit der Adressdatei deines Spiels abgeglichen. Es wird nichts gesendet. Die Namen können falsch sein.\n\nFortfahren?",
                SymbolDatabase.ProjectUrl, SymbolDatabase.Dir),
            L.T("Funktionsnamen laden"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _vm.IsBusy = true;
            var progress = new Progress<string>(msg => _vm.Status = msg);
            await SymbolDatabase.InstallAsync(root, progress);
            _vm.IsBusy = false;
            await RunAnalysisAsync();
            _vm.Status = _vm.Result?.SymbolStatus == SymbolStatus.Ready
                ? L.T("Funktionsnamen geladen.")
                : L.T("Funktionsnamen geladen, passen aber nicht zur installierten Spielversion.");
        }
        catch (Exception ex)
        {
            _vm.IsBusy = false;
            _vm.Status = L.F("Download fehlgeschlagen: {0}", ex.Message);
            MessageBox.Show(this, ex.Message, L.T("Download fehlgeschlagen"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is null) return;
        var dlg = new SaveFileDialog
        {
            Title = L.T("Bericht speichern"),
            Filter = L.T("Markdown-Bericht (*.md)|*.md"),
            FileName = L.F("cyberpunk-absturzbericht-{0:yyyy-MM-dd}.md", DateTime.Now),
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, MarkdownReport.Build(_vm.Result, 10), Encoding.UTF8);
        _vm.Status = L.F("Bericht gespeichert: {0}", dlg.FileName);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedCrash?.Folder is { } folder && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ KI-Prompt

    private void Prompt_Click(object sender, RoutedEventArgs e) => OpenPrompt(null);

    private void CrashPrompt_Click(object sender, RoutedEventArgs e) => OpenPrompt(_vm.SelectedCrash?.Case);

    private void OpenPrompt(CrashCase? focus)
    {
        if (_vm.Result is null) return;
        new PromptWindow(_vm.Result, focus, _vm.Perf.LastReport) { Owner = this }.Show();
    }

    // ------------------------------------------------------------------ Skin

    /// <summary>Wechselt zwischen Deutsch und Englisch, merkt sich die Wahl und analysiert neu.</summary>
    private async void Language_Click(object sender, RoutedEventArgs e)
    {
        var lang = L.Language == "de" ? "en" : "de";
        AppSettings.Update(s => s.Language = lang);
        L.SetLanguage(lang);
        _vm.Perf.RefreshLanguage();
        if (_shownRecording is not null && _recorder is null) ShowRecording(_shownRecording, "");
        if (_vm.IsIdle) await RunAnalysisAsync();
    }

    private void Skin_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = SkinButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var skin in ThemeManager.Skins)
        {
            var item = new MenuItem
            {
                Header = L.T(skin.Name),
                ToolTip = L.T(skin.Description),
                IsCheckable = true,
                IsChecked = skin.Id == ThemeManager.Current.Id,
            };
            item.Click += (_, _) => ThemeManager.Apply(skin.Id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void UseDarkTitleBar() => DarkTitleBar.Apply(this);
}

/// <summary>Dunkle Titelleiste (Windows 10 20H1+ / Windows 11).</summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }
}
