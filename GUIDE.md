# A beginner’s guide to this receiver

This program listens. It does not transmit. You plug in a USB software-defined radio, tell it what antenna you have, pick a range of frequencies, and it looks for strong signals. When you choose one, it plays the audio. Morse, FT8, and speech are also written out as text.

You and an AI share one radio. The web page at http://127.0.0.1:5088, an instruct model you configure, and the tools in Cursor talk to the same session. Only one of them can tune or listen at a time, because a USB radio has one input.

If a word in the page or the code is unfamiliar, it is defined below. The same definitions are on the page under **What these words mean**.

## What you do

1. Start the host with `dotnet run --project Host` and open http://127.0.0.1:5088.
2. Choose the USB radio in the **Radio** list and press **Start radio**. The light beside the title turns green. **Stop radio** closes it and frees the USB port. **Stop** cancels a scan or the live audio; the radio stays open.
3. Enter the antenna, or press **Guess from signals**. Length can be meters or feet. **Vertical** means the wire stands up. **Horizontal** means the wire runs sideways, and **axis toward ° true** is the compass direction one end points. 0° is true north, 90° is east, 180° is south, 270° is west. A guess listens at several spots and takes the strongest part of the dial as the antenna’s natural frequency. The receiver’s own noise at the bottom of the dial, and a broadcast band full of stations, are left out of that choice. A vertical is a quarter of that wave. A horizontal wire would be twice as long. Strength does not show which way the wire points, so a close guess is called vertical and you can correct it.
4. Press **Use this antenna**. Bands the antenna can hear move to the top and are marked **best**. The closest match is selected for you.
5. Press **Scan band**. The table fills with strong signals.
6. Press **Listen** on a row, or type a frequency yourself. Audio starts as soon as samples are demodulated and keeps playing until you press **Stop** beside the playing line. The same **Stop** next to **Scan band** ends a scan. Morse text updates about once a second. FT8 still gathers one 15-second turn, then prints messages while the next turn plays.

**NOAA check** jumps to 162.550 MHz, a US weather-radio channel, so you can confirm the radio hears something without scanning a whole band.

The **Prompt** box is where you tell the agent what to do, in your own words. “Scan 2 m and tell me what you hear” is a prompt. Enter sends it. Shift+Enter starts a new line. The exchange stays on the page. **Send** gives that prompt to an instruct model that speaks the OpenAI chat protocol. The **endpoint** is a web address ending in `/v1`, such as `https://api.openai.com/v1` or a local server like `http://127.0.0.1:11434/v1`. The **model** is the name that server expects. The **API key** is stored in `Host/llm.json` on this computer, is not shown again, and is sent only to that endpoint. **List models** asks the endpoint which names it offers. The model can request the same actions as the buttons. This program runs the action and hands the result back. **Stop** cancels that work. The model only receives.

## What the radio is doing

A normal radio is built for one kind of signal, such as FM broadcast. A software-defined radio (SDR) is more general. The USB device tunes to a center frequency and hands the computer a stream of numbers called samples. This program does the rest in software: it looks at the spectrum, guesses the kind of signal, and turns that signal into sound or text.

One pass looks like this:

1. **Tune.** The radio centers itself on one frequency.
2. **Read.** It fills a buffer with samples. On the NESDR those samples are unsigned 8-bit I/Q. Other radios, opened through SoapySDR, arrive as complex floats and are converted to the same shape.
3. **Spectrum.** A Fourier transform shows how much energy sits at each frequency near the center. Peaks that stand well above the noise are kept.
4. **Guess.** Bandwidth, shape, and the band plan suggest a mode: voice, Morse, broadcast FM, and so on.
5. **Listen.** Demodulation keeps only the chosen signal and turns it into audio at about 48 kHz while the radio is still receiving. The page plays that audio immediately. CW is read as Morse from the audio heard so far. USB and LSB audio is checked for Morse too, because a keyed signal is often copied on sideband. FT8 is read as text at the end of each 15-second turn.

