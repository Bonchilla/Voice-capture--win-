namespace VoiceCapture.Core;

public sealed record Transcript(string Text, string Source);
public sealed record DictationResult(string Text, string Source, bool CorrectionFailed);

public interface IRecognizer
{
    Task<Transcript> TranscribeAsync(byte[] wav, AppSettings settings, CancellationToken cancellationToken);
}

public interface ITextCorrector
{
    Task<string> CorrectAsync(string text, AppSettings settings, CancellationToken cancellationToken);
}

public sealed class RecognitionException(string message) : Exception(message);

/// <summary>First successful nonblank result; late results never reach the caller.</summary>
public static class RecognitionRace
{
    public static async Task<Transcript> RunAsync(
        Func<CancellationToken, Task<Transcript>> cloud,
        Func<CancellationToken, Task<Transcript>> local,
        TimeSpan delay, CancellationToken cancellationToken)
    {
        var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new List<Task<Transcript>>();
        Task<Transcript> cloudTask = Invoke(cloud, race.Token);
        tasks.Add(cloudTask);
        Task<Transcript>? localTask = null;
        try
        {
            var timer = Task.Delay(delay, race.Token);
            if (await Task.WhenAny(cloudTask, timer).WaitAsync(cancellationToken) == cloudTask)
            {
                try { return await Valid(cloudTask, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* Start local immediately on early failure. */ }
            }
            localTask = Invoke(local, race.Token);
            tasks.Add(localTask);
            var pending = new List<Task<Transcript>>(tasks);
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(cancellationToken);
                pending.Remove(finished);
                try { return await Valid(finished, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* Other engine may still succeed. Never expose provider response bodies. */ }
            }
            throw new RecognitionException("Оба движка не дали результата. Проверьте сеть, ключ и локальную модель.");
        }
        finally
        {
            race.Cancel();
            // Keep the linked source alive until native/cloud losers actually finish.
            _ = ObserveAndDisposeAsync(tasks, race);
        }
    }

    private static Task<Transcript> Invoke(Func<CancellationToken, Task<Transcript>> work, CancellationToken token)
    {
        try { return work(token); }
        catch (Exception ex) { return Task.FromException<Transcript>(ex); }
    }

    private static async Task<Transcript> Valid(Task<Transcript> task, CancellationToken token)
    {
        var result = await task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result.Text)) throw new RecognitionException("Пустой результат.");
        return result with { Text = result.Text.Trim() };
    }

    private static async Task ObserveAndDisposeAsync(IEnumerable<Task<Transcript>> tasks, CancellationTokenSource source)
    {
        try { await Task.WhenAll(tasks); } catch { /* Observe losing faults without logging content. */ }
        finally { source.Dispose(); }
    }
}

public sealed class DictationPipeline(IRecognizer cloud, IRecognizer local, ITextCorrector corrector)
{
    public async Task<DictationResult> ProcessAsync(byte[] wav, AppSettings settings,
        bool cloudAvailable, bool localAvailable, Action<string>? status, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.ProcessingTimeoutSeconds));
        var token = deadline.Token;
        status?.Invoke("Распознавание…");
        Transcript result;
        if (settings.Mode == RecognitionMode.Local)
        {
            if (!localAvailable) throw new RecognitionException("Сначала скачайте локальную модель в настройках.");
            result = await local.TranscribeAsync(wav, settings, token).WaitAsync(token);
        }
        else if (settings.Mode == RecognitionMode.OpenAI)
        {
            if (!cloudAvailable) throw new RecognitionException("Укажите ключ и разрешите облачную обработку в настройках.");
            result = await cloud.TranscribeAsync(wav, settings, token).WaitAsync(token);
        }
        else if (cloudAvailable && localAvailable)
        {
            result = await RecognitionRace.RunAsync(
                ct => cloud.TranscribeAsync(wav, settings, ct),
                ct => local.TranscribeAsync(wav, settings, ct),
                TimeSpan.FromMilliseconds(settings.LocalDelayMilliseconds), token);
        }
        else if (localAvailable) result = await local.TranscribeAsync(wav, settings, token).WaitAsync(token);
        else if (cloudAvailable) result = await cloud.TranscribeAsync(wav, settings, token).WaitAsync(token);
        else throw new RecognitionException("Настройте OpenAI или скачайте локальную модель.");

        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result.Text)) throw new RecognitionException("Речь не распознана.");
        string text = result.Text.Trim();
        bool failed = false;
        if (settings.ShouldCorrect)
        {
            if (!cloudAvailable) failed = true;
            else
            {
                status?.Invoke("Коррекция…");
                using var correction = CancellationTokenSource.CreateLinkedTokenSource(token);
                correction.CancelAfter(TimeSpan.FromSeconds(settings.CorrectionTimeoutSeconds));
                try
                {
                    var corrected = await corrector.CorrectAsync(text, settings, correction.Token).WaitAsync(correction.Token);
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(corrected)) failed = true;
                    else text = corrected.Trim();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { failed = true; }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(text, result.Source, failed);
    }
}

public static class AudioSignal
{
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (float value in samples) sum += value * value;
        return Math.Sqrt(sum / samples.Length);
    }

    public static byte[] ToWave(ReadOnlySpan<float> samples)
    {
        using var stream = new MemoryStream(44 + samples.Length * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
        writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        return stream.ToArray();
    }
}
