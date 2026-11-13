import * as vscode from "vscode";
import { execFile } from "child_process";
import * as fs from "fs/promises";
import * as path from "path";
import { CommunicationClient, HostNotification } from "./communicationClient";
import { HostManager } from "./hostManager";
import { HostDebugger } from "./hostDebugger";
import { importHarFile } from "./harImporter";
import { registerWebTestValidator } from "./webTestValidator";
import { WebTestDebugSession, WebTestExecutionEvent } from "./webTestDebugSession";
import { WebTestRequestExplorer } from "./webTestRequestExplorer";
import { WebTestRequestInspector } from "./webTestRequestInspector";
import { WebTestTimingInspector } from "./webTestTimingInspector";
import { WebTestDebugPanel } from "./webTestDebugPanel";
import { WebTestDebugPanelVertical } from "./webTestDebugPanelVertical";

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


async function buildWebTestPackage(
  projectPath: string,
  outputDirectory: string
): Promise<{ stdout: string; stderr: string }> {
  const projectDirectory = path.dirname(projectPath);
  const buildOutputDirectory = path.join(projectDirectory, "bin", "Debug", "net10.0");

  // temp.pkg è dentro la directory del progetto: va rimosso PRIMA della build,
  // altrimenti il glob Compile predefinito include anche i .cs già copiati lì.
  await fs.rm(outputDirectory, { recursive: true, force: true });

  // Usa la build standard del progetto originale: MSBuild risolve tutte le
  // ProjectReference e PackageReference, incluse le dipendenze dei Code Step.
  const buildResult = await new Promise<{ stdout: string; stderr: string }>((resolve, reject) => {
    execFile(
      "dotnet",
      ["build", projectPath, "--configuration", "Debug"],
      { cwd: projectDirectory, maxBuffer: 10 * 1024 * 1024 },
      (error, stdout, stderr) => {
        if (error) {
          const details = [stdout, stderr].filter(Boolean).join("\n");
          reject(new Error(details || error.message));
          return;
        }
        resolve({ stdout, stderr });
      }
    );
  });

  // temp.pkg riproduce il contenuto distribuibile: parte dall'output completo
  // della build normale e rimuove esclusivamente gli artefatti del test runner.
  await fs.mkdir(outputDirectory, { recursive: true });

  const excludedRuntimeFiles = /^(xunit(?:\.|$)|Microsoft\.TestPlatform(?:\.|$)|Microsoft\.VisualStudio\.TestPlatform(?:\.|$)|Microsoft\.VisualStudio\.CodeCoverage\.Shim(?:\.|$)|testhost(?:\.|$)|coverlet(?:\.|$)|Microsoft\.CodeCoverage(?:\.|$)|\.msCoverageSourceRootsMapping(?:_|$)|.+\.(?:deps|runtimeconfig)\.json$)/i;
  const copyRuntimeOutput = async (source: string, destination: string): Promise<void> => {
    const entries = await fs.readdir(source, { withFileTypes: true });
    for (const entry of entries) {
      if (entry.isFile() && excludedRuntimeFiles.test(entry.name)) continue;
      const sourcePath = path.join(source, entry.name);
      const destinationPath = path.join(destination, entry.name);
      if (entry.isDirectory()) {
        await fs.mkdir(destinationPath, { recursive: true });
        await copyRuntimeOutput(sourcePath, destinationPath);
      } else if (entry.isFile()) {
        await fs.copyFile(sourcePath, destinationPath);
      }
    }
  };

  try {
    await copyRuntimeOutput(buildOutputDirectory, outputDirectory);
  } catch (error) {
    throw new Error(
      `Standard build completed, but its output directory was not found: ${buildOutputDirectory}. ${error instanceof Error ? error.message : String(error)}`
    );
  }

  // Elimina ricorsivamente le directory rimaste vuote dopo i filtri.
  const removeEmptyDirectories = async (directory: string): Promise<boolean> => {
    const entries = await fs.readdir(directory, { withFileTypes: true });
    for (const entry of entries) {
      if (!entry.isDirectory()) continue;
      const childDirectory = path.join(directory, entry.name);
      if (await removeEmptyDirectories(childDirectory)) {
        await fs.rmdir(childDirectory);
      }
    }
    return (await fs.readdir(directory)).length === 0;
  };
  await removeEmptyDirectories(outputDirectory);

  return buildResult;
}

