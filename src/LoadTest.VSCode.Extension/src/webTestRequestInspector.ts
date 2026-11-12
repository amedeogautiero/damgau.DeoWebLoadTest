import * as vscode from "vscode";
import { DebugRequest } from "./webTestDebugSession";

const MAX_DISPLAY_CHARS = 64 * 1024;

export class WebTestRequestInspector implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private currentRequestId: number | undefined;
  private disposed = false;

  show(request: DebugRequest, requests: readonly DebugRequest[]): void {
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
    this.panel.webview.html = this.render(request, requests);
  }

  update(requests: readonly DebugRequest[]): void {
    if (!this.panel || this.currentRequestId === undefined) return;
    const request = requests.find(item => item.id === this.currentRequestId);
    if (!request) return;
    this.panel.title = this.titleFor(request);
    this.panel.webview.html = this.render(request, requests);
  }

  private titleFor(request: DebugRequest): string {
    return `Request #${String(request.id).padStart(3, "0")} — ${request.method ?? "HTTP"}`;
  }

  private render(request: DebugRequest, requests: readonly DebugRequest[]): string {
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
</body>
</html>`;
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
