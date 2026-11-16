using AgenticSdr.Dsp;

namespace AgenticSdr.Station;

/// <summary>
/// Turns an authorized CW or digimode message into baseband audio the operator can play locally.
/// Radiated stays false: this never keys a radio or SDR transmitter.
/// </summary>
public static class StationSend
{
    public const double DefaultAudioHz = 1_500;

    public static bool Supports(StationMode mode) => mode is
        StationMode.Cw or
        StationMode.Ft8 or
        StationMode.Js8 or
        StationMode.Olivia or
        StationMode.Jtty or
        StationMode.Packet;

    public static string UnsupportedReason(StationMode mode) => mode switch
    {
        StationMode.Usb or StationMode.Lsb or StationMode.Am or StationMode.Nbfm =>
            "Phone modes need a microphone path. This desk encodes CW and digimodes as audio only.",
        StationMode.Varac =>
            "VarAC transmit is not built into this desk yet.",
        StationMode.Data =>
            "Generic data has no encoder here. Pick FT8, JS8, Olivia, JTTY, Packet, or CW.",
        _ => "That mode has no send audio on this desk."
    };

    /// <summary>Build a WAV the browser can play. Throws <see cref="ArgumentException"/> when the text cannot be packed for the mode.</summary>
    public static SendAudio Render(AuthorizedTransmission transmission, double audioHz = DefaultAudioHz)
    {
        ArgumentNullException.ThrowIfNull(transmission);
        if (!Supports(transmission.Mode))
            throw new ArgumentException(UnsupportedReason(transmission.Mode));

        float[] samples;
        int sampleRate;
        string summary;
        switch (transmission.Mode)
        {
            case StationMode.Cw:
                sampleRate = 8_000;
                samples = MorseDecoder.Modulate(transmission.Text, sampleRate, toneHz: Math.Clamp(audioHz, 400, 1_000));
                summary = $"CW · {audioHz:0} Hz tone · local audio only";
                break;

            case StationMode.Ft8:
            {
                sampleRate = Ft8Decoder.SampleRate;
                samples = new float[sampleRate * 15];
                Ft8Decoder.Modulate(samples, sampleRate, transmission.Text.Trim(), audioHz, 0.5);
                NormalizePeak(samples, 0.7f);
                summary = $"FT8 · {audioHz:0} Hz · one 15 s slot · local audio only";
                break;
            }

            case StationMode.Js8:
            {
                sampleRate = Js8Decoder.SampleRate;
                samples = new float[sampleRate * 30];
                Js8Decoder.Modulate(samples, sampleRate, transmission.Text.Trim(), audioHz, 0.5, transmission.BaseCall);
                NormalizePeak(samples, 0.7f);
                summary = $"JS8 · {audioHz:0} Hz · local audio only";
                break;
            }

            case StationMode.Olivia:
                sampleRate = OliviaDecoder.SampleRate;
                samples = OliviaDecoder.Modulate(transmission.Text, OliviaDecoder.Profiles[0], sampleRate, centerHz: audioHz);
                summary = $"Olivia {OliviaDecoder.Profiles[0].Name} · {audioHz:0} Hz center · local audio only";
                break;

            case StationMode.Jtty:
            {
                sampleRate = JttyDecoder.SampleRate;
                int seconds = Math.Clamp(4 + transmission.Text.Length / 4, 6, 60);
                samples = new float[sampleRate * seconds];
                JttyDecoder.Modulate(samples, sampleRate, transmission.Text.Trim(), lowestToneHz: audioHz);
                NormalizePeak(samples, 0.7f);
                TrimTrailingSilence(ref samples, sampleRate / 10);
                summary = $"JTTY · {audioHz:0} Hz · local audio only";
                break;
            }

            case StationMode.Packet:
            {
                sampleRate = 48_000;
                (string dest, string payload) = SplitPacket(transmission.Text);
                samples = PacketDecoder.SynthesizeUiAudio(
                    transmission.BaseCall,
                    0,
                    dest,
                    0,
                    payload,
                    sampleRate: sampleRate,
                    preambleFlags: 20);
                summary = $"Packet AFSK · {transmission.BaseCall}>{dest} · local audio only";
                break;
            }

            default:
                throw new ArgumentException(UnsupportedReason(transmission.Mode));
        }

        return new SendAudio(
            transmission.Id,
            transmission.Mode,
            sampleRate,
            WavFile.Write(samples, sampleRate),
            summary,
            samples.Length / (double)sampleRate);
    }

    static (string Dest, string Payload) SplitPacket(string text)
    {
        string body = text.Trim();
        int gt = body.IndexOf('>');
        if (gt > 0 && gt < body.Length - 1)
        {
            string dest = body[..gt].Trim();
            string payload = body[(gt + 1)..].Trim();
            if (dest.Length is >= 3 and <= 9 && payload.Length > 0)
                return (dest.ToUpperInvariant(), payload);
        }

        return ("CQ", body);
    }

    static void NormalizePeak(Span<float> samples, float peak)
    {
        float max = 0;
        foreach (float sample in samples)
        {
            float a = Math.Abs(sample);
            if (a > max)
                max = a;
        }

        if (max < 1e-6f)
            return;
        float gain = peak / max;
        for (int i = 0; i < samples.Length; i++)
            samples[i] *= gain;
    }

    static void TrimTrailingSilence(ref float[] samples, int keep)
    {
        int end = samples.Length - 1;
        while (end > keep && Math.Abs(samples[end]) < 1e-4f)
            end--;
        int length = Math.Min(samples.Length, end + 1 + keep);
        if (length < samples.Length)
            Array.Resize(ref samples, length);
    }
}

/// <summary>WAV bytes for one authorized CW/digimode message, for local speaker playback only.</summary>
public sealed record SendAudio(
    string TransmissionId,
    StationMode Mode,
    int SampleRate,
    byte[] Wav,
    string Summary,
    double DurationSeconds);
