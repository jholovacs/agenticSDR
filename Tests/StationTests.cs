using System.Reflection;
using AgenticSdr.Dsp;
using AgenticSdr.Host;
using AgenticSdr.Station;

namespace AgenticSdr.Tests;

/// <summary>
/// The station desk may draft a message, and it may authorize one only after the operator click.
/// Privileges are the US Region 2 table: frequency, emission, and power.
/// </summary>
public class StationTests
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
        Assert.Equal("abc", click.DraftId);
    }

    [Fact]
    public void Agent_tools_cannot_transmit_or_sign_in()
    {
        string[] names = typeof(StationTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            [
                "describe_license",
                "draft_message",
                "draft_repeater_phone",
                "explain_privilege",
                "explain_repeater_privilege",
                "get_antenna",
                "get_control_operator",
                "guess_antenna",
                "list_authorized",
                "list_drafts",
                "list_heard",
                "lookup_grid",
                "note_heard",
                "repeater_catalog",
                "set_antenna"
            ],
            names);
    }

    [Theory]
    [InlineData(LicenseClass.Technician, StationMode.Cw, 7_050_000, 200, true)]
    [InlineData(LicenseClass.Technician, StationMode.Cw, 7_050_000, 201, false)]
    [InlineData(LicenseClass.Technician, StationMode.Usb, 7_200_000, 5, false)]
    [InlineData(LicenseClass.Technician, StationMode.Ft8, 7_074_000, 5, false)]
    [InlineData(LicenseClass.Technician, StationMode.Ft8, 28_074_000, 5, true)]
    [InlineData(LicenseClass.Technician, StationMode.Usb, 28_400_000, 5, true)]
    [InlineData(LicenseClass.Technician, StationMode.Am, 28_400_000, 5, false)]
    [InlineData(LicenseClass.Technician, StationMode.Lsb, 28_400_000, 5, false)]
    [InlineData(LicenseClass.Technician, StationMode.Ft8, 14_074_000, 5, false)]
    [InlineData(LicenseClass.Technician, StationMode.Nbfm, 146_520_000, 5, true)]
    [InlineData(LicenseClass.Technician, StationMode.Nbfm, 14_300_000, 5, false)]
    [InlineData(LicenseClass.Technician, StationMode.Nbfm, 223_500_000, 25, true)]
    [InlineData(LicenseClass.General, StationMode.Usb, 14_300_000, 100, true)]
    [InlineData(LicenseClass.General, StationMode.Usb, 14_200_000, 100, false)]
    [InlineData(LicenseClass.General, StationMode.Ft8, 14_074_000, 5, true)]
    [InlineData(LicenseClass.General, StationMode.Cw, 14_000_100, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Usb, 14_200_000, 100, true)]
    [InlineData(LicenseClass.Extra, StationMode.Cw, 14_000_000, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Cw, 14_000_100, 5, true)]
    [InlineData(LicenseClass.Extra, StationMode.Ft8, 14_074_000, 5, true)]
    [InlineData(LicenseClass.Extra, StationMode.Usb, 10_136_000, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Ft8, 10_136_000, 200, true)]
    [InlineData(LicenseClass.Extra, StationMode.Ft8, 10_136_000, 201, false)]
    [InlineData(LicenseClass.General, StationMode.Lsb, 7_175_000, 5, false)]
    [InlineData(LicenseClass.General, StationMode.Lsb, 7_178_000, 5, true)]
    [InlineData(LicenseClass.General, StationMode.Usb, 5_330_500, 50, true)]
    [InlineData(LicenseClass.General, StationMode.Usb, 5_330_500, 101, false)]
    [InlineData(LicenseClass.General, StationMode.Lsb, 5_330_500, 50, false)]
    [InlineData(LicenseClass.Technician, StationMode.Usb, 5_330_500, 50, false)]
    [InlineData(LicenseClass.Extra, StationMode.Cw, 5_332_000, 50, true)]
    [InlineData(LicenseClass.Extra, StationMode.Nbfm, 100_000_000, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Am, 27_185_000, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Nbfm, 162_550_000, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Nbfm, 462_562_500, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Am, 1_000_000, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Data, 219_500_000, 5, false)]
    [InlineData(LicenseClass.Extra, StationMode.Cw, 475_000, 5, false)]
    public void Privileges_follow_the_license(LicenseClass license, StationMode mode, double hz, double watts, bool allowed)
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
                PrivilegeDecision decision = Part97.Evaluate(LicenseClass.Extra, StationMode.Ft8, channel.FrequencyHz, 5);
                Assert.True(decision.Allowed, channel.Name + " " + decision.Reason);
            }
        }
    }

    [Fact]
    public void Authorize_requires_the_operator_the_call_sign_and_one_click()
    {
        var desk = new StationDesk();
        Assert.Throws<ArgumentException>(() => desk.Draft("CQ DE W1AW", 14_074_000, StationMode.Ft8, 5, DraftSource.Agent));

        desk.SignIn("w1aw", LicenseClass.Extra);
        DraftView missing = desk.Draft("CQ CQ", 14_074_000, StationMode.Ft8, 5, DraftSource.Agent);
        Assert.False(missing.Allowed);
        Assert.True(missing.WithinLicense);

        Assert.True(OperatorClick.TryFromButton(missing.Id, true, out OperatorClick early));
        AuthorizeResult blocked = desk.Authorize(missing.Id, early);
        Assert.False(blocked.Authorized);
        Assert.Contains("W1AW", blocked.Reason);
        desk.Discard(missing.Id);

        DraftView draft = desk.Draft("CQ CQ DE W1AW K", 14_074_000, StationMode.Ft8, 5, DraftSource.Agent);
        Assert.True(draft.Allowed);

        AuthorizedTransmission? raised = null;
        desk.TransmissionAuthorized += (_, transmission) => raised = transmission;

        Assert.False(OperatorClick.TryFromButton(draft.Id, false, out _));
        Assert.True(OperatorClick.TryFromButton("other", true, out OperatorClick wrong));
        Assert.False(desk.Authorize(draft.Id, wrong).Authorized);

        Assert.True(OperatorClick.TryFromButton(draft.Id, true, out OperatorClick click));
        AuthorizeResult sent = desk.Authorize(draft.Id, click);
        Assert.True(sent.Authorized);
        Assert.NotNull(sent.Transmission);
        Assert.False(sent.Transmission.Radiated);
        Assert.Equal("W1AW", sent.Transmission.CallSign);
        Assert.Same(sent.Transmission, raised);

        AuthorizeResult again = desk.Authorize(draft.Id, click);
        Assert.False(again.Authorized);

        desk.SignIn("W1AW", LicenseClass.Technician);
        DraftView phone = desk.Draft("CQ DE W1AW", 14_300_000, StationMode.Usb, 100, DraftSource.Operator);
        Assert.False(phone.WithinLicense);
        Assert.True(OperatorClick.TryFromButton(phone.Id, true, out OperatorClick phoneClick));
        Assert.False(desk.Authorize(phone.Id, phoneClick).Authorized);

        desk.SignOut();
        DraftView stillThere = Assert.Single(desk.Snapshot().Drafts);
        Assert.Equal(phone.Id, stillThere.Id);
        Assert.True(OperatorClick.TryFromButton(phone.Id, true, out OperatorClick signedOut));
        Assert.False(desk.Authorize(phone.Id, signedOut).Authorized);
    }

    [Fact]
    public void Authorized_cw_and_digimodes_render_local_send_audio()
    {
        var desk = new StationDesk();
        desk.SignIn("W1AW", LicenseClass.Extra, "FN42");

        DraftView cwDraft = desk.Draft("CQ DE W1AW K", 14_050_000, StationMode.Cw, 50, DraftSource.Operator);
        Assert.True(OperatorClick.TryFromButton(cwDraft.Id, true, out OperatorClick cwClick));
        AuthorizedTransmission cw = desk.Authorize(cwDraft.Id, cwClick).Transmission!;
        SendAudio cwAudio = StationSend.Render(cw);
        Assert.True(cwAudio.Wav.Length > 44);
        Assert.True(cwAudio.DurationSeconds > 0.5);
        Assert.Contains("CW", cwAudio.Summary);
        Assert.Contains("DE W1AW", MorseDecoder.DecodeCw(
            ReadWavSamples(cwAudio.Wav, out int cwRate), cwRate));

        DraftView ft8Draft = desk.Draft("CQ W1AW FN42", 14_074_000, StationMode.Ft8, 5, DraftSource.Operator);
        Assert.True(OperatorClick.TryFromButton(ft8Draft.Id, true, out OperatorClick ft8Click));
        SendAudio ft8 = StationSend.Render(desk.Authorize(ft8Draft.Id, ft8Click).Transmission!);
        Assert.InRange(ft8.DurationSeconds, 12, 16);

        DraftView packetDraft = desk.Draft("APRS>hello from W1AW", 144_390_000, StationMode.Packet, 5, DraftSource.Operator);
        Assert.True(OperatorClick.TryFromButton(packetDraft.Id, true, out OperatorClick packetClick));
        SendAudio packet = StationSend.Render(desk.Authorize(packetDraft.Id, packetClick).Transmission!);
        Assert.Contains("Packet", packet.Summary);
        Assert.False(StationSend.Supports(StationMode.Usb));
        Assert.False(StationSend.Supports(StationMode.Varac));
    }

    static float[] ReadWavSamples(byte[] wav, out int sampleRate)
    {
        Assert.True(WavFile.TryRead(wav, out float[] samples, out sampleRate));
        return samples;
    }

    [Fact]
    public void Changing_license_rechecks_a_waiting_draft()
    {
        var desk = new StationDesk();
        desk.SignIn("W1AW", LicenseClass.Extra);
        DraftView draft = desk.Draft("CQ DE W1AW", 14_074_000, StationMode.Ft8, 5, DraftSource.Agent);
        Assert.True(draft.Allowed);

        desk.SignIn("W1AW", LicenseClass.Technician);
        DraftView now = Assert.Single(desk.Snapshot().Drafts);
        Assert.False(now.Allowed);
        Assert.True(OperatorClick.TryFromButton(now.Id, true, out OperatorClick click));
        Assert.False(desk.Authorize(now.Id, click).Authorized);
    }

    [Fact]
    public void Heard_copy_is_not_an_authorization()
    {
        var desk = new StationDesk();
        desk.SignIn("KA1ABC", LicenseClass.General);
        HeardNote note = desk.NoteHeard("K1JT 73", 14_074_000, StationMode.Ft8, DraftSource.Agent);
        Assert.Equal("K1JT 73", note.Text);
        Assert.Empty(desk.Snapshot().Authorized);
    }

    [Fact]
    public void Grid_lookup_names_the_square_and_a_bad_square_is_refused()
    {
        GridPlace boston = StationGrid.Require("fn42");
        Assert.Equal("FN42", boston.Grid);
        Assert.Equal("Boston", boston.Place);
        Assert.Contains("N", boston.Coordinates);
        Assert.Contains("W", boston.Coordinates);
        Assert.Null(boston.Licensee);

        var desk = new StationDesk();
        desk.SignIn("W1AW", LicenseClass.Extra);
        Assert.Throws<ArgumentException>(() => desk.SignIn("W1AW", LicenseClass.Extra, "boston"));
        Assert.Null(desk.CopyOperator()!.Grid);
        Assert.Equal(LicenseClass.Extra, desk.CopyOperator()!.License);

        const string card = """
            {"hamdb":{"messages":{"status":"OK"},"callsign":{"call":"W1AW","fname":"ARRL","name":"HQ","addr2":"NEWINGTON","state":"CT","country":"United States","grid":"FN31pr"}}}
            """;
        GridPlace listed = StationGrid.FromHamDb("W1AW", card);
        Assert.Equal("FN31PR", listed.Grid);
        Assert.InRange(listed.Latitude, 40, 43);
        Assert.True(listed.Longitude < 0);
        Assert.Contains("W1AW", listed.Licensee);
        Assert.Equal("hamdb", listed.Source);
        Assert.Empty(desk.Snapshot().Authorized);
    }

    [Fact]
    public void Repeater_phone_checks_the_input_and_keeps_the_tones()
    {
        RepeaterAccess machine = StationRepeater.Parse(new RepeaterBody(
            146_940_000,
            null,
            "minus",
            null,
            "ctcss",
            100.0,
            null,
            "none",
            null,
            null,
            true,
            "W1AW/R",
            "Club machine",
            "Newington",
            "Open"));
        Assert.Equal(146_340_000, machine.InputFrequencyHz);
        Assert.Equal(600_000, machine.OffsetHz);
        Assert.Equal(RepeaterToneMode.Ctcss, machine.EncodeMode);
        Assert.Equal(100.0, machine.EncodeCtcssHz);
        Assert.Contains("100.0 Hz", machine.Summary);

        var desk = new StationDesk();
        desk.SignIn("W1AW", LicenseClass.Technician);
        DraftView draft = desk.Draft("This is W1AW monitoring", 0, StationMode.Nbfm, 25, DraftSource.Operator, machine);
        Assert.True(draft.Allowed);
        Assert.Equal(146_340_000, draft.FrequencyHz);
        Assert.NotNull(draft.Repeater);
        Assert.Equal("W1AW/R", draft.Repeater.RepeaterCallSign);

        RepeaterAccess hfPhone = StationRepeater.Parse(new RepeaterBody(
            14_300_000,
            null,
            "simplex",
            null,
            "none",
            null,
            null,
            "none",
            null,
            null,
            true,
            null,
            null,
            null,
            null));
        PrivilegeDecision denied = StationRepeater.Evaluate(LicenseClass.Technician, hfPhone, 5);
        Assert.False(denied.WithinLicense);
        ArgumentException blocked = Assert.Throws<ArgumentException>(() =>
            desk.Draft("This is W1AW monitoring", 0, StationMode.Nbfm, 5, DraftSource.Operator, hfPhone));
        Assert.Contains("Transmit (input) frequency", blocked.Message);
        Assert.DoesNotContain(desk.Snapshot().Drafts, item => item.FrequencyHz == 14_300_000);

        Assert.True(OperatorClick.TryFromButton(draft.Id, true, out OperatorClick click));
        AuthorizeResult sent = desk.Authorize(draft.Id, click);
        Assert.True(sent.Authorized);
        Assert.NotNull(sent.Transmission!.Repeater);
        Assert.False(sent.Transmission.Radiated);
    }

    [Fact]
    public void Repeater_book_saves_many_and_can_select_edit_and_delete()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agenticsdr-rep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "station-repeater.json");
            var memory = new RepeaterMemory(path);
            RepeaterAccess first = StationRepeater.Parse(new RepeaterBody(
                146_940_000, null, "minus", null, "ctcss", 100.0, null, "none", null, null, true, "W1AW/R", "Club", "Newington", null));
            RepeaterAccess second = StationRepeater.Parse(new RepeaterBody(
                146_670_000, null, "minus", null, "ctcss", 88.5, null, "none", null, null, true, "K1ABC/R", "North", "Hartford", null));

            RepeaterBook book = memory.Create(first, "Club machine", 25);
            Assert.Single(book.Items);
            Assert.Equal(book.Items[0].Id, book.SelectedId);
            Assert.Equal("Club machine", book.Items[0].Label);
            Assert.Equal(25, book.Items[0].PowerWatts);

            book = memory.Create(second, null, 10);
            Assert.Equal(2, book.Items.Count);
            Assert.Equal("North", book.Items.Single(item => item.Access.OutputFrequencyHz == 146_670_000).Label);

            string clubId = book.Items.Single(item => item.Label == "Club machine").Id;
            book = memory.Select(clubId);
            Assert.Equal(clubId, book.SelectedId);

            RepeaterAccess edited = StationRepeater.Parse(new RepeaterBody(
                146_940_000, null, "minus", null, "ctcss", 123.0, null, "none", null, null, true, "W1AW/R", "Club", "Newington", "Open"));
            book = memory.Update(clubId, edited, "Club · Newington", 50);
            Assert.Equal(123.0, book.Items.Single(item => item.Id == clubId).Access.EncodeCtcssHz);
            Assert.Equal("Club · Newington", book.Items.Single(item => item.Id == clubId).Label);

            book = memory.Delete(clubId);
            Assert.Single(book.Items);
            Assert.Equal("North", book.Items[0].Label);

            var again = new RepeaterMemory(path);
            RepeaterBook loaded = again.Book();
            Assert.Single(loaded.Items);
            Assert.Equal(146_670_000, loaded.Items[0].Access.OutputFrequencyHz);

            File.WriteAllText(path, """
                {
                  "outputFrequencyHz": 146520000,
                  "inputFrequencyHz": 146520000,
                  "shift": "Simplex",
                  "encodeMode": "None",
                  "decodeMode": "None",
                  "narrow": true
                }
                """);
            var migrated = new RepeaterMemory(path);
            RepeaterBook legacy = migrated.Book();
            Assert.Single(legacy.Items);
            Assert.Equal(146_520_000, legacy.Items[0].Access.OutputFrequencyHz);
            Assert.NotNull(legacy.SelectedId);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Remembered_operator_and_antenna_come_back_and_still_need_a_click()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agenticsdr-station-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string opPath = Path.Combine(dir, "station-operator.json");
            string antPath = Path.Combine(dir, "antenna.json");
            var desk = new StationDesk();
            desk.SignIn("W1AW", LicenseClass.General, "fn42");
            var memory = new StationMemory(opPath);
            memory.Save(desk.CopyOperator()!);

            var next = new StationDesk();
            Assert.True(new StationMemory(opPath).Restore(next));
            OperatorIdentity op = next.CopyOperator()!;
            Assert.Equal("W1AW", op.CallSign);
            Assert.Equal(LicenseClass.General, op.License);
            Assert.Equal("FN42", op.Grid);
            Assert.Equal("Boston", op.Place);
            Assert.Contains("N", op.Coordinates);

            DraftView draft = next.Draft("CQ DE W1AW", 14_074_000, StationMode.Ft8, 5, DraftSource.Operator);
            Assert.True(draft.Allowed);
            Assert.Empty(next.Snapshot().Authorized);
            Assert.False(OperatorClick.TryFromButton(draft.Id, false, out _));

            var antenna = new StationAntenna(antPath, receiverBase: null);
            AntennaAdvice advice = antenna.Set(0.5, "m", "vertical", null);
            Assert.True(advice.Configured);
            Assert.Contains(advice.Bands, band => band.BandId == "2m");
            Assert.Empty(next.Snapshot().Authorized);

            var again = new StationAntenna(antPath, receiverBase: null);
            AntennaAdvice loaded = again.Current();
            Assert.Equal(advice.LengthMeters, loaded.LengthMeters, 3);
            Assert.Equal("vertical", loaded.Orientation);
            Assert.Contains(loaded.Bands, band => band.BandId == "2m");

            memory.Clear();
            Assert.False(new StationMemory(opPath).Restore(new StationDesk()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
