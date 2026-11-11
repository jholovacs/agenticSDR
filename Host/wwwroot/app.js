const band = document.querySelector("#band");
const hits = document.querySelector("#hits");
const empty = document.querySelector("#empty");
const error = document.querySelector("#error");
const statusLine = document.querySelector("#status-line");
const deviceName = document.querySelector("#device-name");
const led = document.querySelector("#led");
const busyPanel = document.querySelector("#busy-panel");
const busyTitle = document.querySelector("#busy-title");
const busyDetail = document.querySelector("#busy-detail");
const busyBar = document.querySelector("#busy-bar");
const busyPercent = document.querySelector("#busy-percent");
const scanNote = document.querySelector("#scan-note");
const readout = document.querySelector("#readout");
const source = document.querySelector("#source");
const morse = document.querySelector("#morse");
const liveState = document.querySelector("#live-state");
const liveStop = document.querySelector("#live-stop");
const mhz = document.querySelector("#mhz");
const campFreq = document.querySelector("#camp-freq");
const mode = document.querySelector("#mode");

function syncTuneFrequency(text) {
  const value = text ?? mhz.value;
  mhz.value = value;
  if (campFreq) campFreq.value = value;
}

function campFrequencyHz() {
  return Number((campFreq?.value || mhz.value)) * 1e6;
}
const antennaLength = document.querySelector("#antenna-length");
const antennaUnit = document.querySelector("#antenna-unit");
const antennaOrientation = document.querySelector("#antenna-orientation");
const antennaAzimuth = document.querySelector("#antenna-azimuth");
const antennaNote = document.querySelector("#antenna-note");
const radioSelect = document.querySelector("#radio");

let busy = false;
let liveAbort = null;
let agentAbort = null;
const agentHistory = [];
let liveAudio = null;
let recording = null;
let recordingFileId = null;
let recordingStarting = false;
let transcriptSpans = [];
let listeningHz = null;
let listeningMode = "";
let liveSampleRate = 48000;
let listenToken = 0;
let switching = false;
let operatingMode = "explore";
let campActivity = false;
let campPlayKey = "";
let campConnecting = false;
let campRevision = -1;
let scheduleView = null;
let editingId = null;
let liveGain = null;
const mp3Frame = 1152;
let operatingRadio = false;
let deviceOpen = false;
let openIndex = null;
let radioSignature = "";

async function api(path, options) {
  const response = await fetch(path, options);
  const text = await response.text();
  const body = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(body?.error || response.statusText);
  return body;
}

function showError(message) {
  error.hidden = !message;
  error.textContent = message || "";
}

function mhzText(hz) {
  return (hz / 1e6).toFixed(6);
}

function setBusy(value) {
  busy = value;
  const live = liveAbort !== null;
  document.querySelectorAll("button").forEach((button) => {
    if (button.closest("dialog")) return;
    if (button.id === "stop" || button.id === "radio-stop" || button.id === "live-stop" || button.id === "camp-stop") return;
    if (button.id === "record" || button.id === "record-transcript" || button.classList.contains("row-listen")) return;
    if (live && (button.id === "listen" || button.id === "camp-go" || button.id === "noaa" || button.classList.contains("channel-tune"))) return;
    button.disabled = value;
  });
  applyRadioControls();
  syncSignalButtons();
  paintCampControls();
}

function busyHeading(phase) {
  switch (phase) {
    case "scanning": return "Scanning";
    case "listening": return "Listening";
    case "inspecting": return "Inspecting";
    case "camping": return "Camping";
    default: return phase && phase !== "idle" ? phase.charAt(0).toUpperCase() + phase.slice(1) : "Working";
  }
}

function busyBlurb(phase) {
  switch (phase) {
    case "scanning":
      return "The radio is sweeping the band for strong signals. This can take a little while on a wide band.";
    case "listening":
      return "The radio is tuned and capturing audio. Digimodes may wait for the next 15-second slot before decoding.";
    case "inspecting":
      return "Measuring that frequency for mode, bandwidth, and strength.";
    default:
      return "The radio is busy. Wait for this step to finish, or stop it.";
  }
}

function otherDialogOpen() {
  return [...document.querySelectorAll("dialog")].some((dialog) => dialog.open && dialog !== busyPanel);
}

function paintBusyModal(phase, message, progress) {
  if (!busyPanel) return;
  // Live explore/camp streaming keeps phase "listening" for the whole tune —
  // show the modal only for finite work (scan, inspect, clip listen, digi wait).
  const liveStream = liveAbort !== null;
  const campWatch = operatingMode === "camp" && phase === "listening";
  const active = !!(phase && phase !== "idle" && !liveStream && !campWatch);
  if (!active) {
    if (busyPanel.open) busyPanel.close();
    return;
  }

  busyTitle.textContent = busyHeading(phase);
  const detail = (message || "").trim();
  const blurb = busyBlurb(phase);
  busyDetail.textContent = detail && detail !== phase ? `${blurb}\n\n${detail}` : blurb;
  busyDetail.style.whiteSpace = "pre-wrap";
  const pct = Math.max(0, Math.min(100, progress || 0));
  busyBar.style.width = `${pct}%`;
  busyPercent.textContent = pct > 0 ? `${pct}%` : "Starting…";

  if (!busyPanel.open && !otherDialogOpen()) {
    try { busyPanel.showModal(); } catch { /* Nested modal not supported. */ }
  }
}

function applyRadioControls() {
  const hasRadio = [...radioSelect.options].some((option) => !option.disabled && option.value !== "");
  const selectedIsOpen = deviceOpen && openIndex !== null && Number(radioSelect.value) === openIndex;
  document.querySelector("#radio-start").disabled = busy || selectedIsOpen || !hasRadio;
  document.querySelector("#radio-stop").disabled = !deviceOpen;
  document.querySelector("#radio-details").disabled = busy || !hasRadio;
  document.querySelector("#antenna-guess").disabled = busy || !deviceOpen;
  const live = liveAbort !== null;
  document.querySelector("#scan").disabled = busy || !deviceOpen;
  document.querySelector("#listen").disabled = !deviceOpen || (busy && !live);
  document.querySelector("#camp-go").disabled = !deviceOpen || (busy && !live);
  document.querySelector("#noaa").disabled = !deviceOpen || (busy && !live);
  document.querySelectorAll(".channel-tune").forEach((button) => {
    button.disabled = !deviceOpen || (busy && !live);
  });
  document.querySelector("#stop").disabled = operatingRadio || !busy;
}

async function loadRadios() {
  const devices = await api("/api/devices");
  const signature = devices.map((device) => `${device.index}:${device.name}:${device.serial || ""}`).join("|");
  if (signature !== radioSignature) {
    const previous = radioSelect.value;
    radioSelect.innerHTML = "";
    if (devices.length === 0) {
      const option = document.createElement("option");
      option.textContent = "No radio found";
      option.disabled = true;
      radioSelect.append(option);
    } else {
      for (const device of devices) {
        const option = document.createElement("option");
        option.value = String(device.index);
        option.textContent = device.serial ? `${device.name} · ${device.serial}` : device.name;
        radioSelect.append(option);
      }
      const keep = openIndex !== null && devices.some((device) => device.index === openIndex)
        ? String(openIndex)
        : previous;
      if ([...radioSelect.options].some((option) => option.value === keep))
        radioSelect.value = keep;
    }
    radioSignature = signature;
  }
  applyRadioControls();
  paintFoldSummaries();
}

async function startRadio() {
  showError("");
  operatingRadio = true;
  setBusy(true);
  try {
    await api("/api/open", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ index: Number(radioSelect.value) })
    });
    radioSignature = "";
    await loadRadios();
  } catch (ex) {
    showError(ex.message);
  } finally {
    operatingRadio = false;
    setBusy(false);
    refreshStatus();
  }
}

async function showRadioDetails() {
  const panel = document.querySelector("#radio-details-panel");
  const list = document.querySelector("#radio-details-list");
  const title = document.querySelector("#radio-details-title");
  const button = document.querySelector("#radio-details");
  if (panel.open && panel.dataset.index === radioSelect.value) {
    panel.close();
    return;
  }

  showError("");
  panel.dataset.index = radioSelect.value;
  title.textContent = "Reading radio details…";
  list.replaceChildren();
  if (!panel.open) panel.showModal();
  button.disabled = true;
  try {
    const report = await api(`/api/radio-details?index=${encodeURIComponent(radioSelect.value)}`);
    title.textContent = report.title || "Radio details";
    for (const fact of report.facts || []) {
      const label = document.createElement("dt");
      label.textContent = fact.label;
      const value = document.createElement("dd");
      value.textContent = fact.value;
      list.append(label, value);
    }
  } catch (ex) {
    panel.close();
    showError(ex.message);
  } finally {
    applyRadioControls();
  }
}

async function stopRadio() {
  showError("");
  operatingRadio = true;
  setBusy(true);
  await api("/api/stop", { method: "POST" }).catch(() => {});
  try {
    await api("/api/close", { method: "POST" });
  } catch (ex) {
    showError(ex.message);
  } finally {
    operatingRadio = false;
    setBusy(false);
    radioSignature = "";
    await refreshStatus();
    await loadRadios().catch((ex) => showError(ex.message));
  }
}

let transcriptKey = "";

function transcriptSource(kind) {
  if (kind === "morse") return "Morse decoder";
  if (kind === "ft8") return "FT8 decoder";
  if (kind === "js8") return "JS8 decoder";
  if (kind === "olivia") return "Olivia decoder";
  if (kind === "jtty") return "JTTY decoder";
  if (kind === "varac") return "VarAC / xara";
  if (kind === "packet") return "Packet / APRS";
  if (kind === "speech") return "speech to text";
  if (kind === "rds") return "RDS / RBDS";
  if (kind === "note") return "receiver note";
  return kind || "receiver";
}

function stampTranscript(spans) {
  const next = [];
  let cursor = 0;
  for (const span of spans) {
    let match = -1;
    for (let i = cursor; i < transcriptSpans.length; i++) {
      const previous = transcriptSpans[i];
      if (previous.kind !== span.kind) continue;
      const same = previous.text === span.text;
      const grown = span.kind === "morse" && (span.text.startsWith(previous.text) || previous.text.startsWith(span.text));
      if (same || grown) {
        match = i;
        break;
      }
    }
    next.push({
      kind: span.kind,
      text: span.text,
      utc: match >= 0 ? transcriptSpans[match].utc : new Date().toISOString()
    });
    if (match >= 0) cursor = match + 1;
  }
  transcriptSpans = next;
}

