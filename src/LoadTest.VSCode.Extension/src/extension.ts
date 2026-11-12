import * as vscode from "vscode";
import { CommunicationClient } from "./communicationClient";
import { HostManager } from "./hostManager";

export function activate(context: vscode.ExtensionContext): void {
  const hostManager = new HostManager(context.extensionUri.fsPath);
  const communicationClient = new CommunicationClient(hostManager);

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

  const stopHostCommand = vscode.commands.registerCommand(
    "deoloadtest.stopHost",
    () => hostManager.stop()
  );

  context.subscriptions.push(
    showWelcomeCommand,
    startHostCommand,
    stopHostCommand,
    communicationClient,
    hostManager
  );
}

export function deactivate(): void {
  // Resources are disposed through context.subscriptions.
}
