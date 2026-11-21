(() => {
  const $ = (id) => document.getElementById(id);
  const error = $("error");
  const who = $("who");
  const radioLine = $("radio-line");
  const led = $("led");
  const chatAside = $("chat-aside");
  const listenState = $("listen-state");
  const idState = $("id-state");
  const chatLog = $("chat-log");
  const txText = $("tx-text");
  const txResult = $("tx-result");
  const txAudio = $("tx-audio");
  const dialMhz = $("dial-mhz");
  const txBtn = $("tx-btn");
  const listenBtn = $("listen-btn");
  const listenStop = $("listen-stop");
  const rxVolume = $("rx-volume");
  const privilegeDialog = $("privilege-dialog");
  const privilegeReason = $("privilege-reason");
  const signoffDialog = $("signoff-dialog");
  const signoffTitle = $("signoff-title");
  const signoffReason = $("signoff-reason");
  const signoffMessage = $("signoff-message");
  const VOLUME_KEY = "agentic-sdr-volume";
  const txWatts = $("tx-watts");
  const txTone = $("tx-tone");

  let lastChatCount = 0;
  let lastChatKey = "";
  let radioOpen = false;
  let radioCanTransmit = false;
  let radioListening = false;
  let wantListen = false;
  let transmitting = false;
  let identity = null;
  let idStatus = { due: true, hasIdentifiedThisSession: false };
  let armedMhz = Number(dialMhz.value);
  let signoffResolver = null;
  let hearAbort = null;
  let hearMaster = null;
  let hearGain = null;
  let hearToken = 0;

  function unlockAudio(sampleRate) {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC)
      throw new Error("This browser cannot play radio audio.");
    // Once unlocked by a click, keep that context. Sidetone is 8 kHz and listen is 48 kHz —
    // recreating after fetch() loses the user gesture and the new context stays silent.
    if (hearMaster && hearMaster.state !== "closed") {
      if (hearMaster.state === "suspended")
        hearMaster.resume().catch(() => {});
      return hearMaster;
    }
    const want = Number(sampleRate);
    hearMaster = want > 0 ? new AC({ sampleRate: want }) : new AC();
    if (hearMaster.state === "suspended")
      hearMaster.resume().catch(() => {});
    return hearMaster;
  }

  function showError(message) {
    if (!message) {
      error.hidden = true;
      error.textContent = "";
      return;
    }
    error.hidden = false;
    error.textContent = message;
  }

  async function api(path, options) {
    const response = await fetch(path, {
      headers: { "Content-Type": "application/json", ...(options?.headers || {}) },
      ...options
    });
    const text = await response.text();
    let body = null;
    try { body = text ? JSON.parse(text) : null; } catch { body = { error: text }; }
    if (!response.ok) throw new Error(body?.error || response.statusText);
    return body;
  }

  function forceCaps() {
    const start = txText.selectionStart;
    const end = txText.selectionEnd;
    const next = txText.value.toUpperCase();
    if (txText.value !== next) {
      txText.value = next;
      try { txText.setSelectionRange(start, end); } catch { /* ignore */ }
    }
  }

  function paintIdState() {
    if (!identity) {
      idState.textContent = "";
      return;
    }
    if (idStatus.due)
      idState.textContent = "ID due — next TX appends your call sign.";
    else {
      const sec = Math.max(0, Math.round(Number(idStatus.secondsUntilDue) || 0));
      const min = Math.floor(sec / 60);
      const rem = sec % 60;
      idState.textContent = `ID fresh · next in ${min}:${String(rem).padStart(2, "0")}`;
    }
  }

  function paintTransmitButton() {
    const rxOnly = radioOpen && !radioCanTransmit;
    txBtn.disabled = rxOnly;
    txBtn.title = rxOnly
      ? "This radio is receive-only — experimental transmit needs a TX-capable or simulated radio."
      : "Experimental transmit — local sidetone / simulated RF until hardware radiates.";
  }

  function paintListenButtons() {
    listenBtn.disabled = !radioOpen || wantListen;
    listenBtn.title = radioOpen
      ? (wantListen ? "Already listening on the dial frequency." : "Tune the dial and listen for CW.")
      : "Start the radio first.";
    listenStop.hidden = !wantListen;
  }

  function paintListenStatus(listening, error) {
    if (!radioOpen) {
      listenState.textContent = "Start the radio, then click Listen.";
      chatAside.textContent = "Idle";
      return;
    }
    if (listening) {
      listenState.textContent = `Listening CW at ${Number(dialMhz.value).toFixed(3)} MHz`;
      chatAside.textContent = "Listening";
      return;
    }
    listenState.textContent = wantListen
      ? (error || "Starting listener…")
      : "Radio open — click Listen.";
    chatAside.textContent = "Idle";
  }

  function applyVolume() {
    const level = Math.max(0, Math.min(100, Number(rxVolume.value) || 0));
    if (hearGain)
      hearGain.gain.value = level / 100;
    try { localStorage.setItem(VOLUME_KEY, String(level)); } catch { /* ignore */ }
  }

  function persistDeskPrefs() {
    const patch = {
      volume: Math.max(0, Math.min(100, Number(rxVolume.value) || 0)),
      dialMhz: Number(dialMhz.value),
      txWatts: Number(txWatts.value),
      toneHz: Number(txTone.value)
    };
    DeskPanels.saveUiPrefs(patch).catch(() => {});
  }

  function restoreVolume() {
    try {
      const saved = Number(localStorage.getItem(VOLUME_KEY));
      if (Number.isFinite(saved))
        rxVolume.value = String(Math.max(0, Math.min(100, saved)));
    } catch { /* ignore */ }
    applyVolume();
  }

  async function restoreSharedPrefs() {
    const prefs = await DeskPanels.loadUiPrefs();
    if (prefs.volume != null && Number.isFinite(Number(prefs.volume))) {
      rxVolume.value = String(Math.max(0, Math.min(100, Number(prefs.volume))));
      try { localStorage.setItem(VOLUME_KEY, rxVolume.value); } catch { /* ignore */ }
    }
    if (prefs.dialMhz != null && Number(prefs.dialMhz) > 0) {
      dialMhz.value = String(Number(prefs.dialMhz));
      armedMhz = Number(dialMhz.value);
    }
    if (prefs.txWatts != null && Number(prefs.txWatts) > 0)
      txWatts.value = String(Number(prefs.txWatts));
    if (prefs.toneHz != null && Number(prefs.toneHz) > 0)
      txTone.value = String(Number(prefs.toneHz));
    applyVolume();
  }

  async function playPcmStream(body, context, rate, signal, gain) {
    if (!window.RadioListener?.playPcmStream)
      throw new Error("Radio listener script did not load.");
    await RadioListener.playPcmStream(body, context, rate, signal, 1, gain);
  }

  function stopHear() {
    hearToken += 1;
    if (hearAbort) {
      try { hearAbort.abort(); } catch { /* ignore */ }
      hearAbort = null;
    }
    hearGain = null;
  }

  async function ensureRunningAudio(context) {
    if (context.state === "suspended")
      await context.resume();
    if (context.state === "suspended")
      throw new Error("Browser blocked radio audio. Click Listen again to unlock sound.");
  }

  async function startHear() {
    if (hearAbort || !radioListening || transmitting)
      return;
    const token = ++hearToken;
    const controller = new AbortController();
    hearAbort = controller;
    try {
      // Gesture unlock prefers 48 kHz so the stream plays 1:1 with demod (Host-style).
      unlockAudio(48000);
      const response = await fetch("/api/listen/audio", { signal: controller.signal });
      if (token !== hearToken) return;
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.error || "Listening audio is not ready yet.");
      }

      const rate = Number(response.headers.get("X-Audio-Rate")) || 48000;
      const context = unlockAudio(rate);
      await ensureRunningAudio(context);
      if (token !== hearToken) return;
      hearGain = context.createGain();
      hearGain.connect(context.destination);
      applyVolume();
      if (!response.body)
        throw new Error("The desk returned no audio stream.");
      await playPcmStream(response.body, context, rate, controller.signal, hearGain);
    } catch (err) {
      if (err.name !== "AbortError" && radioListening)
        showError(err.message || String(err));
    } finally {
      if (token !== hearToken) return;
      if (hearAbort === controller)
        hearAbort = null;
      hearGain = null;
      if (radioListening)
        setTimeout(() => { if (radioListening && !hearAbort) startHear(); }, 400);
    }
  }

  function syncHear(listening) {
    const next = !!listening;
    if (!next) {
      radioListening = false;
      stopHear();
      return;
    }
    radioListening = true;
    if (!hearAbort)
      startHear();
  }

  function paintChat(messages) {
    const list = Array.isArray(messages) ? messages : [];
    const key = list.length === 0
      ? ""
      : `${list.length}:${list[list.length - 1]?.id || ""}:${list[list.length - 1]?.text || ""}`;
    // Refresh polls every second — rebuilding the whole log each time stalls audio.
    if (key === lastChatKey)
      return;
    lastChatKey = key;
    chatLog.replaceChildren();
    if (list.length === 0) {
      const empty = document.createElement("p");
      empty.className = "quiet";
      empty.textContent = "Decoded Morse and your sent traffic appear here.";
      chatLog.append(empty);
      lastChatCount = 0;
      return;
    }
    for (const message of list) {
      const row = document.createElement("div");
      row.className = `chat-row ${message.direction || "note"}`;
      const meta = document.createElement("span");
      meta.className = "chat-meta";
      const when = message.at ? new Date(message.at) : null;
      const stamp = when && !Number.isNaN(when.valueOf())
        ? when.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })
        : "";
      const whoLabel = message.direction === "tx" ? "You"
        : message.direction === "rx" ? "RX"
          : "Note";
      meta.textContent = stamp ? `${whoLabel} · ${stamp}` : whoLabel;
      const body = document.createElement("pre");
      body.className = "chat-text";
      body.textContent = message.text || "";
      row.append(meta, body);
      chatLog.append(row);
    }
    if (list.length !== lastChatCount) {
      chatLog.scrollTop = chatLog.scrollHeight;
      lastChatCount = list.length;
    }
  }

  function askSignOff({ title, reason, prosign }) {
    return new Promise((resolve) => {
      if (!identity || !idStatus.hasIdentifiedThisSession) {
        resolve("skip");
        return;
      }
      signoffResolver = resolve;
      signoffTitle.textContent = title;
      signoffReason.textContent = reason;
      signoffMessage.textContent = "…";
      signoffDialog.showModal();
      api("/api/signoff/preview", {
        method: "POST",
        body: JSON.stringify({ prosign, reason })
      }).then((preview) => {
        signoffMessage.textContent = preview.text;
        signoffDialog.dataset.prosign = preview.prosign;
      }).catch((err) => {
        signoffDialog.close();
        signoffResolver = null;
        showError(err.message);
        resolve("cancel");
      });
    });
  }

  function headerText(headers, name) {
    const raw = headers.get(name);
    if (!raw) return "";
    try { return decodeURIComponent(raw); } catch { return raw; }
  }

  async function transmitLive(body) {
    // Unlock on the Transmit click before any await. Keep this context for sidetone.
    const context = unlockAudio();
    transmitting = true;
    stopHear();
    txAudio.hidden = true;
    txResult.textContent = "Transmitting…";
    txResult.style.color = "var(--muted)";
    chatAside.textContent = "TX";

    const playSignal = new AbortController();
    try {
      const response = await fetch("/api/transmit", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      if (!response.ok) {
        const text = await response.text();
        let message = text;
        try { message = (text ? JSON.parse(text) : null)?.error || text; } catch { /* keep */ }
        throw new Error(message || response.statusText);
      }

      const radiated = response.headers.get("X-Radiated") === "1";
      const reason = headerText(response.headers, "X-Tx-Reason");
      const sent = headerText(response.headers, "X-Tx-Text");
      const summary = headerText(response.headers, "X-Tx-Summary");
      const rate = Number(response.headers.get("X-Audio-Rate")) || 8000;
      txResult.textContent = `${radiated ? "Radiating" : "Preview"} · ${sent || ""} · ${reason}`;
      txResult.style.color = radiated ? "var(--ok)" : "var(--muted)";

      const gain = context.createGain();
      gain.connect(context.destination);
      gain.gain.value = Math.max(0, Math.min(100, Number(rxVolume.value) || 0)) / 100;
      try {
        await ensureRunningAudio(context);
        if (response.body)
          await playPcmStream(response.body, context, rate, playSignal.signal, gain);
      } finally {
        try { gain.disconnect(); } catch { /* ignore */ }
      }

      txResult.textContent = `${radiated ? "Radiated" : "Not radiated"} · ${sent || ""} · ${summary || reason}`;
      return { ok: true, radiated, reason, text: sent, summary };
    } finally {
      playSignal.abort();
      transmitting = false;
      await refresh().catch(() => {});
      if (radioOpen && wantListen)
        await syncListen().catch(() => {});
    }
  }

  async function transmitSignOff(prosign) {
    return transmitLive({
      requestId: crypto.randomUUID(),
      authorized: true,
      text: "",
      prosign,
      frequencyHz: Number(dialMhz.value) * 1e6,
      powerWatts: Number($("tx-watts").value),
      toneHz: Number($("tx-tone").value)
    });
  }

  async function syncListen() {
    const hz = Number(dialMhz.value) * 1e6;
    if (!(hz > 0)) return;
    if (transmitting)
      return;
    if (!radioOpen) {
      wantListen = false;
      paintListenStatus(false);
      paintListenButtons();
      syncHear(false);
      return;
    }
    wantListen = true;
    paintListenButtons();
    const status = await api("/api/listen", {
      method: "POST",
      body: JSON.stringify({ frequencyHz: hz, enabled: true })
    });
    if (transmitting)
      return;
    paintListenStatus(!!status.listening, status.error);
    paintListenButtons();
    syncHear(status.listening);
  }

  async function stopListen() {
    wantListen = false;
    paintListenButtons();
    syncHear(false);
    if (!radioOpen) {
      paintListenStatus(false);
      return;
    }
    try {
      const status = await api("/api/listen", {
        method: "POST",
        body: JSON.stringify({ enabled: false })
      });
      paintListenStatus(!!status.listening, status.error);
    } catch (err) {
      paintListenStatus(false, err.message);
      throw err;
    } finally {
      paintListenButtons();
    }
  }

  async function refresh() {
    const [op, radio, chat, ids] = await Promise.all([
      api("/api/operator"),
      api("/api/radio"),
      api("/api/chat"),
      api("/api/identity")
    ]);
    identity = op.operator || op.operatorIdentity;
    idStatus = ids || idStatus;
    who.textContent = identity
      ? `${identity.callSign} · ${identity.license}`
      : "No operator";
    radioOpen = !!(radio.open || radio.deviceOpen);
    radioCanTransmit = !!(radio.canTransmit ?? radio.CanTransmit);
    const simulated = !!(radio.simulateTransmit);
    radioLine.textContent = radioOpen
      ? `${radio.name || radio.deviceName} · ${radioCanTransmit
        ? (simulated ? "TX simulated" : "can transmit")
        : "receive only"}${radio.listening ? " · listening" : ""}`
      : "No radio attached (local preview only)";
    led.classList.toggle("on", Boolean(identity));
    paintChat(chat.messages);
    paintIdState();
    paintTransmitButton();
    if (!radioOpen) {
      wantListen = false;
      paintListenStatus(false);
      paintListenButtons();
      syncHear(false);
      return;
    }
    paintListenButtons();
    if (transmitting) {
      chatAside.textContent = "TX";
      return;
    }
    if (wantListen) {
      paintListenStatus(!!radio.listening, radio.listenError);
      syncHear(!!radio.listening);
      if (!radio.listening)
        await syncListen().catch(() => {});
    } else {
      paintListenStatus(false);
      syncHear(false);
    }
  }

  DeskPanels.mountAppNav($("app-nav"), { current: "cw" });

  DeskPanels.mountRadio($("radio-host"), {
    apiBase: "",
    onError: showError,
    calibrate: false,
    beforeClose: async () => {
      const choice = await askSignOff({
        title: "Sign off before stopping the radio?",
        reason: "Going off the air — QRT.",
        prosign: "QRT"
      });
      if (choice === "cancel")
        return false;
      if (choice === "yes") {
        try {
          await transmitSignOff("QRT");
        } catch (err) {
          showError(err.message);
          return false;
        }
      }
      return true;
    },
    onChange: () => {
      refresh().catch((err) => showError(err.message));
    }
  });
  DeskPanels.mountOperator($("operator-host"), {
    apiBase: "",
    onError: showError,
    onSnapshot: () => refresh().catch((err) => showError(err.message))
  });
  DeskPanels.mountAntenna($("antenna-host"), {
    apiBase: "",
    onError: showError
  });
  AgentPanels.mountModel($("model-host"), {
    apiBase: "",
    onError: showError,
    note: "Point at an OpenAI-compatible instruct model. The same endpoint is shared with the receiver. Ask the Prompt panel to translate CW jargon for RX or TX."
  });
  AgentPanels.mountPrompt($("prompt-host"), {
    apiBase: "",
    onError: showError,
    foldNote: "Ask the agent to translate CW jargon for what you heard (RX) or what you plan to send (TX).",
    placeholder: "Translate the last RX line, or explain CQ DE W1AW K for TX.",
    emptyText: "Nothing asked yet. Try “Translate the latest RX Morse into plain English.”"
  });

  $("signoff-yes").addEventListener("click", () => {
    const resolve = signoffResolver;
    signoffResolver = null;
    signoffDialog.close();
    if (resolve) resolve("yes");
  });
  $("signoff-skip").addEventListener("click", () => {
    const resolve = signoffResolver;
    signoffResolver = null;
    signoffDialog.close();
    if (resolve) resolve("skip");
  });
  $("signoff-cancel").addEventListener("click", () => {
    const resolve = signoffResolver;
    signoffResolver = null;
    signoffDialog.close();
    if (resolve) resolve("cancel");
  });

  txText.addEventListener("input", forceCaps);
  txText.addEventListener("blur", forceCaps);

  dialMhz.addEventListener("change", async () => {
    const next = Number(dialMhz.value);
    if (!(next > 0)) return;
    if (Math.abs(next - armedMhz) < 0.0000005) {
      if (wantListen)
        await syncListen().catch((err) => showError(err.message));
      return;
    }
    showError("");
    const choice = await askSignOff({
      title: `Sign off before QSY to ${next.toFixed(3)} MHz?`,
      reason: "Changing frequency — QSY.",
      prosign: "QSY"
    });
    if (choice === "cancel") {
      dialMhz.value = String(armedMhz);
      return;
    }
    if (choice === "yes") {
      try {
        // Sign off on the old frequency first.
        dialMhz.value = String(armedMhz);
        await transmitSignOff("QSY");
      } catch (err) {
        showError(err.message);
        dialMhz.value = String(armedMhz);
        return;
      }
      dialMhz.value = String(next);
    }
    armedMhz = next;
    if (wantListen)
      await syncListen().catch((err) => showError(err.message));
  });

  listenBtn.addEventListener("click", async () => {
    showError("");
    try {
      // Match CW listen rate so RadioListener does not resample (resampling drifts into breakup).
      unlockAudio(48000);
    } catch (err) {
      showError(err.message);
      return;
    }
    if (!radioOpen) {
      showError("Start the radio first, then click Listen.");
      return;
    }
    try {
      await syncListen();
    } catch (err) {
      showError(err.message);
    }
  });

  listenStop.addEventListener("click", async () => {
    showError("");
    try {
      await stopListen();
    } catch (err) {
      showError(err.message);
    }
  });

  function showPrivilegeDenied(reason) {
    privilegeReason.textContent = reason || "This frequency, mode, or power is outside your license.";
    privilegeDialog.showModal();
  }

  $("privilege-ok").addEventListener("click", () => privilegeDialog.close());

  $("tx-form").addEventListener("submit", async (event) => {
    event.preventDefault();
    showError("");
    forceCaps();
    try {
      // Must unlock before any await — privilege check would otherwise lose the click gesture.
      unlockAudio();
      const frequencyHz = Number(dialMhz.value) * 1e6;
      const powerWatts = Number($("tx-watts").value);
      const decision = await api(`/api/privilege?frequencyHz=${frequencyHz}&watts=${powerWatts}`);
      if (!decision.allowed) {
        showPrivilegeDenied(decision.reason);
        return;
      }

      await transmitLive({
        requestId: crypto.randomUUID(),
        authorized: $("tx-authorized").checked,
        text: txText.value.toUpperCase(),
        frequencyHz,
        powerWatts,
        toneHz: Number($("tx-tone").value)
      });
      $("tx-authorized").checked = false;
      txText.value = "";
      armedMhz = Number(dialMhz.value);
    } catch (err) {
      const message = err.message || "";
      if (/license|privileg|Part 97|not authorized|no privileges|reserved|caps this part/i.test(message))
        showPrivilegeDenied(message);
      else
        showError(message);
      if (radioOpen && wantListen) syncListen().catch(() => {});
    }
  });

  window.addEventListener("beforeunload", (event) => {
    if (radioOpen && idStatus.hasIdentifiedThisSession) {
      event.preventDefault();
      event.returnValue = "Sign off with your call sign before leaving?";
    }
  });

  rxVolume.addEventListener("input", () => {
    try { unlockAudio(); } catch { /* ignore */ }
    applyVolume();
    persistDeskPrefs();
  });
  dialMhz.addEventListener("change", () => persistDeskPrefs());
  txWatts.addEventListener("change", () => persistDeskPrefs());
  txTone.addEventListener("change", () => persistDeskPrefs());
  restoreVolume();

  DeskPanels.rememberFolds();
  forceCaps();
  paintListenButtons();
  restoreSharedPrefs()
    .catch(() => {})
    .finally(() => {
      refresh().catch((err) => showError(err.message));
    });
  setInterval(() => {
    refresh().catch(() => {});
  }, 1000);
})();
