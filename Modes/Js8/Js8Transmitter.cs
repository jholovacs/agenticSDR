using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Js8;

public sealed record Js8TransmitRequest(string Text, double FrequencyHz, double PowerWatts, double AudioHz = 1_500);

public sealed record Js8TransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

public static class Js8Transmitter
{
    public static Js8TransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        Js8TransmitRequest request,
        IRadioDevice? radio)
    {
        ArgumentNullException.ThrowIfNull(request);
        TransmitAuthorization gate = TransmitGate.Evaluate(
            op, click, AmateurMode.Js8, request.FrequencyHz, request.PowerWatts, request.Text, radio);

        var samples = new float[Js8Decoder.SampleRate * 30];
        try
        {
            Js8Decoder.Modulate(samples, Js8Decoder.SampleRate, request.Text.Trim(), request.AudioHz, 0.5, op?.BaseCall);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, false, null, 0);
        }

        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(ToIq(samples));
            return new(true, gate.Reason, true, samples, Js8Decoder.SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local baseband only; nothing radiated.", false, samples, Js8Decoder.SampleRate);

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
