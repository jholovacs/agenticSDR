using AgenticSdr.Cw;
using AgenticSdr.Cw.Chat;
using AgenticSdr.Cw.Listen;
using AgenticSdr.Cw.Transmit;
using AgenticSdr.Privilege;
using AgenticSdr.Shared;
using AgenticSdr.Radio;

namespace AgenticSdr.Tests;

public class CwModeTests
{
    [Fact]
    public void Click_without_tx_radio_returns_local_preview_not_radiated()
    {
        var session = new OperatorSession();
        OperatorIdentity op = session.SignIn("W1AW", LicenseClass.Extra);
        Assert.True(OperatorClick.TryFromButton("cw-1", true, out OperatorClick click));

        CwTransmitResult result = CwTransmitter.TryTransmit(
            op,
            click,
            new CwTransmitRequest("CQ DE W1AW K", 14_050_000, 50),
            radio: null);

        Assert.True(result.Ok);
        Assert.False(result.Radiated);
        Assert.NotNull(result.Wav);
        Assert.True(result.Wav!.Length > 44);
    }

    [Fact]
    public void Agent_cannot_transmit_without_the_button()
    {
        var op = new OperatorIdentity("W1AW", "W1AW", LicenseClass.Extra);
        CwTransmitResult result = CwTransmitter.TryTransmit(
            op,
            click: null,
            new CwTransmitRequest("CQ DE W1AW K", 14_050_000, 50),
            radio: null);

        Assert.False(result.Ok);
        Assert.False(result.Radiated);
        Assert.Contains("click", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tx_radio_receives_iq_when_gate_passes()
    {
        var op = new OperatorIdentity("W1AW", "W1AW", LicenseClass.Extra);
        Assert.True(OperatorClick.TryFromButton("cw-2", true, out OperatorClick click));
        var radio = new TxStub();

        CwTransmitResult result = CwTransmitter.TryTransmit(
            op,
            click,
            new CwTransmitRequest("CQ DE W1AW K", 14_050_000, 50),
            radio);

        Assert.True(result.Ok);
        Assert.True(result.Radiated);
        Assert.True(radio.SampleCount > 0);
    }

    [Fact]
    public void Cw_jargon_explains_rx_and_tx_abbreviations()
    {
        CwJargon.CwExplanation rx = CwJargon.Explain("CQ DE W1AW K 73", "rx");
        Assert.Equal("rx", rx.Direction);
        Assert.Contains("general call", rx.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("best regards", rx.Summary, StringComparison.OrdinalIgnoreCase);

        CwJargon.CwExplanation tx = CwJargon.Explain("QSY QRT", "tx");
        Assert.Equal("tx", tx.Direction);
        Assert.Contains("Outbound", tx.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("change frequency", tx.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Simulated_transmit_passes_gate_but_does_not_radiate()
    {
        var op = new OperatorIdentity("W1AW", "W1AW", LicenseClass.Extra);
        Assert.True(OperatorClick.TryFromButton("cw-sim", true, out OperatorClick click));
        var rx = new RxStub();
        var simulated = new SimulatedTransmitRadio(rx);

        Assert.True(simulated.CanTransmit);
        Assert.False(simulated.Radiates);

        CwTransmitResult result = CwTransmitter.TryTransmit(
            op,
            click,
            new CwTransmitRequest("CQ DE W1AW K", 14_050_000, 50),
            simulated);

        Assert.True(result.Ok);
        Assert.False(result.Radiated);
        Assert.Contains("simulated", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cw_audio_is_capped_at_eighteen_wpm()
    {
        Assert.Equal(18, CwAudio.MaxWordsPerMinute);
        float[] samples = CwAudio.Modulate("CQ DE W1AW K");
        Assert.NotEmpty(samples);
    }

    [Fact]
    public void Chat_stores_tx_in_all_caps_and_grows_rx()
    {
        var chat = new CwChat();
        chat.AddTx("cq de w1aw k");
        Assert.Equal("CQ DE W1AW K", chat.Snapshot()[0].Text);
        Assert.Equal("tx", chat.Snapshot()[0].Direction);

        chat.UpdateRx("CQ");
        chat.UpdateRx("CQ DE");
        chat.UpdateRx("CQ DE W1AW");
        Assert.Equal("rx", chat.Snapshot()[^1].Direction);
        Assert.Equal("CQ DE W1AW", chat.Snapshot()[^1].Text);
    }

    [Fact]
    public void Morse_window_growth_keeps_scrolled_letters()
    {
        Assert.Equal("ABCDE", CwChat.Grow("ABC", "BC", "BCDE"));
        Assert.Equal("HI", CwChat.Grow("", "", "HI"));
    }

    [Fact]
    public void Id_tracker_appends_call_when_due_and_builds_signoff()
    {
        var ids = new CwIdTracker();
        var op = new OperatorIdentity("W1AW", "W1AW", LicenseClass.Extra);
        Assert.True(ids.IsDue);
        Assert.Equal("CQ DE W1AW", ids.PrepareOutgoing("CQ", op));
        ids.NoteTransmitted("CQ DE W1AW", op);
        Assert.True(ids.HasFreshId);
        Assert.Equal("RR", ids.PrepareOutgoing("RR", op));
        Assert.Equal("DE W1AW QSY", ids.BuildSignOff(op, "qsy"));
        Assert.Equal("DE W1AW QRT", ids.BuildSignOff(op, "QRT"));
    }

    sealed class TxStub : IRadioDevice, IRadioTransmitter
    {
        public int SampleCount { get; private set; }

        public RadioCapabilities Capabilities => new(
            "tx-stub", null, 1e6, 30e6, 8_000, null, 0, false, CanTransmit: true);

        public bool DirectSampling => false;
        public double CenterFrequencyHz => 14_050_000;
        public double SampleRateHz => 8_000;
        public SampleEncoding Encoding => SampleEncoding.Cf32;
        public bool CanTransmit => true;
        public int ReadAlignment => 8;
        public void Tune(double frequencyHz) { }
        public void SetGain(double? gainDb) { }
        public void SetFrequencyCorrection(int ppm) { }
        public int Read(byte[] buffer) => 0;
        public void Dispose() { }

        public void WriteTransmit(ReadOnlySpan<float> interleavedIq)
        {
            SampleCount = interleavedIq.Length;
        }
    }

    sealed class RxStub : IRadioDevice
    {
        public RadioCapabilities Capabilities => new(
            "rx-stub", null, 1e6, 30e6, 8_000, null, 0, false, CanTransmit: false);

        public bool DirectSampling => false;
        public double CenterFrequencyHz => 14_050_000;
        public double SampleRateHz => 8_000;
        public SampleEncoding Encoding => SampleEncoding.Cf32;
        public bool CanTransmit => false;
        public int ReadAlignment => 8;
        public void Tune(double frequencyHz) { }
        public void SetGain(double? gainDb) { }
        public void SetFrequencyCorrection(int ppm) { }
        public int Read(byte[] buffer) => 0;
        public void Dispose() { }
    }
}
