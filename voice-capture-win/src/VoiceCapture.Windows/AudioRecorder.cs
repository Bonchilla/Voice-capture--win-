using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceCapture.Windows;

public sealed record MicrophoneInfo(string Id, string Name);

public sealed class AudioRecorder : IDisposable
{
    private WasapiCapture? capture;
    private MMDevice? device;
    private MemoryStream? audio;
    private readonly object sync = new();
    private TaskCompletionSource? stopped;
    private Exception? captureError;
    private int limitBytes;
    private bool limitRaised;
    public event Action? LimitReached;
    public event Action? DeviceFailed;
    public bool IsRecording => capture is not null;
    public string? Warning { get; private set; }

    public static IReadOnlyList<MicrophoneInfo> GetMicrophones()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<MicrophoneInfo> { new("", "Системный микрофон по умолчанию") };
        foreach (var item in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (item) result.Add(new(item.ID, item.FriendlyName));
        }
        return result;
    }

    public void Start(AppSettings settings)
    {
        if (capture is not null) throw new InvalidOperationException("Запись уже идёт.");
        Warning = null; captureError = null; limitRaised = false;
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            if (!string.IsNullOrWhiteSpace(settings.MicrophoneId))
            {
                try { device = enumerator.GetDevice(settings.MicrophoneId); }
                catch { Warning = "Выбранный микрофон недоступен — используется системный."; }
                if (device is not null && device.State != DeviceState.Active)
                { device.Dispose(); device = null; Warning = "Выбранный микрофон отключён — используется системный."; }
            }
            device ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            capture = new WasapiCapture(device);
            audio = new MemoryStream();
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            limitBytes = checked(capture.WaveFormat.AverageBytesPerSecond * settings.MaxRecordingSeconds);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
        }
        catch
        {
            DisposeCapture();
            throw new RecognitionException("Не удалось открыть микрофон. Проверьте устройство и разрешение Windows для классических приложений.");
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        bool notify = false;
        lock (sync)
        {
            if (audio is null || capture is null) return;
            int remaining = Math.Max(0, limitBytes - (int)audio.Length);
            int count = Math.Min(e.BytesRecorded, remaining);
            count -= count % capture.WaveFormat.BlockAlign;
            audio.Write(e.Buffer, 0, count);
            if (audio.Length >= limitBytes && !limitRaised) { limitRaised = true; notify = true; }
        }
        if (notify) LimitReached?.Invoke();
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        captureError = e.Exception;
        stopped?.TrySetResult();
        if (e.Exception is not null) DeviceFailed?.Invoke();
    }

    public async Task<byte[]> StopAsync(CancellationToken token)
    {
        var current = capture ?? throw new InvalidOperationException("Нет записи.");
        WaveFormat format = current.WaveFormat;
        try
        {
            current.StopRecording();
            await stopped!.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            if (captureError is not null) throw new RecognitionException("Микрофон отключён или запись прервана.");
            byte[] raw;
            lock (sync) raw = audio!.ToArray();
            return await Task.Run(() => ConvertAudio(raw, format, token), token);
        }
        finally { DisposeCapture(); }
    }

    internal static byte[] ConvertAudio(byte[] raw, WaveFormat format, CancellationToken token)
    {
        if (raw.Length < format.AverageBytesPerSecond / 10)
            throw new RecognitionException("Слишком короткая запись.");
        using var rawStream = new RawSourceWaveStream(new MemoryStream(raw, false), format);
        ISampleProvider source = rawStream.ToSampleProvider();
        source = new MonoProvider(source);
        if (source.WaveFormat.SampleRate != 16000) source = new WdlResamplingSampleProvider(source, 16000);
        var values = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            values.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        var samples = values.ToArray();
        if (AudioSignal.Rms(samples) < 0.0005) throw new RecognitionException("Тишина или слишком тихий сигнал. Проверьте микрофон.");
        return AudioSignal.ToWave(samples);
    }

    public void Cancel() => DisposeCapture();
    private void DisposeCapture()
    {
        var old = capture;
        if (old is not null)
        {
            old.DataAvailable -= OnData; old.RecordingStopped -= OnStopped;
            try { old.StopRecording(); } catch { }
            old.Dispose();
        }
        lock (sync) { capture = null; audio?.Dispose(); audio = null; }
        device?.Dispose(); device = null;
        stopped?.TrySetCanceled();
    }
    public void Dispose() => DisposeCapture();

    private sealed class MonoProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        private float[] buffer = [];
        public int Read(float[] destination, int offset, int count)
        {
            int channels = source.WaveFormat.Channels;
            int required = count * channels;
            if (buffer.Length < required) buffer = new float[required];
            int read = source.Read(buffer, 0, required);
            int frames = read / channels;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += buffer[i * channels + c];
                destination[offset + i] = sum / channels;
            }
            return frames;
        }
    }
}
