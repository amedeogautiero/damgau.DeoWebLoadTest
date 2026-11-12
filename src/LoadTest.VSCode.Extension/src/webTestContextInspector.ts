import * as vscode from "vscode";
import { DebugSessionStatus, WebTestContextSnapshot } from "./webTestDebugSession";

const MAX_VALUE_LENGTH = 8_000;

export class WebTestContextInspector implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private disposed = false;

  show(context: WebTestContextSnapshot | undefined, status: DebugSessionStatus): void {
    if (this.disposed) return;
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel(
        "deoloadtest.contextInspector",
        "WebTest Context Inspector",
        vscode.ViewColumn.Beside,
        { enableScripts: false, enableFindWidget: true, localResourceRoots: [] }
      );
      this.panel.onDidDispose(() => { this.panel = undefined; });
    } else {
      this.panel.reveal(vscode.ViewColumn.Beside, true);
    }
    this.render(context, status);
  }

  update(context: WebTestContextSnapshot | undefined, status: DebugSessionStatus): void {
    if (this.panel) this.render(context, status);
  }

  private render(context: WebTestContextSnapshot | undefined, status: DebugSessionStatus): void {
    if (!this.panel) return;
    const variables = this.renderRecord(context?.variables);
    const predefined = this.renderRecord(context?.predefined);
    const user = this.renderRecord(context?.user);
    const dataRows = Object.entries(context?.dataRows ?? {}).map(([tableName, values]) =>
      this.renderSection(tableName, values)
    ).join("");

    const hasContext = !!context;
    this.panel.webview.html = `<!DOCTYPE html>
<html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
<style>
body{padding:18px 22px 28px;color:var(--vscode-foreground);background:var(--vscode-editor-background);font-family:var(--vscode-font-family);font-size:var(--vscode-font-size)}
h1{font-size:1.35em;margin:0 0 6px}h2{font-size:1.1em;margin:24px 0 10px}
.subtle,.muted{color:var(--vscode-descriptionForeground)}.subtle{margin:0 0 18px}
.section{border:1px solid var(--vscode-panel-border);border-radius:4px;margin:12px 0 18px;overflow:hidden}
.section-title{padding:9px 12px;font-weight:600;background:var(--vscode-editorWidget-background);border-bottom:1px solid var(--vscode-panel-border)}
table{width:100%;border-collapse:collapse;table-layout:fixed}th{text-align:left;color:var(--vscode-descriptionForeground);font-weight:600;font-size:.9em}
th,td{padding:8px 12px;border-bottom:1px solid var(--vscode-panel-border);vertical-align:top}
tr:last-child td{border-bottom:0}th:first-child,td:first-child{width:30%;font-family:var(--vscode-editor-font-family);overflow-wrap:anywhere}
td.value{font-family:var(--vscode-editor-font-family);white-space:pre-wrap;overflow-wrap:anywhere}
.empty{padding:12px;color:var(--vscode-descriptionForeground);font-style:italic}
.badge{display:inline-block;padding:2px 7px;border-radius:3px;background:var(--vscode-editorWidget-background);color:var(--vscode-descriptionForeground);font-size:.9em}
</style></head><body>
<h1>Context Inspector</h1>
<p class="subtle">Session: <span class="badge">${this.escapeHtml(status)}</span> · Values represent the latest context snapshot received from the engine.</p>
${hasContext ? `
<h2>Variables</h2>${variables}
<h2>Data Rows</h2>${dataRows || '<p class="empty">No data rows available.</p>'}
<h2>Predefined values</h2>${predefined}
<h2>User / execution context</h2>${user}
` : '<p class="empty">No context snapshot received yet. Run a WebTest to inspect its variables and execution context.</p>'}
</body></html>`;
  }

  private renderRecord(record?: Record<string, unknown>): string {
    if (!record || Object.keys(record).length === 0) {
      return '<p class="empty">No values available.</p>';
    }
    const rows = Object.entries(record).map(([key, value]) =>
      `<tr><td>${this.escapeHtml(key)}</td><td class="value">${this.formatValue(value)}</td></tr>`
    ).join("");
    return `<div class="section"><table><thead><tr><th>Name</th><th>Value</th></tr></thead><tbody>${rows}</tbody></table></div>`;
  }

  private renderSection(name: string, record: Record<string, string>): string {
    const rows = Object.entries(record ?? {}).map(([key, value]) =>
      `<tr><td>${this.escapeHtml(key)}</td><td class="value">${this.formatValue(value)}</td></tr>`
    ).join("");
    return `<div class="section"><div class="section-title">${this.escapeHtml(name)}</div>${rows
      ? `<table><thead><tr><th>Name</th><th>Value</th></tr></thead><tbody>${rows}</tbody></table>`
      : '<p class="empty">No values available.</p>'}</div>`;
  }

  private formatValue(value: unknown): string {
    let text: string;
    if (typeof value === "string") text = value;
    else {
      try { text = JSON.stringify(value, null, 2) ?? String(value); }
      catch { text = String(value); }
    }
    const truncated = text.length > MAX_VALUE_LENGTH
      ? `${text.slice(0, MAX_VALUE_LENGTH)}\n… value truncated at ${MAX_VALUE_LENGTH.toLocaleString()} characters`
      : text;
    return this.escapeHtml(truncated);
  }

  private escapeHtml(value: string): string {
    return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  dispose(): void {
    this.disposed = true;
    this.panel?.dispose();
    this.panel = undefined;
  }
}
