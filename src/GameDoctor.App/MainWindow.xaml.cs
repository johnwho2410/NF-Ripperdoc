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
        _vm.Status = "Starte Analyse …";
        try
        {
            var options = new CrashAnalyzerOptions { Days = _vm.Days };
            // Fortschritt kommt aus dem Hintergrund-Thread und wird auf den UI-Thread übertragen
            var progress = new Progress<string>(msg => _vm.Status = msg);
            var result = await Task.Run(() => CrashAnalyzer.Run(options, ((IProgress<string>)progress).Report));
            _vm.Load(result);
            await RefreshFixesAsync(result);
            _vm.Status = $"Analyse abgeschlossen: {result.Crashes.Count} Abstürze, {DateTime.Now:HH:mm} Uhr";
        }
        catch (Exception ex)
        {
            _vm.Status = "Analyse fehlgeschlagen: " + ex.Message;
            MessageBox.Show(this, ex.ToString(), "Analyse fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var dlg = new OpenFolderDialog { Title = "Cyberpunk-2077-Spielordner auswählen (der Ordner, in dem „bin“ und „archive“ liegen)" };
        if (dlg.ShowDialog(this) != true) return;

        var folder = NormalizeGameFolder(dlg.FolderName);
        if (folder is null)
        {
            MessageBox.Show(this, $"In „{dlg.FolderName}“ wurde kein Cyberpunk 2077 gefunden (bin\\x64\\Cyberpunk2077.exe fehlt).\n\n" +
                "Bitte den Hauptordner des Spiels wählen – bei Steam z. B. …\\steamapps\\common\\Cyberpunk 2077.",
                "Kein Spielordner", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var dlg = new OpenFolderDialog { Title = "Mod-Organizer-2-Instanz auswählen (Ordner mit ModOrganizer.ini)" };
        if (dlg.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(dlg.FolderName, "ModOrganizer.ini")))
        {
            MessageBox.Show(this, "In diesem Ordner liegt keine ModOrganizer.ini.\n\nBei einer portablen Installation ist das der Ordner von ModOrganizer.exe, " +
                "sonst %LOCALAPPDATA%\\ModOrganizer\\<Instanzname>.", "Keine MO2-Instanz", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                "GameDoctor durchsucht jetzt die lokalen Laufwerke nach Cyberpunk 2077 (höchstens 60 Sekunden).\n\n" +
                "Dabei werden nur Ordnernamen geprüft – keine Dateien geöffnet, nichts übertragen. Windows- und Systemordner werden übersprungen.",
                "Nach dem Spiel suchen", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;

        _vm.IsBusy = true;
        List<string> hits;
        try
        {
            var progress = new Progress<string>(d => _vm.Status = "Durchsuche " + d);
            hits = await Task.Run(() => CyberpunkInstall.SearchDrives(TimeSpan.FromSeconds(60), default, ((IProgress<string>)progress).Report));
        }
        finally
        {
            _vm.IsBusy = false;
        }

        if (hits.Count == 0)
        {
            _vm.Status = "Keine Cyberpunk-Installation gefunden.";
            MessageBox.Show(this, "Keine Installation gefunden. Bitte den Spielordner manuell festlegen.", "Nichts gefunden", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var hit in hits)
        {
            var answer = MessageBox.Show(this, $"Gefunden:\n{hit}\n\nDiesen Ordner verwenden?", "Cyberpunk gefunden",
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
    private async Task RefreshFixesAsync(AnalysisResult result)
    {
        var vms = await Task.Run(() =>
        {
            _inventory = result.Inventory;
            var ledger = PlaySessionLedger.Load();
            ledger.Harvest(result.Install, result.Crashes);
            try { ledger.Save(); } catch { /* nicht kritisch */ }
            var evaluator = new FixEvaluator(result.Crashes, ledger, result.Install, result.Inventory.ExtraRoots);
            return FixAttemptStore.Load().Select(a => new FixAttemptViewModel(a, evaluator.Evaluate(a))).ToList();
        });
        _vm.SetFixes(vms);
    }

    private async void NewFix_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is not { } result || _vm.SelectedCrash?.Case is not { } crash) return;
        var dlg = new FixAttemptWindow(crash, result, _inventory, result.Classifier) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Created is null) return;
        FixAttemptStore.Upsert(dlg.Created);
        await RefreshFixesAsync(result);
        Tabs.SelectedIndex = 4;
        _vm.Status = $"Lösungsversuch gestartet: {dlg.Created.TargetLabel}. Spiel jetzt ganz normal weiter – GameDoctor wertet bei jeder Analyse aus.";
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
        if (MessageBox.Show(this, "Diesen Lösungsversuch entfernen?", "Entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        FixAttemptStore.Remove(id);
        await RefreshFixesAsync(result);
    }

    // ------------------------------------------------------------------ Performance

    private PerformanceRecorder? _recorder;

    private void InitPerformance()
    {
        _vm.Perf.PresentMonAvailable = PresentMonTool.Find() is not null;
        _vm.Perf.Hint = "Starte die Aufnahme, wechsle ins Spiel und spiele 2–5 Minuten. Danach hier auf „Aufnahme beenden“.";
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
            try { ShowRecording(RecordingStore.Load(latest), $"Letzte Aufnahme geladen ({File.GetLastWriteTime(latest):dd.MM. HH:mm})."); }
            catch { /* defekte Datei ignorieren */ }
        }
    }

    private void ShowRecording(PerformanceRecording recording, string hint)
    {
        var report = PerformanceAnalyzer.Analyze(recording, CyberpunkSettings.TryLoad());
        _vm.Perf.ShowReport(report);
        _vm.Perf.Hint = hint;
    }

    private void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Aufnahme öffnen",
            Filter = "GameDoctor-Aufnahme (*.json.gz)|*.json.gz",
            InitialDirectory = Directory.Exists(RecordingStore.Dir) ? RecordingStore.Dir : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ShowRecording(RecordingStore.Load(dlg.FileName), $"Aufnahme geladen: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Aufnahme konnte nicht geladen werden", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        var perf = _vm.Perf;
        if (_recorder is null)
        {
            // Entwickler-Schalter: anderes Programm messen (z. B. zum Testen ohne Spiel)
            var process = Environment.GetEnvironmentVariable("GAMEDOCTOR_PERF_PROCESS") ?? "Cyberpunk2077.exe";
            _recorder = new PerformanceRecorder(process);
            _recorder.LiveUpdated += s => Dispatcher.BeginInvoke(() => perf.ShowLive(s));
            _recorder.Start();
            perf.IsRecording = true;
            perf.Hint = "Aufnahme läuft …";
            _vm.Status = "Performance-Aufnahme gestartet";
            return;
        }

        var recording = _recorder.Stop();
        _recorder.Dispose();
        _recorder = null;
        perf.IsRecording = false;
        string saved = "";
        if (recording.Frames.Count > 0 || recording.Sensors.Count > 0)
        {
            try { saved = " Gespeichert unter " + RecordingStore.Save(recording); }
            catch (Exception ex) { saved = " (Speichern fehlgeschlagen: " + ex.Message + ")"; }
        }
        ShowRecording(recording, $"Aufnahme beendet: {recording.Duration:mm\\:ss} min.");
        _vm.Status = perf.Hint + saved;
    }

    private async void DownloadPresentMon_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "PresentMon (Intel, Open Source, MIT-Lizenz, ca. 1 MB) wird vom offiziellen GitHub-Release heruntergeladen:\n\n" +
            PresentMonTool.DownloadUrl + "\n\nDie Datei wird per Prüfsumme kontrolliert und unter\n" + PresentMonTool.ToolsDir + "\nabgelegt. Fortfahren?",
            "PresentMon herunterladen", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _vm.Status = "Lade PresentMon herunter …";
            await PresentMonTool.DownloadAsync();
            _vm.Perf.PresentMonAvailable = true;
            _vm.Status = "PresentMon installiert.";
        }
        catch (Exception ex)
        {
            _vm.Status = "Download fehlgeschlagen: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Download fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Result is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Bericht speichern",
            Filter = "Markdown-Bericht (*.md)|*.md",
            FileName = $"cyberpunk-absturzbericht-{DateTime.Now:yyyy-MM-dd}.md",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, MarkdownReport.Build(_vm.Result, 10), Encoding.UTF8);
        _vm.Status = $"Bericht gespeichert: {dlg.FileName}";
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