function renderTranscript(spans) {
  const key = JSON.stringify(spans);
  if (key === transcriptKey) return;
  const nearBottom = morse.scrollHeight - morse.scrollTop - morse.clientHeight < 32;
  transcriptKey = key;
  stampTranscript(spans);
  updateTranscriptButton();
  morse.replaceChildren();
  if (spans.length === 0) {
    morse.textContent = "Morse, FT8, JS8, Olivia, JTTY, VarAC, packet, RDS, and speech appear here.";
    return;
  }

  spans.forEach((span, index) => {
    if (index > 0) morse.append("\n");
    const line = document.createElement("span");
    line.className = span.kind === "morse" ? "morse"
      : span.kind === "packet" ? "packet"
      : span.kind === "ft8" || span.kind === "js8" || span.kind === "olivia" || span.kind === "jtty" || span.kind === "varac" ? "ft8"
      : span.kind === "rds" ? "rds"
      : span.kind === "note" ? "note"
      : "speech";
    line.textContent = span.text;
    morse.append(line);
  });
  if (nearBottom) morse.scrollTop = morse.scrollHeight;
}

async function refreshStatus() {
  try {
    const status = await api("/api/status");
    deviceOpen = status.deviceOpen;
    openIndex = status.deviceIndex;
    led.classList.toggle("on", status.deviceOpen);
    deviceName.textContent = status.deviceOpen
      ? (status.deviceName || "SDR")
      : "No radio";
    operatingMode = status.operatingMode === "camp" ? "camp" : "explore";
    campActivity = !!(status.campActivity || (status.camp && status.camp.activity));
    paintModeSwitch();
    paintWorkspace();
    paintCampControls(status);
    paintCampSnr(status);
    applyVolume();
    applyRadioControls();
    const progress = Math.round((status.progress || 0) * 100);
    statusLine.textContent = status.message || status.phase;
    if (Array.isArray(status.transcript))
      renderTranscript(status.transcript);
    if (status.error) showError(status.error);
    if (status.campRevision !== campRevision) {
      campRevision = status.campRevision;
      api("/api/camp").then(renderSchedule).catch(() => {});
    }
    followCamp(status);
    paintBusyModal(status.phase, status.message, progress);
    paintFoldSummaries();
  } catch (ex) {
    statusLine.textContent = ex.message;
  }
}

async function loadBands() {
  const bands = await api("/api/bands");
  const previous = band.value;
  band.innerHTML = "";
  const preferred = bands.find((item) => item.recommended && item.tunable);
  for (const item of bands) {
    const option = document.createElement("option");
    option.value = item.id;
    const marks = [item.recommended ? "best" : "", item.directSampling ? "HF" : ""].filter(Boolean).join(" · ");
    option.textContent = marks ? `${item.name} · ${marks}` : item.name;
    option.disabled = !item.tunable;
    band.append(option);
  }
  const keep = bands.find((item) => item.id === previous && item.tunable);
  const fallback = bands.find((item) => item.id === "2m" && item.tunable) || bands.find((item) => item.tunable);
  band.value = (preferred || keep || fallback)?.id || "";
  paintFoldSummaries();
}

async function loadKnownChannels() {
  const groups = await api("/api/channels");
  const root = document.querySelector("#known-channel-groups");
  if (!root) return;
  root.innerHTML = "";
  for (const group of groups || []) {
    const block = document.createElement("details");
    block.className = "known-group";
    const summary = document.createElement("summary");
    summary.textContent = `${group.name} · ${group.channels.length}`;
    const grid = document.createElement("div");
    grid.className = "channel-grid";
    for (const channel of group.channels) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "channel-tune ghost";
      button.title = `${channel.name} · ${mhzText(channel.frequencyHz)} MHz`;
      button.textContent = `${channel.label} · ${compactNumber(channel.frequencyHz / 1e6, 4)}`;
      button.addEventListener("click", () => {
        tuneKnownChannel(channel.frequencyHz, channel.mode, group.id).catch((ex) => showError(ex.message));
      });
      grid.append(button);
    }
    block.append(summary, grid);
    root.append(block);
  }
  applyRadioControls();
}

function bandIdForFrequency(frequencyHz) {
  const available = new Set([...band.options].map((option) => option.value).filter(Boolean));
  const picks = [
    [1.8e6, 2.0e6, "160m"],
    [3.5e6, 4.0e6, "80m"],
    [5.3e6, 5.5e6, "60m"],
    [7.0e6, 7.3e6, "40m"],
    [10.1e6, 10.15e6, "30m"],
    [14.0e6, 14.35e6, "20m"],
    [18.068e6, 18.168e6, "17m"],
    [21.0e6, 21.45e6, "15m"],
    [24.89e6, 24.99e6, "12m"],
    [28.0e6, 29.7e6, "10m"],
    [50.0e6, 54.0e6, "6m"],
    [144.0e6, 148.0e6, "2m"],
    [222.0e6, 225.0e6, "1.25m"],
    [420.0e6, 450.0e6, "70cm"],
    [1240.0e6, 1300.0e6, "23cm"]
  ];
  for (const [lo, hi, id] of picks) {
    if (frequencyHz >= lo && frequencyHz <= hi && available.has(id))
      return id;
  }
  return null;
}

async function tuneKnownChannel(frequencyHz, heard, bandId) {
  showError("");
  syncTuneFrequency(mhzText(frequencyHz));
  const digiGroups = new Set(["ft8", "js8", "olivia", "jtty", "varac"]);
  const bandChoice = digiGroups.has(bandId) ? (bandIdForFrequency(frequencyHz) || bandId) : bandId;
  if ([...band.options].some((option) => option.value === bandChoice))
    band.value = bandChoice;
  if ([...mode.options].some((option) => option.value === heard))
    mode.value = heard;
  suggestedMode = heard;
  fillHearModes();
  const hear = document.querySelector("#listen-mode");
  if ([...hear.options].some((option) => option.value === heard))
    hear.value = heard;
  syncDmrFields();
  paintFoldSummaries();
  await listen(heard);
}

function compactNumber(value, places) {
  const n = Number(value);
  if (!Number.isFinite(n)) return "";
  return String(parseFloat(n.toFixed(places)));
}

function paintFoldSummaries() {
  const option = radioSelect.selectedOptions[0];
  const radioLabel = option && option.value !== "" ? option.textContent.split(" · ")[0] : "No radio";
  const selectedIsOpen = deviceOpen && openIndex !== null && Number(radioSelect.value) === openIndex;
  document.querySelector("#radio-summary").textContent = radioLabel === "No radio"
    ? "No radio"
    : `${radioLabel}, ${selectedIsOpen ? "open" : "stopped"}`;

  const length = compactNumber(antennaLength.value, 2);
  const unit = antennaUnit.value === "ft" ? "ft" : "m";
  const antennaSummary = document.querySelector("#antenna-summary");
  if (!length) antennaSummary.textContent = "";
  else if (antennaOrientation.value === "horizontal") {
    const degrees = Math.round(Number(antennaAzimuth.value) || 0);
    antennaSummary.textContent = `${length}${unit}, horizontal (${degrees} degrees TN)`;
  } else {
    antennaSummary.textContent = `${length}${unit}, vertical`;
  }

  const modelName = document.querySelector("#llm-model").value.trim();
  const base = document.querySelector("#llm-base").value.trim();
  let modelText = "Not set";
  if (modelName) modelText = modelName;
  else if (base) {
    try { modelText = new URL(base).host; }
    catch { modelText = base; }
  }
  document.querySelector("#model-summary").textContent = modelText;

  const turns = document.querySelectorAll("#prompt-log .turn");
  let promptText = "Nothing asked yet";
  if (turns.length) {
    const body = turns[turns.length - 1].querySelector("p:last-child")?.textContent.replace(/\s+/g, " ").trim() || "";
    promptText = body.length > 48 ? `${body.slice(0, 47)}…` : body;
  }
  document.querySelector("#prompt-summary").textContent = promptText;

  const bandName = band.selectedOptions[0]?.textContent.split(" · ")[0] || "";
  const freq = compactNumber(mhz.value, 6);
  const modeName = mode.selectedOptions[0]?.textContent || "";
  document.querySelector("#tune-summary").textContent = [bandName, freq ? `${freq} MHz` : "", modeName].filter(Boolean).join(", ");

  if (!scanNote.textContent) scanNote.textContent = "None yet";

  const saved = scheduleView?.entries?.length || 0;
  const active = scheduleView?.active;
  document.querySelector("#schedule-summary").textContent = active
    ? `${compactNumber(active.frequencyHz / 1e6, 4)} MHz, ${modeLabel(active.mode)}`
    : saved ? `${saved} saved` : "None";

  const raw = readout.textContent.replace(/\s+/g, " ").trim();
  let selected = "None";
  if (raw && raw !== "—") {
    const match = raw.match(/^([0-9.]+)\s*MHz\s*(.*)$/);
    const heard = modeLabel(listeningMode || document.querySelector("#listen-mode").value);
    selected = match
      ? `${compactNumber(match[1], 6)} MHz${heard ? `, ${heard}` : ""}`
      : raw;
    if (recording) selected += ", recording";
    else if (!liveState.hidden) selected += ", playing";
  }
  document.querySelector("#selected-summary").textContent = selected;
}

function syncAzimuth() {
  const horizontal = antennaOrientation.value === "horizontal";
  antennaAzimuth.disabled = !horizontal;
  paintFoldSummaries();
}

