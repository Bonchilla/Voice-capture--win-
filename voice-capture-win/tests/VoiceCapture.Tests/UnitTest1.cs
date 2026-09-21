using System.Net;
using System.Text;
using System.Text.Json;
using VoiceCapture.Core;

namespace VoiceCapture.Tests;

public class RecognitionTests
{
    [Fact]
    public async Task EarlyCloudSuccessDoesNotStartLocal()
    {
        int calls = 0;
        var result = await RecognitionRace.RunAsync(_ => Task.FromResult(new Transcript(" cloud ", "cloud")),
            _ => { calls++; return Task.FromResult(new Transcript("local", "local")); }, TimeSpan.FromSeconds(2), default);
        Assert.Equal("cloud", result.Text); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EarlyCloudFailureStartsLocalImmediately()
    {
        var operation = RecognitionRace.RunAsync(_ => Task.FromException<Transcript>(new Exception()),
            _ => Task.FromResult(new Transcript("local", "local")), TimeSpan.FromHours(1), default);
        Assert.Equal("local", (await operation.WaitAsync(TimeSpan.FromSeconds(1))).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothFailureOrdersFinishWithoutWatchdog(bool cloudFirst)
    {
        var cloud = new TaskCompletionSource<Transcript>(TaskCreationOptions.RunContinuationsAsynchronously);
        var local = new TaskCompletionSource<Transcript>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = RecognitionRace.RunAsync(_ => cloud.Task, _ => local.Task, TimeSpan.Zero, default);
        (cloudFirst ? cloud : local).SetException(new Exception("private provider body"));
        (cloudFirst ? local : cloud).SetException(new Exception("private provider body"));
        var error = await Assert.ThrowsAsync<RecognitionException>(() => operation.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain("private", error.Message);
    }

    [Fact]
    public async Task BlankResultDoesNotWinAndLoserCancelled()
    {
        var cloud = new TaskCompletionSource<Transcript>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken localToken = default;
        var local = new TaskCompletionSource<Transcript>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = RecognitionRace.RunAsync(_ => cloud.Task, ct => { localToken = ct; return local.Task; }, TimeSpan.Zero, default);
        cloud.SetResult(new("   ", "cloud")); local.SetResult(new("valid", "local"));
        Assert.Equal("valid", (await operation).Text);
        Assert.True(localToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationStopsWaitingForUncooperativeProviders()
    {
        var late = new TaskCompletionSource<Transcript>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var operation = RecognitionRace.RunAsync(_ => late.Task, _ => late.Task, TimeSpan.Zero, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(1)));
        late.SetResult(new("must not deliver", "late"));
    }

    [Fact]
    public async Task HybridCorrectsWinnerOnce()
    {
        var cloud = new FakeEngine("cloud"); var local = new FakeEngine("local");
        var corrector = new FakeCorrector((text, _) => Task.FromResult(text + " corrected"));
        var result = await new DictationPipeline(cloud, local, corrector).ProcessAsync([], new(), true, true, null, default);
        Assert.Equal("cloud corrected", result.Text); Assert.Equal(1, corrector.Calls);
    }

    [Fact]
    public async Task CorrectionFailurePreservesOriginal()
    {
        var corrector = new FakeCorrector((_, _) => Task.FromException<string>(new Exception("secret")));
        var result = await new DictationPipeline(new FakeEngine("original"), new FakeEngine("local"), corrector)
            .ProcessAsync([], new() { Mode = RecognitionMode.OpenAI }, true, false, null, default);
        Assert.Equal("original", result.Text); Assert.True(result.CorrectionFailed);
    }

    [Fact]
    public async Task LocalModeNeverCallsCloudUnlessCorrectionExplicitlyEnabled()
    {
        var cloud = new FakeEngine("cloud"); var corrector = new FakeCorrector((_, _) => throw new Exception());
        var result = await new DictationPipeline(cloud, new FakeEngine("local"), corrector)
            .ProcessAsync([], new() { Mode = RecognitionMode.Local }, true, true, null, default);
        Assert.Equal("local", result.Text); Assert.Equal(0, cloud.Calls); Assert.Equal(0, corrector.Calls);
    }

    [Fact]
    public async Task HybridWithoutModelUsesCloud()
    {
        var local = new FakeEngine("local");
        var result = await new DictationPipeline(new FakeEngine("cloud"), local, new FakeCorrector((t, _) => Task.FromResult(t)))
            .ProcessAsync([], new() { CorrectionEnabled = false }, true, false, null, default);
        Assert.Equal("cloud", result.Text); Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task CancelDuringCorrectionNeverReturnsOriginalForInsertion()
    {
        using var cancel = new CancellationTokenSource();
        var corrector = new FakeCorrector(async (_, token) => { cancel.Cancel(); await Task.Delay(10000, token); return "late"; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DictationPipeline(new FakeEngine("original"), new FakeEngine("local"), corrector)
                .ProcessAsync([], new() { Mode = RecognitionMode.OpenAI }, true, false, null, cancel.Token));
    }

    [Fact]
    public async Task CorrectionTimeoutReturnsOriginalWithoutLateReplacement()
    {
        var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await new DictationPipeline(new FakeEngine("original"), new FakeEngine("local"), new FakeCorrector((_, _) => late.Task))
            .ProcessAsync([], new() { Mode = RecognitionMode.OpenAI, CorrectionTimeoutSeconds = 1 }, true, false, null, default);
        late.SetResult("late correction");
        Assert.Equal("original", result.Text); Assert.True(result.CorrectionFailed);
    }

    private sealed class FakeEngine(string result) : IRecognizer
    {
        public int Calls;
        public Task<Transcript> TranscribeAsync(byte[] wav, AppSettings settings, CancellationToken token)
        { Calls++; token.ThrowIfCancellationRequested(); return Task.FromResult(new Transcript(result, result)); }
    }
    private sealed class FakeCorrector(Func<string, CancellationToken, Task<string>> work) : ITextCorrector
    {
        public int Calls;
        public Task<string> CorrectAsync(string text, AppSettings settings, CancellationToken token) { Calls++; return work(text, token); }
    }
}

public class PersistenceAndAudioTests
{
    [Fact]
    public void SettingsRoundTripContainsNoSecretsOrTranscripts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoiceCaptureTests-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(directory);
            var settings = new AppSettings { Language = "ru", Mode = RecognitionMode.Local, Vocabulary = "Термины" };
            store.Save(settings);
            Assert.Equal(settings, store.Load());
            string json = File.ReadAllText(store.SettingsPath);
            Assert.DoesNotContain("ApiKey", json); Assert.DoesNotContain("history", json);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void CorruptSettingsAreNotOverwritten()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoiceCaptureTests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory); File.WriteAllText(store.SettingsPath, "broken");
            Assert.Equal(new AppSettings(), store.Load()); Assert.NotNull(store.LoadWarning);
            Assert.Equal("broken", File.ReadAllText(store.SettingsPath));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void InvalidModelAndUnmodifiedHotkeyAreRejected()
    {
        Assert.Throws<ArgumentException>(() => (new AppSettings { LocalModel = "../anything" }).Validate());
        Assert.Throws<ArgumentException>(() => (new AppSettings { HotkeyControl = false, HotkeyAlt = false }).Validate());
    }
    [Fact]
    public void WaveIs16KhzMonoPcmAndClamped()
    {
        var wav = AudioSignal.ToWave([-2f, 0f, 2f]);
        Assert.Equal(50, wav.Length); Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24)); Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(-32767, BitConverter.ToInt16(wav, 44)); Assert.Equal(32767, BitConverter.ToInt16(wav, 48));
        Assert.Equal(0, AudioSignal.Rms(new float[160]));
    }
}

public class OpenAiTests
{
    [Fact]
    public async Task AudioUsesDirectEndpointAndOmitsAutoLanguage()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            string body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("gpt-4o-mini-transcribe", body); Assert.DoesNotContain("name=language", body);
            return Json("{\"text\":\"Привет world\"}");
        }));
        Assert.Equal("Привет world", (await new OpenAiService(http, () => "test-key")
            .TranscribeAsync(AudioSignal.ToWave([0]), new(), default)).Text);
    }
    [Fact]
    public async Task CorrectionDisablesStorageAndParsesOnlyCompletedText()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            return Json("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Привет, world.\"}]}]}");
        }));
        Assert.Equal("Привет, world.", await new OpenAiService(http, () => "test")
            .CorrectAsync("привет world", new(), default));
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(429)] [InlineData(500)]
    public async Task ErrorDoesNotExposeResponseBody(int code)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)
            { Content = new StringContent("private text and secret") })));
        var error = await Assert.ThrowsAsync<RecognitionException>(() => new OpenAiService(http, () => "test")
            .TranscribeAsync([], new(), default));
        Assert.DoesNotContain("private", error.Message); Assert.DoesNotContain("secret", error.Message);
    }
    [Fact]
    public async Task IncompleteCorrectionIsRejected()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json("{\"status\":\"incomplete\",\"output\":[]}"))));
        await Assert.ThrowsAsync<RecognitionException>(() => new OpenAiService(http, () => "test").CorrectAsync("text", new(), default));
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }
}
