# Agentic SDR

A receive-only amateur-radio listener for USB SDRs. One .NET process owns the radio, serves a local page, and exposes the same session to an AI over the Model Context Protocol.

New to SDRs? Read [GUIDE.md](GUIDE.md) first. It defines every term used on the page and in the code, in plain language, and walks through what the radio is doing. The same definitions are on the page under **What these words mean**. Comments on each class and interface say the same thing next to the code.

The bring-up radio is a Nooelec NESDR SMArt v5 (RTL2832U). It tunes about 100 kHz–1.75 GHz. The driver switches to Q-branch direct sampling below 24 MHz, which needs an HF antenna. A short VHF whip is enough for 2 m and 70 cm. Other published USB radios open through SoapySDR when that library and the matching module are installed.

## Install

Windows: `bash packaging/build-msi.sh` writes `packaging/out/AgenticSdr.msi`. The installer asks for the OpenAI endpoint, model, and API key, then downloads librtlsdr and the small English speech model. A local OpenAI-compatible server works in the endpoint field. The program is installed under Program Files. The key is stored in `%LOCALAPPDATA%\AgenticSDR\llm.json`. LimeSuiteNG is included when `native/limesuiteng.dll` and `native/limesuiteng-legacyapi.dll` are already built. The MSI does not install a USB driver.

Debian and Ubuntu: `bash packaging/build-deb.sh` writes `packaging/out/agenticsdr_0.1.0_amd64.deb`. `sudo apt install ./agenticsdr_0.1.0_amd64.deb` asks the same three questions through debconf, depends on `librtlsdr0`, and downloads the speech model. The key is stored in `/etc/agenticsdr/llm.json`, readable only by root. `agenticsdr` starts the page.

## Run

```bash
dotnet run --project Host
```

Open http://127.0.0.1:5088 . Enter the antenna length and whether it is vertical or horizontal, or let the page guess the length from where the noise floor is strongest. A horizontal wire also takes the compass direction the wire runs, in degrees true. The matching bands move to the top of the list. A vertical is treated as a quarter-wave and a horizontal wire as a half-wave, including odd harmonics. AM broadcast, FM broadcast, NOAA weather, CB, GMRS, and DMR show up when that antenna and the radio can hear them. DMR is recognized by its framing; the voice codec is proprietary and is not decoded. Pick a band, scan, then listen. Audio is a WAV clip. CW shows decoded Morse. FT8, on a dial frequency such as 14.074 MHz, waits for the next 15-second slot and lists the decoded messages. Broadcast FM uses wideband demodulation.

Cursor can drive the same session. This repo includes `.cursor/mcp.json` pointing at `http://127.0.0.1:5088/mcp`. Start the host first. Tools, in order: `list_devices`, `open_device`, `list_ham_bands`, `scan_band`, `listen`, and `stop`.

The page can also ask an instruct model that speaks the OpenAI chat protocol (`/v1/chat/completions`). Save the endpoint, model name, and API key on the page. The key stays in `Host/llm.json`, which is gitignored, and is sent only to that endpoint. See `Host/llm.example.json`. The model calls the same radio actions. **Stop** cancels them.

## Native library

The C# code is MIT. `librtlsdr` is GPL and is not committed.

- Windows: `bash tools/fetch-native.sh` writes `rtlsdr.dll` into `native/`. If Windows still has the DVB-T driver on the dongle, install WinUSB with Zadig for `USB\VID_0BDA&PID_2838`.
- Linux: `sudo apt install librtlsdr-dev` (or the `rtl-sdr` package).
- macOS: `brew install librtlsdr`.

## Other USB radios

SoapySDR is the path for radios that are not RTL2832U dongles. This program does not ship vendor SDKs. Install SoapySDR, then the module for the radio, and `list_devices` will show it. An RTL dongle that librtlsdr already listed is not listed a second time.

| Radio | Soapy module |
| --- | --- |
| Airspy R2, Airspy Mini | SoapyAirspy |
| Airspy HF+, HF+ Discovery | SoapyAirspyHF |
| HackRF One, Jawbreaker, rad1o | SoapyHackRF |
| bladeRF, bladeRF 2.0 micro | SoapyBladeRF |
| LimeSDR USB, LimeSDR Mini | SoapyLMS7 |
| ADALM-Pluto | SoapyPlutoSDR |
| SDRplay RSP1, RSP1A, RSP2, RSPduo, RSPdx | SoapySDRPlay (needs the SDRplay API) |
| USRP B200, B210, B200mini | SoapyUHD |
| FUNcube Dongle Pro+ | SoapyFCDPP |
| RX-888 and other SDDC receivers | SoapySDR SDDC |
| RFSpace SDR-IQ, NetSDR, CloudIQ | SoapyRFSpace |

- Windows: install [PothosSDR](https://github.com/pothosware/PothosCore/wiki/Downloads), which includes SoapySDR and several modules.
- Linux: `sudo apt install soapysdr-tools` and the module package, for example `soapysdr-module-airspy`, `soapysdr-module-hackrf`, or `soapysdr-module-bladerf`.
- macOS: `brew install soapysdr` and the matching module.

`list_sdr_families` returns this catalog to an MCP client. A module that is installed but not named above still opens.

Optional repeater names: set `Sdr:Latitude` and `Sdr:Longitude`, or a Maidenhead `Sdr:Grid`, in `Host/appsettings.json`. If the lookup fails, hits still carry the band-plan label.

## Bands

The plan is US ITU region 2, from 160 m through 23 cm, plus AM broadcast, FM broadcast, and NOAA weather when the antenna can hear them. A scan reports frequency, a mode guess (NBFM, WBFM, AM, USB, LSB, CW, or FT8 near a known dial frequency), bandwidth, SNR, and a source label such as a calling frequency or band segment. Transmit, other digital modes, and encrypted traffic are out of scope. Those words are defined in [GUIDE.md](GUIDE.md).

`dotnet test` covers the spectrum classifier, demodulator, Morse decoder, FT8 codec, and band plan. The live-dongle test opens the NESDR when it is plugged in and checks that the spectrum is not flat.
