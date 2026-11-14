import * as vscode from "vscode";
import { DebugRequest, DebugStep, WebTestContextSnapshot } from "./webTestDebugSession";

const MAX_DISPLAY_CHARS = 64 * 1024;

export class WebTestRequestInspector implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private currentRequestId: number | undefined;
  private disposed = false;

  show(request: DebugRequest, requests: readonly DebugRequest[], steps: readonly DebugStep[]): void {
    if (this.disposed) return;

    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel(
        "deoloadtest.requestInspector",
        this.titleFor(request),
        vscode.ViewColumn.Beside,
        {
          enableScripts: false,
          enableFindWidget: true,
          localResourceRoots: []
        }
      );
      this.panel.onDidDispose(() => {
        this.panel = undefined;
        this.currentRequestId = undefined;
      });
    } else {
      this.panel.reveal(vscode.ViewColumn.Beside, true);
    }

    this.currentRequestId = request.id;
    this.panel.title = this.titleFor(request);
    this.panel.webview.html = this.render(request, requests, steps);
  }

  update(requests: readonly DebugRequest[], steps: readonly DebugStep[]): void {
    if (!this.panel || this.currentRequestId === undefined) return;
    const request = requests.find(item => item.id === this.currentRequestId);
    if (!request) return;
    this.panel.title = this.titleFor(request);
    this.panel.webview.html = this.render(request, requests, steps);
  }

  private titleFor(request: DebugRequest): string {
    return `Request #${String(request.id).padStart(3, "0")} — ${request.method ?? "HTTP"}`;
  }

  private render(request: DebugRequest, requests: readonly DebugRequest[], steps: readonly DebugStep[]): string {
    const status = request.status === "pending"
      ? "Running"
      : request.status === "succeeded"
        ? "Succeeded"
        : "Failed";
    const statusClass = request.status === "succeeded"
      ? "success"
      : request.status === "failed"
        ? "failure"
        : "pending";

    const requestHeaders = this.formatValue(request.requestHeaders);
    const requestBody = this.formatValue(request.requestBody);
    const responseHeaders = this.formatValue(request.responseHeaders);
    const responseBody = this.formatValue(request.responseBody);
    const definition = this.formatValue(request.requestDefinition);
    const requestContext = this.renderRequestContext(request.contextAtStart, request.contextAtEnd);
    const childSteps = this.renderChildSteps(request, steps);
    const startOffset = this.startOffsetMs(request, requests);
    const duration = typeof request.elapsedMicros === "number"
      ? `${(request.elapsedMicros / 1000).toFixed(2)} ms`
      : request.status === "pending" ? "In progress" : "Unavailable";

    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
  <title>${this.escapeHtml(this.titleFor(request))}</title>
  <style>
    body { padding: 16px 22px 28px; color: var(--vscode-foreground); background: var(--vscode-editor-background); font-family: var(--vscode-font-family); font-size: var(--vscode-font-size); }
    h1 { font-size: 1.35em; font-weight: 600; margin: 0 0 14px; }
    h2 { font-size: 1.05em; font-weight: 600; margin: 24px 0 8px; padding-bottom: 6px; border-bottom: 1px solid var(--vscode-panel-border); }
    .summary { display: flex; flex-wrap: wrap; gap: 8px 18px; margin-bottom: 14px; }
    .badge { font-weight: 600; }
    .success { color: var(--vscode-testing-iconPassed); }
    .failure { color: var(--vscode-testing-iconFailed); }
    .pending { color: var(--vscode-descriptionForeground); }
    .url { overflow-wrap: anywhere; color: var(--vscode-textLink-foreground); margin: 8px 0 18px; }
    .field { margin: 5px 0; }
    .label { color: var(--vscode-descriptionForeground); margin-right: 6px; }
    pre { white-space: pre-wrap; overflow-wrap: anywhere; padding: 10px 12px; background: var(--vscode-textCodeBlock-background); border: 1px solid var(--vscode-panel-border); border-radius: 3px; font-family: var(--vscode-editor-font-family); font-size: var(--vscode-editor-font-size); }
    .empty { color: var(--vscode-descriptionForeground); font-style: italic; }
    .warning { color: var(--vscode-editorWarning-foreground); }
    .context-snapshot { border: 1px solid var(--vscode-panel-border); border-radius: 4px; padding: 10px 12px; margin: 12px 0; }
    .context-table th:nth-child(2), .context-table td:nth-child(2), .context-table th:nth-child(3), .context-table td:nth-child(3) { width: 35%; }
    .change-added { color: var(--vscode-testing-iconPassed); }
    .change-modified { color: var(--vscode-editorWarning-foreground); }
    .change-removed { color: var(--vscode-testing-iconFailed); }
    h3 { font-size: .98em; margin: 16px 0 6px; }
    table { width: 100%; border-collapse: collapse; table-layout: fixed; }
    th, td { text-align: left; vertical-align: top; padding: 6px 8px; border-bottom: 1px solid var(--vscode-panel-border); overflow-wrap: anywhere; }
    th:first-child, td:first-child { width: 25%; }
    td pre { margin: 0; }
  </style>
</head>
<body>
  <h1>Request #${request.id}</h1>
  <div class="summary">
    <span class="badge ${statusClass}">${status}</span>
    <span>${this.escapeHtml(request.method ?? "HTTP")}</span>
    <span>${this.escapeHtml(request.statusCode === undefined ? "No response" : `${request.statusCode} ${request.reasonPhrase ?? ""}`)}</span>
  </div>
  <div class="url">${this.escapeHtml(request.url ?? "URL unavailable")}</div>
  <div class="field"><span class="label">Request name:</span>${this.escapeHtml(request.name ?? "—")}</div>
  <div class="field"><span class="label">Start offset:</span>${startOffset === undefined ? "Unavailable" : `${startOffset.toFixed(2)} ms`}</div>
  <div class="field"><span class="label">Duration:</span>${this.escapeHtml(duration)}</div>
  <div class="field"><span class="label">Started:</span>${this.escapeHtml(request.startedAt ?? "—")}</div>
  <div class="field"><span class="label">Completed:</span>${this.escapeHtml(request.completedAt ?? "—")}</div>

  <h2>Request</h2>
  ${this.section("Headers", requestHeaders)}
  ${this.section("Body", requestBody)}

  <h2>Response</h2>
  ${this.section("Headers", responseHeaders)}
  ${this.section("Body", responseBody)}

  ${request.errorType || request.errorMessage || request.exceptionDetails ? `
    <h2>Error</h2>
    <div class="field"><span class="label">Type:</span>${this.escapeHtml(request.errorType ?? "—")}</div>
    <div class="field"><span class="label">Message:</span>${this.escapeHtml(request.errorMessage ?? "—")}</div>
    ${request.exceptionDetails ? this.section("Exception chain", this.formatValue(request.exceptionDetails)) : ""}
  ` : ""}

  <h2>WebTest definition</h2>
  ${this.section("Original YAML request node (parsed)", definition)}

  ${childSteps}

  <h2>Request Context</h2>
  ${requestContext}
</body>
</html>`;
  }

  private renderChildSteps(request: DebugRequest, steps: readonly DebugStep[]): string {
    const children = steps.filter(step =>
      step.requestId === request.id && (step.type === "Extractor" || step.type === "Validator")
    );
    if (!children.length) return "";

    const rows = children.map(step => {
      const status = step.status === "succeeded" ? "Succeeded" : step.status === "failed" ? "Failed" : "Running";
      const css = step.status === "succeeded" ? "success" : step.status === "failed" ? "failure" : "pending";
      const details = this.formatValue(step.details);
      const icon = step.type === "Extractor" ? "🔍 Extractor" : "✓ Validator";
      return `<div class="field"><strong>${icon} — ${this.escapeHtml(step.name)}</strong> <span class="badge ${css}">${status}</span></div>${details.text ? `<pre>${this.escapeHtml(details.text)}${details.truncated ? "\\n\\n[Display truncated at 64 KB]" : ""}</pre>` : ""}`;
    }).join("");
    return `<h2>Request steps</h2>${rows}`;
  }

  private renderRequestContext(before?: WebTestContextSnapshot, after?: WebTestContextSnapshot): string {
    if (!before && !after) {
      return '<div class="field empty">Context snapshot not available for this request.</div>';
    }

    const formatCell = (value: unknown): string => {
      if (value === undefined) return '<span class="empty">—</span>';
      const formatted = this.formatValue(value);
      return formatted.text
        ? `<pre>${this.escapeHtml(formatted.text)}${formatted.truncated ? "\\n\\n[Display truncated at 64 KB]" : ""}</pre>`
        : '<span class="empty">(empty)</span>';
    };

    const renderChanges = (
      title: string,
      oldValues: Record<string, unknown> | undefined,
      newValues: Record<string, unknown> | undefined
    ): string => {
      const oldMap = oldValues ?? {};
      const newMap = newValues ?? {};
      const keys = [...new Set([...Object.keys(oldMap), ...Object.keys(newMap)])].sort((a, b) => a.localeCompare(b));
      if (keys.length === 0) {
        return `<h3>${this.escapeHtml(title)}</h3><div class="field empty">No values</div>`;
      }

      const rows = keys.map(key => {
        const hasOld = Object.prototype.hasOwnProperty.call(oldMap, key);
        const hasNew = Object.prototype.hasOwnProperty.call(newMap, key);
        const oldValue = hasOld ? this.formatValue(oldMap[key]).text : undefined;
        const newValue = hasNew ? this.formatValue(newMap[key]).text : undefined;
        const changed = hasOld && hasNew && oldValue !== newValue;
        const state = !before || !after ? "Pending" : !hasOld ? "Added" : !hasNew ? "Removed" : changed ? "Modified" : "Unchanged";
        const css = !before || !after ? "" : !hasOld ? "change-added" : !hasNew ? "change-removed" : changed ? "change-modified" : "";
        return `<tr><td>${this.escapeHtml(key)}</td><td>${formatCell(hasOld ? oldMap[key] : undefined)}</td><td>${formatCell(hasNew ? newMap[key] : undefined)}</td><td class="${css}">${state}</td></tr>`;
      }).join("");

      return `<h3>${this.escapeHtml(title)}</h3>
        <table class="context-table"><thead><tr><th>Name</th><th>At request start</th><th>After request</th><th>Change</th></tr></thead><tbody>${rows}</tbody></table>`;
    };

    const dataRows = (snapshot?: WebTestContextSnapshot): Record<string, unknown> => {
      const flattened: Record<string, unknown> = {};
      for (const [table, values] of Object.entries(snapshot?.dataRows ?? {})) {
        for (const [name, value] of Object.entries(values ?? {})) {
          flattened[`${table}.${name}`] = value;
        }
      }
      return flattened;
    };

    return `<div class="context-snapshot">
      <div class="field empty">Only request variables and data-row values are shown. Unchanged values are retained for comparison; added, modified and removed values are identified.</div>
      ${renderChanges("Variables", before?.variables, after?.variables)}
      ${renderChanges("Data Rows", dataRows(before), dataRows(after))}
    </div>`;
  }

  private startOffsetMs(request: DebugRequest, requests: readonly DebugRequest[]): number | undefined {
    const starts = requests.map(item => this.parseTime(item.startedAt))
      .filter((value): value is number => value !== undefined);
    const start = this.parseTime(request.startedAt);
    if (start === undefined || starts.length === 0) return undefined;
    return Math.max(0, start - Math.min(...starts));
  }

  private parseTime(value?: string): number | undefined {
    if (!value) return undefined;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  private section(title: string, value: { text: string; truncated: boolean }): string {
    if (!value.text) return `<div class="field"><span class="label">${this.escapeHtml(title)}:</span><span class="empty">Not available</span></div>`;
    return `<div class="field"><span class="label">${this.escapeHtml(title)}</span></div><pre>${this.escapeHtml(value.text)}${value.truncated ? "\\n\\n[Display truncated at 64 KB]" : ""}</pre>`;
  }

  private formatValue(value: unknown): { text: string; truncated: boolean } {
    if (value === undefined || value === null || value === "") return { text: "", truncated: false };
    let text: string;
    if (typeof value === "string") {
      text = value;
      try {
        const parsed: unknown = JSON.parse(value);
        if (parsed !== null && typeof parsed === "object") text = JSON.stringify(parsed, null, 2);
      } catch {
        // Keep non-JSON response bodies as plain text.
      }
    } else {
      try {
        text = JSON.stringify(value, null, 2) ?? String(value);
      } catch {
        text = String(value);
      }
    }

    if (text.length > MAX_DISPLAY_CHARS) {
      return { text: text.slice(0, MAX_DISPLAY_CHARS), truncated: true };
    }
    return { text, truncated: false };
  }

  private escapeHtml(value: string): string {
    return value
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  dispose(): void {
    this.disposed = true;
    this.panel?.dispose();
    this.panel = undefined;
  }
}
