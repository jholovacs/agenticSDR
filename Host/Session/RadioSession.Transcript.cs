using System.Globalization;
using System.Numerics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using AgenticSdr.Dsp;
using AgenticSdr.Privilege;
using AgenticSdr.Shared;
using AgenticSdr.Processors;
using AgenticSdr.Radio;

namespace AgenticSdr.Host;

public sealed partial class RadioSession
{

    private void AppendMorse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = text.Trim();
        lock (_statusGate)
        {
            string shown = _transcript.Count > 0 && _transcript[^1].Kind == "morse" ? _transcript[^1].Text : "";
            string grown = TranscriptLog.Grow(shown, _morseWindow, text).Trim();
            _morseWindow = text;
            if (grown.Length == 0)
                return;
            if (_transcript.Count > 0 && _transcript[^1].Kind == "morse")
                _transcript[^1] = new TranscriptSpan("morse", grown, "Morse");
            else
                _transcript.Add(new TranscriptSpan("morse", grown, "Morse"));
            PublishTranscript();
        }
    }

    private void AppendSpeech(string text) => AppendSpan("speech", text, "Speech");

    private void AppendRds(string text) => AppendSpan("rds", text, "RDS");

    /// <summary>Split a WBFM clip's combined text into RDS lines vs speech.</summary>
    private void AppendWbfmDecoded(string decoded)
    {
        foreach (string raw in decoded.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("RDS ", StringComparison.Ordinal) || raw.StartsWith("Listening for RDS", StringComparison.Ordinal))
                AppendRds(raw);
            else if (raw.StartsWith("Speech was not read", StringComparison.Ordinal))
                AppendNote(raw);
            else
                AppendSpeech(raw);
        }
    }

    private void AppendFt8(string text) => AppendSpan("ft8", text);

    private void AppendDigi(RadioMode mode, string text)
    {
        HookCallsigns();
        callsigns.PrefetchFromText(text);
        // Scan-path Format() blocks still carry CQ/grid lines — plot Maidenhead centers.
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            NoteDigiLocation(mode, line);
        }
        AppendSpan(CampSchedule.Canonical(mode), callsigns.EnrichText(text));
    }

    /// <summary>
    /// Print digimode text as separate timed lines. A whole-slot Format() block used to look stuck
    /// when the next slot decoded the same CQ (identical text was dropped by AppendSpan) or when
    /// a slow FT8 search stalled the audio loop so later slots never arrived cleanly.
    /// </summary>
    private void AppendDigiLines(RadioMode mode, string formatted, ref bool headerShown)
    {
        if (string.IsNullOrWhiteSpace(formatted))
            return;

        HookCallsigns();
        callsigns.PrefetchFromText(formatted);
        string kind = CampSchedule.Canonical(mode);
        string stamp = DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string[] lines = formatted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? pending = null;
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            bool banner = line is "FT8" or "JS8" or "Olivia" or "JTTY" or "WSPR" or "VarAC"
                || line.StartsWith("FT8 ", StringComparison.Ordinal)
                || line.StartsWith("JS8 ", StringComparison.Ordinal)
                || line.StartsWith("Olivia ", StringComparison.Ordinal)
                || line.StartsWith("JTTY ", StringComparison.Ordinal)
                || line.StartsWith("WSPR ", StringComparison.Ordinal)
                || line.StartsWith("VarAC ", StringComparison.Ordinal);
            if (banner)
            {
                FlushDigiLine(kind, ref pending);
                if (headerShown)
                    continue;
                headerShown = true;
                AppendSpan(kind, line);
                continue;
            }

            // Explanation lines are indented. FT8/JS8/WSPR heads are also left-padded
            // ("  15 dB …") by the SNR format width — those are real decodes, not meanings.
            bool indented = raw.Length > 0 && char.IsWhiteSpace(raw[0]);
            if (indented && !LooksLikeDigiDecodeLine(line))
            {
                string meaning = callsigns.EnrichText(line.TrimStart());
                if (pending is null)
                    pending = $"{stamp}  {meaning}";
                else
                    pending += "\n           " + meaning;
                continue;
            }

            string decodeLine = line.TrimStart();
            FlushDigiLine(kind, ref pending);
            NoteDigiLocation(mode, decodeLine);
            pending = $"{stamp}  {callsigns.EnrichText(decodeLine)}";
        }

        FlushDigiLine(kind, ref pending);
    }

    /// <summary>SNR/Hz decode head from Format(), even when left-padded with spaces.</summary>
    private static bool LooksLikeDigiDecodeLine(string line)
    {
        string text = line.TrimStart();
        return text.Contains(" dB", StringComparison.Ordinal)
            && text.Contains(" Hz", StringComparison.Ordinal);
    }

    /// <summary>Plot Maidenhead grids (4 or 6 characters) at the square center — no lat/lon needed on the wire.</summary>
    private void NoteDigiLocation(RadioMode mode, string line)
    {
        if (!DigiLocation.TryParse(line, CampSchedule.Canonical(mode), out DigiLocationFix fix))
            return;

        string? place = Maidenhead.Describe(fix.Grid);
        string comment = place is null
            ? $"grid {fix.Grid} ({fix.Source})"
            : $"grid {fix.Grid} — {place} ({fix.Source})";

        UpsertWeatherStation(
            fix.Callsign,
            fix.LatitudeDegrees,
            fix.LongitudeDegrees,
            null, null, null, null, null, null, null, null, null,
            comment,
            line.Trim(),
            isWeather: false,
            source: fix.Source,
            grid: fix.Grid);
    }

    private void FlushDigiLine(string kind, ref string? pending)
    {
        if (pending is null)
            return;
        AppendSpan(kind, pending);
        pending = null;
    }

    private void HookCallsigns()
    {
        if (_callsignHooks || !callsigns.Enabled)
            return;
        _callsignHooks = true;
        callsigns.Resolved += OnCallsignResolved;
    }

    private void OnCallsignResolved(CallsignInfo info)
    {
        string? note = info.Note ?? info.Label;
        if (!info.Found || string.IsNullOrWhiteSpace(note))
            return;
        lock (_statusGate)
        {
            if (!_announcedCalls.Add(info.Call))
                return;
        }

        AppendNote($"{info.Call} — {note}");
    }

    private static string FormatDigi(RadioMode mode, float[] audio, int rate) => mode switch
    {
        RadioMode.Ft8 => Ft8Decoder.Format(Ft8Decoder.Decode(audio, rate)),
        RadioMode.Js8 => Js8Decoder.Format(Js8Decoder.Decode(audio, rate)),
        RadioMode.Olivia => OliviaDecoder.Format(OliviaDecoder.Decode(audio, rate)),
        RadioMode.Jtty => JttyDecoder.Format(JttyDecoder.Decode(audio, rate)),
        RadioMode.Wspr => WsprDecoder.Format(WsprDecoder.Decode(audio, rate)),
        RadioMode.Varac => XaraMonitor.FormatAudio(audio, rate),
        _ => ""
    };

    /// <summary>Message line from a Format() banner block (skips the header / explanation).</summary>
    private static string DigiMessageBody(string formatted)
    {
        foreach (string raw in formatted.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || (raw.Length > 0 && char.IsWhiteSpace(raw[0])))
                continue;
            if (line.StartsWith("Olivia ", StringComparison.Ordinal)
                || line.StartsWith("JTTY ", StringComparison.Ordinal)
                || line.StartsWith("WSPR ", StringComparison.Ordinal)
                || line.StartsWith("FT8 ", StringComparison.Ordinal)
                || line.StartsWith("JS8 ", StringComparison.Ordinal)
                || line.StartsWith("VarAC ", StringComparison.Ordinal)
                || line is "FT8" or "JS8" or "Olivia" or "JTTY" or "WSPR" or "VarAC")
                continue;
            return line;
        }

        return "";
    }

    /// <summary>Clip / rolling-buffer length. FT8 pads past 15 s; JS8 uses ~32 s so Slow and stacked Fast/Turbo slots fit.</summary>
    private static int DigiListenSeconds(RadioMode mode) => mode switch
    {
        RadioMode.Ft8 => 18,
        RadioMode.Js8 => 32,
        // 8/250 needs ~2 s per FEC block; keep several blocks so a CQ fits in one window.
        RadioMode.Olivia => 16,
        RadioMode.Jtty => 16,
        // Full ~110.6 s beacon plus pad inside the 120 s UTC slot.
        RadioMode.Wspr => 125,
        RadioMode.Varac => 20,
        _ => 15
    };

    /// <summary>Live digi chop / decode cadence — protocol period for FT8/JS8/WSPR; sliding step for Olivia/JTTY.</summary>
    private static int DigiPeriodSeconds(RadioMode mode) => mode switch
    {
        RadioMode.Ft8 => 15,
        // Normal JS8 is a 15 s slot (same wall clock as FT8). Fast/Turbo fit inside; Slow (~30 s)
        // still runs when the clip is long enough (clip listen keeps DigiListenSeconds at 32).
        RadioMode.Js8 => 15,
        RadioMode.Wspr => 120,
        // Overlapping steps on a longer buffer so mid-block CQs still decode.
        // Olivia Walsh hunt is ~1 s on a 16 s window; 6 s keeps the queue from stacking.
        RadioMode.Olivia => 6,
        RadioMode.Jtty => 4,
        _ => DigiListenSeconds(mode)
    };

    private void AppendPacket(string text)
    {
        NoteWeatherFromPacket(text);
        AppendSpan("packet", AprsExplain.Format(text), "Packet");
    }

    private void NoteWeatherFromPacket(string text)
    {
        // Remember every uncompressed position so Ultimeter (no lat/lon) can be placed later.
        if (AprsExplain.TryParsePositionFix(text, out AprsPositionFix fix))
        {
            _aprsPositions[fix.Callsign] = (fix.LatitudeDegrees, fix.LongitudeDegrees);
            // Plot digis / houses immediately so the map is live; weather frames upgrade the pin.
            if (!AprsExplain.TryParseWeatherReport(text, out _))
            {
                UpsertWeatherStation(
                    fix.Callsign,
                    fix.LatitudeDegrees,
                    fix.LongitudeDegrees,
                    null, null, null, null, null, null, null, null, null,
                    fix.IsWeatherSymbol ? "Weather station" : null,
                    fix.Raw,
                    isWeather: fix.IsWeatherSymbol,
                    source: fix.IsWeatherSymbol ? "weather" : "aprs");
            }
        }

        if (AprsExplain.TryParseWeatherReport(text, out AprsWeatherReport report))
        {
            UpsertWeatherStation(
                report.Callsign,
                report.LatitudeDegrees,
                report.LongitudeDegrees,
                report.WindDegrees,
                report.WindMph,
                report.GustMph,
                report.TemperatureF,
                report.HumidityPercent,
                report.RainLastHourIn,
                report.Rain24hIn,
                report.RainSinceMidnightIn,
                report.PressureMbar,
                report.Comment,
                report.Raw,
                isWeather: true,
                source: "weather");
            return;
        }

        // Status / message packets with a Maidenhead square but no lat/lon — plot the square center.
        if (AprsExplain.TryParseGridFix(text, out AprsGridFix gridFix))
        {
            string? place = Maidenhead.Describe(gridFix.Grid);
            string comment = place is null
                ? $"grid {gridFix.Grid}"
                : $"grid {gridFix.Grid} — {place}";
            UpsertWeatherStation(
                gridFix.Callsign,
                gridFix.LatitudeDegrees,
                gridFix.LongitudeDegrees,
                null, null, null, null, null, null, null, null, null,
                comment,
                gridFix.Raw,
                isWeather: false,
                source: "aprs",
                grid: gridFix.Grid);
        }

        // Peet Bros Ultimeter: weather without a position — reuse last heard fix for that call.
        if (!AprsExplain.TryParseUltimeterMetrics(text, out AprsUltimeterMetrics ultimeter))
            return;

        double lat;
        double lon;
        if (_weatherStations.TryGetValue(ultimeter.Callsign, out AprsWeatherStation? prior))
        {
            lat = prior.Latitude;
            lon = prior.Longitude;
        }
        else if (_aprsPositions.TryGetValue(ultimeter.Callsign, out var pos))
        {
            lat = pos.Lat;
            lon = pos.Lon;
        }
        else
            return;

        UpsertWeatherStation(
            ultimeter.Callsign,
            lat,
            lon,
            ultimeter.WindDegrees,
            ultimeter.WindMph,
            ultimeter.GustMph,
            ultimeter.TemperatureF,
            ultimeter.HumidityPercent,
            null,
            null,
            ultimeter.RainSinceMidnightIn,
            ultimeter.PressureMbar,
            "Ultimeter",
            ultimeter.Raw,
            isWeather: true,
            source: "weather");
    }

    private void UpsertWeatherStation(
        string callsign,
        double latitude,
        double longitude,
        int? windDegrees,
        int? windMph,
        int? gustMph,
        int? temperatureF,
        int? humidityPercent,
        double? rainLastHourIn,
        double? rain24hIn,
        double? rainSinceMidnightIn,
        double? pressureMbar,
        string? comment,
        string raw,
        bool isWeather,
        string source = "aprs",
        string? grid = null)
    {
        if (_weatherStations.TryGetValue(callsign, out AprsWeatherStation? existing))
        {
            // Weather pins beat digi/APRS-only pins; digi grids don't overwrite APRS fixes.
            if (!isWeather && existing.IsWeather)
                return;
            if (IsDigiSource(source) && !IsDigiSource(existing.Source))
                return;
            // Keep a prior Maidenhead label when a weather upgrade omits it.
            if (string.IsNullOrWhiteSpace(grid))
                grid = existing.Grid;
        }

        _weatherStations[callsign] = new AprsWeatherStation(
            callsign,
            latitude,
            longitude,
            windDegrees,
            windMph,
            gustMph,
            temperatureF,
            humidityPercent,
            rainLastHourIn,
            rain24hIn,
            rainSinceMidnightIn,
            pressureMbar,
            comment,
            DateTimeOffset.UtcNow,
            raw,
            isWeather,
            source,
            string.IsNullOrWhiteSpace(grid) ? null : grid.Trim().ToUpperInvariant());
        _aprsPositions[callsign] = (latitude, longitude);
    }

    private static bool IsDigiSource(string? source) =>
        source is not null
        && source is not "aprs" and not "weather";

    private IReadOnlyList<AprsWeatherStation> SnapshotWeatherStations()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - WeatherStationTtl;
        foreach (var pair in _weatherStations)
        {
            if (pair.Value.HeardAt < cutoff)
                _weatherStations.TryRemove(pair.Key, out _);
        }

        return _weatherStations.Values
            .OrderBy(s => s.Callsign, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void AppendRibbit(string text) => AppendSpan("ribbit", text, "Ribbit");

    private void AppendNote(string text) => AppendSpan("note", text, "receiver note");

    private void AppendSpan(string kind, string text, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = text.Trim();
        lock (_statusGate)
        {
            if (_transcript.Count > 0 && _transcript[^1].Kind == kind && _transcript[^1].Text == text)
                return;
            _transcript.Add(new TranscriptSpan(kind, text, source ?? KindSource(kind)));
            PublishTranscript();
        }
    }

    private static string KindSource(string kind) => kind switch
    {
        "morse" => "Morse",
        "speech" => "Speech",
        "ft8" => "FT8",
        "js8" => "JS8",
        "olivia" => "Olivia",
        "jtty" => "JTTY",
        "wspr" => "WSPR",
        "varac" => "VarAC",
        "packet" => "Packet",
        "ribbit" => "Ribbit",
        "rds" => "RDS",
        "note" => "receiver note",
        _ => kind
    };

    private void PublishTranscript()
    {
        const int maxChars = 12_000;
        int total = _transcript.Sum(span => span.Text.Length + 1);
        while (_transcript.Count > 1 && total > maxChars)
        {
            total -= _transcript[0].Text.Length + 1;
            _transcript.RemoveAt(0);
        }

        _liveText = _transcript.Count == 0 ? null : string.Join('\n', _transcript.Select(span => span.Text));
    }

    /// <summary>
    /// When the tune is a known channel or calling dial, briefly measure the spectrum and nudge the
    /// LO so a mistuned dongle still lands on the carrier / digi tone stack.
    /// </summary>
    private async Task<double> RefinePublishedTuneAsync(
        IRadioDevice device,
        double tuneHz,
        RadioMode mode,
        CancellationToken cancellationToken)
    {
        if (!Region2BandPlan.TryPublishedStation(tuneHz, mode, out double publishedHz, out RadioMode publishedMode))
            return tuneHz;

        RadioMode useMode = mode == RadioMode.Unknown ? publishedMode : mode;
        double issDoppler = Region2BandPlan.IssDopplerMaxHz(publishedHz);
        // Terrestrial APRS/packet is bursty. Snapping to the loudest nearby bin while the channel is
        // quiet parks several kHz off 144.390 and kills Bell 202 decode. Stay on the published dial;
        // per-radio ppm covers crystal error. ISS packet still hunts Doppler.
        if ((useMode is RadioMode.Packet or RadioMode.Ribbit) && issDoppler <= 0)
            return publishedHz;

        // FT8/JS8/WSPR are multi-tone windows. Snapping to the single loudest bin shifts the USB
        // filter and parks weaker stations outside the decoder — stay on the published dial.
        if (useMode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Wspr)
            return publishedHz;

        double offset = Math.Min(50_000, device.SampleRateHz * 0.2);
        double expectedRf = DialFrequency.ExpectedRfHz(publishedHz, useMode);
        double captureHz = Math.Clamp(
            expectedRf + offset,
            device.Capabilities.MinFrequencyHz,
            device.Capabilities.MaxFrequencyHz);
        Capture capture = await CaptureAsync(device, captureHz, 120, cancellationToken);
        int fft = capture.Samples.Length >= 65_536 ? 16_384 : 8_192;
        double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, fft);
        double snapped = DialFrequency.SnapPublishedTuneHz(
            db, capture.CenterHz, capture.SampleRate, publishedHz, useMode, dopplerMaxHz: issDoppler);
        if (Math.Abs(snapped - publishedHz) >= 1)
        {
            SetStatus(
                "listening",
                0.5,
                $"Published channel offset {snapped - publishedHz:+0;-0} Hz — retuning.");
        }

        return snapped;
    }

    /// <summary>
    /// Digimodes are weak multi-tone USB windows above published dials. Speech scan misses that comb, so each dial is probed on its own.
    /// </summary>
    private async Task ProbeDigiDialsAsync(
        IRadioDevice device,
        HamBand band,
        List<ScanHit> hits,
        CancellationToken cancellationToken)
    {
        foreach (CallingChannel channel in band.Calling)
        {
            if (channel.Mode is not (RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Olivia or RadioMode.Jtty or RadioMode.Wspr
                or RadioMode.Varac or RadioMode.Packet or RadioMode.Ribbit))
                continue;
            if (channel.FrequencyHz < device.Capabilities.MinFrequencyHz
                || channel.FrequencyHz > device.Capabilities.MaxFrequencyHz)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            string label = CampSchedule.Canonical(channel.Mode).ToUpperInvariant();
            SetStatus(
                "scanning",
                0.92,
                $"Checking {label} at {channel.FrequencyHz / 1e6:0.000} MHz");

            double offset = Math.Min(50_000, device.SampleRateHz * 0.2);
            double tune = Math.Clamp(
                channel.FrequencyHz + offset,
                device.Capabilities.MinFrequencyHz,
                device.Capabilities.MaxFrequencyHz);
            // Fine FFT so narrow digi windows are many bins.
            Capture capture = await CaptureAsync(device, tune, 350, cancellationToken);
            int fft = capture.Samples.Length >= 131_072 ? 65_536 : 16_384;
            double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, fft);
            Ft8Score score = DigiActivity.Score(
                channel.Mode,
                db,
                capture.CenterHz,
                capture.SampleRate,
                channel.FrequencyHz,
                channel.ToleranceHz);
            if (!score.Active)
                continue;

            int existing = hits.FindIndex(hit => NearDigiDial(hit.FrequencyHz, channel.Mode, channel.FrequencyHz, channel.ToleranceHz));
            double width = channel.Mode switch
            {
                RadioMode.Olivia => OliviaDecoder.BandwidthHz,
                RadioMode.Jtty => JttyDecoder.SpanHz,
                RadioMode.Wspr => WsprDecoder.BandwidthHz,
                RadioMode.Varac => XaraMonitor.BandwidthHz,
                RadioMode.Packet or RadioMode.Ribbit => 12_500,
                _ => 3_000
            };
            var hit = new ScanHit(
                channel.FrequencyHz,
                channel.Mode.ToString(),
                DigiActivity.ActivityConfidence(score),
                width,
                Math.Round(score.SnrDb, 1),
                channel.Name + " (activity)",
                band.Id);
            if (existing >= 0)
                hits[existing] = hit;
            else
                hits.Add(hit);
        }
    }

    private static bool NearDigiDial(double frequencyHz, RadioMode mode, double dialHz, double toleranceHz)
    {
        if (Region2BandPlan.TryDigiDial(frequencyHz, mode, out double matched) && Math.Abs(matched - dialHz) < 1)
            return true;
        if (mode is RadioMode.Ft8 or RadioMode.Js8 or RadioMode.Wspr or RadioMode.Varac)
            return frequencyHz >= dialHz - 500 && frequencyHz <= dialHz + toleranceHz;
        return Math.Abs(frequencyHz - dialHz) <= toleranceHz;
    }

    /// <summary>
    /// Auto mode: measure the spectrum first. A published digi dial (e.g. 7.090 JTTY) is only used when
    /// the peak still looks digi — AM/LSB voice on the same kHz must not snap to JTTY.
    /// </summary>
    private async Task<(RadioMode Mode, double TuneHz, double Confidence, bool DmrFramed)> ResolveAutoModeAsync(
        IRadioDevice device,
        double frequencyHz,
        CancellationToken cancellationToken)
    {
        bool nearDigi = Region2BandPlan.TryAnyDigiDial(frequencyHz, out RadioMode digiMode, out double digiDial);
        Measurement? measured = await MeasureAsync(device, frequencyHz, cancellationToken);
        if (measured is null)
        {
            if (nearDigi)
                return (digiMode, digiDial, 0.35, false);
            return (RadioMode.Nbfm, frequencyHz, 0.2, false);
        }

        BandHint hint = Region2BandPlan.HintAt(measured.Peak.FrequencyHz);
        Classification classification = ModeClassifier.Classify(measured.Peak, hint);
        RadioMode mode = classification.Mode == RadioMode.Unknown ? RadioMode.Nbfm : classification.Mode;
        double confidence = classification.Confidence;
        double tuneHz = ModeClassifier.CarrierFrequency(measured.Peak, mode);
        bool dmrFramed = HearsDmr(measured);
        if (dmrFramed)
            return (RadioMode.Dmr, tuneHz, 0.9, true);
        if (HearsDstar(measured))
            return (RadioMode.Dstar, tuneHz, 0.9, false);

        // Digi tones often look like USB/CW to the classifier. On a published digi dial,
        // prefer the dial mode unless the peak is clearly wide voice (AM / NBFM / WBFM).
        // Packet / Ribbit *are* NBFM-looking — still snap to the published FM-data dial.
        bool fmDataDial = digiMode is RadioMode.Packet or RadioMode.Ribbit;
        bool wideVoice = mode is RadioMode.Am or RadioMode.Wbfm
            || (mode is RadioMode.Nbfm && !fmDataDial);
        if (nearDigi && (fmDataDial || !wideVoice))
            return (digiMode, digiDial, Math.Max(confidence, 0.42), false);

        return (mode, tuneHz, confidence, false);
    }

    private async Task<Measurement?> MeasureAsync(IRadioDevice device, double frequencyHz, CancellationToken cancellationToken, double thresholdDb = 8)
    {
        double offset = Math.Min(50_000, device.SampleRateHz * 0.2);
        double tune = Math.Clamp(frequencyHz + offset, device.Capabilities.MinFrequencyHz, device.Capabilities.MaxFrequencyHz);
        Capture capture = await CaptureAsync(device, tune, 160, cancellationToken);
        double[] db = SpectrumAnalyzer.AveragePowerDb(capture.Samples, 8192);
        bool wide = Region2BandPlan.HintAt(frequencyHz).PreferredMode == RadioMode.Wbfm;
        List<SpectralPeak> peaks = PeakPicker.Find(
            db,
            capture.CenterHz,
            capture.SampleRate,
            thresholdDb: thresholdDb,
            maxBandwidthHz: wide ? 300_000 : 20_000,
            collapseImages: capture.RealValued,
            contourDropDb: wide ? 30 : 18,
            minBandwidthHz: wide ? 40_000 : 0);
        SpectralPeak? best = null;
        double bestDistance = double.MaxValue;
        foreach (SpectralPeak peak in peaks)
        {
            double distance = Math.Abs(peak.FrequencyHz - frequencyHz);
            if (distance < bestDistance)
            {
                best = peak;
                bestDistance = distance;
            }
        }

        if (best is null || bestDistance > 150_000)
            return null;
        SpectralPeak chosen = capture.DirectSampling ? best with { PossibleImage = true } : best;
        return new Measurement(chosen, capture.Samples, capture.CenterHz, capture.SampleRate);
    }

    private async Task<(List<double[]> Frames, bool DirectSampling, double CenterHz, double SampleRate)> CaptureSpectraAsync(
        IRadioDevice device,
        double frequencyHz,
        int dwellMs,
        Action<int, int> onSlice,
        CancellationToken cancellationToken)
    {
        int sliceMs = HopPlanner.SliceMilliseconds;
        int slices = Math.Max(1, dwellMs / sliceMs);
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        int unit = Math.Max(1, device.ReadAlignment / stride);
        int samples = Math.Max(unit, (int)(device.SampleRateHz * sliceMs / 1000.0));
        samples = Math.Max(unit, samples / unit * unit);
        var buffer = new byte[samples * stride];
        var frames = new List<double[]>(slices);
        bool direct = false;
        double centerHz = frequencyHz;
        double sampleRate = device.SampleRateHz;
        var chunk = new byte[Aligned(device, 262_144)];
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            direct = device.DirectSampling;
            centerHz = device.CenterFrequencyHz;
            sampleRate = device.SampleRateHz;
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            for (int slice = 0; slice < slices; slice++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onSlice(slice, slices);
                int got = 0;
                while (got + device.ReadAlignment <= buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int ask = Math.Min(chunk.Length, (buffer.Length - got) / device.ReadAlignment * device.ReadAlignment);
                    byte[] block = ask == chunk.Length ? chunk : new byte[ask];
                    int n = device.Read(block);
                    if (n <= 0)
                        throw new RadioException("The radio returned no samples.");
                    int copy = Math.Min(n, buffer.Length - got);
                    Buffer.BlockCopy(block, 0, buffer, got, copy);
                    got += copy;
                }

                (Complex[] iq, _) = ToIq(device, buffer);
                frames.Add(SpectrumAnalyzer.AveragePowerDb(iq, 4096));
            }
        }, cancellationToken);

        return (frames, direct, centerHz, sampleRate);
    }

    private async Task<Capture> CaptureAsync(IRadioDevice device, double frequencyHz, int milliseconds, CancellationToken cancellationToken)
    {
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        int unit = Math.Max(1, device.ReadAlignment / stride);
        int samples = Math.Max(unit, (int)(device.SampleRateHz * milliseconds / 1000.0));
        samples = Math.Max(unit, samples / unit * unit);
        var buffer = new byte[samples * stride];
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            int got = 0;
            var chunk = new byte[Aligned(device, 262_144)];
            while (got + device.ReadAlignment <= buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int ask = Math.Min(chunk.Length, (buffer.Length - got) / device.ReadAlignment * device.ReadAlignment);
                byte[] block = ask == chunk.Length ? chunk : new byte[ask];
                int n = device.Read(block);
                if (n <= 0)
                    throw new RadioException("The radio returned no samples.");
                int copy = Math.Min(n, buffer.Length - got);
                Buffer.BlockCopy(block, 0, buffer, got, copy);
                got += copy;
            }
        }, cancellationToken);

        (Complex[] iq, bool realValued) = ToIq(device, buffer);
        return new Capture(iq, device.CenterFrequencyHz, device.SampleRateHz, device.DirectSampling, realValued);
    }

    private async Task CaptureDemodAsync(
        IRadioDevice device,
        double frequencyHz,
        double seconds,
        Demodulator demod,
        RadioMode mode,
        CancellationToken cancellationToken)
    {
        long target = (long)(seconds * device.SampleRateHz);
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        var chunk = new byte[Aligned(device, 262_144)];
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            lock (_statusGate)
                _directSampling = device.DirectSampling;
            FocusTuner(device, mode);
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            // Align after junk so the first Costas symbols are not clipped by tuner settle.
            if (mode is RadioMode.Ft8)
                AlignDigiSlot(cancellationToken, periodSeconds: 15);
            else if (mode is RadioMode.Js8)
                AlignDigiSlot(cancellationToken, periodSeconds: 30);
            else if (mode is RadioMode.Wspr)
                AlignDigiSlot(cancellationToken, periodSeconds: 120);
            // One Hilbert stream for the whole listen — per-chunk FIR resets destroy FT8 Costas.
            Iq.DirectSamplingState? ds = device.DirectSampling ? new Iq.DirectSamplingState() : null;
            long got = 0;
            while (got < target)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = device.Read(chunk);
                if (n <= 0)
                    throw new RadioException("The radio returned no samples.");
                int usable = n / stride * stride;
                (Complex[] iq, _) = ToIq(device, chunk.AsSpan(0, usable), ds);
                demod.Push(iq);
                got += iq.Length;
                SetStatus("listening", 0.35 + 0.6 * Math.Min(1, got / (double)target), $"Receiving {frequencyHz / 1e6:0.000000} MHz");
            }
        }, cancellationToken);
    }

    private static int Aligned(IRadioDevice device, int bytes)
    {
        int align = device.ReadAlignment;
        if (bytes < align)
            bytes = align;
        return bytes / align * align;
    }

    private static (Complex[] Samples, bool RealValued) ToIq(
        IRadioDevice device,
        ReadOnlySpan<byte> bytes,
        Iq.DirectSamplingState? directSampling = null)
    {
        if (device.Encoding == SampleEncoding.Cu8)
            return Iq.Convert(bytes, device.DirectSampling, directSampling);

        int count = bytes.Length / 8;
        var iq = new Complex[count];
        for (int i = 0; i < count; i++)
        {
            float real = BitConverter.ToSingle(bytes.Slice(i * 8, 4));
            float imag = BitConverter.ToSingle(bytes.Slice(i * 8 + 4, 4));
            iq[i] = new Complex(real, imag);
        }

        return (iq, false);
    }

    /// <summary>
    /// Wait until just before the next UTC digimode boundary of <paramref name="periodSeconds"/>.
    /// <paramref name="earlySeconds"/> leaves room for tuner setup before the exact align.
    /// </summary>
    private async Task WaitForDigiSlot(CancellationToken cancellationToken, double periodSeconds, double earlySeconds = 0)
    {
        double into = DateTime.UtcNow.TimeOfDay.TotalSeconds % periodSeconds;
        double untilBoundary = into < 0.15 ? 0 : periodSeconds - into;
        double wait = Math.Max(0, untilBoundary - Math.Max(0, earlySeconds));
        if (wait <= 0.05)
            return;

        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double left = wait - (DateTime.UtcNow - started).TotalSeconds;
            if (left <= 0.05)
                break;
            SetStatus("listening", 0.05 + 0.25 * (1 - Math.Clamp(left / wait, 0, 1)), $"Waiting {Math.Ceiling(left):0} s for the next {periodSeconds:0} s digimode slot.");
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(20, left * 1000))), cancellationToken);
        }
    }

    /// <summary>
    /// Block until about 0.45 s before the UTC digimode boundary so capture starts slightly early.
    /// Starting after the boundary (old behavior) plus USB/tune latency often clipped the first Costas
    /// array and left Costas sync hits that never CRC — the 28.074 / 14.074 “no translate” failure mode.
    /// </summary>
    private static void AlignDigiSlot(CancellationToken cancellationToken, double periodSeconds)
    {
        double earlyAt = periodSeconds - 0.45;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double into = DateTime.UtcNow.TimeOfDay.TotalSeconds % periodSeconds;
            // Early pad before the boundary, or a few hundred ms after it (do not wait a whole slot).
            if (into >= earlyAt || into < 0.20)
                return;
            int sleepMs = (int)Math.Clamp((earlyAt - into) * 1000.0, 5, 40);
            Thread.Sleep(sleepMs);
        }
    }

    private static string ReadyMessage(RadioMode mode, string? decoded) => mode switch
    {
        RadioMode.Cw when string.IsNullOrWhiteSpace(decoded) => "CW clip ready.",
        RadioMode.Cw => $"CW clip ready: {decoded}",
        RadioMode.Ft8 when string.IsNullOrWhiteSpace(decoded) => "FT8 clip ready. No messages decoded.",
        RadioMode.Ft8 => $"FT8 clip ready. {decoded!.Count(ch => ch == '\n') + 1} message(s).",
        RadioMode.Js8 when string.IsNullOrWhiteSpace(decoded) => "JS8 clip ready. No messages decoded.",
        RadioMode.Js8 => $"JS8 clip ready. {decoded!.Count(ch => ch == '\n') + 1} message(s).",
        RadioMode.Olivia when string.IsNullOrWhiteSpace(decoded) => "Olivia clip ready. No text decoded.",
        RadioMode.Olivia => $"Olivia clip ready. {decoded!.Count(ch => ch == '\n') + 1} line(s).",
        RadioMode.Jtty when string.IsNullOrWhiteSpace(decoded) => "JTTY clip ready. No text decoded.",
        RadioMode.Jtty => $"JTTY clip ready. {decoded!.Count(ch => ch == '\n') + 1} line(s).",
        RadioMode.Wspr when string.IsNullOrWhiteSpace(decoded) => "WSPR clip ready. No beacon decoded.",
        RadioMode.Wspr => $"WSPR clip ready. {decoded!.Count(ch => ch == '\n') + 1} line(s).",
        RadioMode.Varac when string.IsNullOrWhiteSpace(decoded) => "VarAC clip ready. No text decoded (xara/zara monitor may be missing).",
        RadioMode.Varac => $"VarAC clip ready. {decoded!.Count(ch => ch == '\n') + 1} line(s).",
        RadioMode.Packet when string.IsNullOrWhiteSpace(decoded) => "Packet clip ready. No frames decoded.",
        RadioMode.Packet => $"Packet clip ready. {decoded!.Count(ch => ch == '\n') + 1} frame(s).",
        RadioMode.Ribbit when string.IsNullOrWhiteSpace(decoded) => "Ribbit clip ready. No OFDM comb detected.",
        RadioMode.Ribbit => $"Ribbit clip ready. {decoded}",
        RadioMode.Dstar when string.IsNullOrWhiteSpace(decoded) => "D-STAR clip ready. No radio header decoded.",
        RadioMode.Dstar => "D-STAR clip ready. Framing / callsign notes recorded.",
        RadioMode.Dmr => "DMR clip ready. Framing / signaling notes recorded.",
        _ when SpeechDecoder.IsVoice(mode) && !string.IsNullOrWhiteSpace(decoded) => $"Audio clip ready. {decoded}",
        _ => "Audio clip ready."
    };

    private async Task<string> DescribeSourceAsync(double frequencyHz, RadioMode mode, CancellationToken cancellationToken)
    {
        string label = Region2BandPlan.SourceLabel(frequencyHz);
        if (mode != RadioMode.Nbfm || !TryLocation(out double latitude, out double longitude))
            return label;

        try
        {
            string? call = await repeaters.TryFindAsync(frequencyHz, latitude, longitude, cancellationToken);
            return string.IsNullOrWhiteSpace(call) ? label : $"{call} ({label})";
        }
        catch (Exception ex)
        {
            log.LogInformation(ex, "Repeater lookup failed.");
            return label;
        }
    }

    private bool TryLocation(out double latitude, out double longitude)
    {
        // Desk “home” pin from UI prefs wins over config / operator grid.
        try
        {
            UiPrefs prefs = uiPrefs.Load();
            if (prefs.HomeLatitude is double homeLat && prefs.HomeLongitude is double homeLon)
            {
                latitude = homeLat;
                longitude = homeLon;
                return true;
            }

            if (Maidenhead.TryParse(prefs.HomeGrid, out latitude, out longitude))
                return true;
        }
        catch
        {
            // Ignore unreadable prefs.
        }

        if (options.Latitude is not null && options.Longitude is not null)
        {
            latitude = options.Latitude.Value;
            longitude = options.Longitude.Value;
            return true;
        }

        if (Maidenhead.TryParse(options.Grid, out latitude, out longitude))
            return true;

        // Fall back to the signed-in operator grid from the shared desk identity.
        try
        {
            string path = AgenticSdr.Shared.SharedPaths.OperatorJson();
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("grid", out System.Text.Json.JsonElement gridEl)
                    || doc.RootElement.TryGetProperty("Grid", out gridEl))
                {
                    string? grid = gridEl.GetString();
                    if (Maidenhead.TryParse(grid, out latitude, out longitude))
                        return true;
                }
            }
        }
        catch
        {
            // Ignore unreadable operator files.
        }

        latitude = 0;
        longitude = 0;
        return false;
    }

    private IRadioDevice RequireDevice() =>
        _device ?? throw new InvalidOperationException("Open the radio first.");

    private CancellationTokenSource Begin(CancellationToken external)
    {
        lock (_statusGate)
        {
            if (_cancel.IsCancellationRequested)
            {
                _cancel.Dispose();
                _cancel = new CancellationTokenSource();
            }

            return CancellationTokenSource.CreateLinkedTokenSource(external, _cancel.Token);
        }
    }

    private void SetStatus(string phase, double progress, string? message)
    {
        lock (_statusGate)
        {
            _phase = phase;
            _progress = progress;
            _message = message;
        }
    }

    /// <summary>One tuned block of I/Q, plus whether direct sampling and a possible mirror image apply.</summary>
    private sealed record Capture(Complex[] Samples, double CenterHz, double SampleRate, bool DirectSampling, bool RealValued);

    /// <summary>A measured peak together with the samples it was measured in, so a steady local carrier can be told from voice or data.</summary>
    private sealed record Measurement(SpectralPeak Peak, Complex[] Samples, double CenterHz, double SampleRate);
}
