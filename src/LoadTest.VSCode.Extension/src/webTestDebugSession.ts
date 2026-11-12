export type DebugSessionStatus = "idle" | "running" | "completed" | "failed" | "cancelled";
export type DebugRequestStatus = "pending" | "succeeded" | "failed";

export interface WebTestExecutionEvent {
  eventType?: string;
  timestampUtc?: string;
  requestName?: string;
  method?: string;
  url?: string;
  requestHeaders?: Record<string, string>;
  requestBody?: unknown;
  statusCode?: number;
  reasonPhrase?: string;
  responseHeaders?: Record<string, string[]>;
  responseBody?: string;
  elapsedMicros?: number;
  errorType?: string;
  errorMessage?: string;
  exceptionDetails?: string;
  requestDefinition?: unknown;
  contextSnapshot?: WebTestContextSnapshot;
  variableName?: string;
  variableValue?: string;
  success?: boolean;
  failures?: string[];
}

export interface WebTestContextSnapshot {
  variables?: Record<string, string>;
  dataRows?: Record<string, Record<string, string>>;
  predefined?: Record<string, unknown>;
  user?: Record<string, unknown>;
}

export interface DebugRequest {
  readonly id: number;
  readonly name?: string;
  readonly startedAt?: string;
  readonly completedAt?: string;
  readonly method?: string;
  readonly url?: string;
  readonly requestHeaders?: Record<string, string>;
  readonly requestBody?: unknown;
  readonly statusCode?: number;
  readonly reasonPhrase?: string;
  readonly responseHeaders?: Record<string, string[]>;
  readonly responseBody?: string;
  readonly elapsedMicros?: number;
  readonly errorType?: string;
  readonly errorMessage?: string;
  readonly exceptionDetails?: string;
  readonly requestDefinition?: unknown;
  readonly status: DebugRequestStatus;
}

export interface DebugSessionSummary {
  success?: boolean;
  failures: string[];
  elapsedMs?: number;
  requests: number;
}

export class WebTestDebugSession {
  private nextRequestId = 1;
  private readonly requestItems: DebugRequest[] = [];
  private currentStatus: DebugSessionStatus = "idle";
  private currentContext: WebTestContextSnapshot | undefined;
  private currentSummary: DebugSessionSummary | undefined;
  private currentRunId: string | undefined;
  private currentTestName: string | undefined;
  private currentStartedAt: string | undefined;
  private currentFinishedAt: string | undefined;

  get status(): DebugSessionStatus { return this.currentStatus; }
  get runId(): string | undefined { return this.currentRunId; }
  get testName(): string | undefined { return this.currentTestName; }
  get startedAt(): string | undefined { return this.currentStartedAt; }
  get finishedAt(): string | undefined { return this.currentFinishedAt; }
  get requests(): readonly DebugRequest[] { return this.requestItems; }
  get context(): WebTestContextSnapshot | undefined { return this.currentContext; }
  get summary(): DebugSessionSummary | undefined { return this.currentSummary; }

  start(runId: string, startedAt = new Date().toISOString(), testName?: string): void {
    this.nextRequestId = 1;
    this.requestItems.length = 0;
    this.currentStatus = "running";
    this.currentRunId = runId;
    this.currentTestName = testName;
    this.currentStartedAt = startedAt;
    this.currentFinishedAt = undefined;
    this.currentContext = undefined;
    this.currentSummary = undefined;
  }

  clearRequests(): void {
    this.requestItems.length = 0;
  }

  applyExecutionEvent(event: WebTestExecutionEvent): void {
    switch (event.eventType) {
      case "RequestStarted":
        this.startRequest(event);
        break;
      case "RequestCompleted":
        this.completeRequest(event, "succeeded");
        break;
      case "RequestFailed":
        this.completeRequest(event, "failed");
        break;
      case "ContextSnapshot":
        this.currentContext = event.contextSnapshot;
        break;
      case "VariablesUpdated":
        this.updateVariable(event.variableName, event.variableValue);
        break;
      case "TestCompleted":
        this.currentStatus = event.success ? "completed" : "failed";
        this.currentFinishedAt = event.timestampUtc ?? new Date().toISOString();
        this.currentSummary = {
          success: event.success,
          failures: [...(event.failures ?? [])],
          requests: this.requestItems.length
        };
        break;
    }
  }

  complete(result: { success?: boolean; failures?: string[]; elapsedMs?: number; requests?: number }): void {
    this.currentStatus = result.success ? "completed" : "failed";
    this.currentFinishedAt = new Date().toISOString();
    this.currentSummary = {
      success: result.success,
      failures: [...(result.failures ?? [])],
      elapsedMs: result.elapsedMs,
      requests: result.requests ?? this.requestItems.length
    };
  }

  cancel(): void {
    this.currentStatus = "cancelled";
    this.currentFinishedAt = new Date().toISOString();
  }

  fail(): void {
    this.currentStatus = "failed";
    this.currentFinishedAt = new Date().toISOString();
  }

  private startRequest(event: WebTestExecutionEvent): void {
    this.requestItems.push({
      id: this.nextRequestId++,
      name: event.requestName,
      startedAt: event.timestampUtc,
      method: event.method,
      url: event.url,
      requestHeaders: event.requestHeaders,
      requestBody: event.requestBody,
      status: "pending"
    });
  }

  private completeRequest(event: WebTestExecutionEvent, status: "succeeded" | "failed"): void {
    let index = -1;
    for (let i = this.requestItems.length - 1; i >= 0; i--) {
      const item = this.requestItems[i];
      if (item.status === "pending" && item.name === event.requestName) {
        index = i;
        break;
      }
    }

    if (index < 0) {
      this.startRequest(event);
      index = this.requestItems.length - 1;
    }

    const previous = this.requestItems[index];
    this.requestItems[index] = {
      ...previous,
      completedAt: event.timestampUtc,
      method: event.method ?? previous.method,
      url: event.url ?? previous.url,
      requestHeaders: event.requestHeaders ?? previous.requestHeaders,
      requestBody: event.requestBody ?? previous.requestBody,
      statusCode: event.statusCode,
      reasonPhrase: event.reasonPhrase,
      responseHeaders: event.responseHeaders,
      responseBody: event.responseBody,
      elapsedMicros: event.elapsedMicros,
      errorType: event.errorType,
      errorMessage: event.errorMessage,
      exceptionDetails: event.exceptionDetails,
      requestDefinition: event.requestDefinition,
      status
    };
  }

  private updateVariable(name?: string, value?: string): void {
    if (!name) return;
    const variables = { ...(this.currentContext?.variables ?? {}), [name]: value ?? "" };
    this.currentContext = { ...(this.currentContext ?? {}), variables };
  }
}
