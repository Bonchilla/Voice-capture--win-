using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace VoiceCapture.Windows;

public partial class App : Application
{
    private Mutex? instance;
    private Forms.NotifyIcon? tray;
    private HotkeyMonitor? hotkeys;
    private OverlayWindow? overlay;
    private MainWindow? settingsWindow;
    private readonly AudioRecorder recorder = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(100), MaxResponseContentBufferSize = 2_000_000 };
    public SettingsStore Store { get; private set; } = null!;
    public SecretStore Secrets { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = new();
    public LocalParakeetService Local { get; private set; } = null!;
    private CancellationTokenSource? session;
    private AppSettings? sessionSettings;
    private PasteTarget target;
    private bool processing, paused, closing;
    private DateTime recordingStarted;
    private readonly DispatcherTimer recordingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--sherpa-smoke-test"))
        {
            try
            {
                Shutdown(LocalParakeetService.NativeSmokeTest() ? 0 : 2);
            }
            catch { Shutdown(3); }
            return;
        }
        instance = new Mutex(true, @"Local\IVOL.VoiceCapture.Windows", out bool first);
        if (!first) { instance.Dispose(); instance = null; Shutdown(); return; }
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceCapture");
        Store = new(directory); Secrets = new(directory);
        Local = new(Store.ModelsDirectory);
        Settings = Store.Load(Local.Hardware.ApplyRecommendation(new AppSettings()));
        overlay = new();
        try { hotkeys = new(Settings); }
        catch { MessageBox.Show("Не удалось включить глобальный хоткей.", "VoiceCapture"); Shutdown(); return; }
        hotkeys.Pressed += () => Dispatcher.BeginInvoke(StartRecording);
        hotkeys.Released += () => Dispatcher.BeginInvoke(() => _ = FinishRecordingAsync());
        hotkeys.Cancelled += () => Dispatcher.BeginInvoke(() => CancelSession("Отменено"));
        recorder.LimitReached += () => Dispatcher.BeginInvoke(() => _ = FinishRecordingAsync());
        recorder.DeviceFailed += () => Dispatcher.BeginInvoke(() => CancelSession("Микрофон отключён или недоступен"));
        recordingTimer.Tick += (_, _) =>
        {
            if (!recorder.IsRecording || processing) return;
            var elapsed = DateTime.UtcNow - recordingStarted;
            var max = sessionSettings!.MaxRecordingSeconds;
            overlay.Display($"● Запись {elapsed:mm\\:ss} · Esc — отмена" + (elapsed.TotalSeconds > max - 15 ? "\nДостигнут лимит через несколько секунд" : ""));
            if (elapsed.TotalSeconds >= max) _ = FinishRecordingAsync();
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        BuildTray();
        if (e.Args.Contains("--smoke-test"))
        {
            // No recording, clipboard writes, network requests or settings changes.
            ShowSettings();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); Shutdown(); };
            timer.Start();
            return;
        }
        if (!e.Args.Contains("--tray")) ShowSettings();
        if (settingsWindow is null) _ = PrewarmAsync();
    }

    private void BuildTray()
    {
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "VoiceCapture — голосовой ввод" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Настройки…", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        var pause = new Forms.ToolStripMenuItem("Приостановить хоткей") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            paused = pause.Checked;
            if (paused) CancelSession("Хоткей приостановлен");
            UpdateHotkeyState();
        });
        menu.Items.Add(pause);
        menu.Items.Add("Отменить обработку", null, (_, _) => Dispatcher.Invoke(() => CancelSession("Отменено")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(Shutdown));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowSettings);
    }

    public void ShowSettings()
    {
        if (session is not null) { overlay?.Display("Сначала завершите или отмените диктовку", true); return; }
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new MainWindow(this);
        settingsWindow.Closed += (_, _) => { settingsWindow = null; UpdateHotkeyState(); if (!closing) _ = PrewarmAsync(); };
        UpdateHotkeyState();
        settingsWindow.Show(); settingsWindow.Activate();
    }

    private void UpdateHotkeyState()
    {
        if (hotkeys is not null) hotkeys.Enabled = !paused && settingsWindow is null && !closing;
    }

    public async Task SaveSettingsAsync(AppSettings settings, string key)
    {
        settings.Validate();
        Secrets.Save(key);
        StartupRegistration.Apply(settings.StartWithWindows);
        Store.Save(settings);
        Settings = settings;
        hotkeys?.Update(settings);
        UpdateHotkeyState();
        await PrewarmAsync();
    }

    private async Task PrewarmAsync()
    {
        if (Settings.Mode == RecognitionMode.OpenAI || !Local.IsAvailable(Settings)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await Local.PrewarmAsync(Settings, timeout.Token).WaitAsync(timeout.Token); }
        catch { /* Actual local use shows a user-facing error; no model/audio content logged. */ }
    }

    private void StartRecording()
    {
        if (closing || paused || settingsWindow is not null || session is not null) return;
        var s = Settings;
        bool cloud = s.CloudConsent && !string.IsNullOrWhiteSpace(Secrets.Read());
        bool local = Local.IsAvailable(s);
        if ((s.Mode == RecognitionMode.OpenAI && !cloud) || (s.Mode == RecognitionMode.Local && !local) ||
            (s.Mode == RecognitionMode.Hybrid && !cloud && !local))
        { overlay!.Display("Настройте ключ OpenAI или скачайте выбранную модель", true); return; }
        target = PasteTarget.Capture();
        session = new(); sessionSettings = s;
        hotkeys!.SessionActive = true;
        try
        {
            recorder.Start(s);
            recordingStarted = DateTime.UtcNow;
            string hint = s.Mode == RecognitionMode.Hybrid && (!local || !cloud)
                ? (local ? "Только Parakeet: облако не настроено" : "Только OpenAI: модель не скачана") : "Отпустите хоткей для обработки";
            overlay!.Display("● Запись · " + (recorder.Warning ?? hint));
            recordingTimer.Start();
        }
        catch (Exception ex) { CancelSession(SafeMessage(ex)); }
    }

    private async Task FinishRecordingAsync()
    {
        if (session is null || processing || !recorder.IsRecording) return;
        var current = session;
        var s = sessionSettings!;
        processing = true; recordingTimer.Stop();
        try
        {
            overlay!.Display("Подготовка аудио…");
            byte[] wav = await recorder.StopAsync(current.Token);
            if (session != current) return;
            string key = Secrets.Read();
            bool cloud = s.CloudConsent && !string.IsNullOrWhiteSpace(key);
            var openAi = new OpenAiService(http, () => key);
            var pipeline = new DictationPipeline(openAi, Local, openAi);
            var result = await pipeline.ProcessAsync(wav, s, cloud, Local.IsAvailable(s),
                message => Dispatcher.BeginInvoke(() => { if (session == current && !current.IsCancellationRequested) overlay.Display(message); }), current.Token);
            if (session != current || current.IsCancellationRequested) return;
            string delivered = await ClipboardService.DeliverAsync(result.Text, s.AutoPaste, target, current.Token);
            if (session == current) overlay.Display(delivered + " · " + result.Source + (result.CorrectionFailed ? "\nБез коррекции" : ""), true);
        }
        catch (OperationCanceledException)
        {
            if (session == current) overlay!.Display(current.IsCancellationRequested ? "Отменено" : "Превышено время обработки", true);
        }
        catch (Exception ex) { if (session == current) overlay!.Display(SafeMessage(ex), true); }
        finally
        {
            if (session == current)
            {
                session = null; sessionSettings = null; processing = false; hotkeys!.SessionActive = false;
            }
            current.Dispose();
        }
    }

    private void CancelSession(string message)
    {
        recordingTimer.Stop();
        if (session is not null)
        {
            session.Cancel();
            recorder.Cancel();
            // While StopAsync/native work unwinds, block a second recording from sharing capture state.
            if (!processing) { session.Dispose(); session = null; sessionSettings = null; }
        }
        if (hotkeys is not null) hotkeys.SessionActive = false;
        if (!closing) overlay?.Display(message, true);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            Dispatcher.BeginInvoke(() => { CancelSession("Сеанс заблокирован"); hotkeys?.Reset(); });
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.BeginInvoke(() => { CancelSession("Переход в сон"); hotkeys?.Reset(); });
    }
    public static string SafeMessage(Exception exception) => exception switch
    {
        RecognitionException => exception.Message,
        ArgumentException => "Проверьте значения настроек.",
        HttpRequestException => "Сетевая ошибка. Проверьте подключение и доступ к сервису.",
        UnauthorizedAccessException => "Нет доступа к файлу или устройству.",
        IOException => "Ошибка доступа к файлам. Проверьте свободное место и права.",
        _ => "Операция не выполнена. Проверьте настройки и повторите."
    };

    protected override void OnExit(ExitEventArgs e)
    {
        closing = true; CancelSession("");
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        hotkeys?.Dispose(); recorder.Dispose(); Local?.Dispose();
        tray?.Dispose(); overlay?.Close(); http.Dispose();
        instance?.ReleaseMutex(); instance?.Dispose();
        base.OnExit(e);
    }
}
