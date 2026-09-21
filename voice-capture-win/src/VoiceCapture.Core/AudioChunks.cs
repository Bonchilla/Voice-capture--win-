namespace VoiceCapture.Core;

public static class AudioChunks
{
    // Search 100 ms energy windows, placing the cut in the quietest window's centre.
    // This is a memory bound, not linguistic segmentation: continuous speech may split a word.
    public static int FindBoundary(float[] samples, int minimum, int maximum)
    {
        if (minimum < 0 || maximum > samples.Length || maximum <= minimum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        const int window = 1600;
        double best = double.MaxValue;
        int boundary = maximum;
        for (int i = minimum; i + window <= maximum; i += window / 2)
        {
            double energy = 0;
            for (int j = i; j < i + window; j++) energy += (double)samples[j] * samples[j];
            if (energy <= best) { best = energy; boundary = i + window / 2; }
        }
        return boundary;
    }
}
