import * as vscode from "vscode";
import { HostManager } from "./hostManager";

const DEBUG_SESSION_NAME = "DeoLoadTest Host";

export class HostDebugger implements vscode.Disposable {
  private attached = false;
  private starting: Promise<void> | undefined;

  private readonly startSubscription = vscode.debug.onDidStartDebugSession(session => {
    if (session.name === DEBUG_SESSION_NAME) this.attached = true;
  });

  private readonly endSubscription = vscode.debug.onDidTerminateDebugSession(session => {
    if (session.name === DEBUG_SESSION_NAME) this.attached = false;
  });

  constructor(private readonly host: HostManager) {}

  async attach(): Promise<void> {
    if (this.attached) return;
    if (this.starting) return this.starting;

    this.starting = this.attachCore();
    try {
      await this.starting;
    } finally {
      this.starting = undefined;
    }
  }

  private async attachCore(): Promise<void> {
    const processId = this.host.processId;
    if (!processId) {
      throw new Error("DeoLoadTest Host process ID is not available.");
    }

    const csharp = vscode.extensions.getExtension("ms-dotnettools.csharp");
    if (!csharp) {
      throw new Error(
        "The Microsoft C# extension (ms-dotnettools.csharp) is required to debug C# scripts."
      );
    }
    if (!csharp.isActive) await csharp.activate();

    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    const started = await vscode.debug.startDebugging(workspaceFolder, {
      type: "coreclr",
      request: "attach",
      name: DEBUG_SESSION_NAME,
      processId: String(processId),
      justMyCode: true
    });

    if (!started) {
      throw new Error(
        "VS Code could not attach the C# debugger to the DeoLoadTest Host process."
      );
    }

    this.attached = true;
  }

  dispose(): void {
    this.startSubscription.dispose();
    this.endSubscription.dispose();
  }
}
