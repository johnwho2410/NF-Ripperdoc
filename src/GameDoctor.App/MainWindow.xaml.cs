using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GameDoctor.Core.Analysis;
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
    }

    // ------------------------------------------------------------------ Performance

    private PerformanceRecorder? _recorder;

    private void InitPerformance()
    {
        _vm.Perf.PresentMonAvailable = PresentMonTool.Find() is not null;
        _vm.Perf.Hint = "Starte die Aufnahme, wechsle ins Spiel und spiele 2–5 Minuten. Danach hier auf „Aufnahme beenden“.";
        Closing += (_, _) => _recorder?.Stop();
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
        var report = PerformanceAnalyzer.Analyze(recording, CyberpunkSettings.TryLoad());
        perf.ShowReport(report);
        perf.Hint = $"Aufnahme beendet: {recording.Duration:mm\\:ss} min, {report.FrameCount:N0} Frames.";
        _vm.Status = perf.Hint;
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

    // Dunkle Titelleiste (Windows 10 20H1+ / Windows 11)
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }
}