async function applyAntenna() {
  showError("");
  const body = {
    length: Number(antennaLength.value),
    unit: antennaUnit.value,
    orientation: antennaOrientation.value,
    azimuthDegrees: antennaOrientation.value === "horizontal" ? Number(antennaAzimuth.value) : null
  };
  const advice = await api("/api/antenna", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  antennaNote.textContent = advice.summary;
  await loadBands();
  paintFoldSummaries();
}

async function guessAntenna() {
  showError("");
  setBusy(true);
  antennaNote.textContent = "Listening across the dial for where the antenna is strongest.";
  try {
    const advice = await api("/api/antenna/guess", { method: "POST" });
    antennaUnit.value = "m";
    antennaLength.value = advice.lengthMeters;
    antennaOrientation.value = advice.orientation;
    if (advice.azimuthDegrees != null)
      antennaAzimuth.value = String(Math.round(advice.azimuthDegrees));
    syncAzimuth();
    antennaNote.textContent = advice.summary;
    await loadBands();
    paintFoldSummaries();
  } catch (ex) {
    showError(ex.message);
  } finally {
    setBusy(false);
    refreshStatus();
  }
}

function renderHits(report) {
  hits.innerHTML = "";
  const rows = report?.hits || [];
  empty.hidden = rows.length > 0;
  scanNote.textContent = report?.warning || (report ? `${rows.length} signal${rows.length === 1 ? "" : "s"}` : "None yet");
  for (const hit of rows) {
    const row = document.createElement("tr");
    row.innerHTML = `
      <td class="num">${mhzText(hit.frequencyHz)}</td>
      <td>${hit.mode}</td>
      <td class="num">${Math.round(hit.confidence * 100)}%</td>
      <td class="num">${(hit.bandwidthHz / 1000).toFixed(1)}</td>
      <td class="num">${hit.snrDb.toFixed(0)}</td>
      <td>${hit.source}</td>
      <td><button type="button" class="ghost row-listen">Listen</button></td>`;
    row.querySelector("button").addEventListener("click", () => {
      document.querySelectorAll("tr").forEach((tr) => tr.classList.remove("selected"));
      row.classList.add("selected");
      syncTuneFrequency(mhzText(hit.frequencyHz));
      mode.value = hit.mode.toLowerCase();
      suggestedMode = mode.value;
      fillHearModes();
      document.querySelector("#listen-mode").value = suggestedMode;
      syncDmrFields();
      listen().catch((ex) => showError(ex.message));
    });
    hits.append(row);
  }
  syncSignalButtons();
  paintFoldSummaries();
}

async function scan() {
  showError("");
  setBusy(true);
  try {
    const report = await api("/api/scan", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ bandId: band.value })
    });
    renderHits(report);
  } catch (ex) {
    showError(ex.message);
  } finally {
    setBusy(false);
    refreshStatus();
  }
}

function stopLivePlayback() {
  liveAbort?.abort();
  liveAbort = null;
  listeningHz = null;
  listeningMode = "";
  if (liveAudio) {
    liveAudio.close().catch(() => {});
    liveAudio = null;
  }
  liveState.hidden = true;
  liveStop.hidden = true;
  syncSignalButtons();
  paintFoldSummaries();
}

function syncSignalButtons() {
  const live = liveAbort !== null;
  document.querySelectorAll(".row-listen").forEach((button) => {
    button.disabled = !live && busy;
  });
  document.querySelector("#record").disabled = !live && !recording;
  updateTranscriptButton();
}

function updateTranscriptButton() {
  document.querySelector("#record-transcript").disabled = transcriptSpans.length === 0;
}

function utcStamp(date) {
  return date.toISOString().replace(/[-:]/g, "").replace(/\.\d{3}/, "");
}

function modeToken(value) {
  const names = {
    nbfm: "FMNB",
    wbfm: "FMWB",
    am: "AM",
    usb: "SSBUSB",
    lsb: "SSBLSB",
    raw: "Raw",
    cw: "CW",
    ft8: "FT8",
    js8: "JS8",
    olivia: "Olivia",
    jtty: "JTTY",
    varac: "VarAC",
    dmr: "DMR",
    m17: "M17",
    packet: "PKT"
  };
  return names[String(value || "").toLowerCase()] || "Audio";
}

function recordingName(frequencyHz, started, ended, heard) {
  return `${mhzText(frequencyHz)}MHz_${modeToken(heard)}_${utcStamp(started)}-${utcStamp(ended)}.mp3`;
}

async function startRecording() {
  const created = await api("/api/recording", { method: "POST" });
  if (!liveAbort) {
    await removeRecordingFile(created.id);
    showError("Listen to a signal before recording.");
    return;
  }
  const rate = liveSampleRate || 48000;
  recording = {
    frequencyHz: listeningHz ?? Number(mhz.value) * 1e6,
    mode: listeningMode,
    started: new Date(),
    encoder: new lamejs.Mp3Encoder(1, rate, 64),
    pending: new Int16Array(0),
    id: created.id,
    queue: Promise.resolve(),
    batch: [],
    bytes: 0,
    discarded: false,
    failed: null
  };
  recordingFileId = created.id;
  const button = document.querySelector("#record");
  button.textContent = "Stop recording";
  button.classList.add("recording");
  paintFoldSummaries();
}

function appendRecording(samples) {
  if (!recording || samples.length === 0) return;
  const merged = new Int16Array(recording.pending.length + samples.length);
  merged.set(recording.pending, 0);
  merged.set(samples, recording.pending.length);
  let offset = 0;
  while (merged.length - offset >= mp3Frame) {
    const frame = merged.subarray(offset, offset + mp3Frame);
    const mp3 = recording.encoder.encodeBuffer(frame);
    if (mp3.length) queueMp3(recording, mp3);
    offset += mp3Frame;
  }
  recording.pending = new Int16Array(merged.subarray(offset));
}

function queueMp3(rec, data) {
  if (!data || data.length === 0) return;
  const copy = new Uint8Array(data.length);
  copy.set(data);
  rec.bytes += copy.byteLength;
  rec.batch.push(copy);
  let pending = 0;
  for (const part of rec.batch) pending += part.byteLength;
  if (pending < 16384) return;
  flushRecording(rec);
}

function flushRecording(rec) {
  if (rec.batch.length === 0) return;
  const parts = rec.batch;
  rec.batch = [];
  let length = 0;
  for (const part of parts) length += part.byteLength;
  const body = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    body.set(part, offset);
    offset += part.byteLength;
  }
  rec.queue = rec.queue.then(async () => {
    if (rec.discarded || rec.failed) return;
    await api(`/api/recording/${rec.id}`, {
      method: "POST",
      headers: { "Content-Type": "application/octet-stream" },
      body
    });
  }).catch((ex) => {
    rec.failed = ex;
  });
}

async function endRecording(discard) {
  const rec = recording;
  recording = null;
  const button = document.querySelector("#record");
  button.textContent = "Record";
  button.classList.remove("recording");
  syncSignalButtons();
  paintFoldSummaries();
  if (!rec) return null;
  rec.discarded = !!discard;
  if (!discard) {
    if (rec.pending.length) {
      const mp3 = rec.encoder.encodeBuffer(rec.pending);
      if (mp3.length) queueMp3(rec, mp3);
    }
    const tail = rec.encoder.flush();
    if (tail.length) queueMp3(rec, tail);
    flushRecording(rec);
  }
  try {
    await rec.queue;
  } catch (ex) {
    rec.failed = ex;
  }
  if (discard || rec.failed || rec.bytes === 0) {
    await removeRecordingFile(rec.id);
    if (rec.failed && !discard) showError(rec.failed.message);
    return null;
  }
  return { id: rec.id, name: recordingName(rec.frequencyHz, rec.started, new Date(), rec.mode) };
}

async function removeRecordingFile(id) {
  if (!id) return;
  if (recordingFileId === id) recordingFileId = null;
  try {
    await fetch(`/api/recording/${id}`, { method: "DELETE", keepalive: true });
  } catch {
    /* The host is already gone, and the next start clears the temp directory. */
  }
}

async function releaseRecording(file, download) {
  if (!file) return;
  try {
    if (!download) return;
    const response = await fetch(`/api/recording/${file.id}`);
    if (!response.ok) throw new Error("Could not read the recording.");
    const blob = await response.blob();
    if (blob.size === 0) {
      showError("The recording was too short to save.");
      return;
    }
    downloadFile({ blob, name: file.name });
  } finally {
    await removeRecordingFile(file.id);
  }
}

async function offerRecording(file) {
  if (!file) return;
  const choice = await askDownload("Download the recording?", "Download the recording?");
  await releaseRecording(file, choice === "yes");
}

function xmlEscape(text) {
  return String(text)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function transcriptFile() {
  const started = transcriptSpans[0].utc;
  const ended = transcriptSpans[transcriptSpans.length - 1].utc;
  const start = utcStamp(new Date(started));
  const end = utcStamp(new Date(ended));
  const frequencyHz = listeningHz ?? Number(mhz.value) * 1e6;
  const lines = transcriptSpans.map((span) =>
    `  <text utc="${xmlEscape(span.utc)}" source="${xmlEscape(transcriptSource(span.kind))}">${xmlEscape(span.text)}</text>`);
  const xml = `<?xml version="1.0" encoding="UTF-8"?>\n<transcript>\n${lines.join("\n")}\n</transcript>\n`;
  return {
    blob: new Blob([xml], { type: "text/xml" }),
    name: `${mhzText(frequencyHz)}MHz_${modeToken(listeningMode)}_${start}-${end}.xml`
  };
}

function closePrompt() {
  const leave = document.querySelector("#leave-recording");
  if (leave.open) leave.close("stay");
}

function askDownload(heading, message) {
  const dialog = document.querySelector("#leave-recording");
  document.querySelector("#recording-question-title").textContent = heading;
  document.querySelector("#recording-question").textContent = message;
  return new Promise((resolve) => {
    dialog.addEventListener("close", () => resolve(dialog.returnValue || "stay"), { once: true });
    dialog.showModal();
  });
}

function downloadFile(file) {
  const url = URL.createObjectURL(file.blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = file.name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1500);
}

const hearModes = [
  ["nbfm", "FMNB"],
  ["wbfm", "FMWB"],
  ["am", "AM"],
  ["usb", "SSB USB"],
  ["lsb", "SSB LSB"],
  ["raw", "Raw"],
  ["cw", "CW"],
  ["ft8", "FT8"],
  ["js8", "JS8"],
  ["olivia", "Olivia"],
  ["jtty", "JTTY"],
  ["varac", "VarAC"],
  ["dmr", "DMR"],
  ["m17", "M17"],
  ["packet", "Packet (AX.25)"]
];
let suggestedMode = "nbfm";
let listeningDmrKey = "";
let listeningWbfmProgram = "main";

function modeLabel(value) {
  return hearModes.find((item) => item[0] === value)?.[1] || modeToken(value);
}

function fillHearModes() {
  const hear = document.querySelector("#listen-mode");
  const current = hear.value || suggestedMode;
  hear.innerHTML = "";
  const add = (value, label) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    hear.append(option);
  };
  add(suggestedMode, `${modeLabel(suggestedMode)} · suggested`);
  for (const [value, label] of hearModes) {
    if (value !== suggestedMode) add(value, label);
  }
  hear.value = [...hear.options].some((option) => option.value === current) ? current : suggestedMode;
  syncModeExtraFields();
}

