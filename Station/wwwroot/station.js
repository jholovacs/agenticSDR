const modes = [
  ["cw", "CW"],
  ["usb", "USB phone"],
  ["lsb", "LSB phone"],
  ["am", "AM phone"],
  ["nbfm", "FM phone"],
  ["data", "Data"],
  ["ft8", "FT8"],
  ["js8", "JS8"],
  ["olivia", "Olivia"],
  ["jtty", "JTTY"],
  ["varac", "VarAC"],
  ["packet", "Packet"]
];

const presets = [
  ["80 m CW", 3.55, "cw"],
  ["40 m FT8", 7.074, "ft8"],
  ["20 m FT8", 14.074, "ft8"],
  ["20 m USB", 14.3, "usb"],
  ["10 m USB", 28.4, "usb"],
  ["10 m FM", 29.6, "nbfm"],
  ["6 m USB", 50.125, "usb"],
  ["2 m calling", 146.52, "nbfm"],
  ["70 cm calling", 446, "nbfm"]
];

const errorBox = document.querySelector("#error");
let lastSnapshot = "";
let lastOperator = "";
let lookupNote = "";
const draftsEl = document.querySelector("#drafts");
const heardEl = document.querySelector("#heard");
const sentEl = document.querySelector("#sent");
const antennaLength = document.querySelector("#antenna-length");
const antennaUnit = document.querySelector("#antenna-unit");
const antennaOrientation = document.querySelector("#antenna-orientation");
const antennaAzimuth = document.querySelector("#antenna-azimuth");
const antennaNote = document.querySelector("#antenna-note");
const antennaBands = document.querySelector("#antenna-bands");

fillModes(document.querySelector("#check-mode"), "ft8");
fillModes(document.querySelector("#draft-mode"), "ft8");
fillModes(document.querySelector("#heard-mode"), "", true);
renderPresets();

document.querySelector("#operator-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const callSign = document.querySelector("#callsign").value;
  const license = document.querySelector("#license").value;
  const grid = document.querySelector("#grid").value;
  const body = await post("/api/operator", { callSign, license, grid });
  if (!body) return;
  lookupNote = "";
  render(body);
});

document.querySelector("#grid-lookup").addEventListener("click", async () => {
  const grid = document.querySelector("#grid").value;
  const callSign = document.querySelector("#callsign").value;
  const body = await post("/api/grid/lookup", { grid, callSign });
  if (!body) return;
  document.querySelector("#grid").value = body.grid || "";
  lookupNote = body.sentence || "";
  document.querySelector("#grid-place").textContent = lookupNote;
});

document.querySelector("#sign-out").addEventListener("click", async () => {
  const body = await send("/api/operator", { method: "DELETE" });
  if (!body) return;
  lookupNote = "";
  render(body);
});

antennaOrientation.addEventListener("change", syncAntenna);
antennaLength.addEventListener("input", syncAntenna);
antennaUnit.addEventListener("change", syncAntenna);
antennaAzimuth.addEventListener("input", syncAntenna);
syncAntenna();

document.querySelector("#antenna-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const advice = await post("/api/antenna", antennaBody());
  if (advice) paintAntenna(advice, false);
});

document.querySelector("#antenna-guess").addEventListener("click", async () => {
  const button = document.querySelector("#antenna-guess");
  button.disabled = true;
  button.textContent = "Listening…";
  antennaNote.textContent = "Listening across the dial for where the antenna is strongest. This does not transmit.";
  try {
    const advice = await post("/api/antenna/guess", {});
    if (advice) paintAntenna(advice, true);
  } finally {
    button.disabled = false;
    button.textContent = "Guess from signals";
  }
});

document.querySelector("#check-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const mhz = Number(document.querySelector("#check-mhz").value);
  const mode = document.querySelector("#check-mode").value;
  const watts = Number(document.querySelector("#check-watts").value);
  if (!Number.isFinite(mhz) || mhz <= 0) {
    showError("Enter a frequency in MHz.");
    return;
  }
  const url = `/api/privilege?frequencyHz=${Math.round(mhz * 1e6)}&mode=${encodeURIComponent(mode)}&watts=${watts}`;
  const body = await send(url);
  const line = document.querySelector("#check-result");
  if (!body) {
    line.textContent = "";
    return;
  }
  line.textContent = body.reason + (body.caution ? " " + body.caution : "");
});

