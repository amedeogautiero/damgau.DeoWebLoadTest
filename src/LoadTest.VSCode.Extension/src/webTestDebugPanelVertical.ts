import * as vscode from "vscode";
import { DebugRequest, WebTestDebugSession } from "./webTestDebugSession";

export class WebTestDebugPanelVertical implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private selectedRequestId: number | undefined;
  private disposed = false;
  private webviewReady = false;
  private readonly sentRequests = new Map<number, DebugRequest>();
  private lastSentSelectedRequestId: number | undefined;
  private lastSentSelectedRequest: DebugRequest | null | undefined;
  private readonly durationValues = new Map<number, number>();
  private durationSumMs = 0;
  private durationMinMs = Number.POSITIVE_INFINITY;
  private durationMaxMs = 0;

  constructor(
    private readonly session: WebTestDebugSession,
    private readonly onRun: () => void,
    private readonly onDebug: () => void,
    private readonly onStop: () => void,
    private readonly onClear: () => void,
    private readonly onHostStart: () => void,
    private readonly onHostStop: () => void
  ) {}

  show(): void {
    if (this.disposed) return;
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel(
        "deoloadtest.debugPanelVertical",
        "DeoLoadTest Debug Panel - Vertical",
        vscode.ViewColumn.Beside,
        { enableScripts: true, enableFindWidget: true, localResourceRoots: [] }
      );
      this.webviewReady = false;
      this.panel.webview.onDidReceiveMessage(message => {
        if (message?.command === "ready") {
          this.webviewReady = true;
          this.sendSnapshot(true);
        } else if (message?.command === "run") {
          this.onRun();
        } else if (message?.command === "debug") {
          this.onDebug();
        } else if (message?.command === "stop") {
          this.onStop();
        } else if (message?.command === "clear") {
          this.onClear();
        } else if (message?.command === "hostStart") {
          this.onHostStart();
        } else if (message?.command === "hostStop") {
          this.onHostStop();
        } else if (message?.command === "select" && Number.isInteger(message.requestId)) {
          this.selectedRequestId = message.requestId;
          const selectedRequestId = this.selectedRequestId;
          const request = selectedRequestId === undefined
            ? null
            : this.sentRequests.get(selectedRequestId) ?? null;
          this.lastSentSelectedRequestId = selectedRequestId;
          this.lastSentSelectedRequest = request;
          void this.panel?.webview.postMessage({ command: "selectedRequest", request });
        }
      });
      this.panel.onDidDispose(() => {
        this.panel = undefined;
        this.selectedRequestId = undefined;
        this.webviewReady = false;
        this.sentRequests.clear();
        this.lastSentSelectedRequestId = undefined;
        this.lastSentSelectedRequest = undefined;
        this.resetDurationStats();
      });
      this.panel.webview.html = this.renderHtml();
    } else {
      this.panel.reveal(vscode.ViewColumn.Beside, true);
      if (this.webviewReady) this.sendSnapshot(true);
    }
  }

  update(changedRequestId?: number): void {
    if (this.panel && this.webviewReady) this.sendSnapshot(false, changedRequestId);
  }

  private sendSnapshot(force: boolean, changedRequestId?: number): void {
    if (!this.panel || !this.webviewReady) return;

    const requests = this.session.requests;
    const resetRequests = force || (requests.length === 0 && this.sentRequests.size > 0);

    if (resetRequests) {
      this.sentRequests.clear();
      this.selectedRequestId = undefined;
    }

    const changedRequests: Array<{ slot: number; request: DebugRequest }> = [];
    if (force) {
      this.resetDurationStats();
      for (let index = 0; index < requests.length; index++) {
        const request = requests[index];
        changedRequests.push({ slot: index + 1, request });
        this.sentRequests.set(request.id, request);
        this.recordDuration(request);
      }
    } else if (changedRequestId !== undefined) {
      const requestIndex = requests[requests.length - 1]?.id === changedRequestId
        ? requests.length - 1
        : requests.findIndex(item => item.id === changedRequestId);
      const request = requestIndex >= 0 ? requests[requestIndex] : undefined;
      if (request && this.sentRequests.get(request.id) !== request) {
        changedRequests.push({ slot: requestIndex + 1, request });
        this.sentRequests.set(request.id, request);
        this.recordDuration(request);
      }
    }

    if (this.selectedRequestId === undefined || !this.sentRequests.has(this.selectedRequestId)) {
      this.selectedRequestId = requests.length ? requests[requests.length - 1].id : undefined;
    }
    const selectedRequest = this.selectedRequestId === undefined
      ? null
      : this.sentRequests.get(this.selectedRequestId) ?? null;
    const selectedChanged = force ||
      this.selectedRequestId !== this.lastSentSelectedRequestId ||
      selectedRequest !== this.lastSentSelectedRequest;
    this.lastSentSelectedRequestId = this.selectedRequestId;
    this.lastSentSelectedRequest = selectedRequest;

    const succeeded = this.session.successfulRequests;
    const failed = this.session.failedRequests;
    const completed = succeeded + failed;
    const durationCount = this.durationValues.size;
    const start = this.parseTime(this.session.startedAt);
    const end = this.parseTime(this.session.finishedAt) ?? Date.now();
    const elapsedMs = start === undefined ? 0 : Math.max(0, end - start);

    void this.panel.webview.postMessage({
      command: "state",
      status: this.session.status,
      testName: this.session.testName ?? "No WebTest selected",
      runId: this.session.runId ?? "—",
      startedAt: this.session.startedAt ?? "—",
      finishedAt: this.session.finishedAt ?? (this.session.status === "running" ? "In progress" : "—"),
      totalRequests: this.session.totalRequests,
      visibleRequests: requests.length,
      successfulRequests: succeeded,
      failedRequests: failed,
      successRate: completed ? succeeded / completed * 100 : 0,
      elapsedMs,
      requestsPerSecond: elapsedMs > 0 ? completed / (elapsedMs / 1000) : 0,
      averageMs: durationCount ? this.durationSumMs / durationCount : null,
      minMs: durationCount ? this.durationMinMs : null,
      maxMs: durationCount ? this.durationMaxMs : null,
      failures: this.session.summary?.failures ?? [],
      requests: changedRequests,
      resetRequests,
      ...(selectedChanged ? { selectedRequest } : {})
    });
  }

  private recordDuration(request: DebugRequest): void {
    if (typeof request.elapsedMicros !== "number" || this.durationValues.has(request.id)) return;
    const durationMs = request.elapsedMicros / 1000;
    this.durationValues.set(request.id, durationMs);
    this.durationSumMs += durationMs;
    this.durationMinMs = Math.min(this.durationMinMs, durationMs);
    this.durationMaxMs = Math.max(this.durationMaxMs, durationMs);
  }

  private resetDurationStats(): void {
    this.durationValues.clear();
    this.durationSumMs = 0;
    this.durationMinMs = Number.POSITIVE_INFINITY;
    this.durationMaxMs = 0;
  }

  private parseTime(value?: string): number | undefined {
    if (!value) return undefined;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  private renderHtml(): string {
    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<style>${this.styles()}</style>
</head>
<body>
<header>
  <div><h1>DeoLoadTest Debug Panel - Vertical</h1><div class="test-name" id="test-name">No WebTest selected</div></div>
  <div class="toolbar">
    <button id="host-start" hidden>▶ Start Host</button><button id="host-stop" class="danger" hidden>■ Stop Host</button>
    <button id="run">▶ Run</button><button id="debug">🐞 Debug</button><button id="stop" class="danger" disabled>■ Stop</button>
    <button id="clear" disabled>Clear</button><span id="status" class="status muted"><i></i>Idle</span>
  </div>
</header>
<main>
  <section class="explorer">
    <div class="section-heading">REQUEST EXPLORER <span id="request-count">0</span></div>
    <div class="request-list" id="request-list"></div>
  </section>
  <section class="inspector">
    <div class="section-heading">REQUEST INSPECTOR</div>
    <div id="inspector-content" class="inspector-content"><div class="empty">Select a request to inspect its details.</div></div>
  </section>
</main>
<section class="summary">
  <div class="section-heading">EXECUTION SUMMARY <span id="summary-status">Idle</span></div>
  <div class="metrics">
    <div class="metric"><span>Requests</span><strong id="metric-requests">0</strong></div>
    <div class="metric"><span>Successful</span><strong id="metric-success" class="success">0</strong></div>
    <div class="metric"><span>Failed</span><strong id="metric-failed">0</strong></div>
    <div class="metric"><span>Success rate</span><strong id="metric-rate">—</strong></div>
    <div class="metric"><span>Total duration</span><strong id="metric-duration">0.00 ms</strong></div>
    <div class="metric"><span>Requests/sec</span><strong id="metric-throughput">0.00</strong></div>
    <div class="metric"><span>Avg response</span><strong id="metric-average">—</strong></div>
    <div class="metric"><span>Min response</span><strong id="metric-min">—</strong></div>
    <div class="metric"><span>Max response</span><strong id="metric-max">—</strong></div>
  </div>
  <div class="summary-footer"><span id="summary-started">Started: —</span><span id="summary-finished">Finished: —</span><span id="summary-runid">Run ID: —</span></div>
  <div id="summary-failures" class="failures" hidden></div>
</section>
<script>
(function () {
  const vscode = acquireVsCodeApi();
  const BATCH_SIZE = 100;
  const PREFETCH_REMAINING = 5;
  const list = document.getElementById("request-list");
  const inspector = document.getElementById("inspector-content");
  const rows = new Map();
  let allocatedSlots = 0;
  let selectedRequestId = null;
  let sessionStartedAt = null;

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function formatMs(value) {
    return Number(value).toFixed(2) + " ms";
  }

  function formatValue(value) {
    let text;
    if (typeof value === "string") {
      text = value;
      try {
        const parsed = JSON.parse(value);
        if (parsed !== null && typeof parsed === "object") text = JSON.stringify(parsed, null, 2);
      } catch (_) {}
    } else {
      try { text = JSON.stringify(value, null, 2); }
      catch (_) { text = String(value); }
    }
    if (text === undefined) text = String(value);
    return text.length > 65536 ? text.slice(0, 65536) + "\\n[Display truncated at 64 KB]" : text;
  }

  function addSlots(count) {
    const fragment = document.createDocumentFragment();
    const target = allocatedSlots + count;
    for (let id = allocatedSlots + 1; id <= target; id++) {
      const row = element("button", "request-row empty-row");
      row.type = "button";
      row.disabled = true;
      row.dataset.slot = String(id);
      const main = element("span", "request-main");
      main.appendChild(element("span", "request-id", "#" + String(id).padStart(3, "0")));
      main.appendChild(element("span", "method", ""));
      main.appendChild(element("span", "request-url", ""));
      const track = element("span", "timeline-track");
      track.appendChild(element("span", "timeline-bar"));
      main.appendChild(track);
      const meta = element("span", "request-meta");
      meta.appendChild(element("span", "request-state", ""));
      meta.appendChild(element("span", "request-duration", ""));
      meta.appendChild(element("span", "request-offset", ""));
      row.append(main, meta);
      rows.set(id, row);
      fragment.appendChild(row);
    }
    list.appendChild(fragment);
    allocatedSlots = target;
  }

  function ensureCapacity(requestCount) {
    if (allocatedSlots === 0) addSlots(BATCH_SIZE);
    while (requestCount >= allocatedSlots - PREFETCH_REMAINING) addSlots(BATCH_SIZE);
  }

  function trimEmptySlots(requestCount) {
    for (let slot = allocatedSlots; slot > requestCount; slot--) {
      const row = rows.get(slot);
      if (row) row.remove();
      rows.delete(slot);
    }
    allocatedSlots = requestCount;
  }

  function updateRow(request, slot) {
    ensureCapacity(slot);
    let row = rows.get(slot);
    if (!row) return;
    row.disabled = false;
    row.classList.remove("empty-row");
    row.dataset.select = String(request.id);
    delete row.dataset.slot;
    row.querySelector(".method").textContent = request.method || "HTTP";
    row.querySelector(".request-url").textContent = shortUrl(request.url);
    const state = request.status === "succeeded" ? "Succeeded" : request.status === "failed" ? "Failed" : "Running";
    const stateNode = row.querySelector(".request-state");
    stateNode.textContent = state;
    stateNode.className = "request-state " + (request.status === "succeeded" ? "success" : request.status === "failed" ? "failure" : "muted");
    row.querySelector(".request-duration").textContent = typeof request.elapsedMicros === "number" ? formatMs(request.elapsedMicros / 1000) : "—";
    const start = Date.parse(request.startedAt || "");
    const base = Date.parse(sessionStartedAt || "");
    const offset = Number.isFinite(start) && Number.isFinite(base) ? Math.max(0, start - base) : 0;
    row.querySelector(".request-offset").textContent = "+" + (Number.isFinite(start) && Number.isFinite(base) ? formatMs(offset) : "—");
    const bar = row.querySelector(".timeline-bar");
    const duration = typeof request.elapsedMicros === "number" ? request.elapsedMicros / 1000 : 1;
    const scale = 300000;
    const left = Math.min(95, offset / scale * 100);
    const width = Math.max(1, Math.min(100 - left, duration / scale * 100));
    bar.style.left = left.toFixed(2) + "%";
    bar.style.width = width.toFixed(2) + "%";
    bar.className = "timeline-bar " + (request.status === "failed" ? "failed" : request.status === "succeeded" ? "succeeded" : "pending");
    row.classList.toggle("selected", selectedRequestId === request.id);
  }

  function shortUrl(value) {
    if (!value) return "(URL unavailable)";
    try {
      const url = new URL(value);
      return url.host + url.pathname + url.search;
    } catch (_) { return value; }
  }

  function addBlock(parent, title, value) {
    if (value === undefined || value === null || value === "") return;
    parent.appendChild(element("h3", "", title));
    parent.appendChild(element("pre", "", formatValue(value)));
  }

  function addDetail(parent, label, value) {
    const item = element("div", "");
    item.appendChild(element("span", "muted", label));
    item.appendChild(element("strong", "", value === undefined || value === null || value === "" ? "—" : value));
    parent.appendChild(item);
  }

  function flattenDataRows(snapshot) {
    const result = {};
    const tables = snapshot && snapshot.dataRows ? snapshot.dataRows : {};
    Object.keys(tables).forEach(tableName => {
      Object.keys(tables[tableName] || {}).forEach(key => {
        result[tableName + "." + key] = tables[tableName][key];
      });
    });
    return result;
  }

  function addContextTable(parent, title, beforeValues, afterValues, isPending) {
    parent.appendChild(element("h4", "", title));
    const keys = Array.from(new Set(Object.keys(beforeValues || {}).concat(Object.keys(afterValues || {})))).sort();
    if (!keys.length) {
      parent.appendChild(element("div", "empty", "No values"));
      return;
    }
    const table = element("table");
    const thead = element("thead");
    const header = element("tr");
    ["Name", "At request start", "After request", "Change"].forEach(label => header.appendChild(element("th", "", label)));
    thead.appendChild(header);
    table.appendChild(thead);
    const tbody = element("tbody");
    keys.forEach(key => {
      const hasBefore = Object.prototype.hasOwnProperty.call(beforeValues || {}, key);
      const hasAfter = Object.prototype.hasOwnProperty.call(afterValues || {}, key);
      const beforeText = hasBefore ? formatValue(beforeValues[key]) : "";
      const afterText = hasAfter ? formatValue(afterValues[key]) : "";
      const changed = hasBefore && hasAfter && beforeText !== afterText;
      const state = isPending ? "Pending" : !hasBefore ? "Added" : !hasAfter ? "Removed" : changed ? "Modified" : "Unchanged";
      const row = element("tr");
      row.appendChild(element("td", "", key));
      row.appendChild(element("td")).appendChild(element("pre", "", beforeText || "—"));
      row.appendChild(element("td")).appendChild(element("pre", "", afterText || "—"));
      row.appendChild(element("td", state === "Added" ? "success" : state === "Removed" ? "failure" : state === "Modified" ? "warning" : "", state));
      tbody.appendChild(row);
    });
    table.appendChild(tbody);
    parent.appendChild(table);
  }

  function renderInspector(request) {
    selectedRequestId = request ? request.id : null;
    document.querySelectorAll(".request-row[data-select]").forEach(row => {
      row.classList.toggle("selected", Number(row.dataset.select) === selectedRequestId);
    });
    inspector.replaceChildren();
    if (!request) {
      inspector.appendChild(element("div", "empty", "Select a request to inspect its details."));
      return;
    }
    const title = element("div", "detail-title");
    title.appendChild(element("strong", "", "Request #" + request.id + " · " + (request.status === "succeeded" ? "Succeeded" : request.status === "failed" ? "Failed" : "Running")));
    title.appendChild(element("span", "", (request.method || "HTTP") + " · " + (request.statusCode === undefined ? "No response" : request.statusCode + " " + (request.reasonPhrase || ""))));
    inspector.appendChild(title);
    inspector.appendChild(element("div", "url", request.url || "URL unavailable"));
    const grid = element("div", "detail-grid");
    addDetail(grid, "Request name", request.name);
    const started = Date.parse(request.startedAt || "");
    const base = Date.parse(sessionStartedAt || "");
    addDetail(grid, "Start offset", Number.isFinite(started) && Number.isFinite(base) ? formatMs(Math.max(0, started - base)) : "—");
    addDetail(grid, "Duration", typeof request.elapsedMicros === "number" ? formatMs(request.elapsedMicros / 1000) : request.status === "pending" ? "In progress" : "Unavailable");
    addDetail(grid, "Started", request.startedAt);
    addDetail(grid, "Completed", request.completedAt);
    inspector.appendChild(grid);
    addBlock(inspector, "Request headers", request.requestHeaders);
    addBlock(inspector, "Request body", request.requestBody);
    addBlock(inspector, "Response headers", request.responseHeaders);
    addBlock(inspector, "Response body", request.responseBody);
    if (request.errorType || request.errorMessage || request.exceptionDetails) {
      inspector.appendChild(element("h3", "failure", "Error"));
      addBlock(inspector, "Type", request.errorType);
      addBlock(inspector, "Message", request.errorMessage);
      addBlock(inspector, "Exception chain", request.exceptionDetails);
    }
    addBlock(inspector, "WebTest definition (parsed YAML node)", request.requestDefinition);
    inspector.appendChild(element("h3", "", "Request context"));
    if (!request.contextAtStart && !request.contextAtEnd) {
      inspector.appendChild(element("div", "empty", "Context snapshot not available for this request."));
    } else {
      addContextTable(inspector, "Variables", request.contextAtStart?.variables || {}, request.contextAtEnd?.variables || {}, !request.contextAtEnd);
      addContextTable(inspector, "Data rows", flattenDataRows(request.contextAtStart), flattenDataRows(request.contextAtEnd), !request.contextAtEnd);
    }
  }

  function updateSummary(state) {
    const completed = state.successfulRequests + state.failedRequests;
    document.getElementById("test-name").textContent = state.testName;
    document.getElementById("request-count").textContent = String(state.visibleRequests);
    document.getElementById("metric-requests").textContent = String(state.totalRequests);
    document.getElementById("metric-success").textContent = String(state.successfulRequests);
    const failed = document.getElementById("metric-failed");
    failed.textContent = String(state.failedRequests);
    failed.className = state.failedRequests ? "failure" : "";
    document.getElementById("metric-rate").textContent = completed ? state.successRate.toFixed(2) + "%" : "—";
    document.getElementById("metric-duration").textContent = formatMs(state.elapsedMs);
    document.getElementById("metric-throughput").textContent = state.requestsPerSecond.toFixed(2);
    document.getElementById("metric-average").textContent = state.averageMs === null ? "—" : formatMs(state.averageMs);
    document.getElementById("metric-min").textContent = state.minMs === null ? "—" : formatMs(state.minMs);
    document.getElementById("metric-max").textContent = state.maxMs === null ? "—" : formatMs(state.maxMs);
    document.getElementById("summary-started").textContent = "Started: " + state.startedAt;
    document.getElementById("summary-finished").textContent = "Finished: " + state.finishedAt;
    document.getElementById("summary-runid").textContent = "Run ID: " + state.runId;
    document.getElementById("summary-status").textContent = state.status.charAt(0).toUpperCase() + state.status.slice(1);
    const status = document.getElementById("status");
    const label = state.status.charAt(0).toUpperCase() + state.status.slice(1);
    status.className = "status " + (state.status === "completed" ? "success" : state.status === "failed" ? "failure" : state.status === "running" ? "running" : "muted");
    status.replaceChildren(element("i"), document.createTextNode(label));
    document.getElementById("run").disabled = state.status === "running";
    document.getElementById("stop").disabled = state.status !== "running";
    document.getElementById("clear").disabled = state.visibleRequests === 0;
    const failures = document.getElementById("summary-failures");
    failures.replaceChildren();
    if (state.failures && state.failures.length) {
      failures.hidden = false;
      failures.appendChild(element("strong", "", "Failures"));
      const ul = element("ul");
      state.failures.forEach(failure => ul.appendChild(element("li", "", failure)));
      failures.appendChild(ul);
    } else {
      failures.hidden = true;
    }
  }

  document.addEventListener("click", function (event) {
    const target = event.target instanceof Element ? event.target.closest("button") : null;
    if (!target || target.disabled) return;
    if (target.id === "host-start") vscode.postMessage({ command: "hostStart" });
    else if (target.id === "host-stop") vscode.postMessage({ command: "hostStop" });
    else if (target.id === "run") vscode.postMessage({ command: "run" });
    else if (target.id === "debug") vscode.postMessage({ command: "debug" });
    else if (target.id === "stop") vscode.postMessage({ command: "stop" });
    else if (target.id === "clear") vscode.postMessage({ command: "clear" });
    else if (target.dataset.select) vscode.postMessage({ command: "select", requestId: Number(target.dataset.select) });
  });

  window.addEventListener("message", function (event) {
    const message = event.data;
    if (message?.command === "selectedRequest") {
      renderInspector(message.request);
      return;
    }
    if (message?.command !== "state") return;
    if (message.resetRequests) {
      list.replaceChildren();
      rows.clear();
      allocatedSlots = 0;
      ensureCapacity(0);
    } else {
      ensureCapacity(message.visibleRequests || 0);
    }
    sessionStartedAt = message.startedAt === "—" ? null : message.startedAt;
    (message.requests || []).forEach(item => updateRow(item.request, item.slot));
    if (["completed", "failed", "cancelled"].includes(message.status)) {
      trimEmptySlots(message.visibleRequests || 0);
    }
    updateSummary(message);
    if (Object.prototype.hasOwnProperty.call(message, "selectedRequest")) {
      renderInspector(message.selectedRequest || null);
    }
    if (["completed", "failed", "cancelled"].includes(message.status)) {
      document.querySelector(".summary").scrollIntoView({ behavior: "smooth", block: "nearest" });
    }
  });

  addSlots(BATCH_SIZE);
  vscode.postMessage({ command: "ready" });
})();
</script>
</body>
</html>`;
  }

  private styles(): string {
    return 'body{margin:0;padding:16px;color:var(--vscode-foreground);background:var(--vscode-editor-background);font-family:var(--vscode-font-family);font-size:var(--vscode-font-size)}' +
      'header{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;border-bottom:1px solid var(--vscode-panel-border);padding:0 0 14px}h1{font-size:1.3em;margin:0 0 4px}.test-name,.muted,.empty{color:var(--vscode-descriptionForeground)}' +
      '.toolbar{display:flex;align-items:center;gap:8px}.toolbar button{padding:5px 12px;color:var(--vscode-button-foreground);background:var(--vscode-button-background);border:0;border-radius:3px;cursor:pointer}.toolbar button:hover{background:var(--vscode-button-hoverBackground)}.toolbar button:disabled{opacity:.45;cursor:default}.toolbar .danger{background:var(--vscode-errorForeground)}' +
      '.status{font-weight:600;margin-left:8px}.status i{display:inline-block;width:8px;height:8px;border-radius:50%;background:currentColor;margin-right:7px}.success{color:var(--vscode-testing-iconPassed)}.failure{color:var(--vscode-testing-iconFailed)}.running{color:var(--vscode-charts-blue)}.warning{color:var(--vscode-editorWarning-foreground)}' +
      '*{box-sizing:border-box}main{display:grid;grid-template-columns:minmax(0,1fr);grid-template-rows:minmax(180px,38vh) minmax(280px,1fr);gap:14px;height:calc(100vh - 250px);min-height:620px;margin-top:14px}.explorer,.inspector,.summary{border:1px solid var(--vscode-panel-border);border-radius:4px;min-width:0;min-height:0;overflow:hidden}.explorer{display:flex;flex-direction:column}.section-heading{flex:0 0 auto;font-weight:700;font-size:.9em;letter-spacing:.04em;padding:10px 12px;border-bottom:1px solid var(--vscode-panel-border);display:flex;justify-content:space-between}.request-list{flex:1 1 auto;min-height:0;max-height:none;overflow:auto}.request-row{display:flex;flex-direction:column;gap:5px;width:100%;min-height:37px;padding:7px 10px;text-align:left;color:inherit;background:transparent;border:0;border-bottom:1px solid var(--vscode-panel-border)}.request-row:not(:disabled){cursor:pointer}.request-row:not(:disabled):hover,.request-row.selected{background:var(--vscode-list-activeSelectionBackground);color:var(--vscode-list-activeSelectionForeground)}.request-row.empty-row{height:37px;min-height:37px;opacity:.28}.request-main,.request-meta{display:flex;gap:9px;align-items:center;min-width:0}.request-url{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.timeline-track{position:relative;display:inline-block;flex:0 0 90px;height:8px;background:var(--vscode-editorWidget-background);border-radius:3px;margin-left:auto}.timeline-bar{position:absolute;top:0;height:8px;min-width:2px;border-radius:3px;background:var(--vscode-charts-blue)}.timeline-bar.failed{background:var(--vscode-charts-red)}.timeline-bar.succeeded{background:var(--vscode-charts-green)}.timeline-empty{color:var(--vscode-descriptionForeground);font-size:.8em}.request-id,.request-meta{font-size:.88em;color:var(--vscode-descriptionForeground)}.method{font-weight:700;min-width:42px}.request-meta{padding-left:48px;justify-content:space-between}.inspector{padding-bottom:12px;overflow:auto;max-height:none;position:relative}.inspector>.section-heading{position:sticky;top:0;z-index:2;background:var(--vscode-editor-background)}.inspector-content{padding:12px}.detail-title{display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap}.url{color:var(--vscode-textLink-foreground);overflow-wrap:anywhere;margin:10px 0}.detail-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;padding:10px 0;border-top:1px solid var(--vscode-panel-border);border-bottom:1px solid var(--vscode-panel-border)}.detail-grid div{display:flex;flex-direction:column;gap:4px}h3{font-size:1em;margin:18px 0 6px}h4{margin:14px 0 6px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:var(--vscode-textCodeBlock-background);padding:8px;border-radius:3px;margin:6px 0;font-family:var(--vscode-editor-font-family)}table{width:100%;border-collapse:collapse;table-layout:fixed}th,td{text-align:left;vertical-align:top;padding:6px;border-bottom:1px solid var(--vscode-panel-border);overflow-wrap:anywhere}td pre{max-height:160px;overflow:auto}.summary{margin-top:14px}.metrics{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:8px;padding:12px}.metric{border:1px solid var(--vscode-panel-border);border-radius:3px;padding:9px;display:flex;flex-direction:column;gap:5px}.metric span{font-size:.88em;color:var(--vscode-descriptionForeground)}.metric strong{font-size:1.15em}.summary-footer{display:flex;gap:20px;flex-wrap:wrap;padding:0 12px 12px;color:var(--vscode-descriptionForeground);font-size:.88em}.failures{padding:0 12px 12px;color:var(--vscode-errorForeground)}.empty{padding:14px;font-style:italic}@media(max-width:800px){main{grid-template-columns:1fr}.request-list{max-height:240px}}';
  }

  dispose(): void {
    this.disposed = true;
    this.panel?.dispose();
    this.panel = undefined;
  }
}
