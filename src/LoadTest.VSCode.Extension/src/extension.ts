import * as vscode from "vscode";
import { CommunicationClient, HostNotification } from "./communicationClient";
import { HostManager } from "./hostManager";
import { importHarFile } from "./harImporter";
import { registerWebTestValidator } from "./webTestValidator";
import { WebTestDebugSession, WebTestExecutionEvent } from "./webTestDebugSession";
import { WebTestRequestExplorer } from "./webTestRequestExplorer";
import { WebTestRequestInspector } from "./webTestRequestInspector";

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
    success?: boolean;
    failures?: string[];
    elapsedMs?: number;
    requests?: number;
  };
  event?: WebTestExecutionEvent;
  error?: { code?: string; message?: string; details?: string };
}

export function activate(context: vscode.ExtensionContext): void {
  const hostManager = new HostManager(context.extensionUri.fsPath);
  const communicationClient = new CommunicationClient(hostManager);
  const runOutput = vscode.window.createOutputChannel("DeoLoadTest Run");
  registerWebTestValidator(context);
  let activeRunId: string | undefined;
  const webTestDebugSession = new WebTestDebugSession();
  const requestExplorer = new WebTestRequestExplorer(webTestDebugSession);
  const requestInspector = new WebTestRequestInspector(context.extensionUri);
  const requestExplorerView = vscode.window.createTreeView("deoloadtest.requestExplorer", {
    treeDataProvider: requestExplorer,
    showCollapseAll: false
  });
  const requestSelectionSubscription = requestExplorerView.onDidChangeSelection(({ selection }) => {
    const selectedRequest = selection[0];
    if (selectedRequest) requestInspector.show(selectedRequest);
  });
  let webTestRequestRows: string[] = [];
  let webTestFailureDetails: string[] = [];
  let webTestSuccessfulRequests = 0;
  let webTestFailedRequests = 0;

  const notificationSubscription = communicationClient.onNotification((notification) => {
    if (!notification.method.startsWith("run.") && !notification.method.startsWith("webtest.")) return;

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
        if (params.result?.summary) {
          runOutput.appendLine(`Summary: ${JSON.stringify(params.result.summary)}`);
        }
        runOutput.appendLine(
          `Run ${params.runId ?? activeRunId ?? ""} completed. Engine status: ${params.result?.run?.status ?? "unknown"}.`
        );
        activeRunId = undefined;
        void vscode.window.showInformationMessage(
          "DeoLoadTest run completed.",
          { modal: true },
          "OK"
        );
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
      case "webtest.event": {
        const event = params.event ?? {};
        webTestDebugSession.applyExecutionEvent(event);
        if (event.eventType === "RequestStarted" || event.eventType === "RequestCompleted" || event.eventType === "RequestFailed") {
          requestExplorer.refresh();
        }
        if (event.eventType === "RequestCompleted" || event.eventType === "RequestFailed") {
          const success = event.eventType === "RequestCompleted";
          if (success) webTestSuccessfulRequests++;
          else webTestFailedRequests++;

          const index = webTestSuccessfulRequests + webTestFailedRequests;
          const method = (event.method ?? "???").toUpperCase().padEnd(7);
          const url = event.url ?? "n/a";
          const status = typeof event.statusCode === "number"
            ? `${event.statusCode} ${event.reasonPhrase ?? (event.statusCode >= 400 ? "Error" : "OK")}`
            : success ? "OK" : (event.errorType ?? "FAILED");
          const duration = typeof event.elapsedMicros === "number"
            ? `${(event.elapsedMicros / 1000).toFixed(1)} ms`
            : "n/a";
          webTestRequestRows.push(`${String(index).padStart(3, "0")} ${method} ${url}  ${status.padEnd(12)} ${duration}`);

          if (!success) {
            const details = [
              "",
              `REQUEST FAILED: Request ${index} - ${event.method ?? "n/a"}`,
              `  URL: ${url}`,
              `  Type: ${event.errorType ?? "n/a"}`,
              `  Error: ${event.errorMessage ?? "n/a"}`,
              `  Duration: ${duration}`
            ];
            if (event.exceptionDetails) {
              details.push("  Exception chain:");
              details.push(...event.exceptionDetails.split("\n").map(line => `    ${line}`));
            }
            if (event.requestDefinition !== undefined) {
              details.push("  YAML request node (parsed):");
              details.push(...JSON.stringify(event.requestDefinition, null, 2)
                .split("\n").map(line => `    ${line}`));
            }
            webTestFailureDetails.push(details.join("\n"));
          }
        }
        break;
      }
      case "webtest.completed": {
        const result = params.result ?? {};
        webTestDebugSession.complete(result);
        requestExplorer.refresh();
        const total = webTestSuccessfulRequests + webTestFailedRequests;
        runOutput.appendLine("");
        runOutput.appendLine("Requests:");
        webTestRequestRows.forEach((row, index) => {
          const prefix = `[${String(index + 1).padStart(3, "0")}/${total}]`;
          runOutput.appendLine(`${prefix} ${row.slice(4)}`);
        });
        for (const details of webTestFailureDetails) runOutput.appendLine(details);
        runOutput.appendLine("");
        runOutput.appendLine(`WebTest ${result.success ? "succeeded" : "failed"}.`);
        runOutput.appendLine("");
        runOutput.appendLine("Summary:");
        runOutput.appendLine(`  Requests:       ${total}`);
        runOutput.appendLine(`  Successful:     ${webTestSuccessfulRequests}`);
        runOutput.appendLine(`  Failed:         ${webTestFailedRequests}`);
        runOutput.appendLine(`  Total duration: ${(result.elapsedMs ?? 0).toLocaleString("en-US", { minimumFractionDigits: 1, maximumFractionDigits: 1 })} ms`);
        for (const failure of result.failures ?? []) runOutput.appendLine(`  ${failure}`);
        activeRunId = undefined;
        if (result.success) {
          void vscode.window.showInformationMessage("WebTest completed successfully.");
        } else {
          void vscode.window.showErrorMessage("WebTest completed with errors. See DeoLoadTest Run output.");
        }
        break;
      }
      case "webtest.failed":
        webTestDebugSession.fail();
        requestExplorer.refresh();
        runOutput.appendLine(`WebTest execution failed: ${params.error?.message ?? "Unknown error"}`);
        if (params.error?.details) runOutput.appendLine(params.error.details);
        activeRunId = undefined;
        void vscode.window.showErrorMessage(
          `WebTest execution failed: ${params.error?.message ?? "Unknown error"}`
        );
        break;
      case "webtest.cancelled":
        webTestDebugSession.cancel();
        requestExplorer.refresh();
        runOutput.appendLine(`WebTest ${params.runId ?? activeRunId ?? ""} cancelled.`);
        activeRunId = undefined;
        void vscode.window.showInformationMessage("WebTest cancelled.");
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

  const runWebTestCommand = vscode.commands.registerCommand(
    "deoloadtest.runWebTest",
    async () => {
      if (!hostManager.isRunning || !communicationClient.isInitialized) {
        void vscode.window.showErrorMessage(
          "DeoLoadTest Host is not started and initialized. Run 'DeoLoadTest: Start Host' first."
        );
        return;
      }

      const selection = await vscode.window.showOpenDialog({
        canSelectMany: false,
        openLabel: "Run WebTest",
        filters: {
          "DeoLoadTest WebTest files": ["webtest.yaml", "webtest.yml"]
        }
      });
      if (!selection?.length) return;

      try {
        runOutput.clear();
        runOutput.show(true);
        webTestRequestRows = [];
        webTestFailureDetails = [];
        webTestSuccessfulRequests = 0;
        webTestFailedRequests = 0;
        runOutput.appendLine(`Starting WebTest: ${selection[0].fsPath}`);
        runOutput.appendLine("");

        const result = await communicationClient.startWebTest(selection[0].fsPath);
        activeRunId = result.runId;
        webTestDebugSession.start(result.runId);
        requestExplorer.refresh();
        runOutput.appendLine(`WebTest accepted. Run ID: ${result.runId}`);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        runOutput.appendLine(`Unable to start WebTest: ${message}`);
        void vscode.window.showErrorMessage(`Unable to start WebTest: ${message}`);
      }
    }
  );

  const focusRequestExplorerCommand = vscode.commands.registerCommand(
    "deoloadtest.focusRequestExplorer",
    async () => {
      await vscode.commands.executeCommand("workbench.view.explorer");
      await vscode.commands.executeCommand("deoloadtest.requestExplorer.focus");
    }
  );

  const importHarCommand = vscode.commands.registerCommand(
    "deoloadtest.importHar",
    async () => {
      try {
        await importHarFile();
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`Unable to import HAR: ${message}`);
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
    runWebTestCommand,
    importHarCommand,
    stopHostCommand,
    notificationSubscription,
    requestExplorerView,
    requestSelectionSubscription,
    requestExplorer,
    requestInspector,
    focusRequestExplorerCommand,
    communicationClient,
    hostManager,
    runOutput
  );
}

export function deactivate(): void {
  // Resources are disposed through context.subscriptions.
}
