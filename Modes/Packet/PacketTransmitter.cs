using System.Globalization;
using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Radio;

namespace AgenticSdr.Packet;

public sealed record PacketTransmitRequest(string Text, double FrequencyHz, double PowerWatts);

public sealed record PacketTransmitResult(bool Ok, string Reason, bool Radiated, float[]? Samples, int SampleRate);

public static class PacketTransmitter
{
    public const int SampleRate = PacketDecoder.TxAudioRate;

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

        float[] audio;
        float[] iq;
        try
        {
            AprsPath path = ParseAprs(request.Text, op.BaseCall);
            audio = PacketDecoder.SynthesizeUiAudio(
                path.SourceCall,
                path.SourceSsid,
                path.DestCall,
                path.DestSsid,
                path.Payload,
                sampleRate: SampleRate,
                preambleFlags: PacketDecoder.TxPreambleFlags,
                postambleFlags: PacketDecoder.TxPostambleFlags,
                path.Digis);

            int dacRate = radio is not null
                ? (int)Math.Round(radio.SampleRateHz)
                : SampleRate;
            if (dacRate <= 0)
                dacRate = SampleRate;

            System.Numerics.Complex[] baseband = PacketDecoder.ModulateFmIq(audio, SampleRate);
            if (dacRate != SampleRate)
                baseband = PacketDecoder.UpsampleIq(baseband, SampleRate, dacRate);
            iq = PacketDecoder.InterleaveIq(baseband);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, false, null, 0);
        }

        if (gate.MayRadiate && radio is IRadioTransmitter tx)
        {
            tx.WriteTransmit(iq);
            return new(true, gate.Reason, true, audio, SampleRate);
        }

        if (gate.Block is TransmitBlock.RadioRxOnly or TransmitBlock.NoRadio)
            return new(true, gate.Reason + " Local baseband only; nothing radiated.", false, audio, SampleRate);

        return new(false, gate.Reason, false, null, 0);
    }

    /// <summary>
    /// Accepts <c>DEST,DIGI1,DIGI2:payload</c>, <c>SRC&gt;DEST,DIGI…:payload</c>, or bare payload (dest CQ).
    /// </summary>
    internal static AprsPath ParseAprs(string text, string defaultSource)
    {
        string body = (text ?? "").Trim();
        string sourceCall = defaultSource;
        int sourceSsid = 0;

        int gt = body.IndexOf('>');
        if (gt > 0 && gt < body.Length - 1)
        {
            ParseCallSsid(body[..gt].Trim(), out sourceCall, out sourceSsid);
            body = body[(gt + 1)..].Trim();
        }

        string destPart;
        string payload;
        int colon = body.IndexOf(':');
        if (colon >= 0)
        {
            destPart = body[..colon].Trim();
            payload = body[(colon + 1)..];
        }
        else if (body.Contains(','))
        {
            // Path without colon — treat whole string as payload to CQ.
            destPart = "CQ";
            payload = body;
        }
        else
        {
            destPart = "CQ";
            payload = body;
        }

        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Packet text needs an information field.");

        string[] hops = destPart.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hops.Length == 0)
            hops = ["CQ"];

        ParseCallSsid(hops[0], out string destCall, out int destSsid);
        var digis = new List<(string Call, int Ssid, bool Repeated)>();
        for (int i = 1; i < hops.Length; i++)
        {
            string hop = hops[i];
            bool repeated = hop.EndsWith('*');
            if (repeated)
                hop = hop[..^1];
            ParseCallSsid(hop, out string call, out int ssid);
            digis.Add((call, ssid, repeated));
        }

        return new AprsPath(sourceCall, sourceSsid, destCall, destSsid, digis.ToArray(), payload);
    }

    static void ParseCallSsid(string token, out string call, out int ssid)
    {
        string t = token.Trim().ToUpperInvariant();
        int dash = t.LastIndexOf('-');
        if (dash > 0
            && dash < t.Length - 1
            && int.TryParse(t[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            && n is >= 0 and <= 15)
        {
            call = t[..dash];
            ssid = n;
            return;
        }

        call = t;
        ssid = 0;
    }

    internal sealed record AprsPath(
        string SourceCall,
        int SourceSsid,
        string DestCall,
        int DestSsid,
        (string Call, int Ssid, bool Repeated)[] Digis,
        string Payload);
}
