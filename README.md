# Agentic SDR

A receive-only amateur-radio listener for USB SDRs. One .NET process owns the radio, serves a local page at http://127.0.0.1:5088, and exposes the same session to an AI over the Model Context Protocol.

New to SDRs? Read [GUIDE.md](GUIDE.md) first. It defines the terms used on the page and in the code, in plain language, and walks through Explore, Camp, scanning, and each mode. Hover tips on the page repeat the short forms of those ideas. Comments on each class and interface say the same thing next to the code.

The bring-up radio is a Nooelec NESDR SMArt v5 (RTL2832U). It tunes about 100 kHz–1.75 GHz. The driver switches to Q-branch direct sampling below 24 MHz, which needs an HF antenna. A short VHF whip is enough for 2 m and 70 cm. LimeSDR-USB opens through LimeSuiteNG (receive only). Other USB radios open through SoapySDR when that library and the matching module are installed.

## What it does

| Area | Capability |
| --- | --- |
| **Explore** | Scan a band, click a hit, listen live, record MP3, download an XML transcript |
| **Camp** | Quiet schedule / squelch watch; audio opens when the channel is active |
| **Voice modes** | NBFM, WBFM (stereo + RDS + SCA 67/92), AM, USB, LSB, CW (+ Morse text) |
| **Digimodes** | FT8, JS8, Olivia 8/500, JTTY, VarAC (xaramonitor), Packet/APRS AX.25 |
| **Digital voice** | M17 Codec2 when `m17voice` is present; DMR framing + signaling (no proprietary voice) |
| **AI** | On-page instruct model + MCP tools/resources at `/mcp` |
| **Known channels** | NOAA WX, CB, GMRS, and published digimode dials without scanning |

Transmit, trunking follow, and decryption are out of scope.

## Download

