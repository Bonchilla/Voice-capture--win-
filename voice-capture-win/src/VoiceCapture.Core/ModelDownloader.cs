using System.Security.Cryptography;

namespace VoiceCapture.Core;

public sealed record DownloadProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? (double)Received / Total : 0;
}

public sealed class ModelDownloader(HttpClient client, string directory)
{
    public Task DownloadAsync(ModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        if (!ModelCatalog.All.Contains(model)) throw new ArgumentException("Неизвестная модель.");
        return InstallAsync(model, progress, token);
    }

    internal async Task InstallAsync(ModelInfo model, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        // All components are validated before making the package visible to the recognizer.
        string destination = model.GetDirectory(directory);
        string staging = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        string backup = destination + "." + Guid.NewGuid().ToString("N") + ".backup";
        Directory.CreateDirectory(staging);
        try
        {
            long totalReceived = 0;
            foreach (var file in model.Files)
            {
                using var response = await client.GetAsync(
                    $"https://huggingface.co/{model.Repository}/resolve/{model.Revision}/{file.Name}",
                    HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long declared && declared != file.Size)
                    throw new RecognitionException("Размер компонента модели не соответствует каталогу.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                await using (var output = new FileStream(Path.Combine(staging, file.Name), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 131072, true))
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                {
                    byte[] buffer = new byte[131072];
                    int count;
                    while ((count = await input.ReadAsync(buffer, token)) != 0)
                    {
                        received += count;
                        if (received > file.Size) throw new RecognitionException("Компонент модели превышает ожидаемый размер.");
                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        progress?.Report(new(totalReceived + received, model.TotalSize));
                    }
                    await output.FlushAsync(token);
                }
                if (received != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new RecognitionException("Проверка SHA-256 модели не пройдена. Старая модель сохранена.");
                totalReceived += received;
            }
            token.ThrowIfCancellationRequested();
            bool hadPrevious = Directory.Exists(destination);
            if (hadPrevious) Directory.Move(destination, backup);
            try { Directory.Move(staging, destination); }
            catch
            {
                if (hadPrevious) Directory.Move(backup, destination);
                throw;
            }
            // Failure to remove an old backup must not invalidate the successfully installed package.
            if (hadPrevious) TryDelete(backup);
        }
        finally { TryDelete(staging); }
    }

    public static async Task VerifyAsync(ModelInfo model, string root, CancellationToken token)
    {
        foreach (var file in model.Files)
        {
            var path = Path.Combine(model.GetDirectory(root), file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
                throw new RecognitionException("Parakeet не установлен полностью. Скачайте модель в настройках.");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            var hash = await SHA256.HashDataAsync(input, token);
            if (!Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new RecognitionException("Повреждён компонент Parakeet. Повторно скачайте модель.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
