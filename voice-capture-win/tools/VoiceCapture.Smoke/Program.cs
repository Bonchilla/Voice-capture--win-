using VoiceCapture.Core;
using VoiceCapture.Windows;

// Explicit developer-only tool: downloads a public model, never accesses microphone or OpenAI.
if (args.Length < 2)
{
    Console.Error.WriteLine("Arguments: <model-directory> <public-fixture.wav> [expected-keyword; default country]");
    return 2;
}
string directory = Path.GetFullPath(args[0]);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var model = ModelCatalog.Get(ModelCatalog.ParakeetId);
if (!model.IsInstalled(directory))
{
    Console.WriteLine("Downloading public Parakeet TDT v3 INT8 with pinned SHA-256 validation (640 MiB)...");
    await new ModelDownloader(http, directory).DownloadAsync(model, null, timeout.Token);
}
using var engine = new LocalParakeetService(directory);
string keyword = args.ElementAtOrDefault(2) ?? "country";
var settings = new AppSettings
{
    LocalModel = model.FileName, Mode = RecognitionMode.Local, CorrectionEnabled = false,
    Acceleration = AccelerationMode.Cpu
};
Console.WriteLine("Adapters: " + engine.Hardware.Description);
Console.WriteLine($"Intel UHD only: {engine.Hardware.IntelUhdOnly}; mode={settings.Acceleration}; requestGpu={engine.Hardware.ShouldUseGpu(settings.Acceleration)}");
byte[] fixture = await File.ReadAllBytesAsync(args[1], timeout.Token);
var watch = System.Diagnostics.Stopwatch.StartNew();
Transcript result;
try { result = await engine.TranscribeAsync(fixture, settings, timeout.Token); }
catch (Exception ex) { Console.Error.WriteLine("Local inference failed: " + ex.GetType().Name); return 3; }
Console.WriteLine($"Local inference: {(string.IsNullOrWhiteSpace(result.Text) ? "FAIL" : "PASS")}; elapsed={watch.Elapsed.TotalSeconds:F1}s; backend={engine.BackendDescription}");
// Do not print transcripts, even for a public fixture.
bool expected = keyword.Split('|').All(k => result.Text.Contains(k, StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"Expected fixture keyword: {expected}");
if (!expected) return 1;
var times = new List<double>();
for (int i = 0; i < 3; i++)
{
    watch.Restart();
    var warm = await engine.TranscribeAsync(fixture, settings, timeout.Token);
    times.Add(watch.Elapsed.TotalSeconds);
    if (!keyword.Split('|').All(k => warm.Text.Contains(k, StringComparison.OrdinalIgnoreCase))) return 1;
}
Console.WriteLine("Warm runs seconds: " + string.Join(", ", times.Select(t => t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))));
Console.WriteLine($"Warm median seconds: {times.Order().ElementAt(1):F2}");
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
{
    try { await engine.TranscribeAsync(fixture, settings, cancel.Token); Console.WriteLine("Cancellation: operation finished before cancellation"); }
    catch (OperationCanceledException) { Console.WriteLine("Cancellation: PASS"); }
}
var next = await engine.TranscribeAsync(fixture, settings, timeout.Token);
bool reusable = keyword.Split('|').All(k => next.Text.Contains(k, StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"Reuse after cancellation: {reusable}");
return reusable ? 0 : 1;
