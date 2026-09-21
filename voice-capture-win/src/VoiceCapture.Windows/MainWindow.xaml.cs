using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace VoiceCapture.Windows;

public partial class MainWindow : Window
{
    private readonly App app;
    private readonly HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? download;
    private bool initialized, saving;
    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        var s = app.Settings;
        ModeBox.ItemsSource = new[] { "OpenAI — облако", "Parakeet TDT v3 — локально", "Гибрид — первый успешный" };
        ModeBox.SelectedIndex = (int)s.Mode;
        LanguageBox.ItemsSource = new[] { "Авто / смешанная речь", "Русский", "English" };
        LanguageBox.SelectedIndex = s.Language == "ru" ? 1 : s.Language == "en" ? 2 : 0;
        VocabularyBox.Text = s.Vocabulary;
        ApiKeyBox.Password = app.Secrets.Read();
        TranscriptionModelBox.Text = s.TranscriptionModel;
        CorrectionModelBox.Text = s.CorrectionModel;
        CorrectionTimeoutBox.Text = s.CorrectionTimeoutSeconds.ToString();
        CloudConsentBox.IsChecked = s.CloudConsent;
        CorrectionBox.IsChecked = s.CorrectionEnabled;
        LocalCorrectionBox.IsChecked = s.LocalCloudCorrection;
        ModelBox.ItemsSource = ModelCatalog.All; ModelBox.SelectedValue = s.LocalModel;
        AccelerationBox.ItemsSource = new[] { "Авто — CPU (ONNX Runtime)", "Только CPU" };
        HardwareText.Text = "Обнаружено: " + app.Local.Hardware.Description;
        RecommendationText.Text = app.Local.Hardware.Recommendation;
        AccelerationBox.SelectedIndex = (int)s.Acceleration;
        DelayBox.Text = s.LocalDelayMilliseconds.ToString();
        ControlBox.IsChecked = s.HotkeyControl; AltBox.IsChecked = s.HotkeyAlt; ShiftBox.IsChecked = s.HotkeyShift;
        HotkeyBox.ItemsSource = new[] { "Space" }.Concat(Enumerable.Range(1, 12).Select(i => $"F{i}"))
            .Concat(Enumerable.Range('A', 26).Select(i => ((char)i).ToString())).ToArray();
        HotkeyBox.SelectedItem = s.HotkeyVirtualKey == 0x20 ? "Space" : s.HotkeyVirtualKey >= 0x70
            ? $"F{s.HotkeyVirtualKey - 0x6F}" : ((char)s.HotkeyVirtualKey).ToString();
        PasteBox.IsChecked = s.AutoPaste; StartupBox.IsChecked = s.StartWithWindows;
        RefreshMicrophones(null, null!);
        MicrophoneBox.SelectedValue = s.MicrophoneId;
        if (MicrophoneBox.SelectedIndex < 0) MicrophoneBox.SelectedIndex = 0;
        initialized = true; UpdateModelStatus(); UpdateBackendStatus();
        StatusText.Text = app.Store.LoadWarning ?? "Настройки применяются после сохранения. Ключ не передавайте в переписке.";
        Closing += OnClosing;
        Closed += (_, _) => { lifetime.Cancel(); download?.Cancel(); client.Dispose(); };
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (download is not null) { StatusText.Text = "Сначала завершите загрузку."; return; }
        try
        {
            if (!int.TryParse(DelayBox.Text, out int delay) || !int.TryParse(CorrectionTimeoutBox.Text, out int correctionTimeout))
                throw new ArgumentException("Тайм-аут и задержка должны быть целыми числами.");
            string hotkey = HotkeyBox.SelectedItem as string ?? "Space";
            int vk = hotkey == "Space" ? 0x20 : hotkey.Length > 1 && hotkey[0] == 'F' ? 0x6F + int.Parse(hotkey[1..]) : hotkey[0];
            var s = app.Settings with
            {
                Mode = (RecognitionMode)ModeBox.SelectedIndex,
                MicrophoneId = MicrophoneBox.SelectedValue as string ?? "",
                Language = LanguageBox.SelectedIndex == 1 ? "ru" : LanguageBox.SelectedIndex == 2 ? "en" : "auto",
                Vocabulary = VocabularyBox.Text.Trim(), TranscriptionModel = TranscriptionModelBox.Text.Trim(),
                CorrectionModel = CorrectionModelBox.Text.Trim(), CorrectionTimeoutSeconds = correctionTimeout,
                CloudConsent = CloudConsentBox.IsChecked == true, CorrectionEnabled = CorrectionBox.IsChecked == true,
                LocalCloudCorrection = LocalCorrectionBox.IsChecked == true,
                LocalModel = (string)ModelBox.SelectedValue, Acceleration = (AccelerationMode)AccelerationBox.SelectedIndex,
                LocalDelayMilliseconds = delay, HotkeyControl = ControlBox.IsChecked == true,
                HotkeyAlt = AltBox.IsChecked == true, HotkeyShift = ShiftBox.IsChecked == true, HotkeyVirtualKey = vk,
                AutoPaste = PasteBox.IsChecked == true, StartWithWindows = StartupBox.IsChecked == true
            };
            s.Validate();
            saving = true; SaveButton.IsEnabled = false; DownloadButton.IsEnabled = false; DeleteButton.IsEnabled = false;
            StatusText.Text = "Сохранение и подготовка локальной модели…";
            await app.SaveSettingsAsync(s, ApiKeyBox.Password);
            StatusText.Text = "Сохранено. Хоткей: " + s.HotkeyDisplay + ". Закройте настройки для диктовки.";
            UpdateBackendStatus();
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; }
        catch (Exception ex) { StatusText.Text = App.SafeMessage(ex); }
        finally { saving = false; SaveButton.IsEnabled = true; DownloadButton.IsEnabled = true; DeleteButton.IsEnabled = true; }
    }

    private async void CheckKeyClicked(object sender, RoutedEventArgs e)
    {
        if (CloudConsentBox.IsChecked != true) { StatusText.Text = "Сначала разрешите обращение к OpenAI."; return; }
        var button = (Button)sender; button.IsEnabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            string key = ApiKeyBox.Password;
            await new OpenAiService(client, () => key).CheckCredentialsAsync(timeout.Token);
            StatusText.Text = "Ключ принят API. Доступ к распознаванию и биллинг проверяются реальной диктовкой отдельно.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Проверка отменена или превышен тайм-аут."; }
        catch (Exception ex) { StatusText.Text = App.SafeMessage(ex); }
        finally { button.IsEnabled = true; }
    }

    private void RefreshMicrophones(object? sender, RoutedEventArgs e)
    {
        string? selected = MicrophoneBox.SelectedValue as string;
        try
        {
            MicrophoneBox.ItemsSource = AudioRecorder.GetMicrophones();
            MicrophoneBox.SelectedValue = selected ?? "";
        }
        catch { StatusText.Text = "Не удалось получить микрофоны. Проверьте разрешения Windows."; }
    }

    private async void DownloadClicked(object sender, RoutedEventArgs e)
    {
        if (download is not null) { download.Cancel(); return; }
        var model = (ModelInfo)ModelBox.SelectedItem;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        download = cancellation;
        DownloadButton.Content = "Отмена"; DeleteButton.IsEnabled = false; ModelBox.IsEnabled = false; SaveButton.IsEnabled = false;
        try
        {
            await app.Local.UnloadAsync(cancellation.Token);
            var progress = new Progress<DownloadProgress>(p =>
            {
                DownloadProgressBar.Value = p.Fraction;
                ModelStatus.Text = $"Загрузка: {p.Received / 1048576} / {p.Total / 1048576} МБ";
            });
            await new ModelDownloader(client, app.Store.ModelsDirectory).DownloadAsync(model, progress, cancellation.Token);
            StatusText.Text = "Модель скачана, размер и SHA-256 проверены.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Загрузка отменена. Активная модель не заменена."; }
        catch (Exception ex) { StatusText.Text = App.SafeMessage(ex); }
        finally
        {
            download = null; cancellation.Dispose();
            DownloadButton.Content = "Скачать / проверить"; DeleteButton.IsEnabled = true; ModelBox.IsEnabled = true; SaveButton.IsEnabled = true;
            UpdateModelStatus();
        }
    }

    private async void DeleteModelClicked(object sender, RoutedEventArgs e)
    {
        if (download is not null || saving) return;
        var model = (ModelInfo)ModelBox.SelectedItem;
        if (MessageBox.Show(this, "Удалить выбранную локальную модель?", "VoiceCapture", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            await app.Local.UnloadAsync(lifetime.Token);
            string path = model.GetDirectory(app.Store.ModelsDirectory);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            UpdateModelStatus();
        }
        catch (Exception ex) { StatusText.Text = App.SafeMessage(ex); }
    }
    private void OpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(app.Store.ModelsDirectory);
            Process.Start(new ProcessStartInfo(app.Store.ModelsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = App.SafeMessage(ex); }
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && ModeBox.SelectedIndex == (int)RecognitionMode.Local)
            StatusText.Text = "Parakeet работает офлайн, если облачная коррекция выключена. Язык определяет автоматически; словарь OpenAI не применяется.";
    }
    private void ApplyHardwareProfileClicked(object sender, RoutedEventArgs e)
    {
        if (saving || download is not null) return;
        ModelBox.SelectedValue = app.Local.Hardware.RecommendedModel;
        AccelerationBox.SelectedIndex = (int)AccelerationMode.Auto;
        StatusText.Text = "Рекомендация выбрана, но ещё не сохранена. Скачайте модель при необходимости и нажмите «Сохранить».";
        UpdateBackendStatus();
    }
    private void AccelerationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized) UpdateBackendStatus();
    }
    private void UpdateBackendStatus()
    {
        BackendText.Text = "Вычисления: CPU · модель NVIDIA, но CUDA не требуется"
            + "\nЗагружено сейчас: " + app.Local.BackendDescription;
    }
    private void ModelChanged(object sender, SelectionChangedEventArgs e) { if (initialized) UpdateModelStatus(); }
    private void UpdateModelStatus()
    {
        if (ModelBox.SelectedItem is ModelInfo model)
            ModelStatus.Text = model.IsInstalled(app.Store.ModelsDirectory) ? "Компоненты установлены; SHA-256 проверяется перед загрузкой в память" : "Модель не скачана или пакет неполон (640 МиБ)";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (saving) { e.Cancel = true; StatusText.Text = "Дождитесь завершения подготовки модели."; }
        else if (download is not null)
        {
            download.Cancel(); e.Cancel = true;
            StatusText.Text = "Загрузка отменяется. Закройте окно после её остановки.";
        }
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
