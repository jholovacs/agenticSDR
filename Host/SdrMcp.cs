using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgenticSdr.Host;

/// <summary>
/// Audio and decoded text from a listen, addressed so an AI can read them after <c>listen</c> returns.
/// audio://clips/{id} is the WAV. text://decodes/{id} is Morse or FT8 text.
/// </summary>
[McpServerResourceType]
public sealed class SdrResources(ClipStore clips)
{
    [McpServerResource(UriTemplate = "audio://clips/{id}", Name = "audio-clip", MimeType = "audio/wav")]
    [Description("16-bit mono WAV for a listen clip.")]
    public BlobResourceContents ReadAudio(string id)
    {
        if (!clips.TryGet(id, out AudioClip? clip) || clip is null)
            throw new McpException($"No audio clip '{id}'.");
        return BlobResourceContents.FromBytes(clip.Wav, $"audio://clips/{id}", "audio/wav");
    }

    [McpServerResource(UriTemplate = "text://decodes/{id}", Name = "morse-decode", MimeType = "text/plain")]
    [Description("Decoded text for a listen clip. Morse when the mode was CW; FT8/JS8/Olivia/JTTY/VarAC/packet text when those modes were used; empty otherwise.")]
    public string ReadDecode(string id)
    {
        if (!clips.TryGet(id, out AudioClip? clip) || clip is null)
            throw new McpException($"No decode '{id}'.");
        return clip.Morse ?? "";
    }
}

/// <summary>
/// The short operating note given to an AI: open the radio, set the antenna, scan a recommended band, then listen.
/// </summary>
[McpServerPromptType]
public sealed class SdrPrompts
{
    [McpServerPrompt(Name = "scan-and-listen")]
    [Description("How to scan an amateur band and listen to one signal with this radio.")]
    public static string Guide() =>
        """
        You are operating a receive-only USB SDR. RTL2832U dongles such as the NESDR SMArt v5 use the built-in driver. LimeSDR-USB uses the built-in LimeSuiteNG library and must not transmit. Airspy, HackRF, bladeRF, Pluto, SDRplay, USRP, and other published radios open through SoapySDR. Use the tools in this order:
        1. list_devices, then open_device if the radio is not already open. list_sdr_families names the module to install when a radio is missing.
        2. Ask for the antenna length and whether it is vertical or horizontal. For a horizontal wire, also ask which way the wire runs, in degrees true (0 is north). Call set_antenna, or guess_antenna to estimate the length from where the noise floor is strongest. Then list_ham_bands and prefer a recommended band. On an RTL-SDR, bands below 24 MHz use direct sampling and need an HF antenna. Strength cannot show compass direction.
        3. scan_band with one band id. AM broadcast is am-bc, FM broadcast is fm-bc, NOAA weather is noaa, Citizens Band is cb, GMRS is gmrs, and DMR is dmr. Those three are receive-only and appear when the radio can tune them. DMR lists a signal only when its framing is recognized, and the words are not decoded. Report frequency, mode, bandwidth, SNR, and source. Do not invent callsigns or station names.
        4. When the user picks a frequency, call listen. Share the audio URI. For CW, read the Morse text. For FT8/JS8/Olivia/JTTY/VarAC, pass that mode (or tune a published digi dial); the text field lists decoded messages when present. Digi scan activity is occupancy only. If digi text is empty, call analyze_clip with the listen clipId (or list_clips then analyze_clip) and explain the Costas/LDPC/self-test hints — do not invent callsigns. FT8/JS8 occupy the next 15-second slot. For AM, FM, and sideband, the text field is a speech transcript. For broadcast FM, pass mode wbfm.
        5. stop cancels a scan or a capture. In camp mode, stop pauses the schedule.
        6. set_operating_mode camp, then camp_now (or the page Listen button) camps on a frequency right away. The schedule is optional. add_schedule_entry takes frequencyHz, mode, and optional local HH:mm start and end. set_schedule_policy chooses whether a timed entry may take the radio from an explicit camp. follow_schedule and ignore_schedule answer that for the window that is due right now. Overlapping entries are rejected.
        AM broadcast, FM broadcast, and NOAA are in range only when the antenna recommendation includes them.
        """;
}
