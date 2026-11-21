using GameDoctor.Core.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameDoctor.Core.Ai;

namespace GameDoctor.App;

/// <summary>Anbieter, Adresse, API-Key und Modell für die KI-Auswertung einstellen.</summary>
public partial class AiSettingsWindow : Window
{
    private readonly AiSettings _settings;
    private string _providerId;
    private bool _ready;

    /// <summary>Die gespeicherten Einstellungen, wenn der Nutzer auf „Speichern“ geklickt hat.</summary>
    public AiSettings? Saved { get; private set; }

    public AiSettingsWindow(AiSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _providerId = settings.Provider.Id;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        foreach (var p in AiProviders.All)
        {
            var rb = new RadioButton
            {
                Content = L.T(p.Name),
                Tag = p.Id,
                GroupName = "provider",
                Style = (Style)FindResource("Segment"),
                IsChecked = p.Id == _providerId,
            };
            rb.Checked += Provider_Checked;
            ProviderPanel.Children.Add(rb);
        }

        BaseUrlBox.Text = settings.EffectiveBaseUrl;
        ModelBox.Text = settings.EffectiveModel;
        _ready = true;
        UpdateProviderUi();
    }

    private AiProviderPreset Provider => AiProviders.Get(_providerId);

    private void Provider_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string id }) return;
        var old = Provider;
        _providerId = id;
        // Adresse/Modell nur ersetzen, wenn sie noch auf den Vorgaben des alten Anbieters standen.
        if (string.IsNullOrWhiteSpace(BaseUrlBox.Text) || BaseUrlBox.Text.Trim() == old.BaseUrl) BaseUrlBox.Text = Provider.BaseUrl;
        if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text.Trim() == old.DefaultModel) ModelBox.Text = Provider.DefaultModel;
        ModelList.Visibility = Visibility.Collapsed;
        UpdateProviderUi();
    }

    private void UpdateProviderUi()
    {
        ProviderHint.Text = L.T(Provider.Hint);
        KeyPanel.Visibility = Provider.NeedsKey ? Visibility.Visible : Visibility.Collapsed;
        // Beim Anthropic-SDK ist die Adresse fest eingebaut.
        BaseUrlPanel.Visibility = Provider.Api == AiApi.Anthropic ? Visibility.Collapsed : Visibility.Visible;
        bool sameProvider = _providerId == _settings.Provider.Id;
        KeyState.Text = sameProvider && _settings.HasApiKey
            ? L.T("Ein Key ist gespeichert (verschlüsselt, nur für deinen Windows-Benutzer lesbar). Feld leer lassen, um ihn zu behalten.")
            : L.T("Wird verschlüsselt gespeichert und ist nur für deinen Windows-Benutzer auf diesem PC lesbar.");
        SetStatus("", false);
    }

    /// <summary>Einstellungen aus den Feldern zusammenbauen (ohne zu speichern).</summary>
    private AiSettings Collect()
    {
        var s = new AiSettings
        {
            ProviderId = _providerId,
            BaseUrl = BaseUrlBox.Text.Trim() == Provider.BaseUrl ? "" : BaseUrlBox.Text.Trim(),
            Model = ModelBox.Text.Trim() == Provider.DefaultModel ? "" : ModelBox.Text.Trim(),
        };
        if (KeyBox.Password.Length > 0) s.SetApiKey(KeyBox.Password);
        else if (_providerId == _settings.Provider.Id) s.EncryptedApiKey = _settings.EncryptedApiKey;
        if (!Provider.NeedsKey && KeyBox.Password.Length == 0) s.EncryptedApiKey = null;
        return s;
    }

    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        var s = Collect();
        LoadModelsButton.IsEnabled = false;
        SetStatus(L.T("Lade Modellliste …"), false);
        try
        {
            using var client = AiClient.Create(s);
            var models = await client.ListModelsAsync();
            ModelList.ItemsSource = models;
            ModelList.Visibility = models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus(models.Count > 0 ? L.F("{0} Modelle gefunden – eins anklicken.", models.Count) : L.T("Der Dienst hat keine Modelle gemeldet."), false);
        }
        catch (AiException ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            LoadModelsButton.IsEnabled = true;
        }
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is string m) ModelBox.Text = m;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var s = Collect();
        if (!s.IsConfigured)
        {
            SetStatus(MissingText(s), true);
            return;
        }
        TestButton.IsEnabled = false;
        SetStatus(L.F("Teste {0} mit „{1}“ …", L.T(s.Provider.Name), s.EffectiveModel), false);
        try
        {
            using var client = AiClient.Create(s);
            var answer = "";
            await foreach (var part in client.StreamChatAsync([AiMessage.User(L.T("Antworte nur mit dem Wort: OK"))]))
                answer += part;
            SetStatus(L.F("Verbindung klappt. Antwort des Modells: {0}", answer.Trim()), false, good: true);
        }
        catch (AiException ex)
        {
            SetStatus(ex.Message, true);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = Collect();
        if (!s.IsConfigured)
        {
            SetStatus(MissingText(s), true);
            return;
        }
        s.Save();
        Saved = s;
        DialogResult = true;
    }

    private static string MissingText(AiSettings s) =>
        string.IsNullOrWhiteSpace(s.EffectiveBaseUrl) ? L.T("Bitte die Adresse der Schnittstelle eintragen.")
        : string.IsNullOrWhiteSpace(s.EffectiveModel) ? L.T("Bitte ein Modell eintragen oder über „Modelle laden“ auswählen.")
        : L.T("Bitte einen API-Key eintragen.");

    private void SetStatus(string text, bool error, bool good = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
            : good ? (Brush)FindResource("Good")
            : (Brush)FindResource("MutedBrush");
    }
}
