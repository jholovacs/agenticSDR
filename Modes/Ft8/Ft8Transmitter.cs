using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Ft8;

public sealed record Ft8TransmitRequest(string Text, double FrequencyHz, double PowerWatts, double AudioHz = 1_500);

public sealed record Ft8TransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

/// <summary>Experimental FT8 transmit gated by operator click, Part 97, and SDR TX capability.</summary>
public static class Ft8Transmitter
{
    public static Ft8TransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        Ft8TransmitRequest request,
        IRadioDevice? radio)
    {
        ArgumentNullException.ThrowIfNull(request);
        TransmitAuthorization gate = TransmitGate.Evaluate(
            op, click, AmateurMode.Ft8, request.FrequencyHz, request.PowerWatts, request.Text, radio);

        var samples = new float[Ft8Decoder.SampleRate * 15];
        try
        {
            Ft8Decoder.Modulate(samples, Ft8Decoder.SampleRate, request.Text.Trim(), request.AudioHz, 0.5);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, false, null, 0);
        }

        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(ToIq(samples));
            return new(true, gate.Reason, true, samples, Ft8Decoder.SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local baseband only; nothing radiated.", false, samples, Ft8Decoder.SampleRate);

        return new(false, gate.Reason, false, null, 0);
    }

    static float[] ToIq(float[] audio)
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
