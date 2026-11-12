import { ChildProcess, spawn } from "child_process";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

export class HostManager implements vscode.Disposable {
  private process: ChildProcess | undefined;
  private readonly output = vscode.window.createOutputChannel("DeoLoadTest Host");
  private readonly stdoutListeners = new Set<(chunk: string) => void>();

  constructor(private readonly extensionPath: string) {}

  async start(): Promise<void> {
    if (this.process && this.process.exitCode === null) {
      void vscode.window.showInformationMessage("DeoLoadTest Host is already running.");
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

    child.stderr?.on("data", (data: Buffer) => {
      this.output.append(data.toString());
    });

    child.on("error", (error) => {
      this.output.appendLine(`Host process error: ${error.message}`);
      if (this.process === child) this.process = undefined;
    });

    child.on("exit", (code, signal) => {
      this.output.appendLine(
        `Host process exited (code: ${code ?? "none"}, signal: ${signal ?? "none"}).`
      );
      if (this.process === child) this.process = undefined;
    });

    this.output.appendLine("Host process launched.");
    void vscode.window.showInformationMessage("DeoLoadTest Host started.");
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
