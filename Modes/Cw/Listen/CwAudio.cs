using AgenticSdr.Dsp;

namespace AgenticSdr.Cw.Listen;

/// <summary>CW local preview and baseband encoding. Cap is 18 WPM.</summary>
public static class CwAudio
{
    public const double DefaultToneHz = 700;
    public const double MaxWordsPerMinute = 18;
    public const int SampleRate = 8_000;

    public static float[] Modulate(string text, double toneHz = DefaultToneHz) =>
        MorseDecoder.Modulate(
            text,
            SampleRate,
            toneHz: Math.Clamp(toneHz, 400, 1_000),
            wordsPerMinute: MaxWordsPerMinute);

    public static byte[] ToWav(float[] samples)
    {
        short[] pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float clipped = Math.Clamp(samples[i], -1f, 1f);
            pcm[i] = (short)Math.Round(clipped * 32_767f);
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            int dataBytes = pcm.Length * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (short sample in pcm)
                writer.Write(sample);
        }

        return stream.ToArray();
    }
}