document.querySelector("#draft-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const body = await post("/api/drafts", {
    text: document.querySelector("#draft-text").value,
    frequencyHz: Math.round(Number(document.querySelector("#draft-mhz").value) * 1e6),
    mode: document.querySelector("#draft-mode").value,
    powerWatts: Number(document.querySelector("#draft-watts").value)
  });
  if (!body) return;
  document.querySelector("#draft-text").value = "";
  await refresh();
});

let repeaterSelectedId = "";
let repeaterWithinLicense = false;
let repeaterPrivilegeTimer = 0;

document.querySelector("#repeater-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!repeaterWithinLicense) {
    showError("That repeater is outside your license. Part 97 is judged on the transmit (input) frequency.");
    await checkRepeaterPrivilege(true);
    return;
  }
  const repeater = repeaterBody();
  const body = await post("/api/drafts", {
    text: document.querySelector("#rep-text").value,
    frequencyHz: 0,
    mode: "nbfm",
    powerWatts: Number(document.querySelector("#rep-watts").value),
    repeater
  });
  if (!body) return;
  document.querySelector("#rep-text").value = "";
  await restoreRepeaterBook(false);
  await refresh();
});

document.querySelector("#rep-check").addEventListener("click", () => checkRepeaterPrivilege(true));

document.querySelector("#rep-pick").addEventListener("change", async () => {
  const id = document.querySelector("#rep-pick").value;
  if (!id) {
    clearRepeaterForm();
    const book = await send("/api/repeater/selected", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id: null })
    });
    if (book) paintRepeaterBook(book, false);
    return;
  }
  const book = await send("/api/repeater/selected", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id })
  });
  if (book) paintRepeaterBook(book, true);
});

document.querySelector("#rep-new").addEventListener("click", async () => {
  clearRepeaterForm();
  const book = await send("/api/repeater/selected", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id: null })
  });
  if (book) paintRepeaterBook(book, false);
});

