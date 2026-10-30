using AgenticSdr.Dsp;

namespace AgenticSdr.Tests;

/// <summary>Checks that known Morse timings, including a little jitter, decode to the expected letters.</summary>
public class MorseTests
{
    [Theory]
    [InlineData("CQ")]
    [InlineData("SOS")]
    [InlineData("CQ TEST")]
    public void Decodes_clean_text(string text)
    {
        float[] audio = Key(text, wpm: 20, jitter: 0, seed: 1);
        Assert.Equal(text, MorseDecoder.Decode(audio, 48_000));
    }

    [Fact]
    public void Decodes_text_with_timing_jitter()
    {
        float[] audio = Key("CQ TEST", wpm: 18, jitter: 0.15, seed: 7);
        Assert.Equal("CQ TEST", MorseDecoder.Decode(audio, 48_000));
    }

    private static float[] Key(string text, int wpm, double jitter, int seed)
    {
        const int sampleRate = 48_000;
        double dit = sampleRate * 1.2 / wpm;
        var random = new Random(seed);
        var samples = new List<float>();
        double phase = 0;

        void Add(bool mark, double units)
        {
            double scale = 1 + (random.NextDouble() * 2 - 1) * jitter;
            int count = Math.Max(1, (int)Math.Round(dit * units * scale));
            for (int i = 0; i < count; i++)
            {
                phase += 2 * Math.PI * 700 / sampleRate;
                samples.Add(mark ? (float)Math.Sin(phase) : 0);
            }
        }

        foreach (char raw in text)
        {
            if (raw == ' ')
            {
                Add(false, 4);
                continue;
            }

            string pattern = Pattern(raw);
            for (int i = 0; i < pattern.Length; i++)
            {
                Add(true, pattern[i] == '-' ? 3 : 1);
                if (i < pattern.Length - 1)
                    Add(false, 1);
            }

            Add(false, 3);
        }

        return samples.ToArray();
    }

    private static string Pattern(char letter) => letter switch
    {
        'A' => ".-", 'B' => "-...", 'C' => "-.-.", 'D' => "-..", 'E' => ".",
        'F' => "..-.", 'G' => "--.", 'H' => "....", 'I' => "..", 'J' => ".---",
        'K' => "-.-", 'L' => ".-..", 'M' => "--", 'N' => "-.", 'O' => "---",
        'P' => ".--.", 'Q' => "--.-", 'R' => ".-.", 'S' => "...", 'T' => "-",
        'U' => "..-", 'V' => "...-", 'W' => ".--", 'X' => "-..-", 'Y' => "-.--",
        'Z' => "--..",
        _ => throw new ArgumentException(letter.ToString())
    };
}