export function activate(context: vscode.ExtensionContext): void {
  const hostManager = new HostManager(context.extensionUri.fsPath);
  const communicationClient = new CommunicationClient(hostManager);
  const runOutput = vscode.window.createOutputChannel("DeoLoadTest Run");
  const hostDebugger = new HostDebugger(hostManager, runOutput);
  registerWebTestValidator(context);
  let activeRunId: string | undefined;
  let activeWebTestRunId: string | undefined;
  void vscode.commands.executeCommand("setContext", "deoloadtest.webTestRunning", false);
  const webTestDebugSession = new WebTestDebugSession();
  const requestExplorer = new WebTestRequestExplorer(webTestDebugSession);
  const requestInspector = new WebTestRequestInspector();
  const timingInspector = new WebTestTimingInspector();
  const debugPanel = new WebTestDebugPanel(
    webTestDebugSession,
    () => { void vscode.commands.executeCommand("deoloadtest.runWebTest"); },
    () => { void vscode.commands.executeCommand("deoloadtest.stopWebTest"); },
    () => { void vscode.commands.executeCommand("deoloadtest.clearRequestExplorer"); }
  );
  const verticalDebugPanel = new WebTestDebugPanelVertical(
    webTestDebugSession,
    () => { void vscode.commands.executeCommand("deoloadtest.runWebTest"); },
    () => { void vscode.commands.executeCommand("deoloadtest.stopWebTest"); },
    () => { void vscode.commands.executeCommand("deoloadtest.clearRequestExplorer"); },
    () => { void vscode.commands.executeCommand("deoloadtest.startHost"); },
    () => { void vscode.commands.executeCommand("deoloadtest.stopHost"); }
  );
  const requestExplorerView = vscode.window.createTreeView("deoloadtest.requestExplorer", {
    treeDataProvider: requestExplorer,
    showCollapseAll: false
  });
  const requestSelectionSubscription = requestExplorerView.onDidChangeSelection(({ selection }) => {
    const selectedRequest = selection[0];
    if (selectedRequest && (selectedRequest as vscode.TreeItem).contextValue !== "deoloadtest.testHeader") {
      requestInspector.show(selectedRequest as import("./webTestDebugSession").DebugRequest, webTestDebugSession.requests);
    }
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
          const latestRequest = webTestDebugSession.requests[webTestDebugSession.requests.length - 1];
          requestInspector.update(webTestDebugSession.requests);
          timingInspector.update(webTestDebugSession.requests, webTestDebugSession.status);
          debugPanel.update();
          verticalDebugPanel.update(latestRequest?.id);
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
              details.push(...event.exceptionDetails.split("\\n").map(line => `    ${line}`));
            }
            if (event.requestDefinition !== undefined) {
              details.push("  YAML request node (parsed):");
              details.push(...JSON.stringify(event.requestDefinition, null, 2)
                .split("\\n").map(line => `    ${line}`));
            }
            webTestFailureDetails.push(details.join("\\n"));
          }
        }
        break;
      }
      case "webtest.completed": {
        const result = params.result ?? {};
        webTestDebugSession.complete(result);
        requestExplorer.refresh();
        timingInspector.update(webTestDebugSession.requests, webTestDebugSession.status);
        debugPanel.update();
        verticalDebugPanel.update();
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
        activeWebTestRunId = undefined;
        void vscode.commands.executeCommand("setContext", "deoloadtest.webTestRunning", false);
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
        timingInspector.update(webTestDebugSession.requests, webTestDebugSession.status);
        debugPanel.update();
        verticalDebugPanel.update();
        runOutput.appendLine(`WebTest execution failed: ${params.error?.message ?? "Unknown error"}`);
        if (params.error?.details) runOutput.appendLine(params.error.details);
        activeRunId = undefined;
        activeWebTestRunId = undefined;
        void vscode.commands.executeCommand("setContext", "deoloadtest.webTestRunning", false);
        void vscode.window.showErrorMessage(
          `WebTest execution failed: ${params.error?.message ?? "Unknown error"}`
        );
        break;
      case "webtest.cancelled":
        webTestDebugSession.cancel();
        requestExplorer.refresh();
        timingInspector.update(webTestDebugSession.requests, webTestDebugSession.status);
        debugPanel.update();
        verticalDebugPanel.update();
        runOutput.appendLine(`WebTest ${params.runId ?? activeRunId ?? ""} cancelled.`);
        activeRunId = undefined;
        activeWebTestRunId = undefined;
        void vscode.commands.executeCommand("setContext", "deoloadtest.webTestRunning", false);
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
      if (webTestDebugSession.status === "running") {
        void vscode.window.showWarningMessage("A WebTest is already running.");
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

        // Individua il progetto WebTest che contiene il file selezionato.
        const projects = await vscode.workspace.findFiles(
          "**/LoadTest.WebTestSample.csproj",
          "**/{bin,obj,node_modules}/**"
        );
        const selectedFile = path.resolve(selection[0].fsPath);
        const matchingProjects = projects.filter(project => {
          const projectDirectory = path.dirname(project.fsPath);
          const relative = path.relative(projectDirectory, selectedFile);
          return relative !== "" && !relative.startsWith("..") && !path.isAbsolute(relative);
        });

        if (matchingProjects.length === 0) {
          throw new Error("No LoadTest.WebTestSample.csproj contains the selected WebTest file.");
        }

        let projectUri = matchingProjects[0];
        if (matchingProjects.length > 1) {
          const selectedProject = await vscode.window.showQuickPick(
            matchingProjects.map(uri => ({
              label: path.basename(path.dirname(uri.fsPath)),
              description: vscode.workspace.asRelativePath(uri),
              uri
            })),
            { placeHolder: "Select the WebTest project to compile" }
          );
          if (!selectedProject) return;
          projectUri = selectedProject.uri;
        }

        const projectPath = projectUri.fsPath;
        const projectDirectory = path.dirname(projectPath);
        const outputDirectory = path.join(projectDirectory, "bin", "temp.pkg");
        const webTestsDirectory = path.join(projectDirectory, "webtests");
        const selectedFileName = path.basename(selectedFile);
        const packagedWebTestFile = path.join(outputDirectory, "webtests", selectedFileName);

        // Compilazione del progetto e preparazione del pacchetto prima di contattare l'Host.
        runOutput.appendLine(`Compiling WebTest project: ${projectPath}`);
        runOutput.appendLine(`Package directory: ${outputDirectory}`);
        await fs.mkdir(outputDirectory, { recursive: true });
        const buildResult = await buildWebTestPackage(projectPath, outputDirectory);
        if (buildResult.stdout) runOutput.append(buildResult.stdout);
        if (buildResult.stderr) runOutput.append(buildResult.stderr);

        const sourceEntries = await fs.readdir(webTestsDirectory, { withFileTypes: true });
        const yamlFiles = sourceEntries.filter(entry =>
          entry.isFile() && /\.webtest\.ya?ml$/i.test(entry.name)
        );
        if (!yamlFiles.some(file => file.name === selectedFileName)) {
          throw new Error(`The selected WebTest is not directly inside the project's webtests folder: ${selectedFile}`);
        }

        const destinationWebTests = path.dirname(packagedWebTestFile);
        await fs.mkdir(destinationWebTests, { recursive: true });
        for (const file of yamlFiles) {
          await fs.copyFile(
            path.join(webTestsDirectory, file.name),
            path.join(destinationWebTests, file.name)
          );
        }
        runOutput.appendLine(`WebTest package prepared (${yamlFiles.length} YAML files copied).`);

        // Avvia il processo Host e inizializza JSON-RPC.
        if (!hostManager.isRunning) await hostManager.start();
        if (!communicationClient.isInitialized) await communicationClient.initialize();

        // Il debugger è opzionale: un problema di attach non deve impedire il WebTest.
        try {
          await hostDebugger.attach();
          runOutput.appendLine(`C# debugger attached to DeoLoadTest Host (PID ${hostManager.processId}).`);
        } catch (debugError) {
          const debugMessage = debugError instanceof Error ? debugError.message : String(debugError);
          runOutput.appendLine(`C# debugger attach failed; continuing without debugger: ${debugMessage}`);
          void vscode.window.showWarningMessage(
            `WebTest will run without C# debugging: ${debugMessage}`
          );
        }

        const relativeWebTestFile = path.relative(outputDirectory, packagedWebTestFile);
        runOutput.appendLine(`Loading WebTest package: ${outputDirectory}`);
        const result = await communicationClient.startWebTest(outputDirectory, relativeWebTestFile, true);
        activeRunId = result.runId;
        activeWebTestRunId = result.runId;
        const testName = selection[0].fsPath.split(/[\\\\/]/).pop()?.replace(/\\.webtest\\.(yaml|yml)$/i, "") ?? selection[0].fsPath;
        webTestDebugSession.start(result.runId, new Date().toISOString(), testName);
        void vscode.commands.executeCommand("setContext", "deoloadtest.webTestRunning", true);
        requestExplorer.refresh();
        timingInspector.update(webTestDebugSession.requests, webTestDebugSession.status);
        verticalDebugPanel.show();
        runOutput.appendLine(`WebTest accepted. Run ID: ${result.runId}`);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        runOutput.appendLine(`Unable to start WebTest: ${message}`);
        void vscode.window.showErrorMessage(`Unable to start WebTest: ${message}`);
      }
    }
  );

  const stopWebTestCommand = vscode.commands.registerCommand(
    "deoloadtest.stopWebTest",
    async () => {
      const runId = activeWebTestRunId;
      if (!runId) {
        void vscode.window.showInformationMessage("There is no active WebTest to stop.");
        return;
      }

      try {
        const result = await communicationClient.cancelRun(runId);
        if (result.accepted) {
          runOutput.appendLine("Cancellation requested for WebTest " + runId + ".");
        }
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage("Unable to stop WebTest: " + message);
      }
    }
  );

  const clearRequestExplorerCommand = vscode.commands.registerCommand(
    "deoloadtest.clearRequestExplorer",
    async () => {
      if (webTestDebugSession.requests.length === 0) return;
      const answer = await vscode.window.showWarningMessage(
        "Clear all requests from the Request Explorer? The running WebTest will continue.",
        { modal: true },
        "Clear"
      );
      if (answer !== "Clear") return;
      webTestDebugSession.clearRequests();
      requestExplorer.refresh();
      debugPanel.update();
      verticalDebugPanel.update();
    }
  );

  const showDebugPanelCommand = vscode.commands.registerCommand(
    "deoloadtest.showDebugPanel",
    () => debugPanel.show()
  );

  const showVerticalDebugPanelCommand = vscode.commands.registerCommand(
    "deoloadtest.showVerticalDebugPanel",
    () => verticalDebugPanel.show()
  );

  const showTimingInspectorCommand = vscode.commands.registerCommand(
    "deoloadtest.showTimingInspector",
    () => timingInspector.show(webTestDebugSession.requests, webTestDebugSession.status)
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

  const compileWebTestCommand = vscode.commands.registerCommand(
    "deoloadtest.compileWebTest",
    async () => {
      const projects = await vscode.workspace.findFiles(
        "**/LoadTest.WebTestSample.csproj",
        "**/{bin,obj,node_modules}/**"
      );

      if (projects.length === 0) {
        void vscode.window.showErrorMessage(
          "LoadTest.WebTestSample.csproj was not found in the current workspace."
        );
        return;
      }

      let projectUri = projects[0];
      if (projects.length > 1) {
        const selected = await vscode.window.showQuickPick(
          projects.map(uri => ({
            label: path.basename(path.dirname(uri.fsPath)),
            description: vscode.workspace.asRelativePath(uri),
            uri
          })),
          { placeHolder: "Select the WebTest project to compile" }
        );
        if (!selected) return;
        projectUri = selected.uri;
      }

      const projectPath = projectUri.fsPath;
      const projectDirectory = path.dirname(projectPath);
      const outputDirectory = path.join(projectDirectory, "bin", "temp.pkg");
      const webTestsDirectory = path.join(projectDirectory, "webtests");

      runOutput.show(true);
      runOutput.appendLine("");
      runOutput.appendLine(`Compiling WebTest project: ${projectPath}`);
      runOutput.appendLine(`Destination: ${outputDirectory}`);

      try {
        await fs.mkdir(outputDirectory, { recursive: true });

        const buildResult = await buildWebTestPackage(projectPath, outputDirectory);

        if (buildResult.stdout) runOutput.append(buildResult.stdout);
        if (buildResult.stderr) runOutput.append(buildResult.stderr);

        let copiedYamlCount = 0;
        try {
          const entries = await fs.readdir(webTestsDirectory, { withFileTypes: true });
          const yamlFiles = entries.filter(entry =>
            entry.isFile() && /\\.webtest\\.ya?ml$/i.test(entry.name)
          );

          if (yamlFiles.length > 0) {
            const destinationWebTests = path.join(outputDirectory, "webtests");
            await fs.mkdir(destinationWebTests, { recursive: true });
            for (const file of yamlFiles) {
              await fs.copyFile(
                path.join(webTestsDirectory, file.name),
                path.join(destinationWebTests, file.name)
              );
              copiedYamlCount++;
            }
          }
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
          runOutput.appendLine("Webtests source directory not found; no YAML files were copied.");
        }

        runOutput.appendLine("");
        runOutput.appendLine("WebTest compilation completed successfully.");
        runOutput.appendLine(`Output directory: ${outputDirectory}`);
        runOutput.appendLine(`YAML files copied: ${copiedYamlCount}`);
        void vscode.window.showInformationMessage(
          `WebTest compiled successfully. Output: ${path.relative(projectDirectory, outputDirectory)}`
        );
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        runOutput.appendLine(`WebTest compilation failed: ${message}`);
        void vscode.window.showErrorMessage(`WebTest compilation failed: ${message}`);
      }
    }
  );

  const compileScriptCommand = vscode.commands.registerCommand(
    "deoloadtest.compileScript",
    async () => {
      const editor = vscode.window.activeTextEditor;
      if (!editor) {
        void vscode.window.showErrorMessage("Open a .csx file before compiling.");
        return;
      }

      const sourcePath = editor.document.uri.fsPath;
      if (!sourcePath.toLowerCase().endsWith(".csx")) {
        void vscode.window.showErrorMessage("The active file is not a .csx script.");
        return;
      }
      if (!hostManager.isRunning || !communicationClient.isInitialized) {
        void vscode.window.showErrorMessage(
          "DeoLoadTest Host is not started and initialized. Run 'DeoLoadTest: Start Host' first."
        );
        return;
      }

      try {
        if (!(await editor.document.save())) {
          throw new Error("Unable to save the active .csx file before compilation.");
        }
        runOutput.show(true);
        runOutput.appendLine(`Compiling C# script: ${sourcePath}`);
        const result = await communicationClient.compileScript(sourcePath);
        runOutput.appendLine(`Assembly: ${result.assemblyPath} (${result.assemblyBytes} bytes)`);
        runOutput.appendLine(`Portable PDB: ${result.pdbPath} (${result.pdbBytes} bytes)`);
        void vscode.window.showInformationMessage("C# script compiled successfully (DLL + Portable PDB).");
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        runOutput.appendLine(`Script compilation failed: ${message}`);
        void vscode.window.showErrorMessage(`Script compilation failed: ${message}`);
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
    compileScriptCommand,
    compileWebTestCommand,
    stopWebTestCommand,
    importHarCommand,
    stopHostCommand,
    notificationSubscription,
    requestExplorerView,
    requestSelectionSubscription,
    requestExplorer,
    requestInspector,
    timingInspector,
    debugPanel,
    verticalDebugPanel,
    showDebugPanelCommand,
    showVerticalDebugPanelCommand,
    showTimingInspectorCommand,
    clearRequestExplorerCommand,
    focusRequestExplorerCommand,
    communicationClient,
    hostDebugger,
    hostManager,
    runOutput
  );
}

export function deactivate(): void {
  // Resources are disposed through context.subscriptions.
}