document.querySelector("#rep-save").addEventListener("click", async () => {
  const payload = {
    label: document.querySelector("#rep-label").value,
    powerWatts: Number(document.querySelector("#rep-watts").value),
    repeater: repeaterBody()
  };
  const book = repeaterSelectedId
    ? await send(`/api/repeater/${encodeURIComponent(repeaterSelectedId)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    })
    : await post("/api/repeater", payload);
  if (book) paintRepeaterBook(book, true);
});

document.querySelector("#rep-delete").addEventListener("click", async () => {
  if (!repeaterSelectedId) return;
  const label = document.querySelector("#rep-pick").selectedOptions[0]?.textContent || "this repeater";
  if (!window.confirm(`Delete ${label}?`)) return;
  const book = await send(`/api/repeater/${encodeURIComponent(repeaterSelectedId)}`, { method: "DELETE" });
  if (!book) return;
  clearRepeaterForm();
  paintRepeaterBook(book, true);
});

for (const id of ["#rep-out", "#rep-shift", "#rep-offset", "#rep-in", "#rep-encode-mode", "#rep-decode-mode", "#rep-call", "#rep-narrow", "#rep-label", "#rep-name", "#rep-city", "#rep-watts"]) {
  document.querySelector(id).addEventListener("change", syncRepeater);
  document.querySelector(id).addEventListener("input", syncRepeater);
}

document.querySelector("#heard-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const mhz = document.querySelector("#heard-mhz").value;
  const body = await post("/api/heard", {
    text: document.querySelector("#heard-text").value,
    frequencyHz: mhz === "" ? null : Math.round(Number(mhz) * 1e6),
    mode: document.querySelector("#heard-mode").value || null
  });
  if (!body) return;
  document.querySelector("#heard-text").value = "";
  await refresh();
});

draftsEl.addEventListener("change", (event) => {
  const box = event.target.closest(".confirm-box");
  if (!box) return;
  const button = box.closest(".card").querySelector(".transmit");
  button.disabled = !box.checked;
});

draftsEl.addEventListener("click", async (event) => {
  const transmit = event.target.closest(".transmit");
  const drop = event.target.closest(".drop");
  const card = event.target.closest(".card");
  if (!card) return;
  if (drop) {
    const body = await send("/api/drafts/" + card.dataset.id, { method: "DELETE" });
    if (body) render(body);
    return;
  }
  if (!transmit || transmit.disabled) return;
  transmit.disabled = true;
  const body = await post("/api/transmit", { draftId: card.dataset.id, authorized: true });
  if (body) await refresh();
  else transmit.disabled = false;
});

async function refresh() {
  try {
    const response = await fetch("/api/station");
    if (!response.ok) return;
    render(await response.json());
  } catch {
    showError("The station page cannot reach the desk.");
  }
}

function render(snapshot) {
  const operatorKey = JSON.stringify(snapshot.operator);
  const operatorChanged = operatorKey !== lastOperator;
  const key = JSON.stringify({
    operator: snapshot.operator,
    summary: snapshot.licenseSummary,
    drafts: snapshot.drafts,
    heard: snapshot.heard,
    authorized: snapshot.authorized
  });
  if (key === lastSnapshot) return;
  lastSnapshot = key;
  lastOperator = operatorKey;

  const op = snapshot.operator;
  document.querySelector("#led").classList.toggle("on", !!op);
  document.querySelector("#who").textContent = op
    ? op.callSign + " · " + licenseLabel(op.license) + (op.grid ? " · " + op.grid : "")
    : "No operator";
  document.querySelector("#status-line").textContent = op
    ? "Drafts can wait here. Only Transmit authorizes one."
    : "Sign in with your call sign and license class.";
  document.querySelector("#summary").textContent = snapshot.licenseSummary || "";
  if (operatorChanged) {
    const place = document.querySelector("#grid-place");
    if (op) {
      document.querySelector("#callsign").value = op.callSign;
      document.querySelector("#license").value = String(op.license).toLowerCase();
      document.querySelector("#grid").value = op.grid || "";
      place.textContent = op.grid ? gridSentence(op) : lookupNote;
    } else {
      place.textContent = lookupNote;
    }
  }
  const placeholder = op ? "Include " + op.baseCall : "CQ CQ DE W1AW K";
  document.querySelector("#draft-text").placeholder = placeholder;
  document.querySelector("#rep-text").placeholder = op ? "This is " + op.baseCall + " monitoring" : "This is W1AW monitoring";
  if (operatorChanged) scheduleRepeaterPrivilege();

  const checked = new Set([...draftsEl.querySelectorAll(".confirm-box:checked")].map((box) => box.dataset.id));
  draftsEl.replaceChildren();
  const draftsAside = document.querySelector("#drafts-aside");
  if (draftsAside) {
    draftsAside.textContent = snapshot.drafts.length
      ? `${snapshot.drafts.length} waiting`
      : "None waiting";
  }
  if (!snapshot.drafts.length) {
    draftsEl.append(note("No drafts waiting."));
  }
  for (const draft of snapshot.drafts) draftsEl.append(draftCard(draft, checked.has(draft.id)));

  heardEl.replaceChildren();
  if (!snapshot.heard.length) heardEl.append(note("Nothing logged yet."));
  for (const item of [...snapshot.heard].reverse()) heardEl.append(heardCard(item));

  sentEl.replaceChildren();
  if (!snapshot.authorized.length) sentEl.append(note("Nothing authorized yet."));
  for (const item of [...snapshot.authorized].reverse()) sentEl.append(sentCard(item));
}

function draftCard(draft, wasChecked) {
  const card = document.createElement("article");
  card.className = "card" + (draft.allowed ? "" : " bad");
  card.dataset.id = draft.id;

  const header = document.createElement("header");
  const title = document.createElement("strong");
  title.textContent = draft.repeater
    ? "Repeater · " + mhz(draft.repeater.inputFrequencyHz) + " TX · " + draft.powerWatts + " W"
    : mhz(draft.frequencyHz) + " · " + modeLabel(draft.mode) + " · " + draft.powerWatts + " W";
  const source = document.createElement("span");
  source.className = "meta";
  source.textContent = String(draft.source).toLowerCase() === "agent" ? "Agent draft" : "Your draft";
  header.append(title, source);

  const text = document.createElement("p");
  text.className = "text";
  text.textContent = draft.text;

  const path = document.createElement("p");
  path.className = "reason";
  if (draft.repeater?.summary) path.textContent = draft.repeater.summary;

  const reason = document.createElement("p");
  reason.className = "reason" + (draft.allowed ? " ok" : "");
  reason.textContent = draft.reason + (draft.caution ? " " + draft.caution : "");

  const row = document.createElement("div");
  row.className = "row";
  if (draft.allowed) {
    const label = document.createElement("label");
    label.className = "confirm";
    const box = document.createElement("input");
    box.type = "checkbox";
    box.className = "confirm-box";
    box.dataset.id = draft.id;
    box.checked = wasChecked;
    const words = document.createElement("span");
    words.textContent = "I am the control operator and I authorize this transmission.";
    label.append(box, words);
    const button = document.createElement("button");
    button.type = "button";
    button.className = "transmit";
    button.textContent = "Transmit";
    button.disabled = !wasChecked;
    row.append(label, button);
  }
  const drop = document.createElement("button");
  drop.type = "button";
  drop.className = "drop ghost";
  drop.textContent = "Drop";
  row.append(drop);

  card.append(header, text);
  if (path.textContent) card.append(path);
  card.append(reason, row);
  return card;
}

function heardCard(item) {
  const card = document.createElement("article");
  card.className = "card";
  const header = document.createElement("header");
  const title = document.createElement("strong");
  const where = item.frequencyHz ? mhz(item.frequencyHz) : "Copy";
  title.textContent = where + (item.mode ? " · " + modeLabel(item.mode) : "");
  const source = document.createElement("span");
  source.className = "meta";
  source.textContent = String(item.source).toLowerCase() === "agent" ? "Agent" : "You";
  header.append(title, source);
  const text = document.createElement("p");
  text.className = "text";
  text.textContent = item.text;
  card.append(header, text);
  return card;
}

const sendableModes = new Set(["cw", "ft8", "js8", "olivia", "jtty", "packet"]);
let sendAudio = null;

function sentCard(item) {
  const card = document.createElement("article");
  card.className = "card";
  const header = document.createElement("header");
  const title = document.createElement("strong");
  title.textContent = item.repeater
    ? item.callSign + " · repeater · " + mhz(item.frequencyHz) + " TX"
    : item.callSign + " · " + mhz(item.frequencyHz) + " · " + modeLabel(item.mode);
  const flag = document.createElement("span");
  flag.className = "meta";
  flag.textContent = "Authorized, not radiated";
  header.append(title, flag);
  const text = document.createElement("p");
  text.className = "text";
  text.textContent = item.text;
  const reason = document.createElement("p");
  reason.className = "reason";
  reason.textContent = item.reason;
  card.append(header, text);
  if (item.repeater?.summary) {
    const path = document.createElement("p");
    path.className = "reason";
    path.textContent = item.repeater.summary;
    card.append(path);
  }
  card.append(reason);
  if (sendableModes.has(String(item.mode || "").toLowerCase())) {
    const row = document.createElement("div");
    row.className = "row";
    const play = document.createElement("button");
    play.type = "button";
    play.textContent = "Play send audio";
    play.addEventListener("click", () => playAuthorizedAudio(item.id, play));
    const link = document.createElement("a");
    link.href = `/api/authorized/${encodeURIComponent(item.id)}/audio`;
    link.textContent = "Download WAV";
    link.download = `${String(item.mode).toLowerCase()}-${item.id}.wav`;
    link.className = "quiet";
    row.append(play, link);
    card.append(row);
  }
  return card;
}

async function playAuthorizedAudio(id, button) {
  const previous = button.textContent;
  button.disabled = true;
  button.textContent = "Encoding…";
  showError("");
  try {
    const response = await fetch(`/api/authorized/${encodeURIComponent(id)}/audio`);
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      showError(body.error || "Could not encode that message.");
      return;
    }
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    if (sendAudio) {
      sendAudio.pause();
      URL.revokeObjectURL(sendAudio.src);
    }
    sendAudio = new Audio(url);
    sendAudio.addEventListener("ended", () => URL.revokeObjectURL(url));
    await sendAudio.play();
  } catch {
    showError("Could not play send audio in this browser.");
  } finally {
    button.disabled = false;
    button.textContent = previous;
  }
}

function renderPresets() {
  const host = document.querySelector("#presets");
  for (const [label, mhzValue, mode] of presets) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.addEventListener("click", () => {
      document.querySelector("#draft-mhz").value = mhzValue;
      document.querySelector("#draft-mode").value = mode;
      document.querySelector("#check-mhz").value = mhzValue;
      document.querySelector("#check-mode").value = mode;
    });
    host.append(button);
  }
}

function fillModes(select, selected, includeBlank) {
  if (includeBlank) {
    const blank = document.createElement("option");
    blank.value = "";
    blank.textContent = "Mode optional";
    select.append(blank);
  }
  for (const [value, label] of modes) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    if (value === selected) option.selected = true;
    select.append(option);
  }
}

function note(text) {
  const p = document.createElement("p");
  p.className = "empty";
  p.textContent = text;
  return p;
}

function mhz(hz) {
  const value = hz / 1e6;
  const digits = value >= 100 ? 3 : 4;
  return value.toFixed(digits).replace(/0+$/, "").replace(/\.$/, "") + " MHz";
}

function modeLabel(mode) {
  const found = modes.find((item) => item[0] === String(mode).toLowerCase());
  return found ? found[1] : mode;
}

function gridSentence(op) {
  const bits = [op.grid];
  if (op.coordinates) bits.push(op.coordinates);
  if (op.place) bits.push(op.place);
  return bits.join(" · ");
}

function antennaBody() {
  return {
    length: Number(antennaLength.value),
    unit: antennaUnit.value,
    orientation: antennaOrientation.value,
    azimuthDegrees: antennaOrientation.value === "horizontal" ? Number(antennaAzimuth.value) : null
  };
}

function syncAntenna() {
  const horizontal = antennaOrientation.value === "horizontal";
  antennaAzimuth.disabled = !horizontal;
  const length = String(antennaLength.value).trim();
  const unit = antennaUnit.value === "ft" ? "ft" : "m";
  const summary = document.querySelector("#antenna-summary");
  if (!length) summary.textContent = "";
  else if (horizontal) summary.textContent = `${length} ${unit}, horizontal, ${Math.round(Number(antennaAzimuth.value) || 0)}° true`;
  else summary.textContent = `${length} ${unit}, vertical`;
}

function paintAntenna(advice, fromGuess) {
  if (fromGuess && advice.lengthMeters) {
    antennaUnit.value = "m";
    antennaLength.value = compactNumber(advice.lengthMeters, 2);
    antennaOrientation.value = advice.orientation || "vertical";
    if (advice.azimuthDegrees != null) antennaAzimuth.value = String(Math.round(advice.azimuthDegrees));
    syncAntenna();
  }
  antennaNote.textContent = advice.summary || "";
  antennaBands.replaceChildren();
  for (const band of advice.bands || []) {
    const item = document.createElement("li");
    item.textContent = band.name + (band.reason ? " — " + band.reason : "");
    antennaBands.append(item);
  }
}

function compactNumber(value, digits) {
  return Number(value).toFixed(digits).replace(/0+$/, "").replace(/\.$/, "");
}

function fillToneLists(catalog) {
  for (const id of ["#rep-encode-ctcss", "#rep-decode-ctcss"]) {
    const select = document.querySelector(id);
    select.replaceChildren();
    for (const tone of catalog.ctcssHz || []) {
      const option = document.createElement("option");
      option.value = String(tone);
      option.textContent = Number(tone).toFixed(1);
      select.append(option);
    }
  }
  for (const id of ["#rep-encode-dcs", "#rep-decode-dcs"]) {
    const select = document.querySelector(id);
    select.replaceChildren();
    for (const code of catalog.dcsCodes || []) {
      const option = document.createElement("option");
      option.value = code;
      option.textContent = code;
      select.append(option);
    }
  }
}

function standardOffsetKhz(outputMhz) {
  const hz = outputMhz * 1e6;
  if (hz >= 29e6 && hz <= 29.7e6) return 100;
  if (hz >= 51e6 && hz <= 54e6) return 1000;
  if (hz >= 144e6 && hz <= 148e6) return 600;
  if (hz >= 222e6 && hz <= 225e6) return 1600;
  if (hz >= 420e6 && hz <= 450e6) return 5000;
  if (hz >= 902e6 && hz <= 928e6) return 12000;
  if (hz >= 1240e6 && hz <= 1300e6) return 12000;
  return null;
}

function repeaterBody() {
  const shift = document.querySelector("#rep-shift").value;
  const offsetKhz = Number(document.querySelector("#rep-offset").value);
  const encodeMode = document.querySelector("#rep-encode-mode").value;
  const decodeMode = document.querySelector("#rep-decode-mode").value;
  return {
    outputFrequencyHz: Math.round(Number(document.querySelector("#rep-out").value) * 1e6),
    inputFrequencyHz: shift === "split" ? Math.round(Number(document.querySelector("#rep-in").value) * 1e6) : null,
    shift,
    offsetHz: shift === "plus" || shift === "minus"
      ? Math.round((Number.isFinite(offsetKhz) ? offsetKhz : 0) * 1e3)
      : null,
    encodeMode,
    encodeCtcssHz: encodeMode === "ctcss" ? Number(document.querySelector("#rep-encode-ctcss").value) : null,
    encodeDcs: encodeMode === "dcs" ? document.querySelector("#rep-encode-dcs").value : null,
    decodeMode,
    decodeCtcssHz: decodeMode === "ctcss" ? Number(document.querySelector("#rep-decode-ctcss").value) : null,
    decodeDcs: decodeMode === "dcs" ? document.querySelector("#rep-decode-dcs").value : null,
    narrow: document.querySelector("#rep-narrow").checked,
    repeaterCallSign: document.querySelector("#rep-call").value,
    name: document.querySelector("#rep-name").value,
    city: document.querySelector("#rep-city").value,
    notes: document.querySelector("#rep-notes").value
  };
}

function syncRepeater() {
  const shift = document.querySelector("#rep-shift").value;
  const out = Number(document.querySelector("#rep-out").value);
  const offsetField = document.querySelector("#rep-offset");
  const inField = document.querySelector("#rep-in");
  const encodeMode = document.querySelector("#rep-encode-mode").value;
  const decodeMode = document.querySelector("#rep-decode-mode").value;
  offsetField.disabled = shift !== "plus" && shift !== "minus";
  inField.disabled = shift !== "split";
  document.querySelector("#rep-encode-ctcss").disabled = encodeMode !== "ctcss";
  document.querySelector("#rep-encode-dcs").disabled = encodeMode !== "dcs";
  document.querySelector("#rep-decode-ctcss").disabled = decodeMode !== "ctcss";
  document.querySelector("#rep-decode-dcs").disabled = decodeMode !== "dcs";

  let input = Number(inField.value);
  if ((shift === "plus" || shift === "minus") && Number.isFinite(out) && out > 0) {
    if (!offsetField.value) {
      const standard = standardOffsetKhz(out);
      if (standard != null) offsetField.value = String(standard);
    }
    const offsetKhz = Number(offsetField.value);
    if (Number.isFinite(offsetKhz)) {
      input = shift === "plus" ? out + offsetKhz / 1e3 : out - offsetKhz / 1e3;
      inField.value = compactNumber(input, 4);
    }
  } else if (shift === "simplex" && Number.isFinite(out) && out > 0) {
    input = out;
    inField.value = compactNumber(out, 4);
  }

  const line = document.querySelector("#rep-resolved");
  if (!Number.isFinite(out) || out <= 0) {
    line.textContent = "Set the output and shift. Standard offsets fill in for the band.";
    setRepeaterPrivilege(null, "Set the output frequency before holding a draft.");
    return;
  }
  const bits = [`out ${compactNumber(out, 4)} MHz`];
  if (Number.isFinite(input) && input > 0) bits.push(`in ${compactNumber(input, 4)} MHz`);
  bits.push(shift);
  if ((shift === "plus" || shift === "minus") && offsetField.value) bits.push(`${offsetField.value} kHz`);
  bits.push(encodeMode === "none" ? "encode none" : `encode ${encodeMode}`);
  if (decodeMode !== "none") bits.push(`decode ${decodeMode}`);
  bits.push(document.querySelector("#rep-narrow").checked ? "narrow" : "wide");
  const call = document.querySelector("#rep-call").value.trim();
  if (call) bits.push(call);
  line.textContent = bits.join(" · ");
  scheduleRepeaterPrivilege();
}

function scheduleRepeaterPrivilege() {
  window.clearTimeout(repeaterPrivilegeTimer);
  repeaterPrivilegeTimer = window.setTimeout(() => checkRepeaterPrivilege(false), 250);
}

function setRepeaterPrivilege(decision, fallback) {
  const line = document.querySelector("#rep-privilege");
  const hold = document.querySelector("#rep-hold");
  const allowed = !!(decision && decision.withinLicense);
  repeaterWithinLicense = allowed;
  hold.disabled = !allowed;
  line.classList.toggle("ok", allowed);
  line.classList.toggle("bad-text", !!(decision && decision.withinLicense === false));
  if (decision)
    line.textContent = decision.reason + (decision.caution ? " " + decision.caution : "");
  else
    line.textContent = fallback || "";
}

async function checkRepeaterPrivilege(force) {
  const out = Number(document.querySelector("#rep-out").value);
  if (!Number.isFinite(out) || out <= 0) {
    setRepeaterPrivilege(null, "Set the output frequency before holding a draft.");
    return;
  }
  try {
    const response = await fetch("/api/repeater/privilege", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        powerWatts: Number(document.querySelector("#rep-watts").value),
        repeater: repeaterBody()
      })
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      setRepeaterPrivilege(null, body.error || "Sign in with your call and license to check this machine.");
      if (force) showError(body.error || "Could not check privilege.");
      return;
    }
    setRepeaterPrivilege(body);
  } catch {
    setRepeaterPrivilege(null, "The station page cannot reach the desk.");
    if (force) showError("The station page cannot reach the desk.");
  }
}

function paintRepeater(access, label, powerWatts) {
  if (!access) return;
  document.querySelector("#rep-label").value = label || access.name || access.repeaterCallSign || "";
  document.querySelector("#rep-out").value = compactNumber(access.outputFrequencyHz / 1e6, 4);
  document.querySelector("#rep-shift").value = String(access.shift).toLowerCase();
  if (access.offsetHz != null) document.querySelector("#rep-offset").value = compactNumber(access.offsetHz / 1e3, 3);
  document.querySelector("#rep-in").value = compactNumber(access.inputFrequencyHz / 1e6, 4);
  if (powerWatts != null && Number.isFinite(powerWatts))
    document.querySelector("#rep-watts").value = String(powerWatts);
  document.querySelector("#rep-encode-mode").value = String(access.encodeMode).toLowerCase();
  if (access.encodeCtcssHz != null) document.querySelector("#rep-encode-ctcss").value = String(access.encodeCtcssHz);
  if (access.encodeDcs) document.querySelector("#rep-encode-dcs").value = access.encodeDcs;
  document.querySelector("#rep-decode-mode").value = String(access.decodeMode).toLowerCase();
  if (access.decodeCtcssHz != null) document.querySelector("#rep-decode-ctcss").value = String(access.decodeCtcssHz);
  if (access.decodeDcs) document.querySelector("#rep-decode-dcs").value = access.decodeDcs;
  document.querySelector("#rep-narrow").checked = access.narrow !== false;
  document.querySelector("#rep-call").value = access.repeaterCallSign || "";
  document.querySelector("#rep-name").value = access.name || "";
  document.querySelector("#rep-city").value = access.city || "";
  document.querySelector("#rep-notes").value = access.notes || "";
  document.querySelector("#rep-resolved").textContent = access.summary || "";
  syncRepeater();
}

function clearRepeaterForm() {
  repeaterSelectedId = "";
  document.querySelector("#rep-label").value = "";
  document.querySelector("#rep-out").value = "";
  document.querySelector("#rep-shift").value = "minus";
  document.querySelector("#rep-offset").value = "";
  document.querySelector("#rep-in").value = "";
  document.querySelector("#rep-encode-mode").value = "none";
  document.querySelector("#rep-decode-mode").value = "none";
  document.querySelector("#rep-narrow").checked = true;
  document.querySelector("#rep-call").value = "";
  document.querySelector("#rep-name").value = "";
  document.querySelector("#rep-city").value = "";
  document.querySelector("#rep-notes").value = "";
  document.querySelector("#rep-resolved").textContent = "Set the output and shift. Standard offsets fill in for the band.";
  document.querySelector("#rep-delete").disabled = true;
  syncRepeater();
}

function paintRepeaterBook(book, fillForm) {
  const pick = document.querySelector("#rep-pick");
  const aside = document.querySelector("#rep-aside");
  const items = Array.isArray(book?.items) ? book.items : [];
  const selectedId = book?.selectedId || "";
  repeaterSelectedId = selectedId;
  pick.replaceChildren();
  const blank = document.createElement("option");
  blank.value = "";
  blank.textContent = "New repeater…";
  pick.append(blank);
  for (const item of items) {
    const option = document.createElement("option");
    option.value = item.id;
    const mhz = item.access ? compactNumber(item.access.outputFrequencyHz / 1e6, 4) : "";
    option.textContent = mhz ? `${item.label} · ${mhz}` : item.label;
    pick.append(option);
  }
  pick.value = selectedId;
  document.querySelector("#rep-delete").disabled = !selectedId;
  const selected = items.find((item) => item.id === selectedId);
  if (aside)
    aside.textContent = selected ? selected.label : (items.length ? `${items.length} saved` : "None saved");
  if (fillForm && selected?.access)
    paintRepeater(selected.access, selected.label, selected.powerWatts);
  else if (fillForm && !selected)
    clearRepeaterForm();
  else
    syncRepeater();
}

async function restoreRepeaterBook(fillForm = true) {
  const book = await send("/api/repeater");
  if (book) paintRepeaterBook(book, fillForm);
  else syncRepeater();
}

async function restoreRepeater() {
  const catalog = await send("/api/repeater/catalog");
  if (catalog) fillToneLists(catalog);
  await restoreRepeaterBook(true);
}

async function restoreAntenna() {
  const advice = await send("/api/antenna");
  if (!advice || !advice.configured) return;
  const feet = /^\s*[\d.]+\s*ft\b/i.test(advice.summary || "");
  if (feet) {
    antennaUnit.value = "ft";
    antennaLength.value = compactNumber(advice.lengthMeters / 0.3048, 2);
  } else {
    antennaUnit.value = "m";
    antennaLength.value = compactNumber(advice.lengthMeters, 2);
  }
  antennaOrientation.value = advice.orientation;
  if (advice.azimuthDegrees != null) antennaAzimuth.value = String(Math.round(advice.azimuthDegrees));
  syncAntenna();
  paintAntenna(advice, false);
}

function licenseLabel(license) {
  const key = String(license || "").toLowerCase();
  if (key === "extra") return "Amateur Extra";
  if (!key) return "";
  return key.charAt(0).toUpperCase() + key.slice(1);
}

async function post(url, payload) {
  return send(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });
}

async function send(url, options) {
  showError("");
  try {
    const response = await fetch(url, options);
    const body = await response.json().catch(() => ({}));
    if (!response.ok) {
      showError(body.error || body.reason || "The station refused that.");
      return null;
    }
    return body;
  } catch {
    showError("The station page cannot reach the desk.");
    return null;
  }
}

function showError(text) {
  errorBox.hidden = !text;
  errorBox.textContent = text;
}

restoreAntenna();
restoreRepeater();
refresh();
setInterval(refresh, 2000);

function rememberFolds() {
  const folds = document.querySelectorAll("details.fold");
  let saved = {};
  try {
    saved = JSON.parse(localStorage.getItem("agentic-sdr-station-folds") || "{}");
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
        localStorage.setItem("agentic-sdr-station-folds", JSON.stringify(state));
      } catch { /* ignore */ }
    });
  });
}

rememberFolds();

if (window.RadioListener) {
  window.stationRadio = RadioListener.mount(document.querySelector("#receive-panel"), {
    apiBase: "/api/receive",
    onError: showError,
    volumeKey: "agentic-sdr-station-volume",
    heading: false,
    aside: document.querySelector("#rx-aside")
  });
}
