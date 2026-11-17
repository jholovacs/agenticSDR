using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Jtty;

public sealed record JttyTransmitRequest(string Text, double FrequencyHz, double PowerWatts, double AudioHz = 1_500);

public sealed record JttyTransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

public static class JttyTransmitter
{
    public static JttyTransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        JttyTransmitRequest request,
        IRadioDevice? radio)
    {
        ArgumentNullException.ThrowIfNull(request);
        TransmitAuthorization gate = TransmitGate.Evaluate(
            op, click, AmateurMode.Jtty, request.FrequencyHz, request.PowerWatts, request.Text, radio);

        int seconds = Math.Clamp(4 + request.Text.Length / 4, 6, 60);
        var samples = new float[JttyDecoder.SampleRate * seconds];
        try
        {
            JttyDecoder.Modulate(samples, JttyDecoder.SampleRate, request.Text.Trim(), lowestToneHz: request.AudioHz);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, false, null, 0);
        }

        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(ToIq(samples));
            return new(true, gate.Reason, true, samples, JttyDecoder.SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local baseband only; nothing radiated.", false, samples, JttyDecoder.SampleRate);

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
