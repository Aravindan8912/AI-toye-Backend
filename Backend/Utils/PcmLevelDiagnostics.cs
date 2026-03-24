namespace JarvisBackend.Utils;

/// <summary>Quick 16-bit mono LE PCM stats to verify ESP32/mic capture before Whisper.</summary>
public static class PcmLevelDiagnostics
{
    public static (int SampleCount, int PeakAbs, double Rms, double NearSilenceFraction) Analyze16BitLeMono(ReadOnlySpan<byte> pcm)
    {
        var n = pcm.Length / 2;
        if (n <= 0)
            return (0, 0, 0, 1);

        var peak = 0;
        long sumSq = 0;
        var nearSilence = 0;
        const int silenceThreshold = 120; // ~ -48 dBFS ballpark

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var s = (short)(pcm[i] | (pcm[i + 1] << 8));
            var a = s == short.MinValue ? 32768 : Math.Abs((int)s);
            if (a > peak)
                peak = a;
            sumSq += (long)s * s;
            if (a < silenceThreshold)
                nearSilence++;
        }

        var rms = Math.Sqrt(sumSq / (double)n);
        var fracSilent = nearSilence / (double)n;
        return (n, peak, rms, fracSilent);
    }

    /// <summary>Uses PCM after optional 44-byte WAV strip.</summary>
    public static (int SampleCount, int PeakAbs, double Rms, double NearSilenceFraction) AnalyzeRawOrWavPcm(byte[] data)
    {
        if (data == null || data.Length == 0)
            return (0, 0, 0, 1);
        var pcm = WavHelper.IsWav(data) && data.Length > 44
            ? data.AsSpan(44)
            : data.AsSpan();
        return Analyze16BitLeMono(pcm);
    }
}
