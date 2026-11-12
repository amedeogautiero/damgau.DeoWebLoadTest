import * as vscode from "vscode";
import { HostManager } from "./hostManager";

const DEBUG_SESSION_NAME = "DeoLoadTest Host";
const DEBUG_SESSION_START_TIMEOUT_MS = 15000;

export class HostDebugger implements vscode.Disposable {
  private attachedProcessId: number | undefined;
  private starting: Promise<void> | undefined;

  constructor(
    private readonly host: HostManager,
    private readonly output: vscode.OutputChannel
  ) {}

  async attach(): Promise<void> {
    const processId = this.host.processId;
    if (processId && this.attachedProcessId === processId) {
      this.output.appendLine(`[debug] Existing debugger session is associated with Host PID ${processId}.`);
      return;
    }
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

    this.output.appendLine("[debug] Preparing .NET debugger attach.");
    this.output.appendLine(`[debug] Host PID: ${processId}`);
    this.output.appendLine(`[debug] Host running: ${this.host.isRunning}`);
    this.output.appendLine(`[debug] Active VS Code debug session: ${vscode.debug.activeDebugSession?.name ?? "(none)"}`);

    const csharp = vscode.extensions.getExtension("ms-dotnettools.csharp");
    if (!csharp) {
      throw new Error(
        "The Microsoft C# extension (ms-dotnettools.csharp) is required to debug C# scripts."
      );
    }

    this.output.appendLine(`[debug] C# extension active: ${csharp.isActive}`);
    if (!csharp.isActive) {
      await csharp.activate();
      this.output.appendLine("[debug] C# extension activation completed.");
    }

    const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
    this.output.appendLine(`[debug] Workspace: ${workspaceFolder?.uri.fsPath ?? "(no workspace folder)"}`);

    let resolveSession!: (session: vscode.DebugSession) => void;
    let rejectSession!: (error: Error) => void;
    const sessionStarted = new Promise<vscode.DebugSession>((resolve, reject) => {
      resolveSession = resolve;
      rejectSession = reject;
    });

    const startListener = vscode.debug.onDidStartDebugSession(session => {
      this.output.appendLine(
        `[debug] Debug session started: name="${session.name}", type="${session.type}", processId="${String(session.configuration.processId ?? "(missing)")}".`
      );
      if (
        session.name === DEBUG_SESSION_NAME &&
        Number(session.configuration.processId) === processId
      ) {
        resolveSession(session);
      }
    });

    const endListener = vscode.debug.onDidTerminateDebugSession(session => {
      this.output.appendLine(
        `[debug] Debug session terminated: name="${session.name}", type="${session.type}".`
      );
      if (session.name === DEBUG_SESSION_NAME) {
        this.attachedProcessId = undefined;
        rejectSession(new Error("The .NET debug session terminated while attaching to the DeoLoadTest Host."));
      }
    });

    const timeout = setTimeout(() => {
      rejectSession(new Error(
        `VS Code did not report a "${DEBUG_SESSION_NAME}" debug session for Host PID ${processId} within ${DEBUG_SESSION_START_TIMEOUT_MS / 1000} seconds.`
      ));
    }, DEBUG_SESSION_START_TIMEOUT_MS);

    try {
      this.output.appendLine("[debug] Calling vscode.debug.startDebugging (type=coreclr, request=attach)...");
      const started = await vscode.debug.startDebugging(workspaceFolder, {
        type: "coreclr",
        request: "attach",
        name: DEBUG_SESSION_NAME,
        processId: String(processId),
        justMyCode: true
      });

      this.output.appendLine(`[debug] vscode.debug.startDebugging returned: ${started}`);
      if (!started) {
        throw new Error(
          "VS Code rejected the request to attach the C# debugger to the DeoLoadTest Host process."
        );
      }

      const session = await sessionStarted;
      this.attachedProcessId = processId;
      this.output.appendLine(
        `[debug] Matching debug session confirmed: "${session.name}", PID ${processId}.`
      );
      this.output.appendLine(
        `[debug] Active session after attach: ${vscode.debug.activeDebugSession?.name ?? "(none)"}`
      );
    } finally {
      clearTimeout(timeout);
      startListener.dispose();
      endListener.dispose();
    }
  }

  dispose(): void {
    // Debug sessions are owned by VS Code; this class only observes the attach lifecycle.
  }
}
