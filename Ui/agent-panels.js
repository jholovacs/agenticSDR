/**
 * Shared Model + Prompt panels. LLM settings persist via /api/llm (same file across apps).
 */
(() => {
  function createClient(options) {
    const base = String(options.apiBase || "").replace(/\/$/, "");
    const onError = typeof options.onError === "function" ? options.onError : () => {};

    async function request(path, init = {}, { quiet = false } = {}) {
      if (!quiet) onError("");
      try {
        const response = await fetch(base + path, init);
        const text = await response.text();
        let body = null;
        try { body = text ? JSON.parse(text) : null; } catch { body = { error: text }; }
        if (!response.ok) {
          const message = body?.error || body?.reason || response.statusText || "The desk refused that.";
          if (!quiet) onError(message);
          throw new Error(message);
        }
        return body;
      } catch (err) {
        if (err && err.message && !quiet && !/desk refused|endpoint|model|Save an/i.test(err.message))
          onError(err.message || "The page cannot reach the desk.");
        throw err;
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
      }, opts)
    };
  }

  function q(root, sel) {
    return root.querySelector(sel);
  }

  /**
   * options: apiBase, onError, open, preferIds, note (custom empty-state note),
   * onChange(view)
   */
  function mountModel(root, options = {}) {
    if (!root) throw new Error("AgentPanels.mountModel needs a root element.");
    const client = createClient(options);
    const open = options.open !== false;
    const preferIds = options.preferIds !== false;
    const id = (name) => (preferIds ? ` id="${name}"` : "");
    const emptyNote = options.note
      || "An instruct model that speaks the OpenAI chat protocol can run tools on this desk. Ask it in Prompt.";

    root.innerHTML = `
      <details class="panel fold sp-panel" ${open ? "open" : ""} data-fold="model">
        <summary>
          <span class="fold-title">Model</span>
          <span class="fold-aside sp-model-aside"></span>
        </summary>
        <div class="fold-body">
          <p class="quiet">Point at an OpenAI-compatible chat server (Ollama, LM Studio, a cloud API). Settings are shared with every Agentic SDR app on this machine.</p>
          <div class="controls">
            <label>Endpoint
              <input class="sp-llm-base"${id("llm-base")} type="url" placeholder="http://10.10.26.10:11434" autocomplete="off">
            </label>
            <label>Model
              <input class="sp-llm-model"${id("llm-model")} list="llm-models" placeholder="llama3.2" autocomplete="off">
              <datalist id="llm-models" class="sp-llm-models"></datalist>
            </label>
            <label>API key
              <input class="sp-llm-key"${id("llm-key")} type="password" autocomplete="off" placeholder="Optional for Ollama">
            </label>
            <button type="button" class="sp-llm-models-btn"${id("llm-models-btn")}>List models</button>
            <button type="button" class="ghost sp-llm-save"${id("llm-save")}>Save</button>
            <button type="button" class="ghost sp-llm-forget"${id("llm-forget")}>Forget key</button>
            <div class="llm-picks sp-llm-picks"${id("llm-model-picks")} hidden></div>
            <p class="quiet sp-llm-note"${id("llm-note")}>For Ollama, the endpoint can be just the host, such as http://10.10.26.10:11434.</p>
          </div>
        </div>
      </details>
    `;

    const baseInput = q(root, ".sp-llm-base");
    const modelInput = q(root, ".sp-llm-model");
    const keyInput = q(root, ".sp-llm-key");
    const note = q(root, ".sp-llm-note");
    const aside = q(root, ".sp-model-aside");
    const datalist = q(root, ".sp-llm-models");
    const picks = q(root, ".sp-llm-picks");
    let view = { baseUrl: "", model: "", hasKey: false, keyHint: "" };

    function paint() {
      aside.textContent = view.baseUrl
        ? `${view.model || "model"} · ${view.baseUrl.replace(/^https?:\/\//, "")}`
        : "Not configured";
      note.textContent = view.baseUrl
        ? `Using ${view.model || "a model"} at ${view.baseUrl}.`
        : emptyNote;
      if (typeof options.onChange === "function")
        options.onChange(view);
    }

    async function load() {
      view = await client.get("/api/llm");
      baseInput.value = view.baseUrl || "";
      modelInput.value = view.model || "";
      keyInput.value = "";
      keyInput.placeholder = view.hasKey
        ? `Saved (ends ${view.keyHint}). Leave blank to keep it.`
        : "Optional for a local server";
      paint();
      return view;
    }

    async function save(clearKey) {
      view = await client.put("/api/llm", {
        baseUrl: baseInput.value,
        model: modelInput.value,
        apiKey: clearKey ? "" : keyInput.value,
        clearKey: !!clearKey
      });
      keyInput.value = "";
      keyInput.placeholder = view.hasKey
        ? `Saved (ends ${view.keyHint}). Leave blank to keep it.`
        : "Optional for a local server";
      note.textContent = view.baseUrl
        ? `Saved ${view.model || "the model"} at ${view.baseUrl}.`
        : "The endpoint was cleared.";
      paint();
      return view;
    }

    async function listModels() {
      await save(false);
      const names = (await client.get("/api/llm/models")) || [];
      datalist.replaceChildren();
      picks.replaceChildren();
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
          save(false).catch((err) => options.onError?.(err.message));
        });
        picks.append(button);
      }
      if (names.length && !modelInput.value.trim()) {
        modelInput.value = names[0];
        picks.querySelector("button")?.classList.add("active");
        await save(false);
      }
      note.textContent = names.length
        ? `Found ${names.length} model${names.length === 1 ? "" : "s"}. Click one to use it.`
        : "The endpoint listed no models.";
      paint();
      return names;
    }

    q(root, ".sp-llm-save").addEventListener("click", () => {
      save(false).catch((err) => options.onError?.(err.message));
    });
    q(root, ".sp-llm-forget").addEventListener("click", () => {
      save(true).catch((err) => options.onError?.(err.message));
    });
    q(root, ".sp-llm-models-btn").addEventListener("click", () => {
      listModels().catch((err) => options.onError?.(err.message));
    });

    load().catch((err) => options.onError?.(err.message));

    return { root, load, save, listModels, getView: () => view, elements: { baseInput, modelInput, keyInput, note, aside } };
  }

  /**
   * options: apiBase, onError, open, preferIds, placeholder, emptyText, foldNote,
   * beforeAsk(): return false to cancel, onReply(reply), canAsk(): boolean
   */
  function mountPrompt(root, options = {}) {
    if (!root) throw new Error("AgentPanels.mountPrompt needs a root element.");
    const client = createClient(options);
    const open = options.open !== false;
    const preferIds = options.preferIds !== false;
    const id = (name) => (preferIds ? ` id="${name}"` : "");
    const placeholder = options.placeholder || "Ask the agent what to do.";
    const emptyText = options.emptyText || "Nothing asked yet.";
    const foldNote = options.foldNote || "Tell the agent what to do.";

    root.innerHTML = `
      <details class="panel fold sp-panel" ${open ? "open" : ""} data-fold="prompt">
        <summary>
          <span class="fold-title">Prompt</span>
          <span class="fold-aside sp-prompt-aside"></span>
        </summary>
        <div class="fold-body">
          <p class="quiet">${foldNote}</p>
          <div class="sp-prompt-log"${id("prompt-log")} aria-live="polite">
            <p class="quiet sp-prompt-empty"${id("prompt-empty")}>${emptyText}</p>
          </div>
          <form class="sp-prompt-form"${id("prompt-form")}>
            <label class="sp-ask">What should we do?
              <textarea class="sp-llm-ask"${id("llm-ask")} rows="3" placeholder="${placeholder}"></textarea>
            </label>
            <button type="submit" class="sp-llm-send"${id("llm-send")}>Send</button>
          </form>
        </div>
      </details>
    `;

    const log = q(root, ".sp-prompt-log");
    const empty = q(root, ".sp-prompt-empty");
    const aside = q(root, ".sp-prompt-aside");
    const askBox = q(root, ".sp-llm-ask");
    const form = q(root, ".sp-prompt-form");
    const history = [];
    let abort = null;

    function paintAside() {
      const turns = log.querySelectorAll(".sp-turn");
      if (turns.length === 0) {
        aside.textContent = "";
        return;
      }
      const last = turns[turns.length - 1];
      const body = last.querySelector("p:last-child")?.textContent || "";
      aside.textContent = body.length > 48 ? `${body.slice(0, 47)}…` : body;
    }

    function addTurn(kind, who, text, steps) {
      if (empty) empty.hidden = true;
      const turn = document.createElement("article");
      turn.className = `sp-turn ${kind}`;
      const label = document.createElement("p");
      label.className = "sp-who";
      label.textContent = who;
      turn.append(label);
      if (steps) {
        const detail = document.createElement("p");
        detail.className = "sp-steps";
        detail.textContent = steps;
        turn.append(detail);
      }
      const body = document.createElement("p");
      body.textContent = text;
      turn.append(body);
      log.append(turn);
      log.scrollTop = log.scrollHeight;
      paintAside();
      return turn;
    }

    async function ask() {
      const question = askBox.value.trim();
      if (!question) return;
      if (typeof options.canAsk === "function" && options.canAsk() === false)
        return;
      if (abort) {
        options.onError?.("The agent is already working on a prompt.");
        return;
      }
      if (typeof options.beforeAsk === "function") {
        const allow = await options.beforeAsk({ question });
        if (allow === false) return;
      }

      const asked = addTurn("user", "You", question);
      const pending = addTurn("agent", "Agent", "Working on it…");
      asked.scrollIntoView({ block: "nearest" });
      askBox.value = "";
      const controller = new AbortController();
      abort = controller;
      try {
        const response = await fetch((String(options.apiBase || "").replace(/\/$/, "")) + "/api/agent", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ message: question, history }),
          signal: controller.signal
        });
        const text = await response.text();
        let reply = null;
        try { reply = text ? JSON.parse(text) : null; } catch { reply = { error: text }; }
        if (!response.ok)
          throw new Error(reply?.error || response.statusText);
        history.push({ role: "user", content: question });
        history.push({ role: "assistant", content: reply.answer });
        while (history.length > 8) history.shift();
        const steps = (reply.actions || []).map((action) => `${action.tool}: ${action.result}`).join("\n");
        pending.querySelector("p:last-child").textContent = reply.answer;
        if (steps) {
          const detail = document.createElement("p");
          detail.className = "sp-steps";
          detail.textContent = steps;
          pending.querySelector(".sp-who").after(detail);
        }
        paintAside();
        if (typeof options.onReply === "function")
          options.onReply(reply);
      } catch (err) {
        const reason = err.name === "AbortError" ? "Stopped." : (err.message || String(err));
        pending.querySelector("p:last-child").textContent = reason;
        paintAside();
        askBox.value = question;
        options.onError?.(reason);
      } finally {
        abort = null;
        if (typeof options.onSettled === "function")
          options.onSettled();
      }
    }

    form.addEventListener("submit", (event) => {
      event.preventDefault();
      ask().catch((err) => options.onError?.(err.message));
    });
    askBox.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && !event.shiftKey) {
        event.preventDefault();
        ask().catch((err) => options.onError?.(err.message));
      }
    });

    return {
      root,
      ask,
      abort: () => abort?.abort(),
      addTurn,
      getHistory: () => history.slice(),
      elements: { log, askBox, aside, form }
    };
  }

  window.AgentPanels = { mountModel, mountPrompt };
})();
