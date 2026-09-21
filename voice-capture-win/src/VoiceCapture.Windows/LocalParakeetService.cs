using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using SherpaOnnx;

namespace VoiceCapture.Windows;

// CPU-only Parakeet TDT v3; capture stays WASAPI/NAudio, ASR runs through sherpa-onnx.
public sealed class LocalParakeetService(string modelsDirectory) : IRecognizer, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IntPtr recognizer;
    private string? loadedModel;
    private volatile bool disposed;
    public HardwareProfile Hardware { get; } = GraphicsHardware.Detect();
    public string BackendDescription { get; private set; } = "Не загружен";
    public bool IsAvailable(AppSettings settings) => ModelCatalog.Get(settings.LocalModel).IsInstalled(modelsDirectory);

    public async Task<Transcript> TranscribeAsync(byte[] wav, AppSettings settings, CancellationToken token)
    {
        var work = Task.Run(async () =>
        {
            await gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                await EnsureRecognizerAsync(settings, token);
                using var reader = new WaveFileReader(new MemoryStream(wav, false));
                if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1 ||
                    reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.BitsPerSample != 16)
                    throw new RecognitionException("Parakeet ожидает моно PCM16 16 кГц.");
                var source = reader.ToSampleProvider();
                var text = new StringBuilder();
                // Bound full-attention memory and native cancellation latency for five-minute dictation.
                // Prefer a low-energy boundary in the last five seconds; no samples discarded.
                var all = new float[checked((int)(reader.Length / 2))];
                int used = 0, n;
                while (used < all.Length && (n = source.Read(all, used, all.Length - used)) > 0) used += n;
                for (int start = 0; start < used;)
                {
                    token.ThrowIfCancellationRequested();
                    int end = Math.Min(used, start + 25 * 16000);
                    if (end < used) end = AudioChunks.FindBoundary(all, start + 20 * 16000, end);
                    using var stream = new OfflineStream(CreateStream(recognizer));
                    if (stream.Handle == IntPtr.Zero) throw new RecognitionException("Не удалось создать поток Parakeet.");
                    stream.AcceptWaveform(16000, all[start..end]);
                    Decode(recognizer, stream.Handle); // Synchronous; never destroy resources during this call.
                    token.ThrowIfCancellationRequested();
                    var part = stream.Result.Text.Trim();
                    if (part.Length > 0) { if (text.Length > 0) text.Append(' '); text.Append(part); }
                    start = end;
                }
                return new Transcript(text.ToString(), "Parakeet TDT v3");
            }
            catch (OperationCanceledException) { throw; }
            catch (RecognitionException) { throw; }
            catch { throw new RecognitionException("Parakeet недоступен. Проверьте модель, свободную память и VC++ Runtime x64."); }
            finally { if (disposed) ReleaseRecognizer(); gate.Release(); }
        }, token);
        // UI cancellation is immediate. The gate remains owned until native decoding really returns.
        _ = ObserveAsync(work);
        return await work.WaitAsync(token);
    }

    private static async Task ObserveAsync(Task work) { try { await work.ConfigureAwait(false); } catch { } }

    public Task PrewarmAsync(AppSettings settings, CancellationToken token) => Task.Run(async () =>
    {
        await gate.WaitAsync(token);
        try { ObjectDisposedException.ThrowIf(disposed, this); await EnsureRecognizerAsync(settings, token); }
        finally { if (disposed) ReleaseRecognizer(); gate.Release(); }
    }, token);

    private async Task EnsureRecognizerAsync(AppSettings settings, CancellationToken token)
    {
        settings.Validate();
        if (recognizer != IntPtr.Zero && loadedModel == settings.LocalModel) return;
        ReleaseRecognizer();
        var model = ModelCatalog.Get(settings.LocalModel);
        await ModelDownloader.VerifyAsync(model, modelsDirectory, token);
        string directory = model.GetDirectory(modelsDirectory);
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2));
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        // Upstream C# paths are LPStr (ANSI on Windows). Supply UTF-8 explicitly to the C API.
        // This avoids copying large models and works with Cyrillic user/workspace paths.
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<OfflineRecognizerConfig>());
        Marshal.StructureToPtr(config, pointer, false);
        try
        {
            int modelOffset = Marshal.OffsetOf<OfflineRecognizerConfig>(nameof(config.ModelConfig)).ToInt32();
            int transducerOffset = modelOffset + Marshal.OffsetOf<OfflineModelConfig>(nameof(config.ModelConfig.Transducer)).ToInt32();
            SetUtf8(pointer, transducerOffset + Marshal.OffsetOf<OfflineTransducerModelConfig>("Encoder").ToInt32(), Path.Combine(directory, "encoder.int8.onnx"));
            SetUtf8(pointer, transducerOffset + Marshal.OffsetOf<OfflineTransducerModelConfig>("Decoder").ToInt32(), Path.Combine(directory, "decoder.int8.onnx"));
            SetUtf8(pointer, transducerOffset + Marshal.OffsetOf<OfflineTransducerModelConfig>("Joiner").ToInt32(), Path.Combine(directory, "joiner.int8.onnx"));
            SetUtf8(pointer, modelOffset + Marshal.OffsetOf<OfflineModelConfig>("Tokens").ToInt32(), Path.Combine(directory, "tokens.txt"));
            token.ThrowIfCancellationRequested();
            recognizer = CreateRecognizer(pointer);
        }
        finally { Marshal.DestroyStructure<OfflineRecognizerConfig>(pointer); Marshal.FreeHGlobal(pointer); }
        if (recognizer == IntPtr.Zero) throw new RecognitionException("Не удалось загрузить Parakeet. Повторно скачайте модель и проверьте память.");
        loadedModel = settings.LocalModel;
        BackendDescription = "sherpa-onnx · ONNX Runtime CPU · Parakeet TDT v3 INT8";
        token.ThrowIfCancellationRequested();
    }

    private static void SetUtf8(IntPtr config, int offset, string value)
    {
        var replacement = Marshal.StringToCoTaskMemUTF8(value);
        Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(config, offset));
        Marshal.WriteIntPtr(config, offset, replacement);
    }

    public async Task UnloadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { ReleaseRecognizer(); }
        finally { gate.Release(); }
    }
    private void ReleaseRecognizer()
    {
        if (recognizer != IntPtr.Zero) DestroyRecognizer(recognizer);
        recognizer = IntPtr.Zero; loadedModel = null; BackendDescription = "Не загружен";
    }
    public void Dispose()
    {
        disposed = true;
        if (gate.Wait(0)) { try { ReleaseRecognizer(); } finally { gate.Release(); } }
    }

    public static bool NativeSmokeTest() => Marshal.PtrToStringUTF8(GetVersion()) is { Length: > 0 };
    private const string Library = "sherpa-onnx-c-api";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxCreateOfflineRecognizer")]
    private static extern IntPtr CreateRecognizer(IntPtr config);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxDestroyOfflineRecognizer")]
    private static extern void DestroyRecognizer(IntPtr recognizer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxCreateOfflineStream")]
    private static extern IntPtr CreateStream(IntPtr recognizer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxDecodeOfflineStream")]
    private static extern void Decode(IntPtr recognizer, IntPtr stream);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxGetVersionStr")]
    private static extern IntPtr GetVersion();
}
