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
public sealed class SdrTools(RadioSession session)
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

    [McpServerTool, Description("Cancel the scan or listen that is in progress.")]
    public Task<SessionStatus> stop(CancellationToken cancellationToken = default) =>
        Guard(() =>
        {
            session.Stop();
            return Task.FromResult(session.Status);
        });

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
