const band = document.querySelector("#band");
const hits = document.querySelector("#hits");
const empty = document.querySelector("#empty");
const error = document.querySelector("#error");
const statusLine = document.querySelector("#status-line");
const deviceName = document.querySelector("#device-name");
const led = document.querySelector("#led");
const bar = document.querySelector("#bar");
const scanNote = document.querySelector("#scan-note");
const readout = document.querySelector("#readout");
const source = document.querySelector("#source");
const morse = document.querySelector("#morse");
const liveState = document.querySelector("#live-state");
const liveStop = document.querySelector("#live-stop");
const mhz = document.querySelector("#mhz");
const mode = document.querySelector("#mode");
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
  document.querySelectorAll("button").forEach((button) => {
    if (button.id === "stop" || button.id === "radio-stop" || button.id === "live-stop") return;
    button.disabled = value;
  });
  applyRadioControls();
}

function applyRadioControls() {
  const hasRadio = [...radioSelect.options].some((option) => !option.disabled && option.value !== "");
  const selectedIsOpen = deviceOpen && openIndex !== null && Number(radioSelect.value) === openIndex;
  document.querySelector("#radio-start").disabled = busy || selectedIsOpen || !hasRadio;
  document.querySelector("#radio-stop").disabled = !deviceOpen;
  document.querySelector("#antenna-guess").disabled = busy || !deviceOpen;
  document.querySelector("#scan").disabled = busy || !deviceOpen;
  document.querySelector("#listen").disabled = busy || !deviceOpen;
  document.querySelector("#noaa").disabled = busy || !deviceOpen;
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

function renderTranscript(spans) {
  const key = JSON.stringify(spans);
  if (key === transcriptKey) return;
  const nearBottom = morse.scrollHeight - morse.scrollTop - morse.clientHeight < 32;
  transcriptKey = key;
  morse.replaceChildren();
  if (spans.length === 0) {
    morse.textContent = "Morse, FT8, and speech appear here.";
    return;
  }

  spans.forEach((span, index) => {
    if (index > 0) morse.append("\n");
    const line = document.createElement("span");
    line.className = span.kind === "morse" ? "morse" : "speech";
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
    applyRadioControls();
    const progress = Math.round((status.progress || 0) * 100);
    statusLine.textContent = status.message || status.phase;
    if (Array.isArray(status.transcript))
      renderTranscript(status.transcript);
    bar.style.width = status.phase === "idle" ? "0" : `${progress}%`;
    if (status.error) showError(status.error);
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
}

function syncAzimuth() {
  const horizontal = antennaOrientation.value === "horizontal";
  antennaAzimuth.disabled = !horizontal;
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
  scanNote.textContent = report?.warning || (report ? `${rows.length} signals` : "");
  for (const hit of rows) {
    const row = document.createElement("tr");
    row.innerHTML = `
      <td class="num">${mhzText(hit.frequencyHz)}</td>
      <td>${hit.mode}</td>
      <td class="num">${Math.round(hit.confidence * 100)}%</td>
      <td class="num">${(hit.bandwidthHz / 1000).toFixed(1)}</td>
      <td class="num">${hit.snrDb.toFixed(0)}</td>
      <td>${hit.source}</td>
      <td><button type="button" class="ghost">Listen</button></td>`;
    row.querySelector("button").addEventListener("click", () => {
      document.querySelectorAll("tr").forEach((tr) => tr.classList.remove("selected"));
      row.classList.add("selected");
      mhz.value = mhzText(hit.frequencyHz);
      mode.value = hit.mode.toLowerCase();
      listen();
    });
    hits.append(row);
  }
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
  if (liveAudio) {
    liveAudio.close().catch(() => {});
    liveAudio = null;
  }
  liveState.hidden = true;
  liveStop.hidden = true;
}

async function listen() {
  stopLivePlayback();
  showError("");
  setBusy(true);
  const controller = new AbortController();
  liveAbort = controller;
  const frequencyHz = Number(mhz.value) * 1e6;
  const params = new URLSearchParams({ frequencyHz: String(frequencyHz), mode: mode.value });
  try {
    const response = await fetch(`/api/live?${params}`, { signal: controller.signal });
    if (!response.ok) {
      const body = await response.json().catch(() => null);
      throw new Error(body?.error || response.statusText);
    }

    const rate = Number(response.headers.get("X-Audio-Rate")) || 48000;
    const tuneHz = Number(response.headers.get("X-Tune-Hz"));
    readout.textContent = `${mhzText(tuneHz)} MHz  ${response.headers.get("X-Mode") || ""}`;
    source.textContent = response.headers.get("X-Source") || "";
    liveState.hidden = false;
    liveStop.hidden = false;
    liveState.textContent = "Playing as the signal arrives.";

    const context = new AudioContext();
    liveAudio = context;
    await context.resume();
    await playPcmStream(response.body, context, rate, controller.signal);
  } catch (ex) {
    if (ex.name !== "AbortError") showError(ex.message);
  } finally {
    stopLivePlayback();
    setBusy(false);
    refreshStatus();
  }
}

async function playPcmStream(body, context, rate, signal) {
  const reader = body.getReader();
  let pending = new Uint8Array(0);
  let nextTime = context.currentTime + 0.15;
  let played = 0;
  while (!signal.aborted) {
    const { done, value } = await reader.read();
    if (done) break;
    pending = concatBytes(pending, value);
    const usable = pending.length - (pending.length % 2);
    if (usable < 2) continue;
    const count = usable / 2;
    const buffer = context.createBuffer(1, count, rate);
    const channel = buffer.getChannelData(0);
    const view = new DataView(pending.buffer, pending.byteOffset, usable);
    for (let i = 0; i < count; i++)
      channel[i] = view.getInt16(i * 2, true) / 32768;
    const node = context.createBufferSource();
    node.buffer = buffer;
    node.connect(context.destination);
    const start = Math.max(nextTime, context.currentTime + 0.02);
    node.start(start);
    nextTime = start + buffer.duration;
    played += buffer.duration;
    liveState.textContent = `Playing as the signal arrives · ${played.toFixed(1)} s.`;
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

antennaOrientation.addEventListener("change", syncAzimuth);
document.querySelector("#antenna-apply").addEventListener("click", () => {
  applyAntenna().catch((ex) => showError(ex.message));
});
document.querySelector("#antenna-guess").addEventListener("click", guessAntenna);
document.querySelector("#radio-start").addEventListener("click", startRadio);
document.querySelector("#radio-stop").addEventListener("click", () => {
  stopRadio().catch((ex) => showError(ex.message));
});
radioSelect.addEventListener("change", applyRadioControls);
radioSelect.addEventListener("focus", () => {
  loadRadios().catch((ex) => showError(ex.message));
});
document.querySelector("#scan").addEventListener("click", scan);
document.querySelector("#listen").addEventListener("click", listen);
async function stopWork() {
  liveAbort?.abort();
  agentAbort?.abort();
  await api("/api/stop", { method: "POST" });
  refreshStatus();
}
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
  mhz.value = "162.550000";
  mode.value = "nbfm";
  listen();
});

restoreAntenna()
  .then(loadBands)
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
}

async function listModels() {
  showError("");
  const listed = await api("/api/llm/models");
  const datalist = document.querySelector("#llm-models");
  datalist.innerHTML = "";
  for (const name of listed || []) {
    const option = document.createElement("option");
    option.value = name;
    datalist.append(option);
  }
  const count = (listed || []).length;
  document.querySelector("#llm-note").textContent = count
    ? `The endpoint offered ${count} model${count === 1 ? "" : "s"}. Pick one, then save.`
    : "The endpoint listed no models.";
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
  return turn;
}

async function askModel() {
  const box = document.querySelector("#llm-ask");
  const question = box.value.trim();
  if (!question || busy) return;
  showError("");
  const asked = addTurn("user", "You", question);
  const pending = addTurn("agent", "Agent", "Working on it…");
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
    }
    if (reply.scan) renderHits(reply.scan);
  } catch (ex) {
    asked.remove();
    pending.remove();
    box.value = question;
    if (ex.name === "AbortError") showError("Stopped.");
    else showError(ex.message);
    if (!document.querySelector("#prompt-log .turn"))
      document.querySelector("#prompt-empty").hidden = false;
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
}
refreshStatus();
setInterval(refreshStatus, 800);
