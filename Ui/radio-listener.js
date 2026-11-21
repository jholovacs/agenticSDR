/**
 * Shared radio listener for Host (5088) and mode desks that proxy receive.
 * Mount a full panel, or connect to existing controls.
 */
(() => {
  const HEAR_MODES = [
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
    ["wspr", "WSPR"],
    ["varac", "VarAC"],
    ["dmr", "DMR"],
    ["dstar", "D-STAR"],
    ["m17", "M17"],
    ["packet", "Packet (AX.25)"],
    ["ribbit", "Ribbit"]
  ];

  const PRESETS = [
    ["2 m calling", 146.52, "nbfm"],
    ["70 cm calling", 446, "nbfm"],
    ["NOAA", 162.55, "nbfm"],
    ["FM broadcast", 98.1, "wbfm"],
    ["20 m USB", 14.3, "usb"],
    ["40 m CW", 7.05, "cw"],
    ["20 m FT8", 14.074, "ft8"]
  ];

  function escapeHtml(text) {
    return String(text)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;");
  }

  function concatBytes(left, right) {
    if (left.length === 0) return right;
    const merged = new Uint8Array(left.length + right.length);
    merged.set(left, 0);
    merged.set(right, left.length);
    return merged;
  }

  function channelsFromHeaders(headers, mode, program) {
    const raw = Number(headers.get("X-Audio-Channels"));
    if (Number.isFinite(raw) && raw >= 1)
      return Math.min(2, Math.max(1, Math.round(raw)));
    if (String(mode || "").toLowerCase() === "wbfm" && String(program || "main").toLowerCase() === "main")
      return 2;
    return 1;
  }

  async function playPcmStream(body, context, rate, signal, channels, liveGain, onProgress, onSamples) {
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
      const pcm = onSamples ? new Int16Array(frames) : null;
      for (let i = 0; i < frames; i++) {
        let mix = 0;
        for (let c = 0; c < ch; c++) {
          const sample = view.getInt16((i * ch + c) * 2, true);
          source[c][i] = sample / 32768;
          mix += sample;
        }
        if (pcm) pcm[i] = (mix / ch) | 0;
      }
      if (pcm) onSamples(pcm);

      let buffer;
      if (Math.abs(playRate - sourceRate) < 1) {
        buffer = context.createBuffer(ch, frames, playRate);
        for (let c = 0; c < ch; c++) buffer.copyToChannel(source[c], c);
      } else {
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
      node.onended = () => {
        try { node.disconnect(); } catch { /* already gone */ }
      };
      // Keep a short lead-in. If the tab stalled, re-pad once instead of
      // scheduling a clicky trail of near-past buffers.
      if (nextTime < context.currentTime - 0.05)
        nextTime = context.currentTime + 0.12;
      const start = Math.max(nextTime, context.currentTime + 0.02);
      node.start(start);
      nextTime = start + buffer.duration;
      played += buffer.duration;
      if (onProgress) onProgress(played, ch);
      pending = pending.subarray(usable);
    }
  }

  function fillModeSelect(select, preferred) {
    if (!select) return;
    const current = select.value || preferred || "nbfm";
    select.replaceChildren();
    for (const [value, label] of HEAR_MODES) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      select.append(option);
    }
    select.value = [...select.options].some((o) => o.value === current) ? current : (preferred || "nbfm");
  }

  function fillDmrColors(select) {
    if (!select || select.options.length > 1) return;
    for (let code = 0; code <= 15; code++) {
      const option = document.createElement("option");
      option.value = String(code);
      option.textContent = String(code);
      select.append(option);
    }
  }

  function modeLabel(value) {
    return HEAR_MODES.find((item) => item[0] === value)?.[1] || value || "";
  }

  function createPipeline(els) {
    let catalog = [];
    let ids = [];
    let dirty = false;

    function nameOf(id) {
      return catalog.find((p) => p.id === id)?.name || id;
    }

    function render() {
      if (!els.stages) return;
      els.stages.replaceChildren();
      if (els.empty) els.empty.hidden = ids.length > 0;
      ids.forEach((id, index) => {
        if (index > 0) {
          const pipe = document.createElement("li");
          pipe.className = "pipe";
          pipe.textContent = "→";
          els.stages.append(pipe);
        }
        const item = document.createElement("li");
        const label = document.createElement("span");
        label.textContent = nameOf(id);
        item.append(label);
        const up = document.createElement("button");
        up.type = "button";
        up.className = "ghost";
        up.textContent = "↑";
        up.disabled = index === 0;
        up.addEventListener("click", () => move(index, -1));
        const down = document.createElement("button");
        down.type = "button";
        down.className = "ghost";
        down.textContent = "↓";
        down.disabled = index === ids.length - 1;
        down.addEventListener("click", () => move(index, 1));
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "ghost";
        remove.textContent = "×";
        remove.addEventListener("click", () => {
          ids = ids.filter((_, i) => i !== index);
          dirty = true;
          render();
        });
        item.append(up, down, remove);
        els.stages.append(item);
      });
    }

    function move(index, delta) {
      const next = index + delta;
      if (next < 0 || next >= ids.length) return;
      const copy = ids.slice();
      const [item] = copy.splice(index, 1);
      copy.splice(next, 0, item);
      ids = copy;
      dirty = true;
      render();
    }

    function setCatalog(list) {
      catalog = Array.isArray(list) ? list : [];
      if (els.addSelect) {
        els.addSelect.replaceChildren();
        for (const proc of catalog) {
          const option = document.createElement("option");
          option.value = proc.id;
          option.textContent = `${proc.name} (${proc.kind})`;
          els.addSelect.append(option);
        }
      }
      render();
    }

    function setIds(next, markDirty) {
      ids = Array.isArray(next) ? next.slice() : [];
      if (markDirty) dirty = true;
      render();
    }

    if (els.addBtn) {
      els.addBtn.addEventListener("click", () => {
        const id = els.addSelect?.value;
        if (!id) return;
        ids = ids.concat(id);
        dirty = true;
        render();
      });
    }

    return {
      render,
      setCatalog,
      setIds,
      getIds: () => ids.slice(),
      get dirty() { return dirty; },
      set dirty(value) { dirty = !!value; },
      queryParam() {
        return dirty && ids.length > 0 ? ids.join(",") : "";
      }
    };
  }

  function paintDecoded(el, status) {
    if (!el) return;
    const lines = [];
    if (status?.liveText)
      lines.push(escapeHtml(status.liveText));
    const transcript = Array.isArray(status?.transcript) ? status.transcript : [];
    for (const row of transcript) {
      if (!row?.text) continue;
      const kind = (row.kind || row.source || "note").toLowerCase();
      lines.push(`<span class="${kind}">${escapeHtml(row.text)}</span>`
        + (row.source ? `<span class="src">${escapeHtml(row.source)}</span>` : ""));
    }
    el.innerHTML = lines.join("\n") || "Morse, FT8, speech, packet, and Ribbit appear here.";
  }

  /**
   * Connect listener behavior to an existing set of controls.
   * @returns controller
   */
  function connect(options) {
    const apiBase = (options.apiBase || "/api").replace(/\/$/, "");
    const onError = options.onError || (() => {});
    const onLiveChange = options.onLiveChange || (() => {});
    const getFrequencyHz = options.getFrequencyHz
      || (() => Math.round(Number(options.mhz?.value || 0) * 1e6));
    const volumeKey = options.volumeKey || "agentic-sdr-volume";

    const pipeline = createPipeline({
      empty: options.pipelineEmpty,
      stages: options.pipelineStages,
      addSelect: options.pipelineAdd,
      addBtn: options.pipelineAddBtn,
      resetBtn: options.pipelineReset
    });

    let deviceOpen = !!options.deviceOpen;
    let liveAbort = null;
    let liveAudio = null;
    let liveGain = null;
    let listenToken = 0;
    let statusTimer = null;
    let destroyed = false;

    fillModeSelect(options.mode, options.preferredMode || "nbfm");
    fillDmrColors(options.dmrColor);

    if (options.volume) {
      const saved = Number(localStorage.getItem(volumeKey));
      if (Number.isFinite(saved) && saved >= 0 && saved <= 100)
        options.volume.value = String(saved);
      options.volume.addEventListener("input", applyVolume);
    }

    function applyVolume() {
      const level = Number(options.volume?.value);
      if (Number.isFinite(level))
        localStorage.setItem(volumeKey, String(level));
      if (liveGain)
        liveGain.gain.value = Number.isFinite(level) ? level / 100 : 0.8;
    }

    function ensureGain(context) {
      liveGain = context.createGain();
      liveGain.connect(context.destination);
      applyVolume();
      return liveGain;
    }

    function syncModeExtras() {
      const mode = options.mode?.value || "";
      if (options.dmrRoot)
        options.dmrRoot.hidden = mode !== "dmr" && !options.forceDmr;
      if (options.wbfmRoot)
        options.wbfmRoot.hidden = mode !== "wbfm" && !options.forceWbfm;
    }

    async function api(path, init) {
      const response = await fetch(apiBase + path, init);
      const body = await response.json().catch(() => ({}));
      if (!response.ok)
        throw new Error(body.error || response.statusText || "Receiver refused that.");
      return body;
    }

    async function loadProcessors() {
      try {
        pipeline.setCatalog(await api("/processors"));
      } catch {
        pipeline.setCatalog([]);
      }
    }

    async function loadDefaultPipeline(modeName) {
      if (pipeline.dirty) {
        pipeline.render();
        return;
      }
      try {
        const body = await api(`/pipelines/default?mode=${encodeURIComponent(modeName || "nbfm")}`);
        pipeline.setIds(body.pipeline, false);
      } catch {
        pipeline.setIds([], false);
      }
    }

    function stopLive() {
      liveAbort?.abort();
      liveAbort = null;
      if (liveAudio) {
        liveAudio.close().catch(() => {});
        liveAudio = null;
      }
      liveGain = null;
      if (options.liveState) options.liveState.hidden = true;
      if (options.stopBtn) options.stopBtn.hidden = true;
      if (options.listenBtn) options.listenBtn.disabled = !deviceOpen;
      onLiveChange(false);
    }

    function isLive() {
      return liveAbort !== null;
    }

    function paintDevice(status) {
      deviceOpen = !!status?.deviceOpen;
      if (options.aside)
        options.aside.textContent = deviceOpen ? (status.deviceName || "radio open") : "receiver idle";
      if (options.radioStatus) {
        options.radioStatus.textContent = deviceOpen
          ? `Open on the receiver${status.deviceName ? ": " + status.deviceName : ""}.`
          : "No radio open on the receiver.";
      }
      if (options.listenBtn) options.listenBtn.disabled = !deviceOpen || isLive();
      if (options.openBtn) options.openBtn.disabled = deviceOpen;
      if (options.closeBtn) options.closeBtn.disabled = !deviceOpen;
      if (options.stopBtn) options.stopBtn.hidden = !isLive();
    }

    async function refreshStatus() {
      if (destroyed) return null;
      try {
        const status = await api("/status");
        paintDevice(status);
        if (isLive() || options.alwaysDecode)
          paintDecoded(options.decoded, status);
        if (!pipeline.dirty && Array.isArray(status.pipeline) && status.pipeline.length > 0)
          pipeline.setIds(status.pipeline.map((stage) => stage.id), false);
        return status;
      } catch (ex) {
        deviceOpen = false;
        if (options.aside) options.aside.textContent = "receiver offline";
        if (options.radioStatus) options.radioStatus.textContent = ex.message || "Receiver offline.";
        if (options.listenBtn) options.listenBtn.disabled = true;
        if (options.openBtn) options.openBtn.disabled = false;
        if (options.closeBtn) options.closeBtn.disabled = true;
        return null;
      }
    }

    async function loadDevices() {
      if (!options.deviceSelect) return;
      try {
        const devices = await api("/devices");
        options.deviceSelect.replaceChildren();
        const list = Array.isArray(devices) ? devices : [];
        if (list.length === 0) {
          const option = document.createElement("option");
          option.value = "0";
          option.textContent = "No device listed";
          options.deviceSelect.append(option);
          return;
        }
        list.forEach((device, index) => {
          const option = document.createElement("option");
          option.value = String(device.index ?? index);
          option.textContent = device.name || `Device ${index}`;
          options.deviceSelect.append(option);
        });
      } catch {
        options.deviceSelect.replaceChildren();
        const option = document.createElement("option");
        option.value = "0";
        option.textContent = "Receiver offline";
        options.deviceSelect.append(option);
      }
    }

    async function startLive(explicit) {
      const frequencyHz = explicit?.frequencyHz ?? getFrequencyHz();
      const mode = explicit?.mode || options.mode?.value || "nbfm";
      if (!Number.isFinite(frequencyHz) || frequencyHz <= 0) {
        onError("Enter a receive frequency in MHz.");
        return;
      }
      if (options.requireDevice !== false && !deviceOpen) {
        onError("Open a radio on the receiver first.");
        return;
      }

      const token = ++listenToken;
      stopLive();
      onError("");
      const controller = new AbortController();
      liveAbort = controller;
      if (options.listenBtn) options.listenBtn.disabled = true;
      if (options.stopBtn) options.stopBtn.hidden = false;
      if (options.liveState) {
        options.liveState.hidden = false;
        options.liveState.textContent = `Starting ${(frequencyHz / 1e6).toFixed(3)} MHz…`;
      }
      onLiveChange(true);

      const params = new URLSearchParams({
        frequencyHz: String(Math.round(frequencyHz)),
        mode
      });
      if (mode === "dmr") {
        const color = options.dmrColor?.value || "";
        const slot = options.dmrSlot?.value || "";
        const talkgroup = (options.dmrTg?.value || "").trim();
        if (color !== "") params.set("colorCode", color);
        if (slot !== "") params.set("slot", slot);
        if (talkgroup !== "") params.set("talkgroup", talkgroup);
      }
      if (mode === "wbfm")
        params.set("wbfmProgram", options.wbfmProgram?.value || "main");
      const pipe = pipeline.queryParam();
      if (pipe) params.set("pipeline", pipe);

      try {
        if (options.beforeLive)
          await options.beforeLive({ frequencyHz, mode, controller });
        const response = await fetch(`${apiBase}/live?${params}`, { signal: controller.signal });
        if (token !== listenToken) return;
        if (!response.ok) {
          const body = await response.json().catch(() => null);
          throw new Error(body?.error || response.statusText);
        }
        const rate = Number(response.headers.get("X-Audio-Rate")) || 48000;
        const program = (response.headers.get("X-Wbfm-Program") || "main").toLowerCase();
        const channels = channelsFromHeaders(response.headers, mode, program);
        if (options.liveState) {
          options.liveState.textContent = channels > 1
            ? "Playing stereo as the signal arrives."
            : "Playing as the signal arrives.";
        }
        if (options.onLiveHeaders)
          options.onLiveHeaders(response.headers, { frequencyHz, mode, rate, channels });
        const context = new AudioContext({ sampleRate: rate });
        liveAudio = context;
        ensureGain(context);
        await context.resume();
        await playPcmStream(
          response.body,
          context,
          rate,
          controller.signal,
          channels,
          liveGain,
          (played, ch) => {
            if (options.liveState)
              options.liveState.textContent = `Playing${ch > 1 ? " stereo" : ""} · ${played.toFixed(1)} s.`;
            if (options.onPcmProgress)
              options.onPcmProgress(played, ch);
          },
          options.onSamples || null
        );
      } catch (ex) {
        if (ex.name !== "AbortError")
          onError(ex.message || String(ex));
      } finally {
        if (token !== listenToken) return;
        stopLive();
        refreshStatus();
        if (options.afterLive)
          options.afterLive();
      }
    }

    if (options.mode)
      options.mode.addEventListener("change", () => {
        syncModeExtras();
        pipeline.dirty = false;
        loadDefaultPipeline(options.mode.value);
        if (options.onModeChange)
          options.onModeChange(options.mode.value);
      });

    if (options.pipelineReset) {
      options.pipelineReset.addEventListener("click", () => {
        pipeline.dirty = false;
        loadDefaultPipeline(options.mode?.value || "nbfm");
      });
    }

    if (options.listenBtn)
      options.listenBtn.addEventListener("click", () => startLive());

    if (options.stopBtn) {
      options.stopBtn.addEventListener("click", async () => {
        stopLive();
        try { await api("/stop", { method: "POST" }); } catch { /* ignore */ }
        refreshStatus();
      });
    }

    if (options.openBtn) {
      options.openBtn.addEventListener("click", async () => {
        onError("");
        options.openBtn.disabled = true;
        try {
          const index = Number(options.deviceSelect?.value) || 0;
          await api("/open", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ index })
          });
          await refreshStatus();
        } catch (ex) {
          onError(ex.message);
          options.openBtn.disabled = false;
        }
      });
    }

    if (options.closeBtn) {
      options.closeBtn.addEventListener("click", async () => {
        onError("");
        stopLive();
        try { await api("/stop", { method: "POST" }); } catch { /* ignore */ }
        try {
          await api("/close", { method: "POST" });
        } catch (ex) {
          onError(ex.message);
        }
        await refreshStatus();
      });
    }

    if (options.presetsRoot) {
      options.presetsRoot.replaceChildren();
      for (const [label, mhz, mode] of (options.presets || PRESETS)) {
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = label;
        button.addEventListener("click", () => {
          if (options.mhz) options.mhz.value = String(mhz);
          if (options.mode) options.mode.value = mode;
          syncModeExtras();
          pipeline.dirty = false;
          loadDefaultPipeline(mode);
        });
        options.presetsRoot.append(button);
      }
    }

    syncModeExtras();

    const ready = Promise.resolve()
      .then(() => options.deviceSelect ? loadDevices() : null)
      .then(() => loadProcessors())
      .then(() => loadDefaultPipeline(options.mode?.value || "nbfm"))
      .then(() => refreshStatus());

    if (options.pollMs !== 0) {
      statusTimer = setInterval(() => refreshStatus(), options.pollMs || 2000);
    }

    return {
      ready,
      pipeline,
      api,
      startLive,
      stopLive,
      isLive,
      refreshStatus,
      loadDefaultPipeline,
      syncModeExtras,
      paintDecoded: (status) => paintDecoded(options.decoded, status),
      paintDevice,
      getMode: () => options.mode?.value || "nbfm",
      setMode: (mode) => {
        if (!options.mode) return;
        if ([...options.mode.options].some((o) => o.value === mode))
          options.mode.value = mode;
        syncModeExtras();
      },
      get deviceOpen() { return deviceOpen; },
      setDeviceOpen(value) {
        deviceOpen = !!value;
        if (options.listenBtn) options.listenBtn.disabled = !deviceOpen || isLive();
      },
      applyVolume,
      destroy() {
        destroyed = true;
        if (statusTimer) clearInterval(statusTimer);
        stopLive();
      }
    };
  }

  /**
   * Render a desk-style receive panel into root, then connect it.
   */
  function mount(root, options = {}) {
    if (!root) throw new Error("RadioListener.mount needs a root element.");
    const apiBase = options.apiBase || "/api/receive";
    const showHeading = options.heading !== false;
    root.innerHTML = `
      ${showHeading ? '<h2>Receive <span class="aside rl-aside"></span></h2>' : ""}
      <p class="quiet">Listen through the receiver. Filters and decoders run in the signal pipeline. This panel does not transmit.</p>
      <div class="controls rl-device-row">
        <label>Radio
          <select class="rl-device"></select>
        </label>
        <button type="button" class="rl-open">Open radio</button>
        <button type="button" class="ghost rl-close">Close</button>
        <p class="quiet rl-radio-status">Checking the receiver…</p>
      </div>
      <div class="controls">
        <label>MHz
          <input class="rl-mhz" type="number" min="0.1" step="0.001" value="146.52">
        </label>
        <label>Mode
          <select class="rl-mode"></select>
        </label>
        <label>Volume
          <input class="rl-volume" type="range" min="0" max="100" value="80">
        </label>
      </div>
      <div class="controls rl-dmr" hidden>
        <label>Color code
          <select class="rl-dmr-color"><option value="">Any</option></select>
        </label>
        <label>Timeslot
          <select class="rl-dmr-slot">
            <option value="">Any</option>
            <option value="1">1</option>
            <option value="2">2</option>
          </select>
        </label>
        <label>Talkgroup
          <input class="rl-dmr-tg" inputmode="numeric" placeholder="Any">
        </label>
      </div>
      <div class="controls rl-wbfm" hidden>
        <label>Secondary channel
          <select class="rl-wbfm-program">
            <option value="main">Main (stereo)</option>
            <option value="sca67">SCA 67 kHz</option>
            <option value="sca92">SCA 92 kHz</option>
          </select>
        </label>
      </div>
      <div class="presets rl-presets"></div>
      <p class="quiet rl-live" hidden>Playing as the signal arrives.</p>
      <div class="controls">
        <button type="button" class="rl-listen">Listen</button>
        <button type="button" class="ghost rl-stop" hidden>Stop</button>
      </div>
      <h3 class="rl-heading">Signal pipeline</h3>
      <p class="quiet rl-pipeline-empty">Mode picks the default filters and decoders. Edit before you listen.</p>
      <ol class="pipeline-stages rl-pipeline-stages"></ol>
      <div class="controls pipeline-edit rl-pipeline-edit">
        <label>Add
          <select class="rl-pipeline-add"></select>
        </label>
        <button type="button" class="ghost rl-pipeline-add-btn">Add</button>
        <button type="button" class="ghost rl-pipeline-reset">Reset to mode default</button>
      </div>
      <h3 class="rl-heading">Decoded text</h3>
      <pre class="rl-decoded">Morse, FT8, speech, packet, and Ribbit appear here.</pre>
    `;

    const q = (sel) => root.querySelector(sel);
    return connect({
      apiBase,
      onError: options.onError,
      volumeKey: options.volumeKey || "agentic-sdr-station-volume",
      aside: options.aside || q(".rl-aside"),
      deviceSelect: q(".rl-device"),
      openBtn: q(".rl-open"),
      closeBtn: q(".rl-close"),
      radioStatus: q(".rl-radio-status"),
      mhz: q(".rl-mhz"),
      mode: q(".rl-mode"),
      volume: q(".rl-volume"),
      dmrRoot: q(".rl-dmr"),
      dmrColor: q(".rl-dmr-color"),
      dmrSlot: q(".rl-dmr-slot"),
      dmrTg: q(".rl-dmr-tg"),
      wbfmRoot: q(".rl-wbfm"),
      wbfmProgram: q(".rl-wbfm-program"),
      presetsRoot: q(".rl-presets"),
      liveState: q(".rl-live"),
      listenBtn: q(".rl-listen"),
      stopBtn: q(".rl-stop"),
      pipelineEmpty: q(".rl-pipeline-empty"),
      pipelineStages: q(".rl-pipeline-stages"),
      pipelineAdd: q(".rl-pipeline-add"),
      pipelineAddBtn: q(".rl-pipeline-add-btn"),
      pipelineReset: q(".rl-pipeline-reset"),
      decoded: q(".rl-decoded"),
      preferredMode: options.preferredMode || "nbfm",
      pollMs: options.pollMs
    });
  }

  window.RadioListener = {
    HEAR_MODES,
    PRESETS,
    modeLabel,
    fillModeSelect,
    channelsFromHeaders,
    playPcmStream,
    paintDecoded,
    connect,
    mount
  };
})();