A scan watches the band for at least 15 seconds. Where the band is wider than one snapshot, the radio hops, and each stop lasts at least a few seconds. A conversation is kept when the energy arrives as a burst, a pause, and another burst, or as one transmission that starts and stops. A station that stays on, such as weather radio, is kept when it stands clearly above the noise. Each mode is measured at its own width: Morse a few hundred hertz, sideband voice about 2.7 kHz, AM about 10 kHz, narrow FM about 12.5 kHz, and broadcast FM about 200 kHz. A phone segment is also watched for Morse, because a keyed signal can share that channel with voice. Hops overlap so a signal hiding in the hole at the center of one hop is visible in a neighbor.

## The antenna, in plain language

A wire is a comfortable size for some wavelengths and a poor size for others.

- A **vertical** antenna is treated as a quarter-wave: the wire is about one quarter of the wavelength it likes. A 0.75 m vertical likes the FM broadcast band, near 95 MHz.
- A **horizontal** wire is treated as a half-wave: the wire is about half a wavelength. A wire near 66 feet likes 40 meters. It hears best **broadside**, at right angles to the wire. Aimed north, that is east and west.
- **Odd harmonics** (3, 5, and 7 times that frequency) are also listed, with less confidence. A 40 m dipole also reaches 15 m.
- A wire about 15 m or longer can hear strong local AM broadcast stations even though a true AM antenna would be much longer.
- Below 30 MHz the ionosphere scrambles polarization, so vertical versus horizontal matters less. FM broadcast and NOAA are vertical services. A horizontal wire still hears them, more weakly.

The match uses a velocity factor of 0.95, a small allowance because a real wire is electrically a bit longer than its physical length. The length you type is approximate. The program is choosing a band, not cutting a wire.

## Where the ideas live in the code

Start at the type named in parentheses if you want to see the implementation. Each of those types has a comment that says the same thing in fewer words.

| What you see | Where it is decided |
| --- | --- |
| Which USB radio is open | `RadioDevices`, `RtlSdrDevice`, `SoapyDevice`, behind `IRadioDevice` |
| Antenna length and direction | `AntennaPlan` |
| Band names, calling frequencies, NOAA, CB, GMRS, DMR, AM, and FM | `Region2BandPlan` |
| The shared radio, scan, and listen | `RadioSession` |
| Finding peaks | `PeakPicker` and `SpectrumAnalyzer` |
| Guessing the mode | `ModeClassifier` |
| Audio, Morse, and FT8 | `Demodulator`, `MorseDecoder`, `Ft8Decoder` |
| The page and the AI tools | `WebApi`, `SdrTools` |

`Radio` talks to hardware. `Dsp` is the math and does not know which dongle you own. `Host` is the page, the session, and the AI tools. `Tests` checks the math and, when the NESDR is plugged in, that the radio is not silent.

## Words

### The radio

**SDR.** Software-defined radio. The USB device captures a slice of spectrum, and software decides what to do with it.

**Receive only.** This program never transmits. Transmitting on amateur frequencies requires a license and a radio built to transmit.

**RTL-SDR / NESDR / RTL2832U.** A common inexpensive USB receiver. The NESDR SMArt v5 uses the RTL2832U chip and an R820T2 tuner. It covers roughly 100 kHz to 1.75 GHz. This program talks to it through `rtlsdr.dll`.

**SoapySDR.** A common library that opens many other USB radios (Airspy, HackRF, Pluto, and others) with one programming interface. Install SoapySDR and the module for your radio. This program does not include those vendor libraries. A LimeSDR-USB is the exception: it opens through LimeSuiteNG, and only the receive side is started.

**I/Q samples.** A pair of numbers for each moment in time. Together they describe the radio wave well enough to recover the original signal in software. You can think of them as the two knobs that draw the wave.

**Sample rate.** How many I/Q pairs arrive each second. The NESDR path uses 2.4 million per second. A LimeSDR-USB uses 3.072 million per second so reception does not need the transmit DAC powered. Other radios use the closest practical rate their hardware allows.

**Center frequency.** The frequency in the middle of the slice the radio is capturing right now. Signals just above and just below it are in the same capture.

**Tune.** Move the center frequency.

