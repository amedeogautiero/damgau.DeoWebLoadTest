import * as vscode from "vscode";

const diagnosticCollection = vscode.languages.createDiagnosticCollection("deoloadtest");

function validateDocument(document: vscode.TextDocument): void {
  if (!document.fileName.endsWith(".webtest.yaml")) return;

  const diagnostics: vscode.Diagnostic[] = [];

  for (let lineNumber = 0; lineNumber < document.lineCount; lineNumber++) {
    const line = document.lineAt(lineNumber);
    const match = /^\s*version\s*:\s*(\d+\.\d+)\s*(?:#.*)?$/.exec(line.text);
    if (!match) continue;

    const value = match[1];
    const valueStart = line.text.indexOf(value);
    const range = new vscode.Range(
      lineNumber,
      valueStart,
      lineNumber,
      valueStart + value.length
    );

    const diagnostic = new vscode.Diagnostic(
      range,
      "The WebTest version must use integer notation (for example: version: 1). Decimal notation is not supported.",
      vscode.DiagnosticSeverity.Error
    );
    diagnostic.source = "DeoLoadTest";
    diagnostics.push(diagnostic);
  }

  diagnosticCollection.set(document.uri, diagnostics);
}

export function registerWebTestValidator(context: vscode.ExtensionContext): vscode.Disposable {
  const validateOpenDocuments = (): void => {
    for (const document of vscode.workspace.textDocuments) {
      validateDocument(document);
    }
  };

  const subscriptions = [
    vscode.workspace.onDidOpenTextDocument(validateDocument),
    vscode.workspace.onDidChangeTextDocument(event => validateDocument(event.document)),
    vscode.workspace.onDidSaveTextDocument(validateDocument)
  ];

  validateOpenDocuments();

  const disposable = vscode.Disposable.from(...subscriptions, diagnosticCollection);
  context.subscriptions.push(disposable);
  return disposable;
}
