using System.ComponentModel;
using AgenticSdr.Radio;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgenticSdr.Host;

/// <summary>
/// The tools an AI calls. They are the same actions as the buttons on the page, and they share <see cref="RadioSession"/>.
/// Names are snake_case because that is how the AI sees them: list_devices, set_antenna, scan_band, listen, stop.
/// </summary>
[McpServerToolType]
public sealed class SdrTools(RadioSession session, CampBook camp)
{
    [McpServerTool, Description("List USB SDRs currently visible. RTL2832U dongles and LimeSDR-USB use built-in drivers. Other radios appear when SoapySDR and that radio's module are installed.")]
    public async Task<IReadOnlyList<RadioDeviceInfo>> list_devices(CancellationToken cancellationToken = default) =>
        await Guard(() => Task.FromResult(RadioSession.ListDevices()));

    [McpServerTool, Description("Published USB SDR families and the SoapySDR module that opens each one. A radio can still be opened when its module is installed but its name is not in this list.")]
    public IReadOnlyList<SdrFamily> list_sdr_families() => SdrCatalog.Families;

    [McpServerTool, Description("Open a device by the index from list_devices. Index 0 is the first visible radio.")]
    public Task<SessionStatus> open_device(
        [Description("Device index from list_devices. Defaults to 0.")] int index = 0,
        CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            await session.OpenAsync(index, cancellationToken);
            return session.Status;
        });

    [McpServerTool, Description("Close the open SDR so another program can use it.")]
    public Task<SessionStatus> close_device(CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            session.Close();
            return Task.FromResult(session.Status);
        });

    [McpServerTool, Description("Tuning range, sample rate, gain, and whether HF uses direct sampling.")]
    public Task<RadioCapabilities> get_capabilities(CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(session.Capabilities()));

    [McpServerTool, Description("List amateur bands plus AM broadcast, FM broadcast, and NOAA weather. When an antenna is set, recommended bands come first.")]
    public Task<IReadOnlyList<BandListItem>> list_ham_bands(CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(session.ListBands()));

    [McpServerTool, Description("Set the antenna length and orientation. A vertical is treated as a quarter-wave, a horizontal wire as a half-wave aimed on a true compass bearing. The matching bands, including AM, FM, and NOAA when the antenna covers them, are marked recommended.")]
    public Task<AntennaAdvice> set_antenna(
        [Description("Approximate length. Use unit to say whether this number is meters or feet.")] double length,
        [Description("m or ft. Defaults to meters.")] string unit = "m",
        [Description("vertical or horizontal.")] string orientation = "vertical",
        [Description("For a horizontal wire, the direction one end points, in degrees true. 0 is north.")] double? azimuthDegrees = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(session.SetAntenna(length, unit, orientation, azimuthDegrees)));

    [McpServerTool, Description("Guess the antenna from where the noise floor is strongest, then mark the matching bands. Compass direction cannot be read this way. The typed antenna is left as it was when the floors are too even.")]
    public Task<AntennaAdvice> guess_antenna(CancellationToken cancellationToken = default) =>
        Guard(() => session.GuessAntennaAsync(cancellationToken));

    [McpServerTool, Description("The current antenna and the bands it matches.")]
    public AntennaAdvice get_antenna() => session.Antenna();

    [McpServerTool, Description("Scan one band for strong signals. Returns frequency, mode, bandwidth, SNR, and a source label. bandId examples: 2m, 70cm, 20m, 40m, fm-bc, am-bc, noaa, cb, gmrs, dmr.")]
    public Task<ScanReport> scan_band(
        [Description("Band id such as 2m, 70cm, 40m, fm-bc, am-bc, noaa, cb, gmrs, or dmr.")] string bandId,
        CancellationToken cancellationToken = default) =>
        Guard(() => session.ScanAsync(bandId, cancellationToken));

    [McpServerTool, Description("Retune to one frequency in Hz and measure mode, bandwidth, SNR, and the band-plan source.")]
    public Task<InspectReport> inspect_signal(
        [Description("Frequency in Hz, for example 146520000.")] double frequencyHz,
        CancellationToken cancellationToken = default) =>
        Guard(() => session.InspectAsync(frequencyHz, cancellationToken));

    [McpServerTool, Description("Demodulate a frequency for a fixed number of seconds and return a WAV clip. The web page instead plays the same demodulator live until Stop. mode is auto, nbfm, wbfm, am, usb, lsb, cw, ft8, or dmr. CW returns Morse text. FT8 waits for a 15-second slot and returns decoded messages. Voice modes return a speech transcript.")]
    public Task<ListenReport> listen(
        [Description("Frequency in Hz.")] double frequencyHz,
        [Description("auto, nbfm, wbfm, am, usb, lsb, cw, ft8, or dmr.")] string mode = "auto",
        [Description("Clip length in seconds, from 1 to 20. FT8 always uses one 15-second slot.")] double seconds = 10,
        CancellationToken cancellationToken = default) =>
        Guard(() => session.ListenAsync(frequencyHz, mode, seconds, cancellationToken));

    [McpServerTool, Description("Cancel the scan or listen that is in progress. In camp mode this pauses the schedule until camp mode is chosen again or camp_now is called.")]
    public Task<SessionStatus> stop(CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            if (camp.Mode == "camp")
                camp.Pause(DateTime.Now);
            session.Stop();
            return Task.FromResult(session.Status);
        });

    [McpServerTool, Description("Choose explore or camp. Explore scans and listens with the audio on. Camp tunes the schedule and keeps the audio off until the channel is active.")]
    public Task<CampView> set_operating_mode(
        [Description("explore or camp.")] string mode,
        CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            camp.SetMode(mode);
            return Task.FromResult(camp.View(DateTime.Now));
        });

    [McpServerTool, Description("The camp schedule, the current mode, and which entry would use the radio now.")]
    public CampView get_schedule() => camp.View(DateTime.Now);

    [McpServerTool, Description("Add a camp entry. Times are local HH:mm. Omit both to listen whenever no timed entry is on. Overlapping times are rejected. Only one open entry is allowed.")]
    public Task<CampEntry> add_schedule_entry(
        [Description("Frequency in Hz.")] double frequencyHz,
        [Description("nbfm, wbfm, am, usb, lsb, cw, ft8, dmr, or raw.")] string mode,
        [Description("Local start HH:mm. Omit with end for an open watch.")] string? start = null,
        [Description("Local end HH:mm. An earlier end runs past midnight.")] string? end = null,
        [Description("sun, mon, tue, wed, thu, fri, sat, weekdays, weekends, or empty for every day.")] string? days = null,
        [Description("Short name, optional.")] string? label = null,
        [Description("DMR color code 0-15, or omit for any.")] int? colorCode = null,
        [Description("DMR timeslot 1 or 2, or omit for any.")] int? slot = null,
        [Description("DMR talkgroup, or omit for any.")] int? talkgroup = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(camp.Add(new CampDraft
        {
            FrequencyHz = frequencyHz,
            Mode = mode,
            Start = start,
            End = end,
            Days = CampSchedule.ParseDays(days),
            Label = label,
            ColorCode = colorCode,
            Slot = slot,
            Talkgroup = talkgroup
        })));

    [McpServerTool, Description("Replace one schedule entry. Send the full entry. Overlaps are rejected.")]
    public Task<CampEntry> update_schedule_entry(
        [Description("Entry id from get_schedule.")] string id,
        [Description("Frequency in Hz.")] double frequencyHz,
        [Description("nbfm, wbfm, am, usb, lsb, cw, ft8, dmr, or raw.")] string mode,
        [Description("Local start HH:mm, or omit with end for an open watch.")] string? start = null,
        [Description("Local end HH:mm.")] string? end = null,
        [Description("Days, or empty for every day.")] string? days = null,
        [Description("Short name, optional.")] string? label = null,
        [Description("DMR color code 0-15, or omit for any.")] int? colorCode = null,
        [Description("DMR timeslot 1 or 2, or omit for any.")] int? slot = null,
        [Description("DMR talkgroup, or omit for any.")] int? talkgroup = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(camp.Update(id, new CampDraft
        {
            FrequencyHz = frequencyHz,
            Mode = mode,
            Start = start,
            End = end,
            Days = CampSchedule.ParseDays(days),
            Label = label,
            ColorCode = colorCode,
            Slot = slot,
            Talkgroup = talkgroup
        })));

    [McpServerTool, Description("Remove one schedule entry by id.")]
    public Task<CampView> remove_schedule_entry(
        [Description("Entry id from get_schedule.")] string id,
        CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            camp.Remove(id);
            return Task.FromResult(camp.View(DateTime.Now));
        });

    [McpServerTool, Description("Camp on this frequency and mode right away. The schedule is optional. Audio stays off until the channel is active.")]
    public Task<CampView> camp_now(
        [Description("Frequency in Hz.")] double frequencyHz,
        [Description("nbfm, wbfm, am, usb, lsb, cw, ft8, dmr, or raw.")] string mode,
        [Description("Short name, optional.")] string? label = null,
        [Description("DMR color code 0-15, or omit for any.")] int? colorCode = null,
        [Description("DMR timeslot 1 or 2, or omit for any.")] int? slot = null,
        [Description("DMR talkgroup, or omit for any.")] int? talkgroup = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(camp.CampNow(new CampDraft
        {
            FrequencyHz = frequencyHz,
            Mode = mode,
            Label = label,
            ColorCode = colorCode,
            Slot = slot,
            Talkgroup = talkgroup
        }, DateTime.Now)));

    [McpServerTool, Description("Clear the frequency chosen with camp_now. Timed schedule entries stay.")]
    public CampView clear_camp_now()
    {
        camp.ClearNow();
        return camp.View(DateTime.Now);
    }

    [McpServerTool, Description("While camping on an explicit frequency: yieldToSchedule true means a timed schedule entry takes the radio; false means stay on that frequency.")]
    public CampView set_schedule_policy(
        [Description("True to let the schedule take over. False to keep the explicit camp.")] bool yieldToSchedule)
    {
        camp.SetYieldToSchedule(yieldToSchedule);
        return camp.View(DateTime.Now);
    }

    [McpServerTool, Description("While camping on an explicit frequency, switch to the timed schedule entry that is due right now.")]
    public Task<CampView> follow_schedule(CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(camp.FollowSchedule(DateTime.Now)));

    [McpServerTool, Description("While camping on an explicit frequency, keep that frequency through the timed schedule entry that is due right now.")]
    public Task<CampView> ignore_schedule(CancellationToken cancellationToken = default) =>
        Guard(() => Task.FromResult(camp.IgnoreSchedule(DateTime.Now)));

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw new McpException("The radio operation was stopped.");
        }
        catch (Exception ex) when (ex is RadioException or ArgumentException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }
}
