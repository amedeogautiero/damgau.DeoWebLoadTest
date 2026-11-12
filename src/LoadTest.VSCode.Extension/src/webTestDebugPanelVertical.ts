import * as vscode from "vscode";
import { DebugRequest, WebTestDebugSession } from "./webTestDebugSession";

const MAX_DISPLAY_CHARS = 64 * 1024;

export class WebTestDebugPanelVertical implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private selectedRequestId: number | undefined;
  private disposed = false;
  private webviewInitialized = false;

  constructor(
    private readonly session: WebTestDebugSession,
    private readonly onRun: () => void,
    private readonly onStop: () => void,
    private readonly onClear: () => void
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
      this.webviewInitialized = false;
      this.panel.webview.onDidReceiveMessage(message => {
        if (message?.command === "run") this.onRun();
        else if (message?.command === "stop") this.onStop();
        else if (message?.command === "clear") this.onClear();
        else if (message?.command === "select" && Number.isInteger(message.requestId)) {
          this.selectedRequestId = message.requestId;
          this.render();
        }
      });
      this.panel.onDidDispose(() => {
        this.panel = undefined;
        this.selectedRequestId = undefined;
        this.webviewInitialized = false;
      });
    } else {
      this.panel.reveal(vscode.ViewColumn.Beside, true);
    }
    this.render();
  }

  update(): void {
    if (this.panel) this.render();
  }

  private render(): void {
    if (!this.panel) return;
    const requests = this.session.requests;
    if (this.selectedRequestId === undefined || !requests.some(r => r.id === this.selectedRequestId)) {
      this.selectedRequestId = requests.length ? requests[requests.length - 1].id : undefined;
    }
    const selected = requests.find(r => r.id === this.selectedRequestId);
    const html = this.renderHtml(requests, selected);
    if (!this.webviewInitialized) {
      this.panel.webview.html = html;
      this.webviewInitialized = true;
    } else {
      void this.panel.webview.postMessage({ command: "refresh", html });
    }
  }

  private renderHtml(requests: readonly DebugRequest[], selected?: DebugRequest): string {
    const status = this.session.status;
    const running = status === "running";
    const succeeded = this.session.successfulRequests;
    const failed = this.session.failedRequests;
    const completedCount = succeeded + failed;
    const durations = [...this.session.requestDurationsMs];
    const sum = durations.reduce((a, b) => a + b, 0);
    const average = durations.length ? sum / durations.length : 0;
    const min = durations.length ? Math.min(...durations) : 0;
    const max = durations.length ? Math.max(...durations) : 0;
    const start = this.parseTime(this.session.startedAt);
    const end = this.parseTime(this.session.finishedAt) ?? Date.now();
    const elapsed = start === undefined ? 0 : Math.max(0, end - start);
    const rate = elapsed > 0 ? completedCount / (elapsed / 1000) : 0;
    const successRate = completedCount ? succeeded / completedCount * 100 : 0;
    const statusLabel = status.charAt(0).toUpperCase() + status.slice(1);
    const statusClass = status === "completed" ? "success" : status === "failed" ? "failure" : running ? "running" : "muted";
    const rows = requests.map(request => {
      const duration = typeof request.elapsedMicros === "number" ? request.elapsedMicros / 1000 : undefined;
      const offset = this.startOffset(request, requests);
      const selectedClass = selected?.id === request.id ? " selected" : "";
      const requestStatus = request.status === "pending" ? "Running" : request.status === "succeeded" ? "Succeeded" : "Failed";
      const requestClass = request.status === "succeeded" ? "success" : request.status === "failed" ? "failure" : "muted";
      return '<button class="request-row' + selectedClass + '" data-select="' + request.id + '">' +
        '<span class="request-main"><span class="request-id">#' + String(request.id).padStart(3, "0") + '</span>' +
        '<span class="method">' + this.escape(request.method ?? "HTTP") + '</span>' +
        '<span class="request-url">' + this.escape(this.shortUrl(request.url)) + '</span>' + this.timelineFor(request, requests) + '</span>' +
        '<span class="request-meta"><span class="' + requestClass + '">' + requestStatus + '</span>' +
        '<span>' + (duration === undefined ? "—" : this.ms(duration)) + '</span>' +
        '<span>+' + (offset === undefined ? "—" : this.ms(offset)) + '</span></span></button>';
    }).join("");

    return '<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0">' +
      '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'; script-src \'unsafe-inline\';">' +
      '<style>' + this.styles() + '</style></head><body>' +
      '<header><div><h1>DeoLoadTest Debug Panel - Vertical</h1><div class="test-name">' + this.escape(this.session.testName ?? "No WebTest selected") + '</div></div>' +
      '<div class="toolbar"><button id="run" ' + (running ? "disabled" : "") + '>▶ Run</button>' +
      '<button id="stop" class="danger" ' + (!running ? "disabled" : "") + '>■ Stop</button>' +
      '<button id="clear" ' + (requests.length ? "" : "disabled") + '>Clear</button>' +
      '<span class="status ' + statusClass + '"><i></i>' + statusLabel + '</span></div></header>' +
      '<main><section class="explorer"><div class="section-heading">REQUEST EXPLORER <span>' + requests.length + '</span></div>' +
      '<div class="request-list">' + (rows || '<div class="empty">Run a WebTest to inspect its HTTP requests.</div>') + '</div></section>' +
      '<section class="inspector"><div class="section-heading">REQUEST INSPECTOR</div>' +
      (selected ? this.renderRequest(selected, requests) : '<div class="empty">Select a request to inspect its details.</div>') + '</section></main>' +
      '<section class="summary"><div class="section-heading">EXECUTION SUMMARY <span>' + statusLabel + '</span></div>' +
      '<div class="metrics">' +
      this.metric("Requests", String(this.session.totalRequests)) + this.metric("Successful", String(succeeded), "success") +
      this.metric("Failed", String(failed), failed ? "failure" : "") + this.metric("Success rate", completedCount ? successRate.toFixed(2) + "%" : "—") +
      this.metric("Total duration", this.ms(elapsed)) + this.metric("Requests/sec", rate.toFixed(2)) +
      this.metric("Avg response", durations.length ? this.ms(average) : "—") + this.metric("Min response", durations.length ? this.ms(min) : "—") +
      this.metric("Max response", durations.length ? this.ms(max) : "—") + '</div>' +
      '<div class="summary-footer"><span>Started: ' + this.escape(this.session.startedAt ?? "—") + '</span>' +
      '<span>Finished: ' + this.escape(this.session.finishedAt ?? (running ? "In progress" : "—")) + '</span>' +
      '<span>Run ID: ' + this.escape(this.session.runId ?? "—") + '</span></div>' +
      (this.session.summary?.failures.length ? '<div class="failures"><strong>Failures</strong><ul>' + this.session.summary.failures.map(f => '<li>' + this.escape(f) + '</li>').join("") + '</ul></div>' : '') +
      '</section><script>' +
      'const vscode = acquireVsCodeApi();' +
      'document.addEventListener("click",event=>{' +
      'const target=event.target.closest("button");if(!target)return;' +
      'if(target.id==="run")vscode.postMessage({command:"run"});' +
      'else if(target.id==="stop")vscode.postMessage({command:"stop"});' +
      'else if(target.id==="clear")vscode.postMessage({command:"clear"});' +
      'else if(target.dataset.select)vscode.postMessage({command:"select",requestId:Number(target.dataset.select)});' +
      '});' +
      'window.addEventListener("message",event=>{' +
      'const message=event.data;if(message?.command!=="refresh"||typeof message.html!=="string")return;' +
      'const parsed=new DOMParser().parseFromString(message.html,"text/html");' +
      'const currentList=document.querySelector(".request-list"),nextList=parsed.querySelector(".request-list");' +
      'if(currentList&&nextList){' +
      'const existing=new Map(Array.from(currentList.querySelectorAll("[data-select]")).map(row=>[row.dataset.select,row]));' +
      'const nextRows=Array.from(nextList.querySelectorAll("[data-select]"));' +
      'for(const nextRow of nextRows){const id=nextRow.dataset.select;const current=existing.get(id);' +
      'if(current){if(current.outerHTML!==nextRow.outerHTML)current.replaceWith(nextRow);existing.delete(id);}' +
      'else currentList.appendChild(nextRow);}' +
      'for(const stale of existing.values())stale.remove();' +
      'const empty=nextList.querySelector(".empty");const currentEmpty=currentList.querySelector(".empty");' +
      'if(empty&&!currentEmpty)currentList.appendChild(empty);else if(!empty&&currentEmpty)currentEmpty.remove();' +
      '}' +
      'const currentInspector=document.querySelector(".inspector"),nextInspector=parsed.querySelector(".inspector");' +
      'if(currentInspector&&nextInspector){const oldContent=currentInspector.innerHTML,newContent=nextInspector.innerHTML;' +
      'if(oldContent!==newContent){const scroll=currentInspector.scrollTop;currentInspector.innerHTML=newContent;currentInspector.scrollTop=scroll;}}' +
      'const currentHeading=document.querySelector(".explorer>.section-heading"),nextHeading=parsed.querySelector(".explorer>.section-heading");' +
      'if(currentHeading&&nextHeading)currentHeading.innerHTML=nextHeading.innerHTML;' +
      'const currentSummary=document.querySelector(".summary"),nextSummary=parsed.querySelector(".summary");' +
      'if(currentSummary&&nextSummary&&currentSummary.innerHTML!==nextSummary.innerHTML)currentSummary.innerHTML=nextSummary.innerHTML;' +
      'const currentStatus=document.querySelector("header .status"),nextStatus=parsed.querySelector("header .status");' +
      'if(currentStatus&&nextStatus)currentStatus.replaceWith(nextStatus);' +
      'for(const id of ["run","stop","clear"]){const current=document.getElementById(id),next=parsed.getElementById(id);' +
      'if(current&&next)current.disabled=next.disabled;}' +
      'const currentTest=document.querySelector(".test-name"),nextTest=parsed.querySelector(".test-name");' +
      'if(currentTest&&nextTest)currentTest.textContent=nextTest.textContent;' +
      '});' +
      '</script></body></html>';
  }

  private renderRequest(request: DebugRequest, requests: readonly DebugRequest[]): string {
    const startOffset = this.startOffset(request, requests);
    const duration = typeof request.elapsedMicros === "number" ? this.ms(request.elapsedMicros / 1000) : request.status === "pending" ? "In progress" : "Unavailable";
    const status = request.status === "succeeded" ? "Succeeded" : request.status === "failed" ? "Failed" : "Running";
    const context = this.renderContext(request);
    return '<div class="request-detail"><div class="detail-title"><strong>Request #' + request.id + ' · ' + this.escape(status) + '</strong>' +
      '<span>' + this.escape(request.method ?? "HTTP") + ' · ' + this.escape(request.statusCode === undefined ? "No response" : String(request.statusCode) + " " + (request.reasonPhrase ?? "")) + '</span></div>' +
      '<div class="url">' + this.escape(request.url ?? "URL unavailable") + '</div>' +
      '<div class="detail-grid">' + this.detail("Request name", request.name ?? "—") + this.detail("Start offset", startOffset === undefined ? "—" : this.ms(startOffset)) +
      this.detail("Duration", duration) + this.detail("Started", request.startedAt ?? "—") + this.detail("Completed", request.completedAt ?? "—") + '</div>' +
      this.block("Request headers", request.requestHeaders) + this.block("Request body", request.requestBody) +
      this.block("Response headers", request.responseHeaders) + this.block("Response body", request.responseBody) +
      (request.errorType || request.errorMessage || request.exceptionDetails ? '<h3 class="failure">Error</h3>' + this.block("Type", request.errorType) + this.block("Message", request.errorMessage) + this.block("Exception chain", request.exceptionDetails) : '') +
      this.block("WebTest definition (parsed YAML node)", request.requestDefinition) +
      '<h3>Request context</h3>' + context + '</div>';
  }

  private renderContext(request: DebugRequest): string {
    const before = request.contextAtStart;
    const after = request.contextAtEnd;
    if (!before && !after) return '<div class="empty">Context snapshot not available for this request.</div>';
    const table = (title: string, oldValues: Record<string, unknown>, newValues: Record<string, unknown>) => {
      const keys = Array.from(new Set([...Object.keys(oldValues), ...Object.keys(newValues)])).sort();
      if (!keys.length) return '<h4>' + title + '</h4><div class="empty">No values</div>';
      return '<h4>' + title + '</h4><table><thead><tr><th>Name</th><th>At request start</th><th>After request</th><th>Change</th></tr></thead><tbody>' +
        keys.map(key => {
          const hasOld = Object.prototype.hasOwnProperty.call(oldValues, key);
          const hasNew = Object.prototype.hasOwnProperty.call(newValues, key);
          const oldText = hasOld ? this.value(oldValues[key]) : "";
          const newText = hasNew ? this.value(newValues[key]) : "";
          const changed = hasOld && hasNew && oldText !== newText;
          const state = !before || !after ? "Pending" : !hasOld ? "Added" : !hasNew ? "Removed" : changed ? "Modified" : "Unchanged";
          const css = state === "Added" ? "success" : state === "Removed" ? "failure" : state === "Modified" ? "warning" : "";
          return '<tr><td>' + this.escape(key) + '</td><td><pre>' + this.escape(oldText || "—") + '</pre></td><td><pre>' + this.escape(newText || "—") + '</pre></td><td class="' + css + '">' + state + '</td></tr>';
        }).join("") + '</tbody></table>';
    };
    const flatten = (snapshot?: { dataRows?: Record<string, Record<string, string>> }) => {
      const result: Record<string, unknown> = {};
      for (const [tableName, values] of Object.entries(snapshot?.dataRows ?? {})) {
        for (const [key, value] of Object.entries(values ?? {})) result[tableName + "." + key] = value;
      }
      return result;
    };
    return table("Variables", before?.variables ?? {}, after?.variables ?? {}) + table("Data rows", flatten(before), flatten(after));
  }

  private block(title: string, value: unknown): string {
    if (value === undefined || value === null || value === "") return "";
    return '<h3>' + this.escape(title) + '</h3><pre>' + this.escape(this.value(value)) + '</pre>';
  }

  private value(value: unknown): string {
    let text: string;
    if (typeof value === "string") {
      text = value;
      try { const parsed = JSON.parse(value); if (parsed !== null && typeof parsed === "object") text = JSON.stringify(parsed, null, 2); } catch { /* plain text */ }
    } else {
      try { text = JSON.stringify(value, null, 2) ?? String(value); } catch { text = String(value); }
    }
    return text.length > MAX_DISPLAY_CHARS ? text.slice(0, MAX_DISPLAY_CHARS) + "\n[Display truncated at 64 KB]" : text;
  }

  private metric(label: string, value: string, css = ""): string {
    return '<div class="metric"><span>' + this.escape(label) + '</span><strong class="' + css + '">' + this.escape(value) + '</strong></div>';
  }

  private detail(label: string, value: string): string {
    return '<div><span class="muted">' + this.escape(label) + '</span><strong>' + this.escape(value) + '</strong></div>';
  }

  private startOffset(request: DebugRequest, requests: readonly DebugRequest[]): number | undefined {
    const first = requests.map(r => this.parseTime(r.startedAt)).filter((v): v is number => v !== undefined);
    const current = this.parseTime(request.startedAt);
    return current === undefined || !first.length ? undefined : Math.max(0, current - Math.min(...first));
  }

  private parseTime(value?: string): number | undefined {
    if (!value) return undefined;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  private timelineFor(request: DebugRequest, requests: readonly DebugRequest[]): string {
    const starts = requests.map(r => this.parseTime(r.startedAt)).filter((v): v is number => v !== undefined);
    const first = starts.length ? Math.min(...starts) : undefined;
    const current = this.parseTime(request.startedAt);
    if (first === undefined || current === undefined) return '<span class="timeline-empty">··········</span>';
    const maxEnd = Math.max(1, ...requests.map(r => {
      const start = this.parseTime(r.startedAt);
      if (start === undefined) return 0;
      const duration = typeof r.elapsedMicros === "number" ? r.elapsedMicros / 1000 : r.status === "pending" ? Date.now() - start : 1;
      return Math.max(0, start - first) + Math.max(1, duration);
    }));
    const offset = Math.max(0, current - first);
    const duration = typeof request.elapsedMicros === "number" ? request.elapsedMicros / 1000 : request.status === "pending" ? Math.max(1, Date.now() - current) : 1;
    const left = Math.min(99, offset / maxEnd * 100);
    const width = Math.max(1, Math.min(100 - left, duration / maxEnd * 100));
    const state = request.status === "failed" ? "failed" : request.status === "succeeded" ? "succeeded" : "pending";
    return '<span class="timeline-track"><span class="timeline-bar ' + state + '" style="left:' + left.toFixed(2) + '%;width:' + width.toFixed(2) + '%"></span></span>';
  }

  private shortUrl(value?: string): string {
    if (!value) return "(URL unavailable)";
    try { const url = new URL(value); return url.host + url.pathname + url.search; } catch { return value; }
  }

  private ms(value: number): string { return value.toFixed(2) + " ms"; }

  private escape(value: string): string {
    return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  private styles(): string {
    return 'body{margin:0;padding:16px;color:var(--vscode-foreground);background:var(--vscode-editor-background);font-family:var(--vscode-font-family);font-size:var(--vscode-font-size)}' +
      'header{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;border-bottom:1px solid var(--vscode-panel-border);padding:0 0 14px}h1{font-size:1.3em;margin:0 0 4px}.test-name,.muted,.empty{color:var(--vscode-descriptionForeground)}' +
      '.toolbar{display:flex;align-items:center;gap:8px}.toolbar button{padding:5px 12px;color:var(--vscode-button-foreground);background:var(--vscode-button-background);border:0;border-radius:3px;cursor:pointer}.toolbar button:hover{background:var(--vscode-button-hoverBackground)}.toolbar button:disabled{opacity:.45;cursor:default}.toolbar .danger{background:var(--vscode-errorForeground)}' +
      '.status{font-weight:600;margin-left:8px}.status i{display:inline-block;width:8px;height:8px;border-radius:50%;background:currentColor;margin-right:7px}.success{color:var(--vscode-testing-iconPassed)}.failure{color:var(--vscode-testing-iconFailed)}.running{color:var(--vscode-charts-blue)}.warning{color:var(--vscode-editorWarning-foreground)}' +
      '*{box-sizing:border-box}main{display:grid;grid-template-columns:minmax(0,1fr);grid-template-rows:minmax(180px,38vh) minmax(280px,1fr);gap:14px;height:calc(100vh - 250px);min-height:620px;margin-top:14px}.explorer,.inspector,.summary{border:1px solid var(--vscode-panel-border);border-radius:4px;min-width:0;min-height:0;overflow:hidden}.explorer{display:flex;flex-direction:column}.section-heading{flex:0 0 auto;font-weight:700;font-size:.9em;letter-spacing:.04em;padding:10px 12px;border-bottom:1px solid var(--vscode-panel-border);display:flex;justify-content:space-between}.request-list{flex:1 1 auto;min-height:0;max-height:none;overflow:auto}.request-row{display:flex;flex-direction:column;gap:5px;width:100%;padding:9px 10px;text-align:left;color:inherit;background:transparent;border:0;border-bottom:1px solid var(--vscode-panel-border);cursor:pointer}.request-row:hover,.request-row.selected{background:var(--vscode-list-activeSelectionBackground);color:var(--vscode-list-activeSelectionForeground)}.request-main,.request-meta{display:flex;gap:9px;align-items:center;min-width:0}.request-url{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.timeline-track{position:relative;display:inline-block;flex:0 0 90px;height:8px;background:var(--vscode-editorWidget-background);border-radius:3px;margin-left:auto}.timeline-bar{position:absolute;top:0;height:8px;min-width:2px;border-radius:3px;background:var(--vscode-charts-blue)}.timeline-bar.failed{background:var(--vscode-charts-red)}.timeline-bar.succeeded{background:var(--vscode-charts-green)}.timeline-empty{color:var(--vscode-descriptionForeground);font-size:.8em}.request-id,.request-meta{font-size:.88em;color:var(--vscode-descriptionForeground)}.method{font-weight:700;min-width:42px}.request-meta{padding-left:48px;justify-content:space-between}.inspector{padding-bottom:12px;overflow:auto;max-height:none;position:relative}.inspector>.section-heading{position:sticky;top:0;z-index:2;background:var(--vscode-editor-background)}.request-detail{padding:12px}.detail-title{display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap}.url{color:var(--vscode-textLink-foreground);overflow-wrap:anywhere;margin:10px 0}.detail-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;padding:10px 0;border-top:1px solid var(--vscode-panel-border);border-bottom:1px solid var(--vscode-panel-border)}.detail-grid div{display:flex;flex-direction:column;gap:4px}h3{font-size:1em;margin:18px 0 6px}h4{margin:14px 0 6px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:var(--vscode-textCodeBlock-background);padding:8px;border-radius:3px;margin:6px 0;font-family:var(--vscode-editor-font-family)}table{width:100%;border-collapse:collapse;table-layout:fixed}th,td{text-align:left;vertical-align:top;padding:6px;border-bottom:1px solid var(--vscode-panel-border);overflow-wrap:anywhere}td pre{max-height:160px;overflow:auto}.summary{margin-top:14px}.metrics{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:8px;padding:12px}.metric{border:1px solid var(--vscode-panel-border);border-radius:3px;padding:9px;display:flex;flex-direction:column;gap:5px}.metric span{font-size:.88em;color:var(--vscode-descriptionForeground)}.metric strong{font-size:1.15em}.summary-footer{display:flex;gap:20px;flex-wrap:wrap;padding:0 12px 12px;color:var(--vscode-descriptionForeground);font-size:.88em}.failures{padding:0 12px 12px;color:var(--vscode-errorForeground)}.empty{padding:14px;font-style:italic}@media(max-width:800px){main{grid-template-columns:1fr}.request-list{max-height:240px}}';
  }

  dispose(): void {
    this.disposed = true;
    this.panel?.dispose();
    this.panel = undefined;
  }
}
