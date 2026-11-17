using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Cw.Transmit;

public sealed record CwTransmitRequest(
    string Text,
    double FrequencyHz,
    double PowerWatts,
    double ToneHz = CwAudio.DefaultToneHz);

public sealed record CwTransmitResult(
    bool Ok,
    string Reason,
    bool Radiated,
    string? AudioSummary,
    byte[]? Wav);

public sealed record CwTransmitPlan(
    bool Ok,
    string Reason,
    bool MayRadiate,
    bool LocalPreview,
    float[]? Samples,
    string? AudioSummary);

/// <summary>
/// Experimental CW transmit path. Radiates only when TransmitGate allows and the radio implements
/// <see cref="IRadioTransmitter"/>. Until hardware TX exists, a valid click returns local preview
/// audio with <c>Radiated=false</c>.
/// </summary>
public static class CwTransmitter
{
    public static CwTransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        CwTransmitRequest request,
        IRadioDevice? radio,
        bool requireMessageIdentification = true)
    {
        CwTransmitPlan plan = Plan(op, click, request, radio, requireMessageIdentification);
        if (!plan.Ok || plan.Samples is null)
            return new CwTransmitResult(false, plan.Reason, false, null, null);

        byte[] wav = CwAudio.ToWav(plan.Samples);
        if (plan.MayRadiate)
        {
            if (radio is not IRadioTransmitter transmitter)
                return new CwTransmitResult(false, "Transmit-capable radio missing.", false, null, null);

            transmitter.WriteTransmit(AudioToIq(plan.Samples));
            bool radiated = transmitter.Radiates;
            return new CwTransmitResult(
                true,
                radiated
                    ? plan.Reason
                    : plan.Reason + " Simulated transmit — nothing radiated.",
                Radiated: radiated,
                plan.AudioSummary + (radiated ? " · radiated" : " · simulated"),
                wav);
        }

        return new CwTransmitResult(
            true,
            plan.Reason + (plan.LocalPreview ? " Local preview only; nothing radiated." : ""),
            Radiated: false,
            plan.AudioSummary + (plan.LocalPreview ? " · local preview" : ""),
            wav);
    }

    public static CwTransmitPlan Plan(
        OperatorIdentity? op,
        OperatorClick? click,
        CwTransmitRequest request,
        IRadioDevice? radio,
        bool requireMessageIdentification = true)
    {
        ArgumentNullException.ThrowIfNull(request);

        TransmitAuthorization gate = TransmitGate.Evaluate(
            op,
            click,
            AmateurMode.Cw,
            request.FrequencyHz,
            request.PowerWatts,
            request.Text,
            radio,
            requireMessageIdentification);

        float[] audio;
        try
        {
            audio = CwAudio.Modulate(request.Text, request.ToneHz);
        }
        catch (Exception ex)
        {
            return new CwTransmitPlan(false, ex.Message, false, false, null, null);
        }

        string summary = $"CW · {CwAudio.MaxWordsPerMinute:0} WPM · {request.ToneHz:0} Hz";

        if (gate.MayRadiate)
            return new CwTransmitPlan(true, gate.Reason, MayRadiate: true, LocalPreview: false, audio, summary);

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new CwTransmitPlan(true, gate.Reason, MayRadiate: false, LocalPreview: true, audio, summary);

        return new CwTransmitPlan(false, gate.Reason, false, false, null, null);
    }

    /// <summary>
    /// Write 16-bit PCM to <paramref name="output"/> in wall-clock time so the operator hears sidetone
    /// while the same chunks are handed to a transmit-capable radio.
    /// </summary>
    public static async Task PlayAsync(
        CwTransmitPlan plan,
        IRadioDevice? radio,
        Stream output,
        CancellationToken cancellationToken)
    {
        if (!plan.Ok || plan.Samples is null || plan.Samples.Length == 0)
            return;

        IRadioTransmitter? transmitter = plan.MayRadiate ? radio as IRadioTransmitter : null;
        int rate = CwAudio.SampleRate;
        int chunk = Math.Max(1, rate / 10);
        float[] samples = plan.Samples;
        var pcm = new byte[chunk * 2];
        var iq = transmitter is null ? null : new float[chunk * 2];

        for (int offset = 0; offset < samples.Length; offset += chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Math.Min(chunk, samples.Length - offset);
            int pcmBytes = n * 2;
            for (int i = 0; i < n; i++)
            {
                short value = (short)Math.Round(Math.Clamp(samples[offset + i], -1f, 1f) * 32_767f);
                pcm[i * 2] = (byte)value;
                pcm[i * 2 + 1] = (byte)(value >> 8);
                if (iq is not null)
                {
                    iq[i * 2] = samples[offset + i];
                    iq[i * 2 + 1] = 0f;
                }
            }

            if (transmitter is not null && iq is not null)
                transmitter.WriteTransmit(iq.AsSpan(0, n * 2));

            await output.WriteAsync(pcm.AsMemory(0, pcmBytes), cancellationToken);
            await output.FlushAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds((double)n / rate), cancellationToken);
        }
    }

    static float[] AudioToIq(float[] audio)
    {
        var iq = new float[audio.Length * 2];
        for (int i = 0; i < audio.Length; i++)
        {
            iq[i * 2] = audio[i];
            iq[i * 2 + 1] = 0f;
        }

        return iq;
    }
}
