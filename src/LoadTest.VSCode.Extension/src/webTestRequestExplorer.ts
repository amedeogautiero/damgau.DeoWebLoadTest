import * as vscode from "vscode";
import { DebugRequest, WebTestDebugSession } from "./webTestDebugSession";

export class WebTestRequestExplorer implements vscode.TreeDataProvider<DebugRequest> {
  private readonly changed = new vscode.EventEmitter<DebugRequest | undefined | void>();
  readonly onDidChangeTreeData = this.changed.event;

  constructor(private readonly session: WebTestDebugSession) {}

  refresh(): void {
    this.changed.fire();
  }

  getTreeItem(request: DebugRequest): vscode.TreeItem {
    const method = (request.method ?? "???").toUpperCase();
    const label = `#${String(request.id).padStart(3, "0")}  ${method}  ${this.shortUrl(request.url)}`;
    const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
    item.id = `deoloadtest.request.${request.id}`;
    item.contextValue = "deoloadtest.request";
    item.iconPath = this.iconFor(request.status);
    item.description = this.descriptionFor(request);
    item.tooltip = this.tooltipFor(request);
    return item;
  }

  getChildren(element?: DebugRequest): vscode.ProviderResult<DebugRequest[]> {
    if (element) return [];
    return [...this.session.requests];
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
    tooltip.appendMarkdown(`**Request #${request.id}**  \n\n`);
    tooltip.appendMarkdown(`**Status:** ${request.status}  \n\n`);
    if (request.method) tooltip.appendMarkdown(`**Method:** ${this.escape(request.method)}  \n\n`);
    if (request.url) tooltip.appendMarkdown(`**URL:** ${this.escape(request.url)}  \n\n`);
    if (typeof request.statusCode === "number") {
      tooltip.appendMarkdown(`**Response:** ${request.statusCode} ${this.escape(request.reasonPhrase ?? "")}  \n\n`);
    }
    if (typeof request.elapsedMicros === "number") {
      tooltip.appendMarkdown(`**Duration:** ${(request.elapsedMicros / 1000).toFixed(2)} ms  \n\n`);
    }
    if (request.errorMessage) tooltip.appendMarkdown(`**Error:** ${this.escape(request.errorMessage)}  \n\n`);
    tooltip.isTrusted = false;
    return tooltip;
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
    return value.replace(/[\\\\`*_{}[\]()#+.!|>~-]/g, "\\\\$&");
  }

  dispose(): void {
    this.changed.dispose();
  }
}
