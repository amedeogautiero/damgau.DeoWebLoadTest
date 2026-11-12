import { ChildProcess, spawn } from "child_process";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

const HOST_READY_MARKER = "DEOLOADTEST_HOST_READY";
const HOST_START_TIMEOUT_MS = 30000;

export class HostManager implements vscode.Disposable {
  private process: ChildProcess | undefined;
  private startup: Promise<void> | undefined;
  private readonly output = vscode.window.createOutputChannel("DeoLoadTest Host");
  private readonly stdoutListeners = new Set<(chunk: string) => void>();

  constructor(private readonly extensionPath: string) {}

  async start(): Promise<void> {
    if (this.process && this.process.exitCode === null) {
      if (this.startup) await this.startup;
      return;
    }

    const projectPath = path.resolve(
      this.extensionPath,
      "..",
      "LoadTest.VSCode.Host",
      "LoadTest.VSCode.Host.csproj"
    );

    if (!fs.existsSync(projectPath)) {
      throw new Error(
        `Host project not found: ${projectPath}. This development version expects the repository source layout.`
      );
    }

    this.output.show(true);
    this.output.appendLine("Starting DeoLoadTest Host...");

    const child = spawn(
      "dotnet",
      ["run", "--project", projectPath, "--no-launch-profile"],
      {
        cwd: path.dirname(projectPath),
        stdio: ["pipe", "pipe", "pipe"],
        windowsHide: true
      }
    );

    this.process = child;

    child.stdout?.on("data", (data: Buffer) => {
      const chunk = data.toString();
      for (const listener of this.stdoutListeners) {
        listener(chunk);
      }
    });

    let resolveStartup!: () => void;
    let rejectStartup!: (error: Error) => void;
    let ready = false;
    let stderrBuffer = "";

    const startup = new Promise<void>((resolve, reject) => {
      resolveStartup = resolve;
      rejectStartup = reject;
    });
    this.startup = startup;

    const timeout = setTimeout(() => {
      if (!ready) {
        rejectStartup(new Error(
          `DeoLoadTest Host did not become ready within ${HOST_START_TIMEOUT_MS / 1000} seconds.`
        ));
      }
    }, HOST_START_TIMEOUT_MS);

    child.stderr?.on("data", (data: Buffer) => {
      const chunk = data.toString();
      this.output.append(chunk);
      stderrBuffer += chunk;

      if (!ready && stderrBuffer.includes(HOST_READY_MARKER)) {
        ready = true;
        clearTimeout(timeout);
        resolveStartup();
      }

      // Keep only a short tail in case the marker is split across chunks.
      if (stderrBuffer.length > HOST_READY_MARKER.length * 2) {
        stderrBuffer = stderrBuffer.slice(-HOST_READY_MARKER.length);
      }
    });

    child.on("error", (error) => {
      this.output.appendLine(`Host process error: ${error.message}`);
      clearTimeout(timeout);
      if (!ready) rejectStartup(error);
      if (this.process === child) this.process = undefined;
    });

    child.on("exit", (code, signal) => {
      this.output.appendLine(
        `Host process exited (code: ${code ?? "none"}, signal: ${signal ?? "none"}).`
      );
      clearTimeout(timeout);
      if (!ready) {
        rejectStartup(new Error(
          `DeoLoadTest Host exited before becoming ready (code: ${code ?? "none"}, signal: ${signal ?? "none"}).`
        ));
      }
      if (this.process === child) this.process = undefined;
    });

    this.output.appendLine("Host process launched; waiting for readiness...");

    try {
      await startup;
      this.output.appendLine("DeoLoadTest Host is ready.");
      void vscode.window.showInformationMessage("DeoLoadTest Host is ready.");
    } finally {
      clearTimeout(timeout);
      if (this.startup === startup) this.startup = undefined;
    }
  }

  send(message: unknown): void {
    const child = this.process;
    if (!child || child.exitCode !== null || !child.stdin?.writable) {
      throw new Error("DeoLoadTest Host is not running or its input stream is unavailable.");
    }

    child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  onStdout(listener: (chunk: string) => void): vscode.Disposable {
    this.stdoutListeners.add(listener);
    return {
      dispose: () => this.stdoutListeners.delete(listener)
    };
  }

  stop(): void {
    const child = this.process;
    if (!child || child.exitCode !== null) {
      this.process = undefined;
      void vscode.window.showInformationMessage("DeoLoadTest Host is not running.");
      return;
    }

    child.kill();
    this.output.appendLine("Stop signal sent to DeoLoadTest Host.");
    this.process = undefined;
    void vscode.window.showInformationMessage("Stop signal sent to DeoLoadTest Host.");
  }

  dispose(): void {
    if (this.process && this.process.exitCode === null) {
      this.process.kill();
      this.process = undefined;
    }

    this.stdoutListeners.clear();
    this.output.dispose();
  }
}