function dmrKey() {
  const color = document.querySelector("#dmr-color").value;
  const slot = document.querySelector("#dmr-slot").value;
  const talkgroup = document.querySelector("#dmr-tg").value.trim();
  return `${color}|${slot}|${talkgroup}`;
}

function wbfmProgram() {
  return document.querySelector("#wbfm-program")?.value || "main";
}

function syncModeExtraFields() {
  const hear = document.querySelector("#listen-mode").value;
  const scan = document.querySelector("#mode").value;
  document.querySelector("#dmr-fields").hidden = hear !== "dmr" && scan !== "dmr";
  const wbfm = hear === "wbfm" || scan === "wbfm" || listeningMode === "wbfm";
  document.querySelector("#wbfm-fields").hidden = !wbfm;
}

function syncDmrFields() {
  syncModeExtraFields();
}

function fillColorCodes() {
  for (const id of ["#dmr-color", "#camp-color"]) {
    const select = document.querySelector(id);
    for (let code = 0; code <= 15; code++) {
      const option = document.createElement("option");
      option.value = String(code);
      option.textContent = String(code);
      select.append(option);
    }
  }
}

async function listen(explicitMode) {
  const frequencyHz = Number(mhz.value) * 1e6;
  const hear = document.querySelector("#listen-mode").value;
  const sendMode = explicitMode || hear || mode.value;
  const nextDmr = dmrKey();
  const nextWbfm = wbfmProgram();
  const modeChanged = !!liveAbort && sendMode !== "auto" && sendMode !== listeningMode;
  const dmrChanged = !!liveAbort && (sendMode === "dmr" || listeningMode === "dmr") && nextDmr !== listeningDmrKey;
  const wbfmChanged = !!liveAbort && (sendMode === "wbfm" || listeningMode === "wbfm") && nextWbfm !== listeningWbfmProgram;
  const leaving = liveAbort && recording && Math.abs(frequencyHz - recording.frequencyHz) > 100;
  if ((modeChanged || dmrChanged || wbfmChanged) && recording && !leaving) {
    if (switching) return;
    switching = true;
    const epoch = listenToken;
    let choice = "stay";
    try {
      choice = await askDownload("Download the recording?", "Download the recording?");
      if (choice === "stay" || listenToken !== epoch) {
        const hear = document.querySelector("#listen-mode");
        hear.value = listeningMode || suggestedMode;
        if ([...mode.options].some((option) => option.value === hear.value)) mode.value = hear.value;
        const [color, slot, talkgroup] = listeningDmrKey.split("|");
        document.querySelector("#dmr-color").value = color || "";
        document.querySelector("#dmr-slot").value = slot || "";
        document.querySelector("#dmr-tg").value = talkgroup || "";
        document.querySelector("#wbfm-program").value = listeningWbfmProgram || "main";
        syncModeExtraFields();
        return;
      }
      await releaseRecording(await endRecording(choice !== "yes"), choice === "yes");
    } finally {
      switching = false;
    }
  }
  if (leaving) {
    if (switching) return;
    switching = true;
    const epoch = listenToken;
    let choice = "stay";
    try {
      choice = await askDownload("Leave this frequency?", "Download the recording before leaving this frequency?");
      if (choice === "yes" && listenToken === epoch) {
        await releaseRecording(await endRecording(), true);
      } else if (choice === "no" && listenToken === epoch) {
        await endRecording(true);
      }
    } finally {
      switching = false;
    }
    if (choice === "stay" || listenToken !== epoch) return;
  } else if (liveAbort && !modeChanged && !dmrChanged && !wbfmChanged && Math.abs(frequencyHz - (listeningHz ?? frequencyHz)) <= 100) {
    return;
  }

  const token = ++listenToken;
  stopLivePlayback();
  showError("");
  setBusy(true);
  const controller = new AbortController();
  liveAbort = controller;
  if (busyPanel?.open) busyPanel.close();
  listeningHz = frequencyHz;
  listeningMode = sendMode === "auto" ? listeningMode : sendMode;
  listeningDmrKey = nextDmr;
  listeningWbfmProgram = nextWbfm;
  applyRadioControls();
  syncSignalButtons();
  const params = new URLSearchParams({ frequencyHz: String(frequencyHz), mode: sendMode });
  if (sendMode === "dmr" || document.querySelector("#listen-mode").value === "dmr") {
    const color = document.querySelector("#dmr-color").value;
    const slot = document.querySelector("#dmr-slot").value;
    const talkgroup = document.querySelector("#dmr-tg").value.trim();
    if (color !== "") params.set("colorCode", color);
    if (slot !== "") params.set("slot", slot);
    if (talkgroup !== "") params.set("talkgroup", talkgroup);
  }
  if (sendMode === "wbfm" || document.querySelector("#listen-mode").value === "wbfm" || nextWbfm !== "main") {
    params.set("wbfmProgram", nextWbfm);
  }
  try {
    const response = await fetch(`/api/live?${params}`, { signal: controller.signal });
    if (token !== listenToken) return;
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error || response.statusText);
    }

    const rate = Number(response.headers.get("X-Audio-Rate")) || 48000;
    liveSampleRate = rate;
    const tuneHz = Number(response.headers.get("X-Tune-Hz"));
    if (Number.isFinite(tuneHz)) listeningHz = tuneHz;
    const reported = (response.headers.get("X-Mode") || sendMode).toLowerCase();
    if (reported) {
      listeningMode = reported;
      if (sendMode === "auto") {
        suggestedMode = reported;
        fillHearModes();
        document.querySelector("#listen-mode").value = reported;
      }
    }
    syncModeExtraFields();
    const program = (response.headers.get("X-Wbfm-Program") || nextWbfm || "main").toLowerCase();
    listeningWbfmProgram = program;
    if (document.querySelector("#wbfm-program")) document.querySelector("#wbfm-program").value = program;
    const channels = audioChannelsFromHeaders(response.headers, reported || sendMode, program);
    readout.textContent = `${mhzText(listeningHz)} MHz  ${modeLabel(listeningMode)}`;
    source.textContent = response.headers.get("X-Source") || "";
    liveState.hidden = false;
    liveStop.hidden = operatingMode === "camp";
    liveState.textContent = channels > 1
      ? "Playing stereo as the signal arrives."
      : "Playing as the signal arrives.";
    syncSignalButtons();
    paintFoldSummaries();

    const context = new AudioContext({ sampleRate: rate });
    liveAudio = context;
    ensureGain(context);
    await context.resume();
    await playPcmStream(response.body, context, rate, controller.signal, channels);
  } catch (ex) {
    if (ex.name !== "AbortError") showError(ex.message);
  } finally {
    if (token !== listenToken) return;
    const file = recording ? await endRecording() : null;
    stopLivePlayback();
    setBusy(false);
    refreshStatus();
    await offerRecording(file);
  }
}

function audioChannelsFromHeaders(headers, mode, program) {
  const raw = Number(headers.get("X-Audio-Channels"));
  if (Number.isFinite(raw) && raw >= 1)
    return Math.min(2, Math.max(1, Math.round(raw)));
  // Broadcast-FM main is always interleaved stereo from the host; never treat it as mono.
  if (String(mode || "").toLowerCase() === "wbfm" && String(program || "main").toLowerCase() === "main")
    return 2;
  return 1;
}

