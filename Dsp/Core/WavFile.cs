using System.Buffers.Binary;
using System.Text;

namespace AgenticSdr.Dsp;

/// <summary>Writes 16-bit PCM audio into a WAV file the browser can play, and reads those clips back for digi analysis.</summary>
public static class WavFile
{
    public static bool TryRead(ReadOnlySpan<byte> wav, out float[] samples, out int sampleRate)
    {
        samples = [];
        sampleRate = 0;
        if (wav.Length < 44)
            return false;

        int channels = BinaryPrimitives.ReadInt16LittleEndian(wav[22..]);
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav[24..]);
        int bits = BinaryPrimitives.ReadInt16LittleEndian(wav[34..]);
        int dataOfs = 12;
        while (dataOfs + 8 <= wav.Length)
        {
            string tag = Encoding.ASCII.GetString(wav.Slice(dataOfs, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav[(dataOfs + 4)..]);
            if (tag == "data")
            {
                dataOfs += 8;
                break;
            }

            dataOfs += 8 + size;
            if (size < 0 || dataOfs > wav.Length)
                return false;
        }

        if (sampleRate < 4_000 || channels < 1 || bits is not (8 or 16))
            return false;

        int bytesPer = Math.Max(1, bits / 8 * channels);
        int frames = (wav.Length - dataOfs) / bytesPer;
        if (frames <= 0)
            return false;

        samples = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int at = dataOfs + i * bytesPer;
            if (bits == 16)
            {
                float s = BinaryPrimitives.ReadInt16LittleEndian(wav[at..]) / 32768f;
                if (channels > 1)
                {
                    float r = BinaryPrimitives.ReadInt16LittleEndian(wav[(at + 2)..]) / 32768f;
                    s = 0.5f * (s + r);
                }

                samples[i] = s;
            }
            else
            {
                samples[i] = wav[at] / 128f - 1f;
            }
        }

        return true;
    }

    /// <param name="samples">PCM frames. When <paramref name="channels"/> is 2, samples are interleaved L/R.</param>
    public static byte[] Write(ReadOnlySpan<float> samples, int sampleRate, int channels = 1)
    {
        channels = Math.Clamp(channels, 1, 2);
        int dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        wav[0] = (byte)'R';
        wav[1] = (byte)'I';
        wav[2] = (byte)'F';
        wav[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + dataBytes);
        wav[8] = (byte)'W';
        wav[9] = (byte)'A';
        wav[10] = (byte)'V';
        wav[11] = (byte)'E';
        wav[12] = (byte)'f';
        wav[13] = (byte)'m';
        wav[14] = (byte)'t';
        wav[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        wav[36] = (byte)'d';
        wav[37] = (byte)'a';
        wav[38] = (byte)'t';
        wav[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short value = (short)Math.Round(clamped * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), value);
        }

        return wav;
    }
}
