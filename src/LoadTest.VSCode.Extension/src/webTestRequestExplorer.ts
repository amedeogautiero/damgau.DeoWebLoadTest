import * as vscode from "vscode";
import { DebugRequest, WebTestDebugSession } from "./webTestDebugSession";

const TIMELINE_WIDTH = 16;

export class WebTestRequestExplorer implements vscode.TreeDataProvider<DebugRequest | vscode.TreeItem> {
  private readonly changed = new vscode.EventEmitter<DebugRequest | vscode.TreeItem | undefined | void>();
  readonly onDidChangeTreeData = this.changed.event;

  constructor(private readonly session: WebTestDebugSession) {}

  refresh(): void {
    this.changed.fire();
  }

  getTreeItem(request: DebugRequest | vscode.TreeItem): vscode.TreeItem {
    if (typeof (request as DebugRequest).status !== "string") return request as vscode.TreeItem;
    const method = (request.method ?? "???").toUpperCase();
    const label = `#${String(request.id).padStart(3, "0")}  ${method}  ${this.shortUrl(request.url)}  │ ${this.timelineFor(request)}`;
    const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
    item.id = `deoloadtest.request.${request.id}`;
    item.contextValue = "deoloadtest.request";
    item.iconPath = this.iconFor(request.status);
    item.description = this.descriptionFor(request);
    item.tooltip = this.tooltipFor(request);
    return item;
  }

  getChildren(element?: DebugRequest | vscode.TreeItem): vscode.ProviderResult<(DebugRequest | vscode.TreeItem)[]> {
    if (element) return [];
    const items: (DebugRequest | vscode.TreeItem)[] = [];
    if (this.session.testName) {
      const header = new vscode.TreeItem(`Test: ${this.session.testName}`, vscode.TreeItemCollapsibleState.None);
      header.id = "deoloadtest.requestExplorer.testName";
      header.contextValue = "deoloadtest.testHeader";
      header.iconPath = new vscode.ThemeIcon("beaker");
      header.description = this.session.status;
      header.tooltip = `WebTest: ${this.session.testName}`;
      items.push(header);
    }
    items.push(...this.session.requests);
    return items;
  }

  private iconFor(status: DebugRequest["status"]): vscode.ThemeIcon {
    switch (status) {
      case "succeeded": return new vscode.ThemeIcon("check", new vscode.ThemeColor("testing.iconPassed"));
      case "failed": return new vscode.ThemeIcon("error", new vscode.ThemeColor("testing.iconFailed"));
      default: return new vscode.ThemeIcon("clock", new vscode.ThemeColor("descriptionForeground"));
    }
  }

  private descriptionFor(request: DebugRequest): string {
    if (request.status === "pending") return "Running…";
    const parts: string[] = [];
    if (typeof request.statusCode === "number") {
      parts.push(`${request.statusCode}${request.reasonPhrase ? ` ${request.reasonPhrase}` : ""}`);
    } else if (request.errorType) {
      parts.push(request.errorType);
    }
    if (typeof request.elapsedMicros === "number") {
      parts.push(`${(request.elapsedMicros / 1000).toFixed(1)} ms`);
    }
    return parts.join(" · ") || (request.status === "failed" ? "Failed" : "Completed");
  }

  private tooltipFor(request: DebugRequest): vscode.MarkdownString {
    const tooltip = new vscode.MarkdownString(undefined, true);
    tooltip.appendMarkdown(`**Request #${request.id}**  \\n\\n`);
    tooltip.appendMarkdown(`**Status:** ${request.status}  \\n\\n`);
    if (request.method) tooltip.appendMarkdown(`**Method:** ${this.escape(request.method)}  \\n\\n`);
    if (request.url) tooltip.appendMarkdown(`**URL:** ${this.escape(request.url)}  \\n\\n`);
    const offset = this.startOffsetMs(request);
    if (offset !== undefined) tooltip.appendMarkdown(`**Start offset:** ${offset.toFixed(2)} ms  \\n\\n`);
    if (typeof request.statusCode === "number") {
      tooltip.appendMarkdown(`**Response:** ${request.statusCode} ${this.escape(request.reasonPhrase ?? "")}  \\n\\n`);
    }
    if (typeof request.elapsedMicros === "number") {
      tooltip.appendMarkdown(`**Duration:** ${(request.elapsedMicros / 1000).toFixed(2)} ms  \\n\\n`);
    }
    if (request.errorMessage) tooltip.appendMarkdown(`**Error:** ${this.escape(request.errorMessage)}  \\n\\n`);
    tooltip.isTrusted = false;
    return tooltip;
  }

  private timelineFor(request: DebugRequest): string {
    const requests = this.session.requests;
    const firstStart = this.firstStart(requests);
    const start = this.parseTime(request.startedAt);
    if (firstStart === undefined || start === undefined) return "·".repeat(TIMELINE_WIDTH);

    const maxEnd = requests.reduce((max, item) => {
      const itemStart = this.parseTime(item.startedAt);
      if (itemStart === undefined) return max;
      const offset = Math.max(0, itemStart - firstStart);
      const duration = typeof item.elapsedMicros === "number"
        ? item.elapsedMicros / 1000
        : item.status === "pending" ? Math.max(0, Date.now() - itemStart) : 0;
      return Math.max(max, offset + duration);
    }, 1);
    const offset = Math.max(0, start - firstStart);
    const duration = typeof request.elapsedMicros === "number"
      ? request.elapsedMicros / 1000
      : request.status === "pending" ? Math.max(1, Date.now() - start) : 1;
    const left = Math.min(TIMELINE_WIDTH - 1, Math.floor(offset / maxEnd * TIMELINE_WIDTH));
    const width = Math.max(1, Math.min(TIMELINE_WIDTH - left, Math.ceil(duration / maxEnd * TIMELINE_WIDTH)));
    return "·".repeat(left) + "━".repeat(width) + "·".repeat(TIMELINE_WIDTH - left - width);
  }

  private startOffsetMs(request: DebugRequest): number | undefined {
    const firstStart = this.firstStart(this.session.requests);
    const start = this.parseTime(request.startedAt);
    return firstStart === undefined || start === undefined ? undefined : Math.max(0, start - firstStart);
  }

  private firstStart(requests: readonly DebugRequest[]): number | undefined {
    return requests.map(request => this.parseTime(request.startedAt))
      .filter((value): value is number => value !== undefined)
      .reduce<number | undefined>((min, value) => min === undefined || value < min ? value : min, undefined);
  }

  private parseTime(value?: string): number | undefined {
    if (!value) return undefined;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }

  private shortUrl(value?: string): string {
    if (!value) return "(URL unavailable)";
    try {
      const url = new URL(value);
      return `${url.host}${url.pathname}${url.search}`;
    } catch {
      return value;
    }
  }

  private escape(value: string): string {
    return value.replace(/[\\`*_{}\[\]()#+.!|>~-]/g, "\\$&");
  }

  dispose(): void {
    this.changed.dispose();
  }
}