async function playPcmStream(body, context, rate, signal, channels = 1) {
  const ch = Math.max(1, Math.min(2, Math.round(Number(channels)) || 1));
  const sourceRate = Number(rate) || context.sampleRate || 48000;
  const playRate = context.sampleRate || sourceRate;
  const frameBytes = 2 * ch;
  const reader = body.getReader();
  let pending = new Uint8Array(0);
  let nextTime = context.currentTime + 0.15;
  let played = 0;
  while (!signal.aborted) {
    const { done, value } = await reader.read();
    if (done) break;
    pending = concatBytes(pending, value);
    const usable = pending.length - (pending.length % frameBytes);
    if (usable < frameBytes) continue;
    const frames = usable / frameBytes;
    const view = new DataView(pending.buffer, pending.byteOffset, usable);
    const source = [];
    for (let c = 0; c < ch; c++) source[c] = new Float32Array(frames);
    const pcm = recording ? new Int16Array(frames) : null;
    for (let i = 0; i < frames; i++) {
      let mix = 0;
      for (let c = 0; c < ch; c++) {
        const sample = view.getInt16((i * ch + c) * 2, true);
        source[c][i] = sample / 32768;
        mix += sample;
      }
      if (pcm) pcm[i] = (mix / ch) | 0;
    }
    if (pcm) appendRecording(pcm);

    let buffer;
    if (Math.abs(playRate - sourceRate) < 1) {
      buffer = context.createBuffer(ch, frames, playRate);
      for (let c = 0; c < ch; c++) buffer.copyToChannel(source[c], c);
    } else {
      // Resample into the AudioContext clock so pitch/speed stay correct across devices.
      const outFrames = Math.max(1, Math.round(frames * playRate / sourceRate));
      buffer = context.createBuffer(ch, outFrames, playRate);
      for (let c = 0; c < ch; c++) {
        const input = source[c];
        const output = buffer.getChannelData(c);
        for (let i = 0; i < outFrames; i++) {
          const at = i * (frames - 1) / Math.max(1, outFrames - 1);
          const i0 = Math.floor(at);
          const i1 = Math.min(frames - 1, i0 + 1);
          const frac = at - i0;
          output[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }
      }
    }

    const node = context.createBufferSource();
    node.buffer = buffer;
    node.connect(liveGain || context.destination);
    const start = Math.max(nextTime, context.currentTime + 0.02);
    node.start(start);
    nextTime = start + buffer.duration;
    played += buffer.duration;
    liveState.textContent = operatingMode === "camp" && !campActivity
      ? "Waiting for activity."
      : `Playing${ch > 1 ? " stereo" : ""} · ${played.toFixed(1)} s.`;
    pending = pending.subarray(usable);
  }
}

function concatBytes(left, right) {
  if (left.length === 0) return right;
  const merged = new Uint8Array(left.length + right.length);
  merged.set(left, 0);
  merged.set(right, left.length);
  return merged;
}

function ensureGain(context) {
  liveGain = context.createGain();
  liveGain.connect(context.destination);
  applyVolume();
  return liveGain;
}

function applyVolume() {
  const slider = document.querySelector("#volume");
  const level = Number(slider.value);
  if (liveGain) liveGain.gain.value = Number.isFinite(level) ? level / 100 : 0.8;
}

function loadVolume() {
  const raw = localStorage.getItem("agentic-sdr-volume");
  const saved = raw == null ? NaN : Number(raw);
  const slider = document.querySelector("#volume");
  if (Number.isFinite(saved) && saved >= 0 && saved <= 100) slider.value = String(saved);
  applyVolume();
}

function paintModeSwitch() {
  document.querySelector("#mode-explore").setAttribute("aria-pressed", String(operatingMode !== "camp"));
  document.querySelector("#mode-camp").setAttribute("aria-pressed", String(operatingMode === "camp"));
}

function paintWorkspace() {
  document.body.classList.toggle("workspace-camp", operatingMode === "camp");
  document.body.classList.toggle("workspace-explore", operatingMode !== "camp");
  const selectedTitle = document.querySelector('[data-fold="selected"] .fold-title');
  if (selectedTitle)
    selectedTitle.textContent = operatingMode === "camp" ? "Camp" : "Selected signal";
}

function campingActive(status) {
  if (status?.camp) return true;
  if (scheduleView?.active) return true;
  if (scheduleView?.now) return true;
  return false;
}

function paintCampControls(status) {
  const camping = campingActive(status);
  const go = document.querySelector("#camp-go");
  const stop = document.querySelector("#camp-stop");
  const policyRow = document.querySelector("#camp-policy-row");
  const snr = document.querySelector("#camp-snr");
  if (!go || !stop) return;
  go.hidden = operatingMode !== "camp" || camping;
  stop.hidden = operatingMode !== "camp" || !camping;
  if (policyRow) policyRow.hidden = !(operatingMode === "camp" && camping);
  if (snr) snr.hidden = !(operatingMode === "camp" && camping);
  if (operatingMode === "camp") {
    liveStop.hidden = true;
    if (camping)
      liveState.textContent = campActivity ? "Activity." : "Waiting for activity.";
  }
}

const snrMeterMin = -10;
const snrMeterMax = 40;

function squelchThresholdDb(squelch) {
  const level = Math.max(0, Math.min(100, Number(squelch) || 0));
  if (level <= 0) return -40;
  return 10 * Math.log10(1.5 * Math.pow(32, level / 100));
}

function paintCampSnr(status) {
  const meter = document.querySelector("#camp-snr");
  const fill = document.querySelector("#camp-snr-fill");
  const mark = document.querySelector("#camp-snr-mark");
  const label = document.querySelector("#camp-snr-label");
  if (!meter || !fill || !mark || !label) return;
  const camping = operatingMode === "camp" && campingActive(status);
  meter.hidden = !camping;
  if (!camping) {
    fill.style.width = "0%";
    label.textContent = "— dB";
    return;
  }

  const snr = Number(status.campSnrDb);
  const shown = Number.isFinite(snr) ? snr : snrMeterMin;
  const pct = Math.max(0, Math.min(100, ((shown - snrMeterMin) / (snrMeterMax - snrMeterMin)) * 100));
  fill.style.width = `${pct}%`;
  fill.classList.toggle("open", !!status.campActivity);
  label.textContent = Number.isFinite(snr) ? `${shown.toFixed(0)} dB` : "— dB";

  const threshold = squelchThresholdDb(document.querySelector("#camp-squelch")?.value);
  const markPct = Math.max(0, Math.min(100, ((threshold - snrMeterMin) / (snrMeterMax - snrMeterMin)) * 100));
  mark.style.left = `${markPct}%`;
}

function listenCampBody(frequencyHz, heard) {
  const body = { frequencyHz, mode: heard || "nbfm" };
  if (body.mode === "dmr") {
    const color = document.querySelector("#dmr-color").value;
    const slot = document.querySelector("#dmr-slot").value;
    const talkgroup = document.querySelector("#dmr-tg").value.trim();
    if (color !== "") body.colorCode = Number(color);
    if (slot !== "") body.slot = Number(slot);
    if (talkgroup !== "") body.talkgroup = Number(talkgroup);
  }
  return body;
}

async function startCamp() {
  const frequencyHz = campFrequencyHz();
  const heard = document.querySelector("#listen-mode").value || "nbfm";
  if (heard === "auto") {
    showError("Pick a mode to camp on. Camp does not guess.");
    return;
  }
  showError("");
  const view = await api("/api/camp/now", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(listenCampBody(frequencyHz, heard))
  });
  renderSchedule(view);
  paintCampControls({ camp: view.active ? { frequencyHz: view.active.frequencyHz } : null });
}

async function stopCamping() {
  listenToken++;
  campPlayKey = "";
  const file = recording ? await endRecording() : null;
  stopLivePlayback();
  setBusy(false);
  try {
    await api("/api/camp/now", { method: "DELETE" });
  } catch { /* Clearing an empty camp is fine. */ }
  try {
    await api("/api/camp/pause", { method: "POST" });
  } catch { /* Pause keeps a due schedule from taking the radio again. */ }
  await api("/api/stop", { method: "POST" });
  const view = await api("/api/camp");
  renderSchedule(view);
  paintCampControls(null);
  refreshStatus();
  await offerRecording(file);
}

function followCamp(status) {
  const tune = status.operatingMode === "camp" ? status.camp : null;
  if (!tune) {
    if (campPlayKey) {
      campPlayKey = "";
      listenToken += 1;
      stopLivePlayback();
    }
    return;
  }
  if (campConnecting) return;
  const key = `${tune.frequencyHz}|${tune.mode}`;
  if (key === campPlayKey && liveAbort) return;
  campPlayKey = key;
  campConnecting = true;
  playCamp(tune).finally(() => {
    campConnecting = false;
  });
}

async function playCamp(tune) {
  const token = ++listenToken;
  const pendingFile = recording ? endRecording() : Promise.resolve(null);
  stopLivePlayback();
  if (token !== listenToken) return;
  const controller = new AbortController();
  liveAbort = controller;
  if (busyPanel?.open) busyPanel.close();
  listeningHz = tune.frequencyHz;
  listeningMode = tune.mode;
  applyRadioControls();
  syncSignalButtons();
  let missing = false;
  try {
    const file = await pendingFile;
    if (token !== listenToken) {
      if (file) offerRecording(file);
      return;
    }
    if (file) await offerRecording(file);
    if (token !== listenToken) return;
    const response = await fetch("/api/camp/audio", { signal: controller.signal });
    if (token !== listenToken) return;
    if (response.status === 404) {
      missing = true;
      campPlayKey = "";
      return;
    }
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error || response.statusText);
    }
    const rate = Number(response.headers.get("X-Audio-Rate")) || tune.sampleRate || 48000;
    const channels = audioChannelsFromHeaders(response.headers, tune.mode, "main");
    liveSampleRate = rate;
    readout.textContent = `${mhzText(tune.frequencyHz)} MHz  ${modeLabel(tune.mode)}`;
    syncTuneFrequency(mhzText(tune.frequencyHz));
    if ([...document.querySelector("#listen-mode").options].some((option) => option.value === tune.mode))
      document.querySelector("#listen-mode").value = tune.mode;
    syncModeExtraFields();
    liveState.hidden = false;
    liveStop.hidden = true;
    liveState.textContent = campActivity ? "Activity." : "Waiting for activity.";
    paintCampControls({ camp: tune });
    paintFoldSummaries();
    setBusy(true);
    const context = new AudioContext({ sampleRate: rate });
    liveAudio = context;
    ensureGain(context);
    await context.resume();
    await playPcmStream(response.body, context, rate, controller.signal, channels);
  } catch (ex) {
    if (ex.name !== "AbortError" && token === listenToken) showError(ex.message);
  } finally {
    if (token !== listenToken) return;
    if (missing) {
      if (liveAbort === controller) liveAbort = null;
      setBusy(false);
      return;
    }
    const file = recording ? await endRecording() : null;
    stopLivePlayback();
    campPlayKey = "";
    setBusy(false);
    await offerRecording(file);
  }
}

