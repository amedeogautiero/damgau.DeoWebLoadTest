import * as vscode from "vscode";
import { CommunicationClient, HostNotification } from "./communicationClient";
import { HostManager } from "./hostManager";

interface RunNotificationParams {
  runId?: string;
  snapshot?: {
    status?: string;
    elapsed?: unknown;
    activeUsers?: number;
    completedTests?: number;
    failedTests?: number;
    completedRequests?: number;
    failedRequests?: number;
  };
  result?: {
    run?: { status?: string };
    summary?: unknown;
  };
  error?: { code?: string; message?: string; details?: string };
}

export function activate(context: vscode.ExtensionContext): void {
  const hostManager = new HostManager(context.extensionUri.fsPath);
  const communicationClient = new CommunicationClient(hostManager);
  const runOutput = vscode.window.createOutputChannel("DeoLoadTest Run");
  let activeRunId: string | undefined;

  const notificationSubscription = communicationClient.onNotification((notification) => {
    if (!notification.method.startsWith("run.")) return;

    const params = (notification.params ?? {}) as RunNotificationParams;
    if (params.runId && activeRunId && params.runId !== activeRunId) return;

    switch (notification.method) {
      case "run.progress": {
        const snapshot = params.snapshot ?? {};
        runOutput.appendLine(
          `Progress: status=${snapshot.status ?? "unknown"}, elapsed=${String(snapshot.elapsed ?? "n/a")}, activeUsers=${snapshot.activeUsers ?? "n/a"}, tests=${snapshot.completedTests ?? "n/a"} completed/${snapshot.failedTests ?? 0} failed, requests=${snapshot.completedRequests ?? "n/a"} completed/${snapshot.failedRequests ?? 0} failed.`
        );
        break;
      }
      case "run.completed":
        runOutput.appendLine(
          `Run ${params.runId ?? activeRunId ?? ""} completed. Engine status: ${params.result?.run?.status ?? "unknown"}.`
        );
        if (params.result?.summary) {
          runOutput.appendLine(`Summary: ${JSON.stringify(params.result.summary)}`);
        }
        activeRunId = undefined;
        void vscode.window.showInformationMessage("DeoLoadTest run completed.");
        break;
      case "run.failed":
        runOutput.appendLine(
          `Run failed: ${params.error?.message ?? "Unknown execution error"}`
        );
        if (params.error?.details) runOutput.appendLine(params.error.details);
        activeRunId = undefined;
        void vscode.window.showErrorMessage(
          `DeoLoadTest run failed: ${params.error?.message ?? "Unknown error"}`
        );
        break;
      case "run.cancelled":
        runOutput.appendLine(`Run ${params.runId ?? activeRunId ?? ""} cancelled.`);
        activeRunId = undefined;
        void vscode.window.showInformationMessage("DeoLoadTest run cancelled.");
        break;
      default:
        runOutput.appendLine(`Host notification: ${notification.method}`);
    }
  });

  const showWelcomeCommand = vscode.commands.registerCommand(
    "deoloadtest.showWelcome",
    () => {
      void vscode.window.showInformationMessage("DeoLoadTest extension is active.");
    }
  );

  const startHostCommand = vscode.commands.registerCommand(
    "deoloadtest.startHost",
    async () => {
      try {
        await hostManager.start();
        const result = await communicationClient.initialize();
        void vscode.window.showInformationMessage(
          `DeoLoadTest Host initialized (protocol ${result.protocolVersion}).`
        );
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`Unable to start or initialize DeoLoadTest Host: ${message}`);
      }
    }
  );

  const runLoadTestCommand = vscode.commands.registerCommand(
    "deoloadtest.runLoadTest",
    async () => {
      if (!hostManager.isRunning || !communicationClient.isInitialized) {
        void vscode.window.showErrorMessage(
          "DeoLoadTest Host is not started and initialized. Run 'DeoLoadTest: Start Host' first."
        );
        return;
      }

      const selection = await vscode.window.showOpenDialog({
        canSelectMany: false,
        openLabel: "Run Load Test",
        filters: {
          "DeoLoadTest files": ["loadtest.yaml", "yaml", "yml"]
        }
      });
      if (!selection?.length) return;

      try {
        runOutput.clear();
        runOutput.show(true);
        runOutput.appendLine(`Starting load test: ${selection[0].fsPath}`);

        const result = await communicationClient.startRun(selection[0].fsPath);
        activeRunId = result.runId;
        runOutput.appendLine(`Run accepted. Run ID: ${result.runId}`);
        void vscode.window.showInformationMessage("DeoLoadTest run started.");
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        runOutput.appendLine(`Unable to start load test: ${message}`);
        void vscode.window.showErrorMessage(`Unable to start DeoLoadTest run: ${message}`);
      }
    }
  );

  const stopHostCommand = vscode.commands.registerCommand(
    "deoloadtest.stopHost",
    async () => {
      communicationClient.resetInitialization();
      await hostManager.stop();
    }
  );

  context.subscriptions.push(
    showWelcomeCommand,
    startHostCommand,
    runLoadTestCommand,
    stopHostCommand,
    notificationSubscription,
    communicationClient,
    hostManager,
    runOutput
  );
}

export function deactivate(): void {
  // Resources are disposed through context.subscriptions.
}
