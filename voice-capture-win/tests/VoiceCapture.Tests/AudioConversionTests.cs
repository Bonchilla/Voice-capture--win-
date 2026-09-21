using NAudio.Wave;
using VoiceCapture.Windows;

namespace VoiceCapture.Tests;

public class AudioConversionTests
{
    [Theory]
    [InlineData(16000, 1, false)]
    [InlineData(44100, 2, false)]
    [InlineData(48000, 2, true)]
    [InlineData(48000, 1, true)]
    public void CaptureConversionPreservesDurationLevelAndPitch(int rate, int channels, bool floating)
    {
        var format = floating ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, 16, channels);
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, System.Text.Encoding.UTF8, true))
            for (int i = 0; i < rate; i++)
                for (int c = 0; c < channels; c++)
                {
                    float value = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / rate));
                    if (floating) writer.Write(value); else writer.Write((short)(value * 32767));
                }
        byte[] wav = AudioRecorder.ConvertAudio(raw.ToArray(), format, default);
        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels); Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.InRange(reader.TotalTime.TotalSeconds, 0.98, 1.02);
        var provider = reader.ToSampleProvider();
        var samples = new float[20000]; int count = provider.Read(samples, 0, samples.Length);
        Assert.InRange(AudioSignal.Rms(samples[..count]), 0.20, 0.22);
        int crossings = 0;
        for (int i = 1; i < count; i++) if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;
        Assert.InRange(crossings, 435, 445);
    }

    [Fact]
    public void SilenceAndVeryShortInputAreRejected()
    {
        var format = new WaveFormat(16000, 16, 1);
        Assert.Throws<RecognitionException>(() => AudioRecorder.ConvertAudio(new byte[32000], format, default));
        Assert.Throws<RecognitionException>(() => AudioRecorder.ConvertAudio(new byte[100], format, default));
    }

    [Fact]
    public void ConversionHonoursCancellation()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => AudioRecorder.ConvertAudio(new byte[32000], new WaveFormat(16000, 16, 1), cancel.Token));
    }
}