antennaOrientation.addEventListener("change", syncAzimuth);
for (const id of ["#antenna-length", "#antenna-unit", "#antenna-azimuth", "#radio", "#band", "#mhz", "#camp-freq", "#mode", "#llm-model", "#llm-base"]) {
  document.querySelector(id).addEventListener("input", paintFoldSummaries);
  document.querySelector(id).addEventListener("change", paintFoldSummaries);
}
mhz.addEventListener("input", () => syncTuneFrequency(mhz.value));
campFreq?.addEventListener("input", () => syncTuneFrequency(campFreq.value));
document.querySelector("#antenna-apply").addEventListener("click", () => {
  applyAntenna().catch((ex) => showError(ex.message));
});
document.querySelector("#antenna-guess").addEventListener("click", guessAntenna);
document.querySelector("#radio-start").addEventListener("click", startRadio);
document.querySelector("#radio-stop").addEventListener("click", () => {
  stopRadio().catch((ex) => showError(ex.message));
});
radioSelect.addEventListener("change", () => {
  const details = document.querySelector("#radio-details-panel");
  if (details.open) details.close();
  applyRadioControls();
});
document.querySelector("#radio-details").addEventListener("click", () => {
  showRadioDetails().catch((ex) => showError(ex.message));
});
document.querySelector("#radio-details-close").addEventListener("click", () => {
  document.querySelector("#radio-details-panel").close();
});
radioSelect.addEventListener("focus", () => {
  loadRadios().catch((ex) => showError(ex.message));
});
document.querySelector("#scan").addEventListener("click", scan);
document.querySelector("#listen").addEventListener("click", () => {
  listen().catch((ex) => showError(ex.message));
});
document.querySelector("#mode").addEventListener("change", syncDmrFields);
document.querySelector("#listen-mode").addEventListener("change", () => {
  const hear = document.querySelector("#listen-mode").value;
  if ([...mode.options].some((option) => option.value === hear)) mode.value = hear;
  syncDmrFields();
  paintFoldSummaries();
  if (operatingMode === "camp") {
    if (scheduleView?.now || scheduleView?.active)
      startCamp().catch((ex) => showError(ex.message));
    return;
  }
  if (liveAbort) listen(hear).catch((ex) => showError(ex.message));
});
for (const id of ["#dmr-color", "#dmr-slot", "#dmr-tg"]) {
  document.querySelector(id).addEventListener("change", () => {
    if (operatingMode === "camp") {
      if (document.querySelector("#listen-mode").value === "dmr"
          && (scheduleView?.now || scheduleView?.active))
        startCamp().catch((ex) => showError(ex.message));
      return;
    }
    if (document.querySelector("#listen-mode").value === "dmr" && liveAbort)
      listen("dmr").catch((ex) => showError(ex.message));
  });
}
document.querySelector("#wbfm-program").addEventListener("change", () => {
  if (operatingMode === "camp") return;
  if (liveAbort) listen().catch((ex) => showError(ex.message));
});
document.querySelector("#record").addEventListener("click", () => {
  if (recordingStarting) return;
  if (recording) {
    endRecording()
      .then((file) => {
        if (!file) {
          showError("The recording was too short to save.");
          return;
        }
        return offerRecording(file);
      })
      .catch((ex) => showError(ex.message));
    return;
  }
  if (!liveAbort) {
    showError("Listen to a signal before recording.");
    return;
  }
  recordingStarting = true;
  startRecording()
    .catch((ex) => showError(ex.message))
    .finally(() => {
      recordingStarting = false;
    });
});
document.querySelector("#record-transcript").addEventListener("click", () => {
  if (transcriptSpans.length === 0) return;
  downloadFile(transcriptFile());
});
document.querySelector("#leave-yes").addEventListener("click", () => {
  document.querySelector("#leave-recording").close("yes");
});
document.querySelector("#leave-no").addEventListener("click", () => {
  document.querySelector("#leave-recording").close("no");
});
document.querySelector("#busy-stop")?.addEventListener("click", () => {
  stopWork().catch((ex) => showError(ex.message));
});
busyPanel?.addEventListener("cancel", (event) => {
  event.preventDefault();
  stopWork().catch((ex) => showError(ex.message));
});
async function stopWork() {
  listenToken++;
  campPlayKey = "";
  const file = recording ? await endRecording() : null;
  if (busyPanel?.open) busyPanel.close();
  closePrompt();
  stopLivePlayback();
  agentAbort?.abort();
  setBusy(false);
  if (operatingMode === "camp") {
    try { await api("/api/camp/pause", { method: "POST" }); } catch { /* The watch also stops when the page disconnects. */ }
  }
  await api("/api/stop", { method: "POST" });
  refreshStatus();
  await offerRecording(file);
}
window.addEventListener("pagehide", () => {
  const id = recording?.id || recordingFileId;
  if (id) navigator.sendBeacon(`/api/recording/${id}/discard`, "");
});
document.querySelector("#stop").addEventListener("click", stopWork);
liveStop.addEventListener("click", stopWork);
document.querySelector("#llm-save").addEventListener("click", () => {
  saveModel(false).catch((ex) => showError(ex.message));
});
document.querySelector("#llm-forget").addEventListener("click", () => {
  saveModel(true).catch((ex) => showError(ex.message));
});
document.querySelector("#llm-models-btn").addEventListener("click", () => {
  listModels().catch((ex) => showError(ex.message));
});
document.querySelector("#prompt-form").addEventListener("submit", (event) => {
  event.preventDefault();
  askModel().catch((ex) => showError(ex.message));
});
document.querySelector("#llm-ask").addEventListener("keydown", (event) => {
  if (event.key === "Enter" && !event.shiftKey) {
    event.preventDefault();
    askModel().catch((ex) => showError(ex.message));
  }
});
document.querySelector("#noaa").addEventListener("click", () => {
  syncTuneFrequency("162.550000");
  mode.value = "nbfm";
  listen().catch((ex) => showError(ex.message));
});

restoreAntenna()
  .then(loadBands)
  .then(loadKnownChannels)
  .then(loadRadios)
  .then(loadModel)
  .catch((ex) => showError(ex.message));

async function loadModel() {
  const view = await api("/api/llm");
  document.querySelector("#llm-base").value = view.baseUrl || "";
  document.querySelector("#llm-model").value = view.model || "";
  document.querySelector("#llm-key").value = "";
  document.querySelector("#llm-key").placeholder = view.hasKey
    ? `Saved (ends ${view.keyHint}). Leave blank to keep it.`
    : "Optional for a local server";
  document.querySelector("#llm-note").textContent = view.baseUrl
    ? `Using ${view.model || "a model"} at ${view.baseUrl}.`
    : "An instruct model that speaks the OpenAI chat protocol can run the same actions as these buttons. Ask it in the prompt below.";
  paintFoldSummaries();
}

async function saveModel(clearKey) {
  showError("");
  const view = await api("/api/llm", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      baseUrl: document.querySelector("#llm-base").value,
      model: document.querySelector("#llm-model").value,
      apiKey: clearKey ? "" : document.querySelector("#llm-key").value,
      clearKey
    })
  });
  document.querySelector("#llm-key").value = "";
  document.querySelector("#llm-key").placeholder = view.hasKey
    ? `Saved (ends ${view.keyHint}). Leave blank to keep it.`
    : "Optional for a local server";
  document.querySelector("#llm-note").textContent = view.baseUrl
    ? `Saved ${view.model || "the model"} at ${view.baseUrl}.`
    : "The endpoint was cleared.";
  paintFoldSummaries();
}

async function listModels() {
  showError("");
  await saveModel(false);
  const listed = await api("/api/llm/models");
  const names = listed || [];
  const datalist = document.querySelector("#llm-models");
  const picks = document.querySelector("#llm-model-picks");
  const modelInput = document.querySelector("#llm-model");
  datalist.innerHTML = "";
  picks.innerHTML = "";
  picks.hidden = names.length === 0;
  for (const name of names) {
    const option = document.createElement("option");
    option.value = name;
    datalist.append(option);
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = name;
    button.classList.toggle("active", modelInput.value === name);
    button.addEventListener("click", () => {
      modelInput.value = name;
      for (const peer of picks.querySelectorAll("button"))
        peer.classList.toggle("active", peer === button);
      saveModel(false).catch((ex) => showError(ex.message));
    });
    picks.append(button);
  }
  if (names.length && !modelInput.value.trim()) {
    modelInput.value = names[0];
    picks.querySelector("button")?.classList.add("active");
    await saveModel(false);
  }
  document.querySelector("#llm-note").textContent = names.length
    ? `Found ${names.length} model${names.length === 1 ? "" : "s"}. Click one to use it.`
    : "The endpoint listed no models.";
  paintFoldSummaries();
}

function addTurn(kind, who, text, steps) {
  document.querySelector("#prompt-empty").hidden = true;
  const turn = document.createElement("article");
  turn.className = `turn ${kind}`;
  const label = document.createElement("p");
  label.className = "who";
  label.textContent = who;
  turn.append(label);
  if (steps) {
    const detail = document.createElement("p");
    detail.className = "steps";
    detail.textContent = steps;
    turn.append(detail);
  }
  const body = document.createElement("p");
  body.textContent = text;
  turn.append(body);
  const log = document.querySelector("#prompt-log");
  log.append(turn);
  log.scrollTop = log.scrollHeight;
  paintFoldSummaries();
  return turn;
}

async function askModel() {
  const box = document.querySelector("#llm-ask");
  const question = box.value.trim();
  if (!question) return;
  if (busy) {
    showError("The radio is busy. Press Stop, then send the prompt again.");
    return;
  }
  showError("");
  const asked = addTurn("user", "You", question);
  const pending = addTurn("agent", "Agent", "Working on it…");
  asked.scrollIntoView({ block: "nearest" });
  box.value = "";
  setBusy(true);
  agentAbort = new AbortController();
  try {
    const reply = await api("/api/agent", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ message: question, history: agentHistory }),
      signal: agentAbort.signal
    });
    agentHistory.push({ role: "user", content: question });
    agentHistory.push({ role: "assistant", content: reply.answer });
    while (agentHistory.length > 8) agentHistory.shift();
    const steps = (reply.actions || []).map((action) => `${action.tool}: ${action.result}`).join("\n");
    pending.querySelector("p:last-child").textContent = reply.answer;
    paintFoldSummaries();
    if (steps) {
      const detail = document.createElement("p");
      detail.className = "steps";
      detail.textContent = steps;
      pending.querySelector(".who").after(detail);
    }
    if (reply.antenna) {
      antennaUnit.value = "m";
      antennaLength.value = reply.antenna.lengthMeters;
      antennaOrientation.value = reply.antenna.orientation;
      if (reply.antenna.azimuthDegrees != null)
        antennaAzimuth.value = String(Math.round(reply.antenna.azimuthDegrees));
      syncAzimuth();
      antennaNote.textContent = reply.antenna.summary;
      await loadBands();
      paintFoldSummaries();
    }
    if (reply.scan) renderHits(reply.scan);
  } catch (ex) {
    const reason = ex.name === "AbortError" ? "Stopped." : ex.message;
    pending.querySelector("p:last-child").textContent = reason;
    paintFoldSummaries();
    box.value = question;
    showError(reason);
  } finally {
    agentAbort = null;
    setBusy(false);
    refreshStatus();
  }
}