**→ [Downloads](https://github.com/jholovacs/agenticSDR/wiki/Downloads)** — latest installers, SHA-256 checksums, and a short quick-start.

Every merge to `main` publishes a new GitHub Release tagged **`v0.1.<run_number>`** (for example `v0.1.17`) with Debian (`.deb`), Windows (`.msi` service), and macOS (`.zip`) packages plus `SHA256SUMS.txt`. The wiki page always mirrors the newest build; older versions stay under [Releases](https://github.com/jholovacs/agenticSDR/releases).

## Build from source

Local builds default to version `0.1.0` unless you set `VERSION`. Packaging scripts:

**Windows.** `bash packaging/build-msi.sh` writes `packaging/out/AgenticSdr-<version>.msi`. The installer asks for the OpenAI endpoint, model, and API key, then downloads librtlsdr and the small English speech model. A local OpenAI-compatible server works in the endpoint field. The program is installed under Program Files and runs as the **AgenticSdr** Windows service (auto-start, LocalSystem). The Start Menu shortcut opens http://127.0.0.1:5088. The key is stored in `%ProgramData%\AgenticSDR\llm.json` so the service can read it. Installing a newer MSI upgrades in place (service restart, same folder, keeps `llm.json`). LimeSuiteNG DLLs are included when `native/limesuiteng.dll` and `native/limesuiteng-legacyapi.dll` are already built. The MSI does not install a USB driver.

**Debian / Ubuntu.** `bash packaging/build-deb.sh` writes `packaging/out/agenticsdr_<version>_amd64.deb`. `sudo apt install ./…` asks the same three questions through debconf, depends on `librtlsdr0`, and downloads the speech model. The key is stored in `/etc/agenticsdr/llm.json`, readable only by root. `agenticsdr` starts the page.

**macOS.** `bash packaging/build-mac.sh` writes arm64 and x64 zips under `packaging/out/`. Unzip, run `./AgenticSdr.Host`, and open http://127.0.0.1:5088. Install librtlsdr with Homebrew (`brew install librtlsdr`).

## Run

On Windows from source, double-click **start** in the project folder to launch the host and open the page. Or:

```bash
dotnet run --project Host
```

Open http://127.0.0.1:5088. After **Start radio**, a short smoke test is **Scan and listen** → **Known channels** → **NOAA weather** (or **NOAA check**). If you hear weather radio, the SDR path is working. The same panel lists CB, GMRS, and digimode dials.

Enter the antenna length and whether it is vertical or horizontal, or let the page **Guess from signals**. Matching bands move to the top. Pick **Explore** to scan and listen, or **Camp** to watch a schedule quietly. Audio plays live in Explore (and in Camp when squelch opens). CW shows Morse; FT8/JS8 wait for the next 15-second slot; broadcast FM uses wideband demodulation with stereo/RDS when present.

## Configuration

LLM settings are chosen in this order: `Host/llm.json` next to the program (development), then the user file, then the system file a package wrote.

| Platform | User LLM file | System LLM file |
| --- | --- | --- |
| Windows (dev / interactive) | `%LOCALAPPDATA%\AgenticSDR\llm.json` | |
| Windows (MSI service) | | `%ProgramData%\AgenticSDR\llm.json` |
| Linux | `~/.config/agenticsdr/llm.json` | `/etc/agenticsdr/llm.json` |
| macOS | `~/.config/agenticsdr/llm.json` | |

`schedule.json` (Camp) and `callsigns.json` (digimode lookups) live in the same folder as the active LLM file. See `Host/llm.example.json`.

Optional keys under `Sdr` in `Host/appsettings.json`:

| Key | Meaning |
| --- | --- |
| `SampleRateHz`, `GainDb`, `AutoGain`, `Ppm` | Default radio settings |
| `ItuRegion` | Band plan region (default `2`) |
| `Latitude` / `Longitude` or `Grid` | Optional repeater name lookup |
| `CallsignLookup` | Background HamDB / HamQTH DXCC enrichment (default `true`) |

## AI and MCP

Cursor and Antigravity can drive the same session. This repo includes `.cursor/mcp.json` and `.agents/mcp_config.json` pointing at `http://127.0.0.1:5088/mcp`. Start the host first.

**MCP tools** (grouped):

- Devices: `list_devices`, `list_sdr_families`, `open_device`, `close_device`, `get_capabilities`
- Antenna / bands: `set_antenna`, `guess_antenna`, `get_antenna`, `list_ham_bands`
- RF work: `scan_band`, `inspect_signal`, `listen`, `list_clips`, `analyze_clip`, `stop`
- Camp / schedule: `set_operating_mode`, `get_schedule`, `add_schedule_entry`, `update_schedule_entry`, `remove_schedule_entry`, `camp_now`, `clear_camp_now`, `set_schedule_policy`, `follow_schedule`, `ignore_schedule`

**Resources:** `audio://clips/{id}`, `text://decodes/{id}`  
**Prompt:** `scan-and-listen`

The page **Prompt** box talks to an OpenAI-compatible `/v1/chat/completions` endpoint. The model calls the same radio actions. **Stop** cancels them. The key stays on this computer and is sent only to that endpoint.

## Modes and bands

The plan is US ITU region 2, from 160 m through 23 cm, plus AM broadcast, FM broadcast, NOAA, CB, GMRS, and DMR segments when the antenna and radio can hear them. A scan reports frequency, a mode guess, bandwidth, SNR, and a source label. Digimode dials can appear as **(activity)** occupancy without a full decode.

| Mode | Notes |
| --- | --- |
| Auto | Classify from spectrum + band plan; digi dials pick the published mode |
| NBFM | Amateur FM, NOAA, GMRS |
| WBFM | FM broadcast; stereo when the 19 kHz pilot locks; RDS text; SCA 67/92 |
| AM | Broadcast + CB |
| USB / LSB | Voice; Morse also watched on sideband audio |
| CW | Tone + Morse decode |
| FT8 / JS8 | 15 s slots; optional callsign / grid plain reading |
| Olivia / JTTY | Keyboard digimodes on published dials |
| VarAC | Bundled MIT xaramonitor (not EA5HVK VARA) |
| Packet | 1200 baud AX.25 / APRS |
| M17 | Open Codec2 via bundled `m17voice` |
| DMR | Framing required for a scan hit; signaling text only |
| Raw | Wide discriminator, minimal voice filtering |

## Native libraries

The C# host is MIT. M17/Codec2 and VarAC decode come from Rust crates under `native/m17voice` and `native/xaramonitor` (m17voice links GPL-2 `native/libm17`). Rebuild for your OS with:

```bash
bash tools/build-native.sh
```

That writes `m17voice` / `xaramonitor` into `Host/native/` (`.dll` on Windows, `.so` on Linux, `.dylib` on macOS). Windows DLLs are also committed there so MSI/dev builds work without Rust. Package CI rebuilds from source on Linux and macOS.

Windows **librtlsdr** (GPL) is committed under `native/` as `rtlsdr.dll` plus `pthreadVC2.dll` / `msvcr100.dll`, and the MSI ships those files. Refresh with `bash tools/fetch-native.sh` if needed.

- Windows: if Windows still has the DVB-T driver on the dongle, install WinUSB with Zadig for `USB\VID_0BDA&PID_2838`.
- Linux: `sudo apt install librtlsdr-dev` (or the `rtl-sdr` package).
- macOS: `brew install librtlsdr`.

## Other USB radios

SoapySDR is the path for radios that are not RTL2832U dongles and not opened through LimeSuiteNG. This program does not ship vendor SDKs. Install SoapySDR, then the module for the radio, and `list_devices` will show it. An RTL dongle that librtlsdr already listed is not listed a second time. LimeSDR-USB uses LimeSuiteNG (RX only), not SoapyLMS7.

| Radio | How it opens |
| --- | --- |
| RTL2832U / NESDR | Built-in `librtlsdr` |
| LimeSDR USB / Mini | Built-in LimeSuiteNG (receive only) |
| Airspy R2, Mini | SoapyAirspy |
| Airspy HF+, Discovery | SoapyAirspyHF |
| HackRF One, Jawbreaker, rad1o | SoapyHackRF |
| bladeRF, bladeRF 2.0 micro | SoapyBladeRF |
| ADALM-Pluto | SoapyPlutoSDR |
| SDRplay RSP1/1A/2/duo/dx | SoapySDRPlay (needs the SDRplay API) |
| USRP B200, B210, B200mini | SoapyUHD |
| FUNcube Dongle Pro+ | SoapyFCDPP |
| RX-888 and other SDDC | SoapySDR SDDC |
| RFSpace SDR-IQ, NetSDR, CloudIQ | SoapyRFSpace |

- Windows: install [PothosSDR](https://github.com/pothosware/PothosCore/wiki/Downloads) for SoapySDR and several modules.
- Linux: `sudo apt install soapysdr-tools` and the module package, for example `soapysdr-module-airspy`.
- macOS: `brew install soapysdr` and the matching module.

`list_sdr_families` returns this catalog to an MCP client. A module that is installed but not named above still opens.

## Develop and test

```bash
dotnet test
dotnet run --project Host
```

`dotnet test` covers spectrum classification, demodulation (including WBFM pacing), Morse, FT8/JS8, Olivia/JTTY, RDS helpers, packet, M17 plumbing, camp/schedule, callsign lookup, and the band plan. The live-dongle test opens the NESDR when it is plugged in and checks that the spectrum is not flat.

Packaging scripts: `packaging/build-msi.sh`, `packaging/build-deb.sh`, `packaging/build-mac.sh`. CI workflow: `.github/workflows/packages.yml`.
