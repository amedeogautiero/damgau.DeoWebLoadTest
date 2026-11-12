import * as vscode from "vscode";
import * as path from "path";
import { stringify } from "yaml";

interface HarHeader {
  name: string;
  value: string;
}

interface HarPostData {
  mimeType?: string;
  text?: string;
  encoding?: string;
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
    body?: { raw: string } | { file: string } | { form: Record<string, string> } | {
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

interface BinaryAsset {
  relativePath: string;
  content: Buffer;
}

interface ConvertedWebTest {
  yaml: string;
  assets: BinaryAsset[];
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

function isTextMimeType(mimeType: string): boolean {
  const mime = mimeType.split(";", 1)[0].trim().toLowerCase();
  return mime.startsWith("text/") ||
    mime === "application/json" ||
    mime === "application/xml" ||
    mime === "application/javascript" ||
    mime === "application/x-www-form-urlencoded" ||
    mime === "application/graphql" ||
    mime.endsWith("+json") ||
    mime.endsWith("+xml");
}

function containsBinaryControlCharacters(value: string): boolean {
  // Keep the ordinary text controls used in textual payloads (TAB, LF, CR).
  return /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/.test(value);
}

function getBinaryBytes(postData: HarPostData): Buffer {
  if ((postData.encoding ?? "").toLowerCase() === "base64") {
    return Buffer.from(postData.text ?? "", "base64");
  }

  const text = postData.text ?? "";
  // HAR exporters commonly expose binary request bodies as a byte-preserving
  // Latin-1 string. If it contains non-Latin-1 characters, retain its UTF-8
  // representation rather than silently truncating those characters.
  const isByteString = Array.from(text).every(character => character.codePointAt(0)! <= 0xff);
  return Buffer.from(text, isByteString ? "latin1" : "utf8");
}

function getRequestBody(
  postData: HarPostData | undefined,
  requestNumber: number,
  assetsDirectoryName: string
): { body: WebTestStep["request"]["body"]; asset?: BinaryAsset } {
  if (!postData) return { body: undefined };

  if (typeof postData.text === "string") {
    const mimeType = postData.mimeType ?? "";
    const isBinary = (postData.encoding ?? "").toLowerCase() === "base64" ||
      (mimeType.length > 0 && !isTextMimeType(mimeType)) ||
      containsBinaryControlCharacters(postData.text);

    if (isBinary) {
      const fileName = `request-${String(requestNumber).padStart(3, "0")}.body.bin`;
      const relativePath = path.posix.join(assetsDirectoryName, fileName);
      return {
        body: { file: relativePath },
        asset: { relativePath, content: getBinaryBytes(postData) }
      };
    }

    return { body: { raw: postData.text } };
  }

  if (postData.params?.length) {
    const isMultipart = (postData.mimeType ?? "").toLowerCase().includes("multipart/form-data");
    if (isMultipart) {
      return {
        body: {
          multipart: postData.params.map(parameter => ({
            name: parameter.name,
            ...(parameter.value !== undefined ? { value: parameter.value } : {}),
            ...(parameter.fileName ? { fileName: parameter.fileName } : {}),
            ...(parameter.contentType ? { contentType: parameter.contentType } : {})
          }))
        }
      };
    }

    const form: Record<string, string> = {};
    for (const parameter of postData.params) {
      form[parameter.name] = parameter.value ?? "";
    }
    return { body: { form } };
  }

  return { body: undefined };
}

function toWebTestYaml(
  name: string,
  entries: HarEntry[],
  assetsDirectoryName: string
): ConvertedWebTest {
  const assets: BinaryAsset[] = [];
  const steps: WebTestStep[] = entries.map((entry, index) => {
    const request = entry?.request;
    if (!request || typeof request.method !== "string" || typeof request.url !== "string") {
      throw new Error(`HAR entry ${index + 1} does not contain a valid HTTP request (method and URL are required).`);
    }

    const headers = getRequestHeaders(request.headers);
    const convertedBody = getRequestBody(request.postData, index + 1, assetsDirectoryName);
    if (convertedBody.asset) assets.push(convertedBody.asset);

    return {
      name: `Request ${String(index + 1).padStart(3, "0")} - ${request.method.toUpperCase()}`,
      request: {
        method: request.method.toUpperCase(),
        url: request.url,
        ...(Object.keys(headers).length ? { headers } : {}),
        ...(convertedBody.body ? { body: convertedBody.body } : {})
      }
    };
  });

  // Use a YAML serializer instead of hand-written quoting. This safely escapes
  // quotes, newlines and control characters in URLs, headers and text bodies.
  const yaml = stringify({
    version: 1,
    name,
    steps: steps.length ? steps : []
  }, { lineWidth: 0 });

  return { yaml, assets };
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
  const assetsDirectoryName = `${baseName}.webtest.assets`;
  const assetsDirectoryUri = vscode.Uri.joinPath(workspaceFolder.uri, assetsDirectoryName);

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

  const converted = toWebTestYaml(baseName, entries, assetsDirectoryName);

  // Binary bodies are stored as sidecar files. WebTest's existing body.file
  // support resolves these paths relative to the generated YAML file.
  if (converted.assets.length > 0) {
    try {
      await vscode.workspace.fs.stat(assetsDirectoryUri);
      await vscode.workspace.fs.delete(assetsDirectoryUri, { recursive: true, useTrash: false });
    } catch (error) {
      if (!(error instanceof vscode.FileSystemError) || error.code !== "FileNotFound") {
        throw error;
      }
    }

    await vscode.workspace.fs.createDirectory(assetsDirectoryUri);
    for (const asset of converted.assets) {
      const assetUri = vscode.Uri.joinPath(assetsDirectoryUri, path.posix.basename(asset.relativePath));
      await vscode.workspace.fs.writeFile(assetUri, asset.content);
    }
  }

  await vscode.workspace.fs.writeFile(outputUri, Buffer.from(converted.yaml, "utf8"));

  const openDocument = await vscode.workspace.openTextDocument(outputUri);
  await vscode.window.showTextDocument(openDocument);
  void vscode.window.showInformationMessage(
    `HAR imported: ${entries.length} HTTP requests converted to WebTest and saved in the workspace (${converted.assets.length} binary body files).`
  );
}
