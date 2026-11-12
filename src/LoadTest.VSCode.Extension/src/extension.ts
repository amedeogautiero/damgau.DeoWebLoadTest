import * as vscode from "vscode";
import { HostManager } from "./hostManager";

export function activate(context: vscode.ExtensionContext): void {
  const hostManager = new HostManager(context.extensionUri.fsPath);
  const showWelcomeCommand = vscode.commands.registerCommand(
    "deoloadtest.showWelcome",
    () => {
      void vscode.window.showInformationMessage(
        "DeoLoadTest extension is active."
      );
    }
  );

  const startHostCommand = vscode.commands.registerCommand(
    "deoloadtest.startHost",
    async () => {
      try {
        await hostManager.start();
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(
          `Unable to start DeoLoadTest Host: ${message}`
        );
      }
    }
  );

  const stopHostCommand = vscode.commands.registerCommand(
    "deoloadtest.stopHost",
    () => {
      hostManager.stop();
    }
  );

  context.subscriptions.push(
    showWelcomeCommand,
    startHostCommand,
    stopHostCommand,
    hostManager
  );
}

export function deactivate(): void {
  // The HostManager is disposed through context.subscriptions.
}
