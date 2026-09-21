using System.Net;
using System.Security.Cryptography;
using System.Text;
using VoiceCapture.Core;

namespace VoiceCapture.Tests;

public class ModelDownloaderTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("public test model bytes");
    private static ModelInfo Fixture => new("test-package", "Test", "test/repo", new string('a', 40),
        new[] { "encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" }
            .Select(n => new ModelFile(n, Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes)))).ToArray());

    [Theory]
    [InlineData("success")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("http")]
    [InlineData("cancel")]
    public async Task PackageBecomesVisibleOnlyAfterAllComponentsValidate(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "VoiceCaptureModels-" + Guid.NewGuid());
        var model = Fixture;
        string destination = model.GetDirectory(root);
        Directory.CreateDirectory(destination);
        foreach (var file in model.Files) File.WriteAllText(Path.Combine(destination, file.Name), "old");
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        try
        {
            using var client = new HttpClient(new Handler(request =>
            {
                calls++;
                Assert.Contains(model.Revision, request.RequestUri!.AbsolutePath);
                Assert.Equal("old", File.ReadAllText(Path.Combine(destination, model.Files[0].Name)));
                byte[] bytes = Bytes.ToArray();
                if (calls == 4)
                {
                    if (scenario == "corrupt") bytes[0] ^= 1;
                    if (scenario == "truncated") bytes = bytes[..^1];
                    if (scenario == "http") return new(HttpStatusCode.Forbidden);
                    if (scenario == "cancel") cancellation.Cancel();
                }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }));
            var task = new ModelDownloader(client, root).InstallAsync(model, null, cancellation.Token);
            if (scenario == "success")
            {
                await task;
                Assert.True(model.IsInstalled(root));
                await ModelDownloader.VerifyAsync(model, root, default);
            }
            else
            {
                if (scenario == "http") await Assert.ThrowsAsync<HttpRequestException>(() => task);
                else if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                else await Assert.ThrowsAsync<RecognitionException>(() => task);
                foreach (var file in model.Files) Assert.Equal("old", File.ReadAllText(Path.Combine(destination, file.Name)));
            }
            Assert.Single(Directory.GetDirectories(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SameSizeCorruptionAndMissingTokensAreRejectedBeforeNativeLoad()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoiceCaptureModels-" + Guid.NewGuid());
        var model = Fixture;
        Directory.CreateDirectory(model.GetDirectory(root));
        try
        {
            foreach (var file in model.Files) File.WriteAllBytes(Path.Combine(model.GetDirectory(root), file.Name), Bytes);
            await ModelDownloader.VerifyAsync(model, root, default);
            var corrupt = Bytes.ToArray(); corrupt[0] ^= 1;
            File.WriteAllBytes(Path.Combine(model.GetDirectory(root), "tokens.txt"), corrupt);
            await Assert.ThrowsAsync<RecognitionException>(() => ModelDownloader.VerifyAsync(model, root, default));
            File.Delete(Path.Combine(model.GetDirectory(root), "tokens.txt"));
            Assert.False(model.IsInstalled(root));
            await Assert.ThrowsAsync<RecognitionException>(() => ModelDownloader.VerifyAsync(model, root, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PublicDownloaderRejectsArbitraryPackages()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected")));
        await Assert.ThrowsAsync<ArgumentException>(() => new ModelDownloader(http, ".").DownloadAsync(Fixture, null, default));
    }

    [Fact]
    public void ProductionManifestIsPinnedAndComplete()
    {
        var model = ModelCatalog.Get(ModelCatalog.ParakeetId);
        Assert.Equal(4, model.Files.Count);
        Assert.Equal(670478772, model.TotalSize);
        Assert.Equal(40, model.Revision.Length);
        Assert.All(model.Files, f => { Assert.Equal(64, f.Sha256.Length); Assert.Equal(Path.GetFileName(f.Name), f.Name); });
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}

public class AudioChunkTests
{
    [Fact]
    public void QuietBoundaryStaysWithinBounds()
    {
        var samples = Enumerable.Repeat(0.5f, 400000).ToArray();
        Array.Clear(samples, 350000, 4000);
        int cut = AudioChunks.FindBoundary(samples, 320000, 400000);
        Assert.InRange(cut, 350000, 354000);
        Assert.Equal(samples.Length, samples[..cut].Length + samples[cut..].Length);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, 10)]
    [InlineData(0, 101)]
    public void InvalidRangesRejected(int start, int end) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioChunks.FindBoundary(new float[100], start, end));
}
