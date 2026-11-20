using System.IO;
using System.Text;
using System.Windows;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Performance;
using GameDoctor.Core.Report;
using Microsoft.Win32;

namespace GameDoctor.App;

/// <summary>Zeigt den KI-Prompt an; Optionen werden live übernommen.</summary>
public partial class PromptWindow : Window
{
    private readonly AnalysisResult _result;
    private readonly CrashCase? _focus;
    private readonly PerformanceReport? _perf;
    private bool _ready;

    public PromptWindow(AnalysisResult result, CrashCase? focus, PerformanceReport? perf)
    {
        InitializeComponent();
        _result = result;
        _focus = focus;
        _perf = perf;
        CbPerf.IsEnabled = perf is { FrameCount: > 0 };
        if (!CbPerf.IsEnabled) CbPerf.IsChecked = false;
        var shown = focus ?? result.Crashes.FirstOrDefault();
        FocusText.Text = shown is null
            ? "Keine Abstürze im gewählten Zeitraum."
            : $"Im Detail: Absturz vom {shown.Report.Time:dd.MM.yyyy HH:mm} ({shown.FaultLabel}) · dazu die übrigen {Math.Min(10, Math.Max(0, result.Crashes.Count - 1))} neuesten Abstürze";
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _ready = true;
        Rebuild();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Rebuild();
    }

    private void Rebuild()
    {
        var text = AiPromptBuilder.Build(_result, new AiPromptOptions
        {
            FocusCrash = _focus,
            IncludePatterns = CbPatterns.IsChecked == true,
            IncludeEnvironment = CbEnvironment.IsChecked == true,
            IncludeLogErrors = CbLogs.IsChecked == true,
            IncludeModList = CbModList.IsChecked == true,
            Performance = CbPerf.IsChecked == true ? _perf : null,
            Anonymize = CbAnonymize.IsChecked == true,
        });
        PromptBox.Text = text;
        // Grobe Faustregel: ~4 Zeichen pro Token
        InfoText.Text = $"{text.Length:N0} Zeichen (ca. {text.Length / 4:N0} Tokens) – passt in alle gängigen KI-Chats.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(PromptBox.Text);
        InfoText.Text = "Kopiert! Jetzt einfach im KI-Chat einfügen (Strg+V).";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Prompt speichern",
            Filter = "Textdatei (*.txt)|*.txt",
            FileName = $"gamedoctor-prompt-{DateTime.Now:yyyy-MM-dd}.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, PromptBox.Text, Encoding.UTF8);
        InfoText.Text = "Gespeichert: " + dlg.FileName;
    }
}