**Gain.** Amplification inside the radio, in decibels. Too little and weak signals disappear. Too much and strong signals overload the receiver. Automatic gain lets the radio choose.

**PPM.** Parts per million. A small correction when the radio’s clock is slightly fast or slow, which would otherwise make every frequency read a little high or low.

**Direct sampling.** Below 24 MHz the NESDR stops using its VHF tuner and listens through a simpler input on the chip. That can hear HF, but it needs a real HF antenna. A short whip made for 2 m hears little there. The spectrum can also contain a mirror image, a copy of a signal reflected around the center. The page marks those bands **HF**.

**Driver.** The piece of software that actually opens the USB device. `rtlsdr` is built in. Other names, such as `hackrf` or `airspy`, mean a SoapySDR module.

### Frequencies and bands

**Hz, kHz, MHz.** Hertz is one cycle per second. A kilohertz is a thousand. A megahertz is a million. The page asks for MHz because that is how people say these frequencies out loud. 146.520 MHz is 146,520,000 Hz.

**Band.** A named range of frequencies set aside for a purpose. **2 m** means wavelengths around 2 meters, which is about 144–148 MHz. **40 m** is about 7.0–7.3 MHz. The meter name is the approximate wavelength, not the width of the band.

**ITU region 2.** The Americas. This program’s amateur plan is the United States region 2 plan. Other countries place some band edges differently.

**HF.** High frequency, roughly 3–30 MHz. On this radio it also means “direct sampling, bring an HF antenna.”

**VHF and UHF.** Higher than HF. 2 m and FM broadcast are VHF. 70 cm is UHF. A short whip is reasonable here.

**Calling frequency.** A spot where operators listen for each other before moving elsewhere. The source column names these when a signal sits on one.

**AM broadcast.** Ordinary medium-wave broadcast radio in the Americas, 530–1700 kHz. Each station is about 10 kHz wide.

**FM broadcast.** Ordinary FM radio, 88–108 MHz. Each station is about 200 kHz wide, much wider than amateur FM. The mode is WBFM.

**NOAA weather.** Seven US weather-radio channels from 162.400 to 162.550 MHz. They use narrow FM. The channels are named WX1 through WX7.

**CB.** Citizens Band. Forty channels from 26.965 to 27.405 MHz. Most traffic is AM or narrow FM. Channel 9 is the emergency channel and channel 19 is the highway channel. This program only receives. The band is listed when the open radio can tune near 27 MHz.

**GMRS.** General Mobile Radio Service. Narrow FM channels near 462 MHz, and the repeater inputs 5 MHz higher, near 467 MHz. This program only receives. The band is listed when the radio can tune there.

**DMR.** Digital Mobile Radio on parts of 2 m (145.2–145.5 MHz) and 70 cm (440–450 MHz). A scan keeps a hit only when a published framing word is present. The words use a proprietary voice codec, so they are not decoded. Listening plays the digital signal and says that the words were not read. This program only receives.

### The antenna

**Wavelength.** The physical length of one cycle of the radio wave. Higher frequency means a shorter wave. Wavelength in meters is about 300 divided by the frequency in MHz.

**Quarter-wave.** An upright antenna about one quarter of that wavelength. This is the model used for **vertical**.

**Half-wave.** A wire about half a wavelength long. This is the model used for **horizontal**.

**Harmonic.** A whole-number multiple of the frequency the antenna likes best. This program keeps the odd ones: 3, 5, and 7.

**Velocity factor.** Real wire is electrically a little longer than a tape measure says. 0.95 shortens the calculated wavelength to match.

**Polarization.** The direction the wave’s electric field points. A vertical antenna prefers vertical waves. FM broadcast and weather radio are vertical. Below 30 MHz the sky mixes this, so either antenna can hear HF.

**True north.** Geographic north, not magnetic north. The compass bearing you enter is degrees clockwise from true north.

**Broadside.** The directions at right angles to a horizontal wire. A dipole hears and radiates best that way, and poorly off its ends.

### What a scan measures

**Spectrum.** A plot of “how much energy at each frequency.” The scan looks for peaks in that plot.

**SNR.** Signal-to-noise ratio, in decibels. How far the peak stands above the surrounding noise. A larger number is a cleaner signal. The table shows this in the SNR column.

