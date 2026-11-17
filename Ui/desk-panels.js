/**
 * Shared Operator, Antenna, and Repeater phone panels for mode desks and Host.
 * Mount into empty hosts. APIs default to /api/... routes used by the mode projects.
 */
(() => {
  function compactNumber(value, digits) {
    return Number(value).toFixed(digits).replace(/0+$/, "").replace(/\.$/, "");
  }

  function licenseSelectValue(license) {
    const key = String(license || "").toLowerCase().replace(/[^a-z]/g, "");
    if (key === "extra" || key === "amateurextra" || key === "e" || key === "ae") return "extra";
    if (key === "general" || key === "gen" || key === "g") return "general";
    if (key === "technician" || key === "tech" || key === "t") return "technician";
    return "extra";
  }

  function licenseLabel(license) {
    const value = licenseSelectValue(license);
    if (value === "extra") return "Amateur Extra";
    if (!license) return "";
    return value.charAt(0).toUpperCase() + value.slice(1);
  }

  function gridSentence(op) {
    if (!op) return "";
    const bits = [op.grid];
    if (op.coordinates) bits.push(op.coordinates);
    if (op.place) bits.push(op.place);
    return bits.filter(Boolean).join(" · ");
  }

  function createClient(options) {
    const base = String(options.apiBase || "").replace(/\/$/, "");
    const onError = typeof options.onError === "function" ? options.onError : () => {};

    async function request(path, init = {}, { quiet = false } = {}) {
      if (!quiet) onError("");
      try {
        const response = await fetch(base + path, init);
        const body = await response.json().catch(() => ({}));
        if (!response.ok) {
          const message = body.error || body.reason || "The station refused that.";
          if (!quiet) onError(message);
          return null;
        }
        return body;
      } catch {
        if (!quiet) onError("The page cannot reach the desk.");
        return null;
      }
    }

    return {
      get: (path, opts) => request(path, { method: "GET" }, opts),
      put: (path, payload, opts) => request(path, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      }, opts),
      post: (path, payload, opts) => request(path, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(payload)
      }, opts),
      send: (path, init, opts) => request(path, init, opts)
    };
  }

  function q(root, sel) {
    return root.querySelector(sel);
  }

  const FOLDS_KEY = "agentic-sdr-folds";

  async function loadUiPrefs(options = {}) {
    const client = createClient({ ...options, onError: () => {} });
    const body = await client.get("/api/ui-prefs", { quiet: true });
    return body && typeof body === "object" ? body : {};
  }

  async function saveUiPrefs(patch, options = {}) {
    const client = createClient({ ...options, onError: () => {} });
    return client.put("/api/ui-prefs", patch || {}, { quiet: true });
  }

  /**
   * Make every details.fold remember open/closed across Host and mode desks.
   * Uses /api/ui-prefs (shared file) with localStorage as a same-origin cache.
   * Call once after the page (and shared mounts) are in the DOM.
   * options: apiBase
   */
  function rememberFolds(scope = document, options = {}) {
    const folds = scope.querySelectorAll("details.fold[data-fold]");
    let saved = {};
    try {
      saved = JSON.parse(localStorage.getItem(FOLDS_KEY) || "{}");
    } catch {
      saved = {};
    }

    function apply(state) {
      folds.forEach((details) => {
        const id = details.dataset.fold;
        if (id && Object.hasOwn(state, id)) details.open = Boolean(state[id]);
      });
    }

    function snapshot() {
      const state = {};
      scope.querySelectorAll("details.fold[data-fold]").forEach((item) => {
        if (item.dataset.fold) state[item.dataset.fold] = item.open;
      });
      return state;
    }

    async function persist() {
      const state = snapshot();
      try {
        localStorage.setItem(FOLDS_KEY, JSON.stringify(state));
      } catch {
        /* Sections still collapse when storage is unavailable. */
      }
      await saveUiPrefs({ folds: state }, options);
    }

    apply(saved);
    folds.forEach((details) => {
      details.addEventListener("toggle", () => { persist(); });
    });

    loadUiPrefs(options).then((prefs) => {
      const shared = prefs.folds && typeof prefs.folds === "object" ? prefs.folds : null;
      if (shared && Object.keys(shared).length) {
        saved = { ...saved, ...shared };
        try { localStorage.setItem(FOLDS_KEY, JSON.stringify(saved)); } catch { /* ignore */ }
        apply(saved);
      } else if (Object.keys(saved).length) {
        saveUiPrefs({ folds: saved }, options);
      }
    });
  }

  /**
   * Operator sign-in, grid lookup, license summary.
   * Restores the shared operator file via GET /api/operator.
   * options: apiBase, onError, onSnapshot(snapshot), open
   */
  function mountOperator(root, options = {}) {
    if (!root) throw new Error("DeskPanels.mountOperator needs a root element.");
    const client = createClient(options);
    const open = options.open !== false;
    root.innerHTML = `
      <details class="panel fold sp-panel" ${open ? "open" : ""} data-fold="operator">
        <summary>
          <span class="fold-title">Operator</span>
          <span class="fold-aside sp-operator-aside"></span>
        </summary>
        <div class="fold-body">
          <p class="quiet">Entering a class attests what you hold. This desk does not look you up in the FCC database. A grid is your Maidenhead square. Look up turns that square into coordinates, or fills it from the published call sign when the box is empty. That lookup is not a license check. The signed-in operator is shared with every Agentic SDR app on this machine.</p>
          <form class="controls sp-operator-form">
            <label>Call sign
              <input class="sp-callsign" name="callsign" autocomplete="off" spellcheck="false" placeholder="W1AW" required>
            </label>
            <label>License class
              <select class="sp-license" name="license">
                <option value="technician">Technician</option>
                <option value="general">General</option>
                <option value="extra" selected>Amateur Extra</option>
              </select>
            </label>
            <label>Grid
              <input class="sp-grid" name="grid" autocomplete="off" spellcheck="false" placeholder="FN42" maxlength="6">
            </label>
            <button class="ghost sp-grid-lookup" type="button">Look up</button>
            <button type="submit">Set control operator</button>
            <button class="ghost sp-sign-out" type="button">Sign out</button>
          </form>
          <p class="quiet sp-grid-place"></p>
          <p class="quiet sp-summary"></p>
        </div>
      </details>
    `;

    const form = q(root, ".sp-operator-form");
    const callsign = q(root, ".sp-callsign");
    const license = q(root, ".sp-license");
    const grid = q(root, ".sp-grid");
    const place = q(root, ".sp-grid-place");
    const summary = q(root, ".sp-summary");
    const aside = q(root, ".sp-operator-aside");
    let lookupNote = "";

    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      const body = await client.post("/api/operator", {
        callSign: callsign.value,
        license: license.value,
        grid: grid.value
      });
      if (!body) return;
      lookupNote = "";
      applyOperator(body.operator || body.operatorIdentity, body.licenseSummary);
      if (typeof options.onSnapshot === "function") options.onSnapshot(body);
    });

    q(root, ".sp-grid-lookup").addEventListener("click", async () => {
      const body = await client.post("/api/grid/lookup", {
        grid: grid.value,
        callSign: callsign.value
      });
      if (!body) return;
      grid.value = body.grid || "";
      lookupNote = body.sentence || "";
      place.textContent = lookupNote;
    });

    q(root, ".sp-sign-out").addEventListener("click", async () => {
      const body = await client.send("/api/operator", { method: "DELETE" });
      if (!body) return;
      lookupNote = "";
      applyOperator(null, null);
      if (typeof options.onSnapshot === "function") options.onSnapshot(body);
    });

    function applyOperator(op, licenseSummary) {
      summary.textContent = licenseSummary || "";
      if (op) {
        callsign.value = op.callSign || "";
        license.value = licenseSelectValue(op.license);
        grid.value = op.grid || "";
        place.textContent = op.grid ? gridSentence(op) : lookupNote;
        if (aside) aside.textContent = `${op.callSign} · ${licenseLabel(op.license)}`;
      } else {
        place.textContent = lookupNote;
        if (aside) aside.textContent = "";
        callsign.value = "";
        grid.value = "";
      }
    }

    async function restore() {
      const body = await client.get("/api/operator", { quiet: true });
      if (!body) return;
      applyOperator(body.operator || body.operatorIdentity, body.licenseSummary);
      if ((body.operator || body.operatorIdentity) && typeof options.onSnapshot === "function")
        options.onSnapshot(body);
    }

    restore();

    return {
      root,
      applyOperator,
      restore,
      setLookupNote(text) { lookupNote = text || ""; },
      elements: { callsign, license, grid, place, summary }
    };
  }

  /**
   * Antenna length / orientation / guess.
   * options: apiBase, onError, open, aside (optional element for fold summary)
   */
  function mountAntenna(root, options = {}) {
    if (!root) throw new Error("DeskPanels.mountAntenna needs a root element.");
    const client = createClient(options);
    const open = options.open !== false;
    root.innerHTML = `
      <details class="panel fold sp-panel" ${open ? "open" : ""} data-fold="antenna">
        <summary>
          <span class="fold-title">Antenna</span>
          <span class="fold-aside sp-antenna-summary"></span>
        </summary>
        <div class="fold-body">
          <p class="quiet">A vertical antenna is a quarter-wave. A horizontal wire is a half-wave. Guess listens to the noise floor and does not transmit. If the receiver already has the radio open, the guess uses that radio. Length and orientation are shared with every Agentic SDR app on this machine.</p>
          <form class="controls sp-antenna-form">
            <label>Length
              <input class="sp-antenna-length" type="number" min="0.05" step="0.01" value="0.75">
            </label>
            <label>Unit
              <select class="sp-antenna-unit">
                <option value="m">meters</option>
                <option value="ft">feet</option>
              </select>
            </label>
            <label>Orientation
              <select class="sp-antenna-orientation">
                <option value="vertical">Vertical</option>
                <option value="horizontal">Horizontal</option>
              </select>
            </label>
            <label>Axis toward ° true
              <input class="sp-antenna-azimuth" type="number" min="0" max="360" value="0" disabled>
            </label>
            <button type="submit">Use this antenna</button>
            <button class="ghost sp-antenna-guess" type="button">Guess from signals</button>
          </form>
          <p class="quiet sp-antenna-note">Set the length and orientation. Matching bands are listed under it.</p>
          <ul class="bands sp-antenna-bands"></ul>
        </div>
      </details>
    `;

    const length = q(root, ".sp-antenna-length");
    const unit = q(root, ".sp-antenna-unit");
    const orientation = q(root, ".sp-antenna-orientation");
    const azimuth = q(root, ".sp-antenna-azimuth");
    const note = q(root, ".sp-antenna-note");
    const bands = q(root, ".sp-antenna-bands");
    const summary = options.aside || q(root, ".sp-antenna-summary");

    function body() {
      return {
        length: Number(length.value),
        unit: unit.value,
        orientation: orientation.value,
        azimuthDegrees: orientation.value === "horizontal" ? Number(azimuth.value) : null
      };
    }

    function sync() {
      const horizontal = orientation.value === "horizontal";
      azimuth.disabled = !horizontal;
      const len = String(length.value).trim();
      const u = unit.value === "ft" ? "ft" : "m";
      if (!summary) return;
      if (!len) summary.textContent = "";
      else if (horizontal) summary.textContent = `${len} ${u}, horizontal, ${Math.round(Number(azimuth.value) || 0)}° true`;
      else summary.textContent = `${len} ${u}, vertical`;
    }

    function paint(advice, fromGuess) {
      if (fromGuess && advice.lengthMeters) {
        unit.value = "m";
        length.value = compactNumber(advice.lengthMeters, 2);
        orientation.value = advice.orientation || "vertical";
        if (advice.azimuthDegrees != null) azimuth.value = String(Math.round(advice.azimuthDegrees));
        sync();
      }
      note.textContent = advice.summary || "";
      bands.replaceChildren();
      for (const band of advice.bands || []) {
        const item = document.createElement("li");
        item.textContent = band.name + (band.reason ? " — " + band.reason : "");
        bands.append(item);
      }
    }

    orientation.addEventListener("change", sync);
    length.addEventListener("input", sync);
    unit.addEventListener("change", sync);
    azimuth.addEventListener("input", sync);
    sync();

    q(root, ".sp-antenna-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      const advice = await client.post("/api/antenna", body());
      if (advice) paint(advice, false);
    });

    q(root, ".sp-antenna-guess").addEventListener("click", async () => {
      const button = q(root, ".sp-antenna-guess");
      button.disabled = true;
      button.textContent = "Listening…";
      note.textContent = "Listening across the dial for where the antenna is strongest. This does not transmit.";
      try {
        const advice = await client.post("/api/antenna/guess", {});
        if (advice) paint(advice, true);
      } finally {
        button.disabled = false;
        button.textContent = "Guess from signals";
      }
    });

    async function restore() {
      const advice = await client.get("/api/antenna", { quiet: true });
      if (!advice || !advice.configured) return;
      const feet = /^\s*[\d.]+\s*ft\b/i.test(advice.summary || "");
      if (feet) {
        unit.value = "ft";
        length.value = compactNumber(advice.lengthMeters / 0.3048, 2);
      } else {
        unit.value = "m";
        length.value = compactNumber(advice.lengthMeters, 2);
      }
      orientation.value = advice.orientation;
      if (advice.azimuthDegrees != null) azimuth.value = String(Math.round(advice.azimuthDegrees));
      sync();
      paint(advice, false);
    }

    restore();

    return { root, restore, paint, sync, body };
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

  /**
   * Repeater book + FM phone settings.
   * options: apiBase, onError, open, enableDraft (default true), onHeld(), draftPlaceholder()
   */
  function mountRepeater(root, options = {}) {
    if (!root) throw new Error("DeskPanels.mountRepeater needs a root element.");
    const client = createClient(options);
    const open = options.open !== false;
    const enableDraft = options.enableDraft !== false;
    root.innerHTML = `
      <details class="panel fold sp-panel" ${open ? "open" : ""} data-fold="repeater">
        <summary>
          <span class="fold-title">Repeater phone</span>
          <span class="fold-aside sp-rep-aside"></span>
        </summary>
        <div class="fold-body">
          <p class="quiet">FM phone on a repeater or simplex. Save machines you use often, then pick one. Output is the published listen frequency. Input is what you transmit, and Part 97 has to allow that frequency for your license before you can hold a draft. Encode opens the machine. Decode is optional receive squelch.</p>
          <div class="controls sp-rep-book-bar">
            <label class="grow">Saved
              <select class="sp-rep-pick">
                <option value="">New repeater…</option>
              </select>
            </label>
            <button class="ghost sp-rep-new" type="button">New</button>
            <button class="sp-rep-save" type="button">Save</button>
            <button class="ghost sp-rep-delete" type="button" disabled>Delete</button>
          </div>
          <form class="stack sp-repeater-form">
            <label class="grow">Label
              <input class="sp-rep-label" autocomplete="off" maxlength="80" placeholder="Club machine · town">
            </label>
            <div class="controls">
              <label>Output MHz
                <input class="sp-rep-out" type="number" min="0.1" step="0.001" placeholder="146.940" required>
              </label>
              <label>Shift
                <select class="sp-rep-shift">
                  <option value="minus" selected>Minus (−)</option>
                  <option value="plus">Plus (+)</option>
                  <option value="simplex">Simplex</option>
                  <option value="split">Split</option>
                </select>
              </label>
              <label>Offset kHz
                <input class="sp-rep-offset" type="number" min="0" step="0.001" placeholder="600">
              </label>
              <label>Input MHz
                <input class="sp-rep-in" type="number" min="0.1" step="0.001" placeholder="146.340" disabled>
              </label>
              <label>Watts
                <input class="sp-rep-watts" type="number" min="0.1" max="1500" step="0.1" value="5" required>
              </label>
            </div>
            <div class="controls">
              <label>Encode
                <select class="sp-rep-encode-mode">
                  <option value="none" selected>None</option>
                  <option value="ctcss">CTCSS</option>
                  <option value="dcs">DCS</option>
                </select>
              </label>
              <label>Encode CTCSS Hz
                <select class="sp-rep-encode-ctcss" disabled></select>
              </label>
              <label>Encode DCS
                <select class="sp-rep-encode-dcs" disabled></select>
              </label>
              <label>Decode
                <select class="sp-rep-decode-mode">
                  <option value="none" selected>None</option>
                  <option value="ctcss">CTCSS</option>
                  <option value="dcs">DCS</option>
                </select>
              </label>
              <label>Decode CTCSS Hz
                <select class="sp-rep-decode-ctcss" disabled></select>
              </label>
              <label>Decode DCS
                <select class="sp-rep-decode-dcs" disabled></select>
              </label>
              <label class="check">Narrow FM
                <input class="sp-rep-narrow" type="checkbox" checked>
              </label>
            </div>
            <div class="controls">
              <label>Repeater call
                <input class="sp-rep-call" autocomplete="off" spellcheck="false" placeholder="W1AW/R" maxlength="20">
              </label>
              <label>Name
                <input class="sp-rep-name" autocomplete="off" placeholder="Landmark" maxlength="80">
              </label>
              <label>City
                <input class="sp-rep-city" autocomplete="off" placeholder="City" maxlength="80">
              </label>
            </div>
            <label class="grow">Notes
              <input class="sp-rep-notes" autocomplete="off" maxlength="400" placeholder="Optional notes">
            </label>
            ${enableDraft ? `
            <label class="grow">What you will say
              <textarea class="sp-rep-text" rows="2" maxlength="2000" required placeholder="This is W1AW monitoring"></textarea>
            </label>` : ""}
            <p class="quiet sp-rep-resolved">Set the output and shift. Standard offsets fill in for the band.</p>
            <p class="quiet sp-rep-privilege"></p>
            <div class="controls">
              <button class="ghost sp-rep-check" type="button">Check privilege</button>
              ${enableDraft ? `<button class="sp-rep-hold" type="submit" disabled>Hold repeater draft</button>` : ""}
            </div>
          </form>
        </div>
      </details>
    `;

    const pick = q(root, ".sp-rep-pick");
    const aside = q(root, ".sp-rep-aside");
    const hold = q(root, ".sp-rep-hold");
    const privilege = q(root, ".sp-rep-privilege");
    const resolved = q(root, ".sp-rep-resolved");
    const text = q(root, ".sp-rep-text");
    let selectedId = "";
    let withinLicense = false;
    let privilegeTimer = 0;

    function el(name) {
      return q(root, `.sp-rep-${name}`);
    }

    function repeaterBody() {
      const shift = el("shift").value;
      const offsetKhz = Number(el("offset").value);
      const encodeMode = el("encode-mode").value;
      const decodeMode = el("decode-mode").value;
      return {
        outputFrequencyHz: Math.round(Number(el("out").value) * 1e6),
        inputFrequencyHz: shift === "split" ? Math.round(Number(el("in").value) * 1e6) : null,
        shift,
        offsetHz: shift === "plus" || shift === "minus"
          ? Math.round((Number.isFinite(offsetKhz) ? offsetKhz : 0) * 1e3)
          : null,
        encodeMode,
        encodeCtcssHz: encodeMode === "ctcss" ? Number(el("encode-ctcss").value) : null,
        encodeDcs: encodeMode === "dcs" ? el("encode-dcs").value : null,
        decodeMode,
        decodeCtcssHz: decodeMode === "ctcss" ? Number(el("decode-ctcss").value) : null,
        decodeDcs: decodeMode === "dcs" ? el("decode-dcs").value : null,
        narrow: el("narrow").checked,
        repeaterCallSign: el("call").value,
        name: el("name").value,
        city: el("city").value,
        notes: el("notes").value
      };
    }

    function fillToneLists(catalog) {
      for (const key of ["encode-ctcss", "decode-ctcss"]) {
        const select = el(key);
        select.replaceChildren();
        for (const tone of catalog.ctcssHz || []) {
          const option = document.createElement("option");
          option.value = String(tone);
          option.textContent = Number(tone).toFixed(1);
          select.append(option);
        }
      }
      for (const key of ["encode-dcs", "decode-dcs"]) {
        const select = el(key);
        select.replaceChildren();
        for (const code of catalog.dcsCodes || []) {
          const option = document.createElement("option");
          option.value = code;
          option.textContent = code;
          select.append(option);
        }
      }
    }

    function setPrivilege(decision, fallback) {
      const allowed = !!(decision && decision.withinLicense);
      withinLicense = allowed;
      if (hold) hold.disabled = !allowed;
      privilege.classList.toggle("ok", allowed);
      privilege.classList.toggle("bad-text", !!(decision && decision.withinLicense === false));
      privilege.textContent = decision
        ? decision.reason + (decision.caution ? " " + decision.caution : "")
        : (fallback || "");
    }

    async function checkPrivilege(force) {
      const out = Number(el("out").value);
      if (!Number.isFinite(out) || out <= 0) {
        setPrivilege(null, "Set the output frequency before holding a draft.");
        return;
      }
      try {
        const response = await fetch((String(options.apiBase || "").replace(/\/$/, "")) + "/api/repeater/privilege", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            powerWatts: Number(el("watts").value),
            repeater: repeaterBody()
          })
        });
        const body = await response.json().catch(() => ({}));
        if (!response.ok) {
          setPrivilege(null, body.error || "Sign in with your call and license to check this machine.");
          if (force && typeof options.onError === "function")
            options.onError(body.error || "Could not check privilege.");
          return;
        }
        setPrivilege(body);
      } catch {
        setPrivilege(null, "The page cannot reach the desk.");
        if (force && typeof options.onError === "function")
          options.onError("The page cannot reach the desk.");
      }
    }

    function schedulePrivilege() {
      window.clearTimeout(privilegeTimer);
      privilegeTimer = window.setTimeout(() => checkPrivilege(false), 250);
    }

    function sync() {
      const shift = el("shift").value;
      const out = Number(el("out").value);
      const offsetField = el("offset");
      const inField = el("in");
      const encodeMode = el("encode-mode").value;
      const decodeMode = el("decode-mode").value;
      offsetField.disabled = shift !== "plus" && shift !== "minus";
      inField.disabled = shift !== "split";
      el("encode-ctcss").disabled = encodeMode !== "ctcss";
      el("encode-dcs").disabled = encodeMode !== "dcs";
      el("decode-ctcss").disabled = decodeMode !== "ctcss";
      el("decode-dcs").disabled = decodeMode !== "dcs";

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

      if (!Number.isFinite(out) || out <= 0) {
        resolved.textContent = "Set the output and shift. Standard offsets fill in for the band.";
        setPrivilege(null, "Set the output frequency before holding a draft.");
        return;
      }
      const bits = [`out ${compactNumber(out, 4)} MHz`];
      if (Number.isFinite(input) && input > 0) bits.push(`in ${compactNumber(input, 4)} MHz`);
      bits.push(shift);
      if ((shift === "plus" || shift === "minus") && offsetField.value) bits.push(`${offsetField.value} kHz`);
      bits.push(encodeMode === "none" ? "encode none" : `encode ${encodeMode}`);
      if (decodeMode !== "none") bits.push(`decode ${decodeMode}`);
      bits.push(el("narrow").checked ? "narrow" : "wide");
      const call = el("call").value.trim();
      if (call) bits.push(call);
      resolved.textContent = bits.join(" · ");
      schedulePrivilege();
    }

    function paintAccess(access, label, powerWatts) {
      if (!access) return;
      el("label").value = label || access.name || access.repeaterCallSign || "";
      el("out").value = compactNumber(access.outputFrequencyHz / 1e6, 4);
      el("shift").value = String(access.shift).toLowerCase();
      if (access.offsetHz != null) el("offset").value = compactNumber(access.offsetHz / 1e3, 3);
      el("in").value = compactNumber(access.inputFrequencyHz / 1e6, 4);
      if (powerWatts != null && Number.isFinite(powerWatts))
        el("watts").value = String(powerWatts);
      el("encode-mode").value = String(access.encodeMode).toLowerCase();
      if (access.encodeCtcssHz != null) el("encode-ctcss").value = String(access.encodeCtcssHz);
      if (access.encodeDcs) el("encode-dcs").value = access.encodeDcs;
      el("decode-mode").value = String(access.decodeMode).toLowerCase();
      if (access.decodeCtcssHz != null) el("decode-ctcss").value = String(access.decodeCtcssHz);
      if (access.decodeDcs) el("decode-dcs").value = access.decodeDcs;
      el("narrow").checked = access.narrow !== false;
      el("call").value = access.repeaterCallSign || "";
      el("name").value = access.name || "";
      el("city").value = access.city || "";
      el("notes").value = access.notes || "";
      resolved.textContent = access.summary || "";
      sync();
    }

    function clearForm() {
      selectedId = "";
      el("label").value = "";
      el("out").value = "";
      el("shift").value = "minus";
      el("offset").value = "";
      el("in").value = "";
      el("encode-mode").value = "none";
      el("decode-mode").value = "none";
      el("narrow").checked = true;
      el("call").value = "";
      el("name").value = "";
      el("city").value = "";
      el("notes").value = "";
      resolved.textContent = "Set the output and shift. Standard offsets fill in for the band.";
      el("delete").disabled = true;
      sync();
    }

    function paintBook(book, fillForm) {
      const items = Array.isArray(book?.items) ? book.items : [];
      const id = book?.selectedId || "";
      selectedId = id;
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
      pick.value = id;
      el("delete").disabled = !id;
      const selected = items.find((item) => item.id === id);
      aside.textContent = selected ? selected.label : (items.length ? `${items.length} saved` : "None saved");
      if (fillForm && selected?.access)
        paintAccess(selected.access, selected.label, selected.powerWatts);
      else if (fillForm && !selected)
        clearForm();
      else
        sync();
    }

    async function restoreBook(fillForm = true) {
      const book = await client.get("/api/repeater");
      if (book) paintBook(book, fillForm);
      else sync();
    }

    async function restore() {
      const catalog = await client.get("/api/repeater/catalog");
      if (catalog) fillToneLists(catalog);
      await restoreBook(true);
    }

    pick.addEventListener("change", async () => {
      const id = pick.value;
      if (!id) {
        clearForm();
        const book = await client.send("/api/repeater/selected", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ id: null })
        });
        if (book) paintBook(book, false);
        return;
      }
      const book = await client.send("/api/repeater/selected", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id })
      });
      if (book) paintBook(book, true);
    });

    el("new").addEventListener("click", async () => {
      clearForm();
      const book = await client.send("/api/repeater/selected", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: null })
      });
      if (book) paintBook(book, false);
    });

    el("save").addEventListener("click", async () => {
      const payload = {
        label: el("label").value,
        powerWatts: Number(el("watts").value),
        repeater: repeaterBody()
      };
      const book = selectedId
        ? await client.send(`/api/repeater/${encodeURIComponent(selectedId)}`, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload)
        })
        : await client.post("/api/repeater", payload);
      if (book) paintBook(book, true);
    });

    el("delete").addEventListener("click", async () => {
      if (!selectedId) return;
      const label = pick.selectedOptions[0]?.textContent || "this repeater";
      if (!window.confirm(`Delete ${label}?`)) return;
      const book = await client.send(`/api/repeater/${encodeURIComponent(selectedId)}`, { method: "DELETE" });
      if (!book) return;
      clearForm();
      paintBook(book, true);
    });

    el("check").addEventListener("click", () => checkPrivilege(true));

    for (const name of ["out", "shift", "offset", "in", "encode-mode", "decode-mode", "call", "narrow", "label", "name", "city", "watts"]) {
      const node = el(name);
      node.addEventListener("change", sync);
      node.addEventListener("input", sync);
    }

    if (enableDraft) {
      q(root, ".sp-repeater-form").addEventListener("submit", async (event) => {
        event.preventDefault();
        if (!withinLicense) {
          if (typeof options.onError === "function")
            options.onError("That repeater is outside your license. Part 97 is judged on the transmit (input) frequency.");
          await checkPrivilege(true);
          return;
        }
        const body = await client.post("/api/drafts", {
          text: text.value,
          frequencyHz: 0,
          mode: "nbfm",
          powerWatts: Number(el("watts").value),
          repeater: repeaterBody()
        });
        if (!body) return;
        text.value = "";
        await restoreBook(false);
        if (typeof options.onHeld === "function") await options.onHeld(body);
      });
    }

    function setDraftPlaceholder(call) {
      if (!text) return;
      text.placeholder = call ? `This is ${call} monitoring` : "This is W1AW monitoring";
    }

    return {
      root,
      restore,
      restoreBook,
      schedulePrivilege,
      setDraftPlaceholder,
      isWithinLicense: () => withinLicense
    };
  }

  /**
   * USB radio open/close panel. Uses /api/devices, /api/open, /api/close, /api/radio-details,
   * and /api/status (or /api/radio). Remembers the preferred device via the desk backend.
   * options: apiBase, onError, onChange(status), open, ownEvents (default true), preferIds (default true),
   * beforeClose(): return false / Promise&lt;false&gt; to cancel Stop radio
   */
  function mountRadio(root, options = {}) {
    if (!root) throw new Error("DeskPanels.mountRadio needs a root element.");
    const client = createClient(options);
    const open = options.open !== false;
    const ownEvents = options.ownEvents !== false;
    const preferIds = options.preferIds !== false;
    const id = (name) => (preferIds ? ` id="${name}"` : "");
    root.innerHTML = `
      <details class="panel fold sp-panel" ${open ? "open" : ""} data-fold="radio">
        <summary data-tip="radio">
          <span class="fold-title">Radio</span>
          <span class="fold-aside sp-radio-aside"${id("radio-summary")}></span>
        </summary>
        <div class="fold-body">
          <p class="quiet">Start opens the selected USB radio. Stop releases it. The preferred radio is remembered for every Agentic SDR app on this machine.</p>
          <div class="controls">
            <label data-tip="device">Radio
              <select class="sp-radio-device"${id("radio")}></select>
            </label>
            <button type="button" class="sp-radio-start"${id("radio-start")}>Start radio</button>
            <button type="button" class="ghost sp-radio-stop"${id("radio-stop")}>Stop radio</button>
            <button type="button" class="ghost sp-radio-details"${id("radio-details")}>Radio details</button>
          </div>
          <label class="check sp-radio-simulate">
            <input type="checkbox" class="sp-radio-simulate-tx"${id("radio-simulate-tx")}>
            Simulate transmit capable (experimental — RF goes nowhere)
          </label>
          <p class="quiet sp-radio-note">No radio open.</p>
        </div>
      </details>
      <dialog class="sp-radio-details-dialog"${id("radio-details-panel")}>
        <h2 class="sp-radio-details-title"${id("radio-details-title")}>Radio details</h2>
        <dl class="sp-radio-details-list"${id("radio-details-list")}></dl>
        <div class="controls">
          <button type="button" class="ghost sp-radio-details-close"${id("radio-details-close")}>Close</button>
        </div>
      </dialog>
    `;

    const deviceSelect = q(root, ".sp-radio-device");
    const startBtn = q(root, ".sp-radio-start");
    const stopBtn = q(root, ".sp-radio-stop");
    const detailsBtn = q(root, ".sp-radio-details");
    const simulateBox = q(root, ".sp-radio-simulate-tx");
    const aside = q(root, ".sp-radio-aside");
    const note = q(root, ".sp-radio-note");
    const dialog = q(root, ".sp-radio-details-dialog");
    const detailsTitle = q(root, ".sp-radio-details-title");
    const detailsList = q(root, ".sp-radio-details-list");
    const detailsClose = q(root, ".sp-radio-details-close");

    let devices = [];
    let status = { deviceOpen: false, canTransmit: false, simulateTransmit: false };

    function paint() {
      const openNow = !!(status.deviceOpen || status.open);
      const name = status.deviceName || status.name || "";
      const simulated = !!(status.simulateTransmit ?? simulateBox?.checked);
      const canTx = !!(status.canTransmit ?? status.CanTransmit) || simulated;
      const label = openNow
        ? `${name || "Radio open"}${canTx ? (simulated ? " · TX simulated" : " · can TX") : " · receive only"}`
        : (deviceSelect.selectedOptions[0]?.textContent || "No radio");
      if (aside) {
        aside.textContent = openNow
          ? (canTx ? (simulated ? "Open · TX sim" : "Open · TX") : "Open · RX")
          : "Stopped";
      }
      if (note) {
        note.textContent = openNow
          ? `${name || "Radio"} is open${canTx
            ? (simulated ? " with simulated transmit (nothing radiates)." : " and can transmit.")
            : " (receive only)."}`
          : "No radio open.";
      }
      if (simulateBox)
        simulateBox.checked = simulated;
      startBtn.disabled = openNow || deviceSelect.options.length === 0 || deviceSelect.options[0]?.disabled;
      stopBtn.disabled = !openNow;
      detailsBtn.disabled = deviceSelect.options.length === 0 || deviceSelect.options[0]?.disabled;
      if (typeof options.onChange === "function") {
        options.onChange({
          ...status,
          deviceOpen: openNow,
          canTransmit: canTx,
          simulateTransmit: simulated,
          label
        });
      }
    }

    async function loadDevices() {
      const list = await client.get("/api/devices", { quiet: true });
      devices = Array.isArray(list) ? list : [];
      const previous = deviceSelect.value;
      deviceSelect.replaceChildren();
      if (devices.length === 0) {
        const option = document.createElement("option");
        option.textContent = "No radio found";
        option.disabled = true;
        deviceSelect.append(option);
      } else {
        for (const device of devices) {
          const option = document.createElement("option");
          option.value = String(device.index ?? 0);
          option.textContent = device.serial ? `${device.name} · ${device.serial}` : (device.name || `Device ${device.index}`);
          deviceSelect.append(option);
        }
        const preferred = await client.get("/api/radio/preference", { quiet: true });
        const prefer = preferred && preferred.index != null ? String(preferred.index) : "";
        const openIdx = status.deviceIndex != null ? String(status.deviceIndex) : "";
        const keep = [openIdx, prefer, previous].find((value) =>
          value !== "" && [...deviceSelect.options].some((option) => option.value === value));
        if (keep) deviceSelect.value = keep;
      }
      paint();
    }

    async function refreshStatus() {
      const body = await client.get("/api/status", { quiet: true })
        || await client.get("/api/radio", { quiet: true });
      if (body) {
        status = {
          deviceOpen: !!(body.deviceOpen ?? body.open),
          open: !!(body.deviceOpen ?? body.open),
          deviceName: body.deviceName || body.name || null,
          name: body.deviceName || body.name || null,
          serial: body.serial || null,
          deviceIndex: body.deviceIndex ?? null,
          canTransmit: !!(body.canTransmit),
          simulateTransmit: !!(body.simulateTransmit)
        };
      }
      paint();
      return status;
    }

    async function loadSimulate() {
      const body = await client.get("/api/radio/simulate-transmit", { quiet: true });
      if (body && typeof body.enabled === "boolean")
        status = { ...status, simulateTransmit: !!body.enabled };
      else if (body && typeof body.simulateTransmit === "boolean")
        status = { ...status, simulateTransmit: !!body.simulateTransmit };
      if (simulateBox)
        simulateBox.checked = !!status.simulateTransmit;
      paint();
    }

    async function setSimulateTransmit(enabled) {
      const body = await client.post("/api/radio/simulate-transmit", { enabled: !!enabled });
      if (body) {
        status = {
          ...status,
          canTransmit: !!(body.canTransmit),
          simulateTransmit: !!(body.simulateTransmit ?? enabled),
          deviceOpen: !!(body.deviceOpen ?? body.open ?? status.deviceOpen),
          open: !!(body.deviceOpen ?? body.open ?? status.open),
          deviceName: body.deviceName || body.name || status.deviceName,
          name: body.deviceName || body.name || status.name,
          serial: body.serial || status.serial,
          deviceIndex: body.deviceIndex ?? status.deviceIndex
        };
      } else {
        status = { ...status, simulateTransmit: !!enabled };
      }
      paint();
      return status;
    }

    async function startRadio() {
      const index = Number(deviceSelect.value);
      const body = await client.post("/api/open", { index });
      if (!body) return null;
      status = {
        deviceOpen: true,
        open: true,
        deviceName: body.deviceName || body.name || devices.find((d) => Number(d.index) === index)?.name || null,
        name: body.deviceName || body.name || null,
        serial: body.serial || null,
        deviceIndex: body.deviceIndex ?? index,
        canTransmit: !!(body.canTransmit),
        simulateTransmit: !!(body.simulateTransmit ?? status.simulateTransmit)
      };
      await loadDevices();
      await refreshStatus();
      return status;
    }

    async function stopRadio() {
      if (typeof options.beforeClose === "function") {
        const allow = await options.beforeClose({ status, deviceSelect });
        if (allow === false)
          return null;
      }
      await client.post("/api/stop", {}, { quiet: true });
      const body = await client.post("/api/close", {});
      if (body === null) return null;
      status = {
        deviceOpen: false,
        open: false,
        canTransmit: false,
        simulateTransmit: !!(body?.simulateTransmit ?? status.simulateTransmit),
        deviceName: null,
        name: null,
        deviceIndex: null
      };
      await refreshStatus();
      await loadDevices();
      return status;
    }

    async function showDetails() {
      if (dialog.open && dialog.dataset.index === deviceSelect.value) {
        dialog.close();
        return;
      }
      dialog.dataset.index = deviceSelect.value;
      detailsTitle.textContent = "Reading radio details…";
      detailsList.replaceChildren();
      if (!dialog.open) dialog.showModal();
      const report = await client.get(`/api/radio-details?index=${encodeURIComponent(deviceSelect.value)}`);
      if (!report) {
        dialog.close();
        return;
      }
      detailsTitle.textContent = report.title || "Radio details";
      detailsList.replaceChildren();
      for (const fact of report.facts || []) {
        const label = document.createElement("dt");
        label.textContent = fact.label;
        const value = document.createElement("dd");
        value.textContent = fact.value;
        detailsList.append(label, value);
      }
    }

    if (ownEvents) {
      startBtn.addEventListener("click", () => startRadio());
      stopBtn.addEventListener("click", () => stopRadio());
      detailsBtn.addEventListener("click", () => showDetails());
      detailsClose.addEventListener("click", () => dialog.close());
      deviceSelect.addEventListener("change", () => {
        if (dialog.open) dialog.close();
        paint();
      });
      deviceSelect.addEventListener("focus", () => loadDevices());
      if (simulateBox) {
        simulateBox.addEventListener("change", () => {
          setSimulateTransmit(simulateBox.checked).catch((err) => {
            simulateBox.checked = !!status.simulateTransmit;
            if (typeof options.onError === "function")
              options.onError(err.message || String(err));
          });
        });
      }
    }

    refreshStatus().then(loadDevices).then(loadSimulate);

    return {
      root,
      elements: { deviceSelect, startBtn, stopBtn, detailsBtn, simulateBox, aside, note, dialog },
      loadDevices,
      refreshStatus,
      loadSimulate,
      setSimulateTransmit,
      startRadio,
      stopRadio,
      showDetails,
      getStatus: () => status
    };
  }

  /**
   * Shared desk switcher: Receiver + mode desks on their loopback ports.
   * Fetches /api/apps when present; otherwise uses the built-in catalog.
   */
  const FALLBACK_APPS = [
    { id: "receiver", name: "Receiver", url: "http://127.0.0.1:5088/", kind: "receive", available: true, blurb: "Explore and Camp — receive only" },
    { id: "cw", name: "CW", url: "http://127.0.0.1:5091/", kind: "mode", available: true, blurb: "Morse desk — listen + experimental transmit" },
    { id: "ft8", name: "FT8", url: "", kind: "mode", available: false, blurb: "Mode library ready — desk follows CW" },
    { id: "js8", name: "JS8", url: "", kind: "mode", available: false, blurb: "Mode library ready — desk follows CW" },
    { id: "olivia", name: "Olivia", url: "", kind: "mode", available: false, blurb: "Mode library ready — desk follows CW" },
    { id: "jtty", name: "JTTY", url: "", kind: "mode", available: false, blurb: "Mode library ready — desk follows CW" },
    { id: "packet", name: "Packet", url: "", kind: "mode", available: false, blurb: "Mode library ready — desk follows CW" },
    { id: "ribbit", name: "Ribbit", url: "", kind: "mode", available: false, blurb: "Mode library ready — desk follows CW" }
  ];

  function mountAppNav(root, options = {}) {
    if (!root) throw new Error("DeskPanels.mountAppNav needs a root element.");
    const current = String(options.current || "").toLowerCase();
    const client = createClient({ ...options, onError: () => {} });

    root.classList.add("sp-app-nav");
    root.setAttribute("aria-label", "Agentic SDR apps");
    root.innerHTML = `
      <div class="sp-app-nav-row" role="list"></div>
      <p class="sp-app-nav-note quiet">Each app is its own process. Only one should hold the USB radio at a time.</p>
    `;
    const row = q(root, ".sp-app-nav-row");

    function paint(apps, currentId) {
      const list = Array.isArray(apps) && apps.length ? apps : FALLBACK_APPS;
      const here = String(currentId || current || "").toLowerCase();
      row.replaceChildren();
      for (const app of list) {
        const id = String(app.id || "").toLowerCase();
        const available = !!app.available && !!app.url;
        const isCurrent = id === here || !!app.current;
        const el = document.createElement(available && !isCurrent ? "a" : "span");
        el.className = "sp-app-link";
        el.setAttribute("role", "listitem");
        el.dataset.id = id;
        if (isCurrent) {
          el.classList.add("is-current");
          el.setAttribute("aria-current", "page");
        }
        if (!available) {
          el.classList.add("is-soon");
          el.title = app.blurb || "Desk not available yet";
        } else {
          el.title = app.blurb || app.name || id;
        }
        if (available && !isCurrent) {
          el.href = app.url;
        }
        el.textContent = app.name || id;
        row.append(el);
      }
    }

    paint(FALLBACK_APPS, current);
    client.get("/api/apps", { quiet: true }).then((body) => {
      if (!body) return;
      paint(body.apps, body.current || current);
    });

    return { refresh: () => client.get("/api/apps", { quiet: true }).then((body) => {
      if (body) paint(body.apps, body.current || current);
    }) };
  }

  window.DeskPanels = {
    compactNumber,
    licenseSelectValue,
    licenseLabel,
    gridSentence,
    loadUiPrefs,
    saveUiPrefs,
    rememberFolds,
    mountAppNav,
    mountOperator,
    mountAntenna,
    mountRadio,
    mountRepeater
  };
})();
