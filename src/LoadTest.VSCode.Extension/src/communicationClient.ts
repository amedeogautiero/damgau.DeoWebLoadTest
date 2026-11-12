import * as vscode from "vscode";
import { HostManager } from "./hostManager";

interface JsonRpcResponse<T> {
  jsonrpc: "2.0";
  id: number;
  result?: T;
  error?: {
    code: number;
    message: string;
    data?: unknown;
  };
}

export interface HostNotification {
  jsonrpc: "2.0";
  method: string;
  params?: unknown;
}

interface InitializeResult {
  protocolVersion: string;
  serverName: string;
  capabilities: Record<string, boolean>;
}

interface RunStartResult {
  runId: string;
}

export class CommunicationClient implements vscode.Disposable {
  private nextRequestId = 1;
  private buffer = "";
  private readonly pending = new Map<
    number,
    { resolve: (value: unknown) => void; reject: (error: Error) => void; timer: NodeJS.Timeout }
  >();
  private readonly notificationListeners = new Set<(notification: HostNotification) => void>();
  private readonly stdoutSubscription: vscode.Disposable;
  private readonly output = vscode.window.createOutputChannel("DeoLoadTest Communication");

  constructor(private readonly host: HostManager) {
    this.stdoutSubscription = this.host.onStdout((chunk) => this.handleStdout(chunk));
  }

  async initialize(): Promise<InitializeResult> {
    const result = await this.request<InitializeResult>("initialize", {});
    this.output.appendLine(
      `Connected to ${result.serverName}; protocol version ${result.protocolVersion}.`
    );
    this.output.appendLine(`Capabilities: ${JSON.stringify(result.capabilities)}`);
    this.output.show(true);
    return result;
  }

  async startRun(loadTestFile: string): Promise<RunStartResult> {
    return this.request<RunStartResult>("run.start", { loadTestFile });
  }

  onNotification(listener: (notification: HostNotification) => void): vscode.Disposable {
    this.notificationListeners.add(listener);
    return {
      dispose: () => this.notificationListeners.delete(listener)
    };
  }

  private request<T>(method: string, params: unknown): Promise<T> {
    const id = this.nextRequestId++;

    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Timeout waiting for JSON-RPC response to '${method}'.`));
      }, 10000);

      this.pending.set(id, {
        resolve: (value) => resolve(value as T),
        reject,
        timer
      });

      try {
        this.host.send({ jsonrpc: "2.0", id, method, params });
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private handleStdout(chunk: string): void {
    this.buffer += chunk;

    while (true) {
      const newlineIndex = this.buffer.indexOf("\n");
      if (newlineIndex < 0) break;

      const line = this.buffer.slice(0, newlineIndex).trim();
      this.buffer = this.buffer.slice(newlineIndex + 1);
      if (!line) continue;

      let message: JsonRpcResponse<unknown> | HostNotification;
      try {
        message = JSON.parse(line);
      } catch {
        this.output.appendLine(`Ignoring non-JSON Host output: ${line}`);
        continue;
      }

      if ("id" in message && typeof message.id === "number") {
        const pending = this.pending.get(message.id);
        if (!pending) continue;

        clearTimeout(pending.timer);
        this.pending.delete(message.id);

        if (message.error) {
          pending.reject(new Error(`JSON-RPC ${message.error.code}: ${message.error.message}`));
        } else {
          pending.resolve(message.result);
        }
      } else if ("method" in message) {
        for (const listener of this.notificationListeners) {
          listener(message);
        }
      }
    }
  }

  dispose(): void {
    this.stdoutSubscription.dispose();
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(new Error("Communication client disposed."));
    }
    this.pending.clear();
    this.notificationListeners.clear();
    this.output.dispose();
  }
}
