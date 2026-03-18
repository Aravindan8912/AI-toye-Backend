namespace JarvisBackend.Utils;

/// <summary>Builds a minimal WAV header for raw PCM (16-bit mono). Used when client (e.g. ESP32) sends raw PCM instead of full WAV.</summary>
public static class WavHelper
{
    private const int SampleRate = 16000;
    private const int NumChannels = 1;
    private const int BitsPerSample = 16;

    /// <summary>Returns true if the data starts with "RIFF" (valid WAV).</summary>
    public static bool IsWav(byte[] data)
    {
        if (data == null || data.Length < 4) return false;
        return data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F';
    }

    /// <summary>If data is WAV (has 44-byte header), returns only the raw PCM (bytes 44..end). Otherwise returns data as-is. Use when sending to ESP32 to avoid null-byte truncation in the header.</summary>
    public static byte[] StripWavHeader(byte[]? data)
    {
        if (data == null || data.Length <= 44) return data ?? Array.Empty<byte>();
        if (!IsWav(data)) return data;
        var pcm = new byte[data.Length - 44];
        Buffer.BlockCopy(data, 44, pcm, 0, pcm.Length);
        return pcm;
    }

    /// <summary>If data is raw PCM, returns 44-byte header + data. If already WAV, returns data as-is.</summary>
    public static byte[] EnsureWav(byte[]? data)
    {
        if (data == null || data.Length == 0) return Array.Empty<byte>();
        if (IsWav(data)) return data;

        int dataSize = data.Length;
        int byteRate = SampleRate * NumChannels * (BitsPerSample / 8);
        byte[] header = new byte[44];
        int o = 0;

        void Write(byte[] buf, int offset, byte[] value) { for (int i = 0; i < value.Length; i++) buf[offset + i] = value[i]; }
        void Write16(int offset, ushort v) { header[offset] = (byte)v; header[offset + 1] = (byte)(v >> 8); }
        void Write32(int offset, uint v) { header[offset] = (byte)v; header[offset + 1] = (byte)(v >> 8); header[offset + 2] = (byte)(v >> 16); header[offset + 3] = (byte)(v >> 24); }

        Write(header, o, new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); o += 4;
        Write32(o, (uint)(36 + dataSize)); o += 4;
        Write(header, o, new byte[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); o += 8;
        Write32(o, 16); o += 4;
        Write16(o, 1); o += 2;
        Write16(o, (ushort)NumChannels); o += 2;
        Write32(o, (uint)SampleRate); o += 4;
        Write32(o, (uint)byteRate); o += 4;
        Write16(o, (ushort)(NumChannels * (BitsPerSample / 8))); o += 2;
        Write16(o, (ushort)BitsPerSample); o += 2;
        Write(header, o, new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); o += 4;
        Write32(o, (uint)dataSize);

        var result = new byte[44 + dataSize];
        Buffer.BlockCopy(header, 0, result, 0, 44);
        Buffer.BlockCopy(data, 0, result, 44, dataSize);
        return result;
    }
}
