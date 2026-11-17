using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Packet;

public sealed record PacketTransmitRequest(string Text, double FrequencyHz, double PowerWatts);

public sealed record PacketTransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

public static class PacketTransmitter
{
    public const int SampleRate = 48_000;

    public static PacketTransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        PacketTransmitRequest request,
        IRadioDevice? radio)
    {
        ArgumentNullException.ThrowIfNull(request);
        TransmitAuthorization gate = TransmitGate.Evaluate(
            op, click, AmateurMode.Packet, request.FrequencyHz, request.PowerWatts, request.Text, radio);

        if (op is null)
            return new(false, "Sign in as the control operator first.", false, null, 0);

        float[] samples;
        try
        {
            (string dest, string payload) = Split(request.Text);
            samples = PacketDecoder.SynthesizeUiAudio(
                op.BaseCall,
                0,
                dest,
                0,
                payload,
                sampleRate: SampleRate,
                preambleFlags: 20);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, false, null, 0);
        }

        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(ToIq(samples));
            return new(true, gate.Reason, true, samples, SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local baseband only; nothing radiated.", false, samples, SampleRate);

        return new(false, gate.Reason, false, null, 0);
    }

    static (string Dest, string Payload) Split(string text)
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
