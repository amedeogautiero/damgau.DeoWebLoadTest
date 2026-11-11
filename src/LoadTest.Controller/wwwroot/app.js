(() => {
  const $ = id => document.getElementById(id);
  const terminal = new Set(["Completed", "Failed", "Cancelled"]);
  const state = { runs: [], selectedId: null, filter: "all", source: null, progress: null, refreshTimer: null };
  const number = value => new Intl.NumberFormat("it-IT", { maximumFractionDigits: 0 }).format(value ?? 0);
  const decimal = (value, digits = 2) => new Intl.NumberFormat("it-IT", { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value ?? 0);
  const escapeHtml = value => String(value ?? "—").replace(/[&<>"']/g, c => ({ "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;" })[c]);
  const isActive = run => !terminal.has(run.status || "") && !["Finished", "Completed", "Failed", "Cancelled"].includes(run.state);
  const statusOf = run => run.status || (run.state === "Finished" ? "Completed" : run.state || "Queued");
  const statusClass = status => status === "Failed" ? "failed" : status === "Completed" ? "completed" : status === "Cancelled" ? "cancelled" : "running";
  const duration = seconds => {
    const s = Math.max(0, Math.floor(seconds || 0)), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
    return h ? [h,m,sec].map(x => String(x).padStart(2,"0")).join(":") : [m,sec].map(x => String(x).padStart(2,"0")).join(":");
  };
  const shortId = id => id ? id.slice(0, 12) : "—";
  const formatTime = value => value ? new Date(value).toLocaleString("it-IT") : "Ora non disponibile";
  const formatMs = value => value > 0 ? decimal(value, 1) : "—";
  const setStream = (label, connected = false, closed = false) => {
    $("streamLabel").textContent = label;
    $("streamDot").className = "stream-dot" + (connected ? " connected" : closed ? " closed" : "");
  };

  async function request(path) {
    const response = await fetch(path, { headers: { "Accept": "application/json" } });
    if (!response.ok) throw new Error(`HTTP ${response.status} su ${path}`);
    return response.json();
  }

  function renderOverview() {
    $("totalRuns").textContent = number(state.runs.length);
    $("activeRuns").textContent = number(state.runs.filter(isActive).length);
    $("completedRuns").textContent = number(state.runs.filter(r => statusOf(r) === "Completed").length);
    $("failedRuns").textContent = number(state.runs.filter(r => ["Failed","Cancelled"].includes(statusOf(r))).length);
    $("runCount").textContent = number(state.runs.length);
  }

  function renderRuns() {
    const list = $("runList");
    const filtered = state.runs.filter(r => state.filter === "all" || (state.filter === "active" ? isActive(r) : !isActive(r)));
    if (!filtered.length) {
      list.innerHTML = '<div class="empty-state">Nessuna run in questa categoria.</div>';
      return;
    }
    list.innerHTML = filtered.map(run => {
      const status = statusOf(run), active = isActive(run);
      const selected = run.runId === state.selectedId;
      const count = run.requests ?? 0;
      return `<button class="run-item ${selected ? "selected" : ""}" data-run-id="${escapeHtml(run.runId)}">
        <div class="run-item-top"><span class="run-item-name">${escapeHtml(run.name || "Run senza nome")}</span><span class="status-text"><i class="status-dot ${statusClass(status)}"></i>${escapeHtml(status)}</span></div>
        <div class="run-id">${escapeHtml(shortId(run.runId))}</div>
        <div class="run-item-meta"><span>${escapeHtml(run.state || status)}</span><span>${number(count)} req.</span></div>
      </button>`;
    }).join("");
    list.querySelectorAll("[data-run-id]").forEach(button => button.addEventListener("click", () => selectRun(button.dataset.runId)));
  }

  function renderProgress(progress) {
    state.progress = progress;
    $("runIdShort").textContent = shortId(progress.runId);
    const run = state.runs.find(r => r.runId === progress.runId);
    $("runName").textContent = run?.name || "Run " + shortId(progress.runId);
    $("runState").textContent = progress.status || "—";
    $("runState").className = "status-pill " + statusClass(progress.status);
    $("runStarted").textContent = "Avviata: " + formatTime(progress.startedUtc);
    $("duration").textContent = duration(progress.elapsedSeconds);
    $("users").textContent = number(progress.activeUsers);
    $("requests").textContent = number(progress.completedRequests);
    $("errors").innerHTML = number(progress.failedRequests) + ' <em>' + decimal(progress.completedRequests > 0 ? 100 * progress.failedRequests / progress.completedRequests : 0) + '%</em>';
    $("testsCompleted").textContent = number(progress.completedTests);
    $("testsFailed").textContent = number(progress.failedTests);
    $("throughput").innerHTML = decimal(progress.requestsPerSecond) + ' <em>req/s</em>';
    const rows = (progress.series || []).filter(x => x.kind === "Request").sort((a,b) => (b.count || 0) - (a.count || 0));
    $("seriesCount").textContent = rows.length + (rows.length === 1 ? " richiesta" : " richieste");
    $("requestRows").innerHTML = rows.length ? rows.map(item => {
      const requestName = item.requestName || item.pageName || item.transactionName || "—";
      return `<tr><td>${escapeHtml(item.scenarioName)}</td><td>${escapeHtml(item.testName)}</td><td class="request-name">${escapeHtml(requestName)}</td><td class="numeric">${number(item.count)}</td><td class="numeric">${formatMs(item.p50Milliseconds)}</td><td class="numeric">${formatMs(item.p95Milliseconds)}</td><td class="numeric">${formatMs(item.p99Milliseconds)}</td><td class="numeric">${formatMs(item.maxMilliseconds)}</td><td class="numeric">${number(item.failed)}</td></tr>`;
    }).join("") : '<tr><td colspan="9" class="table-empty">Nessuna richiesta misurata.</td></tr>';
    $("snapshotInfo").textContent = "Snapshot #" + number(progress.sequence) + " · aggiornato " + (progress.capturedUtc ? new Date(progress.capturedUtc).toLocaleTimeString("it-IT") : "—");
    $("emptyDetail").hidden = true;
    $("runDetail").hidden = false;
    if (terminal.has(progress.status)) setStream("Esecuzione terminata", false, true);
  }

  function closeStream() {
    if (state.source) { state.source.close(); state.source = null; }
  }

  async function selectRun(id) {
    if (!id) return;
    closeStream();
    state.selectedId = id;
    state.progress = null;
    renderRuns();
    $("emptyDetail").hidden = true;
    $("runDetail").hidden = false;
    setStream("Caricamento…");
    try {
      const progress = await request(`/api/runs/${encodeURIComponent(id)}/progress`);
      if (state.selectedId !== id) return;
      renderProgress(progress);
      if (terminal.has(progress.status)) { setStream("Esecuzione terminata", false, true); return; }
      const source = new EventSource(`/api/runs/${encodeURIComponent(id)}/events`);
      state.source = source;
      setStream("Connessione live…");
      source.addEventListener("progress", event => {
        if (state.selectedId !== id) return;
        try {
          const snapshot = JSON.parse(event.data);
          renderProgress(snapshot);
          setStream(terminal.has(snapshot.status) ? "Esecuzione terminata" : "Live · SSE", !terminal.has(snapshot.status), terminal.has(snapshot.status));
          if (terminal.has(snapshot.status)) { source.close(); if (state.source === source) state.source = null; refreshRuns(false); }
        } catch (error) { console.error("Snapshot SSE non valido", error); }
      });
      source.onopen = () => { if (state.selectedId === id) setStream("Live · SSE", true); };
      source.onerror = () => {
        if (state.selectedId === id && !terminal.has(state.progress?.status)) setStream("Riconnessione SSE…");
      };
    } catch (error) {
      console.error(error);
      setStream("Errore di caricamento");
      $("requestRows").innerHTML = '<tr><td colspan="9" class="table-empty">' + escapeHtml(error.message) + '</td></tr>';
    }
  }

  async function refreshRuns(autoSelect = true) {
    try {
      state.runs = await request("/api/runs");
      renderOverview();
      renderRuns();
      $("lastUpdated").textContent = "Aggiornato alle " + new Date().toLocaleTimeString("it-IT");
      if (state.selectedId) {
        const selected = state.runs.find(r => r.runId === state.selectedId);
        if (selected) {
          $("runName").textContent = selected.name || "Run " + shortId(selected.runId);
          if (!state.progress && autoSelect) await selectRun(selected.runId);
        } else {
          closeStream(); state.selectedId = null;
          $("runDetail").hidden = true; $("emptyDetail").hidden = false;
        }
      } else if (autoSelect && state.runs.length) {
        const first = state.runs.find(isActive) || state.runs[0];
        await selectRun(first.runId);
      }
    } catch (error) {
      console.error(error);
      $("runList").innerHTML = '<div class="error-state">' + escapeHtml(error.message) + '</div>';
      $("lastUpdated").textContent = "Errore di connessione";
    }
  }

  document.querySelectorAll(".filter").forEach(button => button.addEventListener("click", () => {
    document.querySelectorAll(".filter").forEach(x => x.classList.toggle("active", x === button));
    state.filter = button.dataset.filter; renderRuns();
  }));
  $("refreshButton").addEventListener("click", () => refreshRuns(false));
  refreshRuns();
  state.refreshTimer = setInterval(() => refreshRuns(false), 3000);
  window.addEventListener("beforeunload", closeStream);
})();