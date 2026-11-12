import * as vscode from "vscode";

export function activate(context: vscode.ExtensionContext): void {
  const showWelcomeCommand = vscode.commands.registerCommand(
    "deoloadtest.showWelcome",
    () => {
      void vscode.window.showInformationMessage(
        "DeoLoadTest extension is active."
      );
    }
  );

  context.subscriptions.push(showWelcomeCommand);
}

export function deactivate(): void {
  // Host process lifecycle will be implemented in the next step.
}
