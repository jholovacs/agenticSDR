using AgenticSdr.Dsp;
using AgenticSdr.Host;
using AgenticSdr.Privilege;
using AgenticSdr.Shared;
using AgenticSdr.Radio;

namespace AgenticSdr.Tests;

public class PrivilegeTests
{
    [Theory]
    [InlineData("W1AW", "W1AW", "W1AW")]
    [InlineData("ka1abc", "KA1ABC", "KA1ABC")]
    [InlineData("W1AW/7", "W1AW/7", "W1AW")]
    [InlineData("KH6/W1AW", "KH6/W1AW", "W1AW")]
    [InlineData("W1AW/KH6", "W1AW/KH6", "W1AW")]
    [InlineData("N7ABC/MM", "N7ABC/MM", "N7ABC")]
    public void Call_signs_normalize(string text, string display, string baseCall)
    {
        Assert.True(CallSign.TryNormalize(text, out string shown, out string found));
        Assert.Equal(display, shown);
        Assert.Equal(baseCall, found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("K1")]
    [InlineData("Q1ABC")]
    [InlineData("CB19")]
    public void Bad_call_signs_are_refused(string text)
    {
        Assert.False(CallSign.TryNormalize(text, out _, out _));
    }

    [Fact]
    public void Identification_matches_the_base_call_only()
    {
        Assert.True(CallSign.IsIdentified("CQ CQ DE W1AW K", "W1AW"));
        Assert.True(CallSign.IsIdentified("de w1aw/7", "W1AW"));
        Assert.False(CallSign.IsIdentified("DE W1AWA", "W1AW"));
        Assert.False(CallSign.IsIdentified("DE KA1ABC", "W1AW"));
    }

    [Fact]
    public void Button_flag_is_required_to_build_a_click()
    {
        Assert.False(OperatorClick.TryFromButton("abc", false, out _));
        Assert.False(OperatorClick.TryFromButton("  ", true, out _));
        Assert.True(OperatorClick.TryFromButton(" abc ", true, out OperatorClick click));
        Assert.Equal("abc", click.RequestId);
    }

    [Theory]
    [InlineData(LicenseClass.Technician, AmateurMode.Cw, 7_050_000, 200, true)]
    [InlineData(LicenseClass.Technician, AmateurMode.Cw, 7_050_000, 201, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Usb, 7_200_000, 5, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Ft8, 7_074_000, 5, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Ft8, 28_074_000, 5, true)]
    [InlineData(LicenseClass.Technician, AmateurMode.Usb, 28_400_000, 5, true)]
    [InlineData(LicenseClass.Technician, AmateurMode.Am, 28_400_000, 5, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Lsb, 28_400_000, 5, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Ft8, 14_074_000, 5, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Nbfm, 146_520_000, 5, true)]
    [InlineData(LicenseClass.Technician, AmateurMode.Nbfm, 14_300_000, 5, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Nbfm, 223_500_000, 25, true)]
    [InlineData(LicenseClass.General, AmateurMode.Usb, 14_300_000, 100, true)]
    [InlineData(LicenseClass.General, AmateurMode.Usb, 14_200_000, 100, false)]
    [InlineData(LicenseClass.General, AmateurMode.Ft8, 14_074_000, 5, true)]
    [InlineData(LicenseClass.General, AmateurMode.Cw, 14_000_100, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Usb, 14_200_000, 100, true)]
    [InlineData(LicenseClass.Extra, AmateurMode.Cw, 14_000_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Cw, 14_000_100, 5, true)]
    [InlineData(LicenseClass.Extra, AmateurMode.Ft8, 14_074_000, 5, true)]
    [InlineData(LicenseClass.Extra, AmateurMode.Usb, 10_136_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Ft8, 10_136_000, 200, true)]
    [InlineData(LicenseClass.Extra, AmateurMode.Ft8, 10_136_000, 201, false)]
    [InlineData(LicenseClass.General, AmateurMode.Lsb, 7_175_000, 5, false)]
    [InlineData(LicenseClass.General, AmateurMode.Lsb, 7_178_000, 5, true)]
    [InlineData(LicenseClass.General, AmateurMode.Usb, 5_330_500, 50, true)]
    [InlineData(LicenseClass.General, AmateurMode.Usb, 5_330_500, 101, false)]
    [InlineData(LicenseClass.General, AmateurMode.Lsb, 5_330_500, 50, false)]
    [InlineData(LicenseClass.Technician, AmateurMode.Usb, 5_330_500, 50, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Cw, 5_332_000, 50, true)]
    [InlineData(LicenseClass.Extra, AmateurMode.Nbfm, 100_000_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Am, 27_185_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Nbfm, 162_550_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Nbfm, 156_800_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Usb, 2_182_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Nbfm, 462_562_500, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Am, 1_000_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Data, 219_500_000, 5, false)]
    [InlineData(LicenseClass.Extra, AmateurMode.Cw, 475_000, 5, false)]
    public void Privileges_follow_the_license(LicenseClass license, AmateurMode mode, double hz, double watts, bool allowed)
    {
        PrivilegeDecision decision = Part97.Evaluate(license, mode, hz, watts);
        Assert.Equal(allowed, decision.Allowed);
        Assert.Equal(allowed, decision.WithinLicense);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Extra_class_may_use_the_published_ft8_dials_at_low_power()
    {
        foreach (HamBand band in Region2BandPlan.Bands)
        {
            foreach (CallingChannel channel in band.Calling)
            {
                if (channel.Mode != RadioMode.Ft8)
                    continue;
                PrivilegeDecision decision = Part97.Evaluate(LicenseClass.Extra, AmateurMode.Ft8, channel.FrequencyHz, 5);
                Assert.True(decision.Allowed, channel.Name + " " + decision.Reason);
            }
        }
    }

    [Fact]
    public void Reserved_segments_refuse_the_wrong_emission()
    {
        Assert.False(Part97.Evaluate(LicenseClass.Extra, AmateurMode.Ft8, 50_050_000, 5).WithinLicense);
        Assert.Contains("reserved", Part97.Evaluate(LicenseClass.Extra, AmateurMode.Ft8, 50_050_000, 5).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(Part97.Evaluate(LicenseClass.Extra, AmateurMode.Usb, 50_050_000, 5).WithinLicense);
        Assert.True(Part97.Evaluate(LicenseClass.Extra, AmateurMode.Cw, 50_050_000, 5).WithinLicense);

        Assert.False(Part97.Evaluate(LicenseClass.Extra, AmateurMode.Usb, 10_120_000, 5).WithinLicense);
        Assert.Contains("reserved", Part97.Evaluate(LicenseClass.Extra, AmateurMode.Usb, 10_120_000, 5).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(Part97.Evaluate(LicenseClass.Extra, AmateurMode.Ft8, 10_120_000, 5).WithinLicense);
    }

    [Fact]
    public void Transmit_requires_click_license_and_tx_radio()
    {
        var op = new OperatorIdentity("W1AW", "W1AW", LicenseClass.Extra);
        Assert.True(OperatorClick.TryFromButton("req-1", true, out OperatorClick click));

        TransmitAuthorization noClick = TransmitGate.Evaluate(
            op, null, AmateurMode.Cw, 14_050_000, 50, "CQ DE W1AW K", radio: null);
        Assert.Equal(TransmitBlock.NoClick, noClick.Block);
        Assert.False(noClick.MayRadiate);

        TransmitAuthorization noId = TransmitGate.Evaluate(
            op, click, AmateurMode.Cw, 14_050_000, 50, "CQ CQ", radio: null);
        Assert.Equal(TransmitBlock.Privilege, noId.Block);

        TransmitAuthorization noRadio = TransmitGate.Evaluate(
            op, click, AmateurMode.Cw, 14_050_000, 50, "CQ DE W1AW K", radio: null);
        Assert.Equal(TransmitBlock.NoRadio, noRadio.Block);
        Assert.True(noRadio.Privilege!.Allowed);

        var rxOnly = new StubRadio(canTransmit: false);
        TransmitAuthorization rx = TransmitGate.Evaluate(
            op, click, AmateurMode.Cw, 14_050_000, 50, "CQ DE W1AW K", rxOnly);
        Assert.Equal(TransmitBlock.RadioRxOnly, rx.Block);

        var txRadio = new StubRadio(canTransmit: true);
        TransmitAuthorization ok = TransmitGate.Evaluate(
            op, click, AmateurMode.Cw, 14_050_000, 50, "CQ DE W1AW K", txRadio);
        Assert.Equal(TransmitBlock.None, ok.Block);
        Assert.True(ok.MayRadiate);
    }

    [Fact]
    public void Ui_prefs_merge_across_patches()
    {
        string path = Path.Combine(Path.GetTempPath(), "agenticsdr-ui-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UiPrefsStore(path);
            store.Patch(new UiPrefsPatch(Folds: new Dictionary<string, bool> { ["radio"] = true }, Volume: 70));
            store.Patch(new UiPrefsPatch(Folds: new Dictionary<string, bool> { ["prompt"] = false }, DialMhz: 7.050));
            UiPrefs prefs = store.Load();
            Assert.True(prefs.Folds!["radio"]);
            Assert.False(prefs.Folds["prompt"]);
            Assert.Equal(70, prefs.Volume);
            Assert.Equal(7.050, prefs.DialMhz);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Operator_session_without_path_stays_in_memory()
    {
        string shared = SharedPaths.OperatorJson();
        string? backup = File.Exists(shared) ? File.ReadAllText(shared) : null;
        try
        {
            if (File.Exists(shared))
                File.Delete(shared);

            var session = new OperatorSession();
            session.SignIn("W1AW", LicenseClass.Extra, "FN42");
            Assert.False(File.Exists(shared));
            Assert.Equal("W1AW", session.Current!.CallSign);
        }
        finally
        {
            if (backup is null)
            {
                if (File.Exists(shared))
                    File.Delete(shared);
            }
            else
                File.WriteAllText(shared, backup);
        }
    }

    [Fact]
    public void Operator_session_restores_camel_case_and_round_trips()
    {
        string path = Path.Combine(Path.GetTempPath(), "agenticsdr-op-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"callSign":"KJ5SKJ","license":"Technician","grid":"dm62"}
                """);

            var fromFile = new OperatorSession(path);
            Assert.True(fromFile.Restore());
            Assert.Equal("KJ5SKJ", fromFile.Current!.CallSign);
            Assert.Equal(LicenseClass.Technician, fromFile.Current.License);
            Assert.Equal("DM62", fromFile.Current.Grid);

            fromFile.SignIn("KJ5SKJ", LicenseClass.Technician, "DM62");
            var again = new OperatorSession(path);
            Assert.True(again.Restore());
            Assert.Equal("KJ5SKJ", again.Current!.CallSign);
            Assert.Equal("DM62", again.Current.Grid);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Operator_session_prefers_newer_legacy_station_file()
    {
        string shared = SharedPaths.OperatorJson();
        string legacy = Path.Combine(SharedPaths.UserConfigDirectory(), "station-operator.json");
        string? sharedBackup = File.Exists(shared) ? File.ReadAllText(shared) : null;
        string? legacyBackup = File.Exists(legacy) ? File.ReadAllText(legacy) : null;
        try
        {
            Directory.CreateDirectory(SharedPaths.UserConfigDirectory());
            File.WriteAllText(shared, """
                {"CallSign":"W1AW","License":"Extra","Grid":null}
                """);
            File.SetLastWriteTimeUtc(shared, DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllText(legacy, """
                {"callSign":"KJ5SKJ","license":"Technician","grid":"DM62"}
                """);
            File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow);

            var session = new OperatorSession(shared);
            Assert.True(session.Restore());
            Assert.Equal("KJ5SKJ", session.Current!.CallSign);
            Assert.Equal(LicenseClass.Technician, session.Current.License);
            Assert.Equal("DM62", session.Current.Grid);
        }
        finally
        {
            RestoreBackup(shared, sharedBackup);
            RestoreBackup(legacy, legacyBackup);
        }
    }

    static void RestoreBackup(string path, string? backup)
    {
        if (backup is null)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        else
            File.WriteAllText(path, backup);
    }

    sealed class StubRadio : IRadioDevice, IRadioTransmitter
    {
        readonly bool _canTransmit;

        public StubRadio(bool canTransmit) => _canTransmit = canTransmit;

        public RadioCapabilities Capabilities => new(
            "stub", null, 1e6, 30e6, 48_000, null, 0, false, _canTransmit);

        public bool DirectSampling => false;
        public double CenterFrequencyHz => 14_050_000;
        public double SampleRateHz => 48_000;
        public SampleEncoding Encoding => SampleEncoding.Cf32;
        public bool CanTransmit => _canTransmit;
        public int ReadAlignment => 8;
        public void Tune(double frequencyHz) { }
        public void SetGain(double? gainDb) { }
        public void SetFrequencyCorrection(int ppm) { }
        public int Read(byte[] buffer) => 0;
        public void Dispose() { }
        public void WriteTransmit(ReadOnlySpan<float> interleavedIq) { }
    }
}
