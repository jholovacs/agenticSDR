using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Wspr;

public sealed record WsprTransmitRequest(
    string Callsign,
    string Grid4,
    int PowerDbm,
    double FrequencyHz,
    double PowerWatts,
    double AudioHz = 1_500);

public sealed record WsprTransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

/// <summary>Experimental WSPR Type-1 transmit gated by operator click, Part 97, and SDR TX capability.</summary>
public static class WsprTransmitter
{
    public static WsprTransmitResult TryTransmit(
        OperatorIdentity? op,
        OperatorClick? click,
        WsprTransmitRequest request,
        IRadioDevice? radio)
    {
        ArgumentNullException.ThrowIfNull(request);
        string body = $"{request.Callsign.Trim()} {request.Grid4.Trim()} {request.PowerDbm}";
        TransmitAuthorization gate = TransmitGate.Evaluate(
            op, click, AmateurMode.Wspr, request.FrequencyHz, request.PowerWatts, body, radio);

        float[] samples;
        try
        {
            samples = WsprDecoder.ModulateSlot(
                request.Callsign, request.Grid4, request.PowerDbm,
                WsprDecoder.SampleRate, request.AudioHz);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, false, null, 0);
        }

        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(ToIq(samples));
            return new(true, gate.Reason, true, samples, WsprDecoder.SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local baseband only; nothing radiated.", false, samples, WsprDecoder.SampleRate);

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