async function restoreAntenna() {
  const advice = await api("/api/antenna");
  if (!advice.configured) return;
  antennaUnit.value = "m";
  antennaLength.value = advice.lengthMeters;
  antennaOrientation.value = advice.orientation;
  if (advice.azimuthDegrees != null) antennaAzimuth.value = String(Math.round(advice.azimuthDegrees));
  syncAzimuth();
  antennaNote.textContent = advice.summary;
  paintFoldSummaries();
}
function rememberFolds() {
  const folds = document.querySelectorAll("details.fold");
  let saved = {};
  try {
    saved = JSON.parse(localStorage.getItem("agentic-sdr-folds") || "{}");
  } catch {
    saved = {};
  }
  folds.forEach((details) => {
    const id = details.dataset.fold;
    if (id && Object.hasOwn(saved, id)) details.open = saved[id];
    details.addEventListener("toggle", () => {
      const state = {};
      folds.forEach((item) => {
        if (item.dataset.fold) state[item.dataset.fold] = item.open;
      });
      try {
        localStorage.setItem("agentic-sdr-folds", JSON.stringify(state));
      } catch {
        /* The sections still collapse when the browser will not store the choice. */
      }
    });
  });
}
function wiki(page, label) {
  return `<a href="https://en.wikipedia.org/wiki/${page}" target="_blank" rel="noopener noreferrer">${label}</a>`;
}

const tips = {
  receive: "This program only receives. It does not transmit.",
  "device-name": "The radio that is open.",
  status: "What the radio is doing right now. While a scan or listen runs, a panel explains the step and shows progress.",
  radio: "Open or close the USB radio this page tunes.",
  device: `The connected ${wiki("Software-defined_radio", "software-defined radio")}. It captures a slice of spectrum, and this program decides what the signal is.`,
  start: "Opens the selected radio.",
  "stop-radio": "Releases the radio. A scan that is running stops too.",
  details: "Shows the tuning range, sample rate, and versions.",
  antenna: "How long the antenna is, and which way it points. Matching bands move to the top.",
  length: `A vertical antenna is treated as a quarter-wave ${wiki("Monopole_antenna", "monopole")}. A horizontal wire is treated as a half-wave ${wiki("Dipole_antenna", "dipole")}.`,
  unit: "Meters or feet. The length is converted when the antenna is saved.",
  orientation: `Vertical stands up. Horizontal runs sideways. See ${wiki("Monopole_antenna", "monopole")} and ${wiki("Dipole_antenna", "dipole")}.`,
  azimuth: `Degrees clockwise from true north for a horizontal wire. 0 is north and 90 is east. The wire hears best at right angles to its ${wiki("Azimuth", "azimuth")}.`,
  apply: "Saves this length and orientation. Bands near that length move to the top.",
  guess: "Listens at several spots and treats the loudest noise floor as the antenna’s natural frequency. Strength does not show which way a wire points.",
  model: `An ${wiki("Large_language_model", "instruct model")} that can run the same actions as these buttons.`,
  endpoint: "OpenAI-compatible chat root. For Ollama you can enter http://host:11434; /v1 is added for you. Cloud APIs usually end in /v1.",
  "model-name": "The model name on that server. List models fills this from the endpoint.",
  key: "Stored on this computer and sent only to that endpoint. Ollama usually needs none.",
  "save-model": "Stores the endpoint, model, and key without listing.",
  "list-models": "Saves the endpoint, then asks it for models (OpenAI /v1/models, then Ollama /api/tags).",
  forget: "Clears the saved API key.",
  prompt: "Ask the model to scan, listen, or change the antenna. Stop cancels that work.",
  ask: "The sentence for the model, such as “Scan 2 m and tell me what you hear.”",
  send: "Sends that sentence. The model asks this program to do the work.",
  tune: "Pick a band or a frequency and listen.",
  band: `A named range of ${wiki("Radio_frequency", "frequencies")}. Amateur bands follow the ${wiki("Amateur_radio_frequency_allocations", "amateur allocations")}. Below 24 MHz on an RTL-SDR the tuner steps aside and the chip samples the antenna more directly (${wiki("High_frequency", "HF")}). Also listed: ${wiki("Citizens_band_radio", "CB")}, ${wiki("General_Mobile_Radio_Service", "GMRS")}, ${wiki("Digital_mobile_radio", "DMR")}, and ${wiki("NOAA_Weather_Radio", "NOAA weather")}.`,
  scan: `Hops across the band and lists peaks that stand out from the noise. A steady whistle with no voice or data is skipped as a ${wiki("Spurious_emission", "local spur")}.`,
  stop: "Stops a scan, a listen, or a model request.",
  freq: `The frequency to tune, in megahertz. 146.520 is the 2 m FM calling spot. See ${wiki("Radio_frequency", "radio frequency")}.`,
  mode: `How the voice or data was put on the wave. Auto guesses. ${wiki("Amplitude_modulation", "AM")}, ${wiki("Frequency_modulation", "narrow and wide FM")}, ${wiki("Single-sideband_modulation", "USB and LSB")}, ${wiki("Continuous_wave", "CW")} Morse, ${wiki("FT8", "FT8")}, ${wiki("Digital_mobile_radio", "DMR")}, ${wiki("M17_(amateur_radio)", "M17")}, and ${wiki("Packet_radio", "packet radio (AX.25 / APRS)")}.`,
  hear: "The mode to play this signal as. The suggested mode is first. Changing it while a recording is running stops that recording and asks whether to download it.",
  "dmr-color": "The DMR color code, 0 through 15. Any accepts every color code on the channel.",
  "dmr-slot": "DMR timeslot 1 or 2. Any accepts both slots.",
  "dmr-tg": "The talkgroup to write down. Leave this blank to accept every talkgroup.",
  "wbfm-program": `Broadcast FM main program (stereo when a ${wiki("FM_broadcasting", "19 kHz pilot")} is present), or an ${wiki("Subsidiary_communications_authority", "SCA")} secondary subcarrier at 67 or 92 kHz. RDS station text appears in Decoded text.`,
  listen: "Tunes and plays as the signal arrives.",
  "camp-go": "Start camping on this frequency and mode. The speaker stays quiet until the channel is active.",
  "camp-stop": "Stop camping. A due schedule stays paused until you Camp again or leave and return to Camp.",
  hear: "Mode to demodulate. Camp needs an explicit mode; it does not guess.",
  freq: "Frequency to listen to or camp on.",
  noaa: `Listens to one ${wiki("NOAA_Weather_Radio", "NOAA weather radio")} channel so you can tell the radio is working.`,
  "known-channels": `Fixed ${wiki("Citizens_band_radio", "CB")}, ${wiki("General_Mobile_Radio_Service", "GMRS")}, ${wiki("NOAA_Weather_Radio", "NOAA weather")}, and ${wiki("FT8", "FT8")} dial frequencies. Start with NOAA to check a new SDR. Open FT8 and click a dial such as 10 m · 28.074 when you want that mode without scanning.`,
  signals: "Peaks from the last scan.",
  "col-mhz": `Frequency in megahertz. See ${wiki("Radio_frequency", "radio frequency")}.`,
  "col-mode": "The kind of signal the scan guessed.",
  "col-conf": "How sure that mode guess is. Listening is the check.",
  "col-khz": `How wide the signal is, in kilohertz. See ${wiki("Bandwidth_(signal_processing)", "bandwidth")}.`,
  "col-snr": `How far the signal stands above the noise, in decibels. Bigger is cleaner. See ${wiki("Signal-to-noise_ratio", "signal-to-noise ratio")}.`,
  "col-source": `A ${wiki("Band_plan", "band plan")} label, such as a calling frequency or “FM broadcast.” It is not a station’s name.`,
  selected: "The frequency you are hearing or camping on, with live audio and decoded text.",
  record: "Records the audio you are hearing. Stopping asks whether to download the MP3, then the temporary file is removed.",
  transcript: `Downloads ${wiki("Morse_code", "Morse")}, ${wiki("FT8", "FT8")}, ${wiki("Radio_Data_System", "RDS")}, and ${wiki("Speech_recognition", "speech")} text as XML, with the time and which decoder wrote each line.`,
  decoded: `Letters from ${wiki("Morse_code", "Morse")}, messages from ${wiki("FT8", "FT8")} / JS8 / Olivia / JTTY / VarAC (xara/zara monitor), ${wiki("Speech_recognition", "speech")}, DMR talkgroup / radio ID / alias text, and broadcast-FM ${wiki("Radio_Data_System", "RDS")} name and radio text.`,
  "operating-mode": "Explore scans and listens with the audio on. Camp is the camping workspace: camping starts when you press Camp, or when a schedule window is due.",
  volume: "How loud this page plays the radio. Camping stays silent until there is activity, then uses this level.",
  squelch: "How strong a station must be before Camp opens the speaker. Low is open; high stays quiet until a clear signal. The mark on the SNR meter is this threshold.",
  "camp-snr": "Live signal-to-noise on the camped channel versus nearby empty channels. The speaker opens when the bar crosses the squelch mark.",
  schedule: "Optional times to camp on. A due window can start camping while you are in Camp. You can also press Camp on a frequency and leave this empty.",
  "schedule-label": "A short name for this watch, such as NOAA.",
  "schedule-freq": "The frequency to tune when this entry is on.",
  "schedule-mode": "The mode to hear that frequency as. Camp does not guess.",
  "schedule-start": "Local start time. Leave this and the end empty to listen only when nothing else is scheduled.",
  "schedule-end": "Local end time. An end earlier than the start continues past midnight.",
  "schedule-days": "Leave every day unchecked to use this entry every day.",
  "schedule-save": "Saves this entry. A time that overlaps another entry is refused.",
  "schedule-policy": "While camping on a frequency you chose with Camp: stay there when a schedule starts, or switch to that schedule."
};
function placeTip(target) {
  const text = tips[target.dataset.tip];
  const tip = document.querySelector("#tip");
  if (!text || !tip) return;
  tip.innerHTML = text;
  tip.hidden = false;
  const box = target.getBoundingClientRect();
  const width = tip.offsetWidth;
  const height = tip.offsetHeight;
  let left = Math.min(Math.max(8, box.left), window.innerWidth - width - 8);
  let top = box.bottom + 8;
  if (top + height > window.innerHeight - 8) top = Math.max(8, box.top - height - 8);
  tip.style.left = `${left}px`;
  tip.style.top = `${top}px`;
}

