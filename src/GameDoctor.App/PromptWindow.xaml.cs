using System.IO;
using System.Text;
using System.Windows;
using GameDoctor.Core.Ai;
using GameDoctor.Core.Analysis;
using GameDoctor.Core.Performance;
using GameDoctor.Core.Report;
using Microsoft.Win32;

namespace GameDoctor.App;

/// <summary>
/// Zeigt den KI-Prompt an (Optionen werden live übernommen) und kann ihn direkt von einer KI auswerten lassen –
/// mit dem eigenen Anbieter des Nutzers oder einem lokalen Modell.
/// </summary>
public partial class PromptWindow : Window
{
    private readonly AnalysisResult _result;
    private readonly CrashCase? _focus;
    private readonly PerformanceReport? _perf;
    private bool _ready;
    private CancellationTokenSource? _cts;

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
        Closing += (_, _) => _cts?.Cancel();
        _ready = true;
        Rebuild();
    }

    private bool ShowingAnswer => ViewAnswer.IsChecked == true;
    private bool Busy => _cts is not null;

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Rebuild();
    }

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        PromptPanel.Visibility = ShowingAnswer ? Visibility.Collapsed : Visibility.Visible;
        AnswerPanel.Visibility = ShowingAnswer ? Visibility.Visible : Visibility.Collapsed;
        if (!Busy) UpdateInfo();
    }

    private void Rebuild()
    {
        PromptBox.Text = AiPromptBuilder.Build(_result, new AiPromptOptions
        {
            FocusCrash = _focus,
            IncludePatterns = CbPatterns.IsChecked == true,
            IncludeEnvironment = CbEnvironment.IsChecked == true,
            IncludeLogErrors = CbLogs.IsChecked == true,
            IncludeModList = CbModList.IsChecked == true,
            Performance = CbPerf.IsChecked == true ? _perf : null,
            Anonymize = CbAnonymize.IsChecked == true,
        });
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (ShowingAnswer)
        {
            InfoText.Text = AnswerBox.Text.Length == 0 ? "Noch keine Antwort – „Mit KI auswerten“ klicken." : "";
            return;
        }
        // Grobe Faustregel: ~4 Zeichen pro Token
        var s = AiSettings.Load();
        var target = s.IsConfigured ? $" · Auswertung über {s.Provider.Name} ({s.EffectiveModel})" : "";
        InfoText.Text = $"{PromptBox.Text.Length:N0} Zeichen (ca. {PromptBox.Text.Length / 4:N0} Tokens){target}";
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;

        var settings = AiSettings.Load();
        if (!settings.IsConfigured)
        {
            var dlg = new AiSettingsWindow(settings) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Saved is null) return;
            settings = dlg.Saved;
        }

        var prompt = PromptBox.Text;
        if (!settings.Provider.IsLocal)
        {
            var ok = MessageBox.Show(this,
                $"Der angezeigte Prompt (ca. {prompt.Length / 4:N0} Tokens) wird jetzt an {settings.Provider.Name} geschickt " +
                $"und dort vom Modell „{settings.EffectiveModel}“ ausgewertet.\n\n" +
                (CbAnonymize.IsChecked == true ? "Benutzername, PC-Name und persönliche Pfade sind entfernt." : "Achtung: „Anonymisieren“ ist ausgeschaltet.") +
                "\n\nJetzt senden?",
                "Mit KI auswerten", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
        }

        ViewAnswer.IsChecked = true;
        AnswerBox.Clear();
        SetBusy(true);
        InfoText.Text = $"{settings.Provider.Name} ({settings.EffectiveModel}) denkt nach …";
        _cts = new CancellationTokenSource();
        bool gotText = false;
        try
        {
            using var client = new AiClient(settings);
            await foreach (var part in client.StreamChatAsync([AiMessage.User(prompt)], _cts.Token))
            {
                if (!gotText) { gotText = true; InfoText.Text = $"{settings.Provider.Name} ({settings.EffectiveModel}) antwortet …"; }
                AnswerBox.AppendText(part);
                AnswerBox.ScrollToEnd();
            }
            InfoText.Text = $"Fertig · {settings.Provider.Name} ({settings.EffectiveModel}). KI-Antworten können falsch sein – Schritte bitte einzeln ausprobieren.";
        }
        catch (OperationCanceledException)
        {
            InfoText.Text = "Abgebrochen.";
        }
        catch (AiException ex)
        {
            if (gotText) AnswerBox.AppendText("\n\n");
            AnswerBox.AppendText("⚠ " + ex.Message);
            InfoText.Text = "Die Auswertung ist fehlgeschlagen.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        AskButton.IsEnabled = !busy;
        AskButton.Content = busy ? "Läuft …" : "Mit KI auswerten";
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.IsEnabled = !busy;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        new AiSettingsWindow(AiSettings.Load()) { Owner = this }.ShowDialog();
        if (!Busy) UpdateInfo();
    }

    private string CurrentText => ShowingAnswer ? AnswerBox.Text : PromptBox.Text;

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentText.Length == 0) return;
        Clipboard.SetText(CurrentText);
        InfoText.Text = ShowingAnswer ? "Antwort kopiert." : "Kopiert! Jetzt einfach im KI-Chat einfügen (Strg+V).";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentText.Length == 0) return;
        var dlg = new SaveFileDialog
        {
            Title = ShowingAnswer ? "KI-Antwort speichern" : "Prompt speichern",
            Filter = ShowingAnswer ? "Markdown (*.md)|*.md|Textdatei (*.txt)|*.txt" : "Textdatei (*.txt)|*.txt",
            FileName = ShowingAnswer ? $"gamedoctor-ki-auswertung-{DateTime.Now:yyyy-MM-dd}.md" : $"gamedoctor-prompt-{DateTime.Now:yyyy-MM-dd}.txt",
        };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, CurrentText, Encoding.UTF8);
        InfoText.Text = "Gespeichert: " + dlg.FileName;
    }
}
