using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Olivia;

public sealed record OliviaTransmitRequest(string Text, double FrequencyHz, double PowerWatts, double AudioHz = 1_500);

public sealed record OliviaTransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

public static class OliviaTransmitter
{
    public static OliviaTransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        OliviaTransmitRequest request,
        IRadioDevice? radio)
    {
        ArgumentNullException.ThrowIfNull(request);
        TransmitAuthorization gate = TransmitGate.Evaluate(
            op, click, AmateurMode.Olivia, request.FrequencyHz, request.PowerWatts, request.Text, radio);

        float[] samples;
        try
        {
            samples = OliviaDecoder.Modulate(request.Text, OliviaDecoder.Profiles[0], OliviaDecoder.SampleRate, centerHz: request.AudioHz);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, false, null, 0);
        }

        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(ToIq(samples));
            return new(true, gate.Reason, true, samples, OliviaDecoder.SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local baseband only; nothing radiated.", false, samples, OliviaDecoder.SampleRate);

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
