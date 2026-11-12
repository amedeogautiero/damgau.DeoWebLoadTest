import * as vscode from "vscode";
import * as path from "path";

interface HarHeader {
  name: string;
  value: string;
}

interface HarPostData {
  mimeType?: string;
  text?: string;
  params?: Array<{
    name: string;
    value?: string;
    fileName?: string;
    contentType?: string;
  }>;
}

interface HarRequest {
  method: string;
  url: string;
  headers?: HarHeader[];
  postData?: HarPostData;
}

interface HarEntry {
  request: HarRequest;
}

interface HarDocument {
  log?: {
    entries?: HarEntry[];
    pages?: Array<{ title?: string }>;
  };
}

interface WebTestStep {
  name: string;
  request: {
    method: string;
    url: string;
    headers?: Record<string, string>;
    body?: { raw: string } | { form: Record<string, string> } | {
      multipart: Array<{
        name: string;
        value?: string;
        file?: string;
        fileName?: string;
        contentType?: string;
      }>;
    };
  };
}

function yamlString(value: string): string {
  // JSON string literals are valid YAML double-quoted scalars and preserve
  // newlines, quotes, backslashes and other special characters.
  return JSON.stringify(value);
}

function getRequestHeaders(headers: HarHeader[] | undefined): Record<string, string> {
  const result: Record<string, string> = {};
  for (const header of headers ?? []) {
    if (!header || typeof header.name !== "string" || typeof header.value !== "string") {
      throw new Error("The HAR file contains an invalid request header.");
    }

    const existingName = Object.keys(result).find(
      key => key.toLowerCase() === header.name.toLowerCase()
    );

    if (existingName) {
      // WebTest request headers are represented as a string map. Preserve
      // repeated values rather than silently discarding subsequent entries.
      result[existingName] = `${result[existingName]}, ${header.value}`;
    } else {
      result[header.name] = header.value;
    }
  }
  return result;
}

function getRequestBody(postData: HarPostData | undefined): WebTestStep["request"]["body"] {
  if (!postData) return undefined;

  if (typeof postData.text === "string") {
    return { raw: postData.text };
  }

  if (postData.params?.length) {
    const isMultipart = (postData.mimeType ?? "").toLowerCase().includes("multipart/form-data");
    if (isMultipart) {
      return {
        multipart: postData.params.map(parameter => ({
          name: parameter.name,
          ...(parameter.value !== undefined ? { value: parameter.value } : {}),
          ...(parameter.fileName ? { fileName: parameter.fileName } : {}),
          ...(parameter.contentType ? { contentType: parameter.contentType } : {})
        }))
      };
    }

    const form: Record<string, string> = {};
    for (const parameter of postData.params) {
      form[parameter.name] = parameter.value ?? "";
    }
    return { form };
  }

  return undefined;
}

function toWebTestYaml(name: string, entries: HarEntry[]): string {
  const steps: WebTestStep[] = entries.map((entry, index) => {
    const request = entry?.request;
    if (!request || typeof request.method !== "string" || typeof request.url !== "string") {
      throw new Error(`HAR entry ${index + 1} does not contain a valid HTTP request (method and URL are required).`);
    }

    const headers = getRequestHeaders(request.headers);
    const body = getRequestBody(request.postData);

    return {
      name: `Request ${String(index + 1).padStart(3, "0")} - ${request.method.toUpperCase()}`,
      request: {
        method: request.method.toUpperCase(),
        url: request.url,
        ...(Object.keys(headers).length ? { headers } : {}),
        ...(body ? { body } : {})
      }
    };
  });

  const lines = [
    "version: 1",
    `name: ${yamlString(name)}`,
    "steps:"
  ];

  if (steps.length === 0) {
    lines.push("  []");
    return lines.join("\n") + "\n";
  }

  for (const step of steps) {
    lines.push(`  - name: ${yamlString(step.name)}`);
    lines.push("    request:");
    lines.push(`      method: ${yamlString(step.request.method)}`);
    lines.push(`      url: ${yamlString(step.request.url)}`);

    if (step.request.headers) {
      lines.push("      headers:");
      for (const [headerName, headerValue] of Object.entries(step.request.headers)) {
        lines.push(`        ${yamlString(headerName)}: ${yamlString(headerValue)}`);
      }
    }

    if (step.request.body) {
      if ("raw" in step.request.body) {
        lines.push("      body:");
        lines.push(`        raw: ${yamlString(step.request.body.raw)}`);
      } else if ("form" in step.request.body) {
        lines.push("      body:");
        lines.push("        form:");
        for (const [fieldName, fieldValue] of Object.entries(step.request.body.form)) {
          lines.push(`          ${yamlString(fieldName)}: ${yamlString(fieldValue)}`);
        }
      } else {
        lines.push("      body:");
        lines.push("        multipart:");
        for (const part of step.request.body.multipart) {
          lines.push(`          - name: ${yamlString(part.name)}`);
          if (part.value !== undefined) lines.push(`            value: ${yamlString(part.value)}`);
          if (part.fileName !== undefined) lines.push(`            fileName: ${yamlString(part.fileName)}`);
          if (part.contentType !== undefined) lines.push(`            contentType: ${yamlString(part.contentType)}`);
        }
      }
    }
  }

  return lines.join("\n") + "\n";
}

export async function importHarFile(): Promise<void> {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  if (!workspaceFolder) {
    throw new Error("Open a DeoLoadTest workspace folder before importing a HAR file.");
  }

  const harSelection = await vscode.window.showOpenDialog({
    canSelectMany: false,
    openLabel: "Import HAR",
    filters: { "HTTP Archive": ["har"] }
  });
  if (!harSelection?.length) return;

  const harUri = harSelection[0];
  let document: HarDocument;
  try {
    const bytes = await vscode.workspace.fs.readFile(harUri);
    document = JSON.parse(Buffer.from(bytes).toString("utf8")) as HarDocument;
  } catch (error) {
    throw new Error(`Unable to read or parse HAR file: ${error instanceof Error ? error.message : String(error)}`);
  }

  const entries = document?.log?.entries;
  if (!Array.isArray(entries)) {
    throw new Error("The selected file is not a valid HAR archive: log.entries is missing.");
  }

  const baseName = path.basename(harUri.fsPath, path.extname(harUri.fsPath));
  const outputUri = vscode.Uri.joinPath(workspaceFolder.uri, `${baseName}.webtest.yaml`);

  try {
    await vscode.workspace.fs.stat(outputUri);
    const choice = await vscode.window.showWarningMessage(
      `The file "${path.basename(outputUri.fsPath)}" already exists. Do you want to overwrite it?`,
      { modal: true },
      "Overwrite"
    );
    if (choice !== "Overwrite") return;
  } catch (error) {
    if (!(error instanceof vscode.FileSystemError) || error.code !== "FileNotFound") {
      throw error;
    }
  }

  const yaml = toWebTestYaml(baseName, entries);
  await vscode.workspace.fs.writeFile(outputUri, Buffer.from(yaml, "utf8"));

  const openDocument = await vscode.workspace.openTextDocument(outputUri);
  await vscode.window.showTextDocument(openDocument);
  void vscode.window.showInformationMessage(
    `HAR imported: ${entries.length} HTTP requests converted to WebTest and saved in the workspace.`
  );
}
