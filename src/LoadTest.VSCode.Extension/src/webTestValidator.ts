import * as vscode from "vscode";
import Ajv, { ErrorObject } from "ajv";
import { parseDocument, isMap, isSeq, isScalar, Node } from "yaml";
import * as fs from "node:fs";
import * as path from "node:path";

const diagnosticCollection = vscode.languages.createDiagnosticCollection("deoloadtest");
const ajv = new Ajv({ allErrors: true, strict: false });
const schemaPath = path.join(__dirname, "..", "schemas", "webtest.schema.json");
const schema = JSON.parse(fs.readFileSync(schemaPath, "utf8")) as object;
const validateSchema = ajv.compile(schema);

function nodeAtPath(root: Node | null, segments: (string | number)[]): Node | null {
  let current: Node | null = root;
  for (const segment of segments) {
    if (isMap(current)) {
      const pair = current.items.find(item =>
        isScalar(item.key) && item.key.value === segment
      );
      current = pair?.value ?? null;
    } else if (isSeq(current) && typeof segment === "number") {
      current = current.items[segment] ?? null;
    } else {
      return null;
    }
  }
  return current;
}

function errorPath(error: ErrorObject): (string | number)[] {
  const segments = error.instancePath
    .split("/")
    .filter(Boolean)
    .map(segment => segment.replace(/~1/g, "/").replace(/~0/g, "~"))
    .map(segment => /^\d+$/.test(segment) ? Number(segment) : segment);

  if (error.keyword === "additionalProperties" &&
      typeof error.params.additionalProperty === "string") {
    segments.push(error.params.additionalProperty);
  }
  return segments;
}

function rangeForNode(document: vscode.TextDocument, node: Node | null): vscode.Range {
  if (!node || !node.range) return new vscode.Range(0, 0, 0, 1);
  const start = document.positionAt(node.range[0]);
  const end = document.positionAt(Math.max(node.range[0] + 1, node.range[1]));
  return new vscode.Range(start, end);
}

function rangeForYamlError(document: vscode.TextDocument, error: { pos: [number, number] }): vscode.Range {
  const start = document.positionAt(error.pos[0]);
  const end = document.positionAt(Math.max(error.pos[0] + 1, error.pos[1]));
  return new vscode.Range(start, end);
}

function validateDocument(document: vscode.TextDocument): void {
  if (!document.fileName.endsWith(".webtest.yaml") &&
      !document.fileName.endsWith(".webtest.yml")) return;

  const diagnostics: vscode.Diagnostic[] = [];
  const yamlDocument = parseDocument(document.getText(), { uniqueKeys: true });

  for (const error of yamlDocument.errors) {
    const diagnostic = new vscode.Diagnostic(
      rangeForYamlError(document, error),
      error.message,
      vscode.DiagnosticSeverity.Error
    );
    diagnostic.source = "DeoLoadTest";
    diagnostics.push(diagnostic);
  }

  if (yamlDocument.errors.length === 0) {
    const value = yamlDocument.toJS();
    if (!validateSchema(value)) {
      for (const error of validateSchema.errors ?? []) {
        const node = nodeAtPath(yamlDocument.contents, errorPath(error));
        const diagnostic = new vscode.Diagnostic(
          rangeForNode(document, node),
          formatSchemaError(error),
          vscode.DiagnosticSeverity.Error
        );
        diagnostic.source = "DeoLoadTest";
        diagnostics.push(diagnostic);
      }
    }
  }

  // Preserve the exact source-notation rule: YAML parses 1.0 as JS number 1.
  for (let lineNumber = 0; lineNumber < document.lineCount; lineNumber++) {
    const line = document.lineAt(lineNumber);
    const match = /^\s*version\s*:\s*(\d+\.\d+)\s*(?:#.*)?$/.exec(line.text);
    if (!match) continue;
    const valueStart = line.text.indexOf(match[1]);
    const diagnostic = new vscode.Diagnostic(
      new vscode.Range(lineNumber, valueStart, lineNumber, valueStart + match[1].length),
      "The WebTest version must use integer notation (for example: version: 1). Decimal notation is not supported.",
      vscode.DiagnosticSeverity.Error
    );
    diagnostic.source = "DeoLoadTest";
    diagnostics.push(diagnostic);
  }

  diagnosticCollection.set(document.uri, diagnostics);
}

function formatSchemaError(error: ErrorObject): string {
  switch (error.keyword) {
    case "additionalProperties":
      return `Unknown property '${String(error.params.additionalProperty)}'.`;
    case "required":
      return `Missing required property '${String(error.params.missingProperty)}'.`;
    case "type":
      return `Expected ${String(error.params.type)}.`;
    case "enum":
      return "Value is not one of the permitted values.";
    case "minimum":
      return `Value must be greater than or equal to ${String(error.params.limit)}.`;
    case "minLength":
      return `Value must contain at least ${String(error.params.limit)} characters.`;
    default:
      return error.message ?? "Invalid WebTest value.";
  }
}

export function registerWebTestValidator(context: vscode.ExtensionContext): vscode.Disposable {
  const validateOpenDocuments = (): void => {
    for (const document of vscode.workspace.textDocuments) validateDocument(document);
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
