import * as vscode from "vscode";
import { DebugRequest, DebugSessionStatus } from "./webTestDebugSession";

export class WebTestTimingInspector implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private disposed = false;

  show(requests: readonly DebugRequest[], status: DebugSessionStatus): void {
    if (this.disposed) return;
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel("deoloadtest.timingInspector", "WebTest Timing Inspector", vscode.ViewColumn.Beside, {
        enableScripts: false, enableFindWidget: true, localResourceRoots: []
      });
      this.panel.onDidDispose(() => { this.panel = undefined; });
    } else {
      this.panel.reveal(vscode.ViewColumn.Beside, true);
    }
    this.render(requests, status);
  }

  update(requests: readonly DebugRequest[], status: DebugSessionStatus): void {
    if (this.panel) this.render(requests, status);
  }

  private render(requests: readonly DebugRequest[], status: DebugSessionStatus): void {
    if (!this.panel) return;
    const completed = requests.filter(r => typeof r.elapsedMicros === "number");
    const durations = completed.map(r => r.elapsedMicros! / 1000);
    const total = durations.reduce((sum, value) => sum + value, 0);
    const average = durations.length ? total / durations.length : 0;
    const minimum = durations.length ? Math.min(...durations) : 0;
    const maximum = durations.length ? Math.max(...durations) : 0;
    const firstStart = requests.map(r => this.parseTime(r.startedAt)).filter((v): v is number => v !== undefined)
      .reduce<number | undefined>((min, value) => min === undefined || value < min ? value : min, undefined);
    const maxOffset = requests.reduce((max, request) => {
      const start = this.parseTime(request.startedAt);
      if (start === undefined || firstStart === undefined) return max;
      const offset = Math.max(0, start - firstStart);
      const duration = typeof request.elapsedMicros === "number" ? request.elapsedMicros / 1000 : 0;
      return Math.max(max, offset + duration);
    }, 0);

    const rows = requests.map(request => {
      const start = this.parseTime(request.startedAt);
      const offset = start !== undefined && firstStart !== undefined ? Math.max(0, start - firstStart) : undefined;
      const duration = typeof request.elapsedMicros === "number" ? request.elapsedMicros / 1000 : undefined;
      const width = duration !== undefined && maxOffset > 0 ? Math.max(0.5, duration / maxOffset * 100) : 0;
      const left = offset !== undefined && maxOffset > 0 ? offset / maxOffset * 100 : 0;
      const statusLabel = request.status === "pending" ? "Running" : request.status === "succeeded" ? "Succeeded" : "Failed";
      const statusClass = request.status === "pending" ? "pending" : request.status === "succeeded" ? "success" : "failure";
      const bar = duration === undefined ? '<span class="running-bar">In progress</span>' :
        `<div class="track"><div class="bar ${statusClass}" style="left:${left.toFixed(3)}%;width:${width.toFixed(3)}%"></div></div>`;
      return `<tr><td>${String(request.id).padStart(3, "0")}</td>
        <td><span class="badge ${statusClass}">${statusLabel}</span></td>
        <td><strong>${this.escapeHtml(request.method ?? "HTTP")}</strong><br><span class="muted">${this.escapeHtml(request.name ?? `Request #${request.id}`)}</span><br><span class="url">${this.escapeHtml(request.url ?? "")}</span></td>
        <td class="number">${offset === undefined ? "—" : this.formatMs(offset)}</td>
        <td class="number duration">${duration === undefined ? "—" : this.formatMs(duration)}</td>
        <td class="timeline">${bar}</td></tr>`;
    }).join("");

    this.panel.webview.html = `<!DOCTYPE html>
<html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
<style>
body{padding:18px 22px 28px;color:var(--vscode-foreground);background:var(--vscode-editor-background);font-family:var(--vscode-font-family);font-size:var(--vscode-font-size)}
h1{font-size:1.35em;margin:0 0 6px}.muted,.subtle{color:var(--vscode-descriptionForeground)}.subtle{margin:0 0 18px}
.metrics{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;margin:18px 0 24px}
.metric{border:1px solid var(--vscode-panel-border);border-radius:4px;padding:10px 12px}.metric-label{display:block;color:var(--vscode-descriptionForeground);font-size:.9em}
.metric-value{display:block;font-size:1.25em;font-weight:600;margin-top:4px}
table{width:100%;border-collapse:collapse;table-layout:auto}th{text-align:left;color:var(--vscode-descriptionForeground);font-weight:600;font-size:.9em}
th,td{padding:9px 8px;border-bottom:1px solid var(--vscode-panel-border);vertical-align:middle}
.number{text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}.duration{font-weight:600}.badge{font-size:.9em;font-weight:600;white-space:nowrap}
.success{color:var(--vscode-testing-iconPassed)}.failure{color:var(--vscode-testing-iconFailed)}.pending{color:var(--vscode-descriptionForeground)}
.url{display:inline-block;max-width:420px;overflow-wrap:anywhere;color:var(--vscode-descriptionForeground);font-size:.9em}
.timeline{min-width:180px;width:35%}.track{position:relative;height:14px;background:var(--vscode-editorWidget-background);border-radius:3px;min-width:100px}
.bar{position:absolute;top:2px;height:10px;min-width:2px;border-radius:2px;background:var(--vscode-charts-blue)}.bar.failure{background:var(--vscode-charts-red)}
.running-bar,.empty{color:var(--vscode-descriptionForeground);font-style:italic}@media(max-width:760px){body{padding:12px}.timeline{min-width:100px}.url{max-width:220px}}
</style></head><body>
<h1>Timing Inspector</h1>
<p class="subtle">Session: ${this.escapeHtml(status)} · Duration is the measured HTTP send time reported by the engine. DNS, TCP, TLS and other unmeasured phases are not inferred.</p>
<div class="metrics">
<div class="metric"><span class="metric-label">Requests</span><span class="metric-value">${requests.length}</span></div>
<div class="metric"><span class="metric-label">Completed timings</span><span class="metric-value">${completed.length}</span></div>
<div class="metric"><span class="metric-label">Sum of request times</span><span class="metric-value">${this.formatMs(total)}</span></div>
<div class="metric"><span class="metric-label">Average</span><span class="metric-value">${this.formatMs(average)}</span></div>
<div class="metric"><span class="metric-label">Minimum</span><span class="metric-value">${durations.length ? this.formatMs(minimum) : "—"}</span></div>
<div class="metric"><span class="metric-label">Maximum</span><span class="metric-value">${durations.length ? this.formatMs(maximum) : "—"}</span></div></div>
${requests.length ? `<table><thead><tr><th>#</th><th>Status</th><th>Request</th><th class="number">Start offset</th><th class="number">Duration</th><th>Timeline</th></tr></thead><tbody>${rows}</tbody></table>` : '<p class="empty">Run a WebTest to collect request timings.</p>'}
</body></html>`;
  }

  private parseTime(value?: string): number | undefined {
    if (!value) return undefined;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  private formatMs(value: number): string { return `${value.toFixed(2)} ms`; }

  private escapeHtml(value: string): string {
    return value.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  dispose(): void { this.disposed = true; this.panel?.dispose(); this.panel = undefined; }
}