**Bandwidth (kHz).** How wide the peak is. A Morse signal is a sliver. An FM broadcast station is a wide hump. The width is the main clue to the mode.

**Confidence (Conf).** How sure the mode guess is, from the shape of the peak and whether it sits where that mode is expected. A high number on the wrong kind of signal is still a guess. Listening is how you check.

**Source.** A label from the band plan: a calling frequency, a band segment, “FM broadcast,” or a NOAA channel. It is not a station name and not a callsign unless you configured a location and a nearby repeater matches.

**DC spike.** A false peak exactly at the center frequency, caused by the receiver, not by a station. The scan ignores a narrow hole in the middle and overlaps hops so a real signal there is seen from the next hop.

**Local spur.** A steady whistle made by the dongle or the computer nearby. Many of them sit on a harmonic of the 14.4 MHz clock (28.8 MHz, 144.0 MHz, and so on). Others slide sideways when the radio retunes, which a real station does not. A carrier that stays on for the whole listen is measured again. If it has no speech, no Morse keying, and no data tone, it is left off the list. A keyed Morse signal, a voice transmission that comes and goes, and an FT8 dial are kept.

**Image.** A mirror copy that direct sampling can create on the other side of the center. Those hits are marked less certain.

### Modes

**Auto.** Measure the signal and guess. On a known FT8 dial frequency, auto selects FT8 and waits for the next 15-second slot.

**AM.** Amplitude modulation. The loudness of the voice changes the strength of the wave. Used by AM broadcast and some amateur signals. You hear it as ordinary audio.

**NBFM.** Narrowband FM. The voice nudges the frequency a few kilohertz. This is the usual mode for amateur repeaters on 2 m and 70 cm, and for NOAA weather. The tuner sits 50 kHz off the station so the dongle's center spike stays outside the voice channel, and a hot gain setting is eased to 12 dB while that station is playing.

**WBFM.** Wideband FM. The station swings about 75 kHz either side of its frequency, so the channel is about 200 kHz wide. This is FM broadcast. A narrow FM decoder cannot hold it. Listen with WBFM. The tuner sits 250 kHz off the station so the dongle's center spike is not on the voice, and a hot gain setting is eased to 12 dB so a strong local station does not overload the 8-bit converter. While that station is playing, the tuner filter narrows to about 900 kHz so the rest of the band is not poured into the 8-bit converter. A scan opens the filter again. The listed frequency is the loudest point in the station. The channel is reduced before the sharp filter, so the receiver keeps up with the dongle. A filter that falls behind drops samples, and each drop is a click.

**USB and LSB.** Upper sideband and lower sideband, the two halves of single-sideband voice. Amateur HF uses LSB below 10 MHz and USB above. The carrier itself is mostly missing, which is why these signals look lopsided in the spectrum.

**CW.** Continuous wave, the on-off carrier used for Morse code. The program plays a tone you can hear and also tries to print the letters.

**FT8.** A digital mode used on amateur bands. Stations take turns in 15-second slots that start at :00, :15, :30, and :45 of each UTC minute. A message is a short line such as a callsign and a grid. The program tunes a known dial frequency, waits for the next slot, and prints any messages it can decode. A quiet channel prints none. That is a result, not a failure.

**DMR.** The mode choice for Digital Mobile Radio. Listening plays the digital signal as audio and, when the framing word is found, says that the words were not decoded.

**Demodulation.** Undoing the mode: turning I/Q samples into audio, and sometimes into text.

**Morse.** The code of dits and dahs behind CW. The decoded-text pane shows the letters when a clip is CW.

## What this program will not do

It will not transmit, follow trunked radio systems, or decrypt anything. The API key goes only to the endpoint you saved. It does not decode the proprietary DMR voice codec. Other digital modes are left as audio. A scan label is a guess from the spectrum and the band plan, except a DMR hit, which requires the framing word. The text pane fills while you listen, for Morse, FT8, speech, and that DMR note. Speech uses a small English model downloaded once into `models/`. It writes what it thinks was said. A noisy channel will be misheard.