function bindTips() {
  const tip = document.querySelector("#tip");
  if (!tip) return;
  let hideTimer = 0;
  const hideSoon = () => {
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => {
      tip.hidden = true;
    }, 160);
  };
  const show = (target) => {
    if (!target || !tips[target.dataset.tip]) return;
    clearTimeout(hideTimer);
    placeTip(target);
  };
  document.addEventListener("mouseover", (event) => {
    const target = event.target.closest("[data-tip]");
    if (target) show(target);
    else if (event.target.closest("#tip")) clearTimeout(hideTimer);
  });
  document.addEventListener("mouseout", (event) => {
    const leaving = event.target.closest("[data-tip], #tip");
    const next = event.relatedTarget && event.relatedTarget.closest ? event.relatedTarget.closest("[data-tip], #tip") : null;
    if (leaving && leaving !== next) hideSoon();
  });
  document.addEventListener("focusin", (event) => show(event.target.closest("[data-tip]")));
  document.addEventListener("focusout", (event) => {
    const next = event.relatedTarget;
    if (next && next.closest && next.closest("[data-tip], #tip")) return;
    hideSoon();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") tip.hidden = true;
  });
  window.addEventListener("scroll", () => {
    tip.hidden = true;
  }, true);
}

function dayNames(days) {
  const names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  if (!days || days.length === 0 || days.length === 7) return "Every day";
  return days.map((day) => names[day] || "").filter(Boolean).join(" ");
}

function whenText(entry) {
  if (!entry.start) return "Any time";
  return `${entry.start}–${entry.end} · ${dayNames(entry.days)}`;
}

function renderSchedule(view) {
  scheduleView = view;
  if (view?.mode) operatingMode = view.mode === "camp" ? "camp" : "explore";
  paintModeSwitch();
  paintWorkspace();
  paintCampControls(view?.active ? { camp: { frequencyHz: view.active.frequencyHz } } : null);
  const note = document.querySelector("#schedule-note");
  note.hidden = !view?.note;
  note.textContent = view?.note || "";
  const policy = document.querySelector("#camp-policy");
  if (policy) policy.value = view?.yieldToSchedule ? "yield" : "hold";
  const squelch = document.querySelector("#camp-squelch");
  if (squelch && view && Number.isFinite(view.squelch) && document.activeElement !== squelch)
    squelch.value = String(view.squelch);
  const now = document.querySelector("#camp-now");
  const actions = document.querySelector("#camp-actions");
  const follow = document.querySelector("#camp-follow");
  const ignore = document.querySelector("#camp-ignore");
  const inCamp = operatingMode === "camp";
  if (inCamp && view?.now) {
    now.hidden = false;
    actions.hidden = false;
    const activeIsNow = view.active && view.active.id === "now";
    now.textContent = activeIsNow
      ? `Camping on ${mhzText(view.now.frequencyHz)} MHz ${modeLabel(view.now.mode)}`
      : `Returns to ${mhzText(view.now.frequencyHz)} MHz ${modeLabel(view.now.mode)} after the schedule`;
    follow.hidden = !view.due;
    ignore.hidden = !(view.active && view.active.id !== "now" && view.now);
  } else {
    now.hidden = true;
    actions.hidden = !(inCamp && (view?.due || view?.active || view?.now));
    follow.hidden = !(inCamp && view?.due);
    ignore.hidden = true;
  }
  const rows = document.querySelector("#schedule-rows");
  rows.innerHTML = "";
  const entries = view?.entries || [];
  document.querySelector("#schedule-empty").hidden = entries.length > 0;
  for (const entry of entries) {
    const row = document.createElement("tr");
    if (view.active && view.active.id === entry.id) row.className = "camp-on";
    row.innerHTML = `<td>${whenText(entry)}</td><td class="num">${mhzText(entry.frequencyHz)}</td><td>${modeLabel(entry.mode)}</td><td>${entry.label || ""}</td><td></td>`;
    const actions = row.lastElementChild;
    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "ghost";
    edit.textContent = "Edit";
    edit.addEventListener("click", () => fillScheduleForm(entry));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "ghost";
    remove.textContent = "Remove";
    remove.addEventListener("click", () => {
      api(`/api/camp/entries/${entry.id}`, { method: "DELETE" })
        .then((view) => {
          showError("");
          renderSchedule(view);
        })
        .catch((ex) => showError(ex.message));
    });
    actions.append(edit, document.createTextNode(" "), remove);
    rows.append(row);
  }
  paintFoldSummaries();
}

function fillScheduleForm(entry) {
  editingId = entry.id;
  document.querySelector("#camp-label").value = entry.label || "";
  document.querySelector("#camp-mhz").value = mhzText(entry.frequencyHz);
  document.querySelector("#camp-mode").value = entry.mode;
  document.querySelector("#camp-start").value = entry.start || "";
  document.querySelector("#camp-end").value = entry.end || "";
  document.querySelectorAll("#schedule-form .day-row input").forEach((box) => {
    box.checked = Array.isArray(entry.days) && entry.days.includes(Number(box.value));
  });
  document.querySelector("#camp-color").value = entry.colorCode == null ? "" : String(entry.colorCode);
  document.querySelector("#camp-slot").value = entry.slot == null ? "" : String(entry.slot);
  document.querySelector("#camp-tg").value = entry.talkgroup == null ? "" : String(entry.talkgroup);
  syncCampDmr();
  document.querySelector("#camp-save").textContent = "Save";
  document.querySelector("#camp-cancel").hidden = false;
}

function clearScheduleForm() {
  editingId = null;
  document.querySelector("#schedule-form").reset();
  document.querySelector("#camp-mhz").value = "162.550000";
  document.querySelector("#camp-mode").value = "nbfm";
  syncCampDmr();
  document.querySelector("#camp-save").textContent = "Add to schedule";
  document.querySelector("#camp-cancel").hidden = true;
}

function syncCampDmr() {
  document.querySelector("#camp-dmr").hidden = document.querySelector("#camp-mode").value !== "dmr";
}

function scheduleDraft() {
  const frequencyHz = Number(document.querySelector("#camp-mhz").value) * 1e6;
  const days = [...document.querySelectorAll("#schedule-form .day-row input:checked")].map((box) => Number(box.value));
  const body = {
    label: document.querySelector("#camp-label").value.trim(),
    frequencyHz,
    mode: document.querySelector("#camp-mode").value,
    start: document.querySelector("#camp-start").value,
    end: document.querySelector("#camp-end").value,
    days: days.length ? days : null
  };
  if (body.mode === "dmr") {
    const color = document.querySelector("#camp-color").value;
    const slot = document.querySelector("#camp-slot").value;
    const talkgroup = document.querySelector("#camp-tg").value.trim();
    if (color !== "") body.colorCode = Number(color);
    if (slot !== "") body.slot = Number(slot);
    if (talkgroup !== "") body.talkgroup = Number(talkgroup);
  }
  return body;
}

async function chooseMode(mode) {
  const view = await api("/api/camp/mode", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ mode })
  });
  showError("");
  renderSchedule(view);
  if (mode === "explore") {
    listenToken++;
    campPlayKey = "";
    stopLivePlayback();
    await api("/api/stop", { method: "POST" }).catch(() => {});
  }
  refreshStatus();
}

document.querySelector("#mode-explore").addEventListener("click", () => {
  chooseMode("explore").catch((ex) => showError(ex.message));
});
document.querySelector("#mode-camp").addEventListener("click", () => {
  chooseMode("camp").catch((ex) => showError(ex.message));
});
document.querySelector("#camp-go").addEventListener("click", () => {
  startCamp().catch((ex) => showError(ex.message));
});
document.querySelector("#camp-stop").addEventListener("click", () => {
  stopCamping().catch((ex) => showError(ex.message));
});
document.querySelector("#volume").addEventListener("input", () => {
  try { localStorage.setItem("agentic-sdr-volume", document.querySelector("#volume").value); } catch { /* The slider still changes the level for this visit. */ }
  applyVolume();
});
document.querySelector("#camp-squelch").addEventListener("input", () => {
  const value = Number(document.querySelector("#camp-squelch").value);
  const mark = document.querySelector("#camp-snr-mark");
  if (mark) {
    const threshold = squelchThresholdDb(value);
    const markPct = Math.max(0, Math.min(100, ((threshold - snrMeterMin) / (snrMeterMax - snrMeterMin)) * 100));
    mark.style.left = `${markPct}%`;
  }
  api("/api/camp/squelch", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ squelch: value })
  }).then((view) => {
    showError("");
    renderSchedule(view);
  }).catch((ex) => showError(ex.message));
});
document.querySelector("#camp-mode").addEventListener("change", syncCampDmr);
document.querySelector("#camp-cancel").addEventListener("click", clearScheduleForm);
document.querySelector("#camp-clear").addEventListener("click", () => {
  api("/api/camp/now", { method: "DELETE" })
    .then((view) => {
      showError("");
      renderSchedule(view);
    })
    .catch((ex) => showError(ex.message));
});
document.querySelector("#camp-policy").addEventListener("change", () => {
  const yieldToSchedule = document.querySelector("#camp-policy").value === "yield";
  api("/api/camp/policy", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ yieldToSchedule })
  }).then((view) => {
    showError("");
    renderSchedule(view);
  }).catch((ex) => showError(ex.message));
});
document.querySelector("#camp-follow").addEventListener("click", () => {
  api("/api/camp/follow-schedule", { method: "POST" })
    .then((view) => {
      showError("");
      renderSchedule(view);
    })
    .catch((ex) => showError(ex.message));
});
document.querySelector("#camp-ignore").addEventListener("click", () => {
  api("/api/camp/ignore-schedule", { method: "POST" })
    .then((view) => {
      showError("");
      renderSchedule(view);
    })
    .catch((ex) => showError(ex.message));
});
document.querySelector("#schedule-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const path = editingId ? `/api/camp/entries/${editingId}` : "/api/camp/entries";
  api(path, {
    method: editingId ? "PUT" : "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(scheduleDraft())
  }).then((view) => {
    showError("");
    clearScheduleForm();
    renderSchedule(view);
  }).catch((ex) => showError(ex.message));
});

fillColorCodes();
fillHearModes();
loadVolume();
syncCampDmr();
paintWorkspace();
bindTips();
rememberFolds();
refreshStatus();
loadRadios().catch((ex) => showError(ex.message));
setInterval(refreshStatus, 800);
