using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Ribbit;

public sealed record RibbitTransmitRequest(string Text, double FrequencyHz, double PowerWatts);

public sealed record RibbitTransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

/// <summary>
/// Local baseband Ribbit tone-comb for desk tests. Full polar-coded payloads need the native modem.
/// </summary>
public static class RibbitTransmitter
{
    public const int SampleRate = RibbitDecoder.SampleRate;

    public static RibbitTransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        RibbitTransmitRequest request,
        IRadioDevice? radio)
    {
        ArgumentNullException.ThrowIfNull(request);
        TransmitAuthorization gate = TransmitGate.Evaluate(
            op, click, AmateurMode.Ribbit, request.FrequencyHz, request.PowerWatts, request.Text, radio);

        if (op is null)
            return new(false, "Sign in as the control operator first.", false, null, 0);

        float[] samples = RibbitDecoder.SynthesizeToneComb(SampleRate, durationSeconds: 0.8);
        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(ToIq(samples));
            return new(true, gate.Reason, true, samples, SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local OFDM comb only; nothing radiated.", false, samples, SampleRate);

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
