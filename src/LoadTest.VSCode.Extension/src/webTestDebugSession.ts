export type DebugSessionStatus = "idle" | "running" | "paused" | "completed" | "failed" | "cancelled";
export type DebugRequestStatus = "pending" | "paused" | "succeeded" | "failed";
export type DebugStepType = "Request" | "Code" | "Extractor" | "Validator";
export interface DebugStep {
  readonly id: number;
  readonly type: DebugStepType;
  readonly name: string;
  readonly status: "pending" | "succeeded" | "failed";
  readonly startedAt?: string;
  readonly completedAt?: string;
  readonly details?: unknown;
  readonly requestId?: number;
  readonly engineRequestId?: string;
  readonly method?: string;
  readonly url?: string;
}

export interface WebTestExecutionEvent {
  eventType?: string;
  timestampUtc?: string;
  requestName?: string;
  requestId?: string;
  stepType?: DebugStepType;
  stepName?: string;
  stepStatus?: "Started" | "Succeeded" | "Failed";
  stepDetails?: unknown;
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
  readonly engineRequestId?: string;
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
  readonly contextAtStart?: WebTestContextSnapshot;
  readonly contextAtEnd?: WebTestContextSnapshot;
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
  private readonly stepItems: DebugStep[] = [];
  private nextStepId = 1;
  private currentStatus: DebugSessionStatus = "idle";
  private currentContext: WebTestContextSnapshot | undefined;
  private currentSummary: DebugSessionSummary | undefined;
  private currentRunId: string | undefined;
  private currentTestName: string | undefined;
  private currentStartedAt: string | undefined;
  private currentFinishedAt: string | undefined;
  private totalRequestCount = 0;
  private successfulRequestCount = 0;
  private failedRequestCount = 0;
  private readonly requestDurationValues: number[] = [];

  get status(): DebugSessionStatus { return this.currentStatus; }
  get runId(): string | undefined { return this.currentRunId; }
  get testName(): string | undefined { return this.currentTestName; }
  get startedAt(): string | undefined { return this.currentStartedAt; }
  get finishedAt(): string | undefined { return this.currentFinishedAt; }
  get totalRequests(): number { return this.totalRequestCount; }
  get successfulRequests(): number { return this.successfulRequestCount; }
  get failedRequests(): number { return this.failedRequestCount; }
  get requestDurationsMs(): readonly number[] { return this.requestDurationValues; }
  get requests(): readonly DebugRequest[] { return this.requestItems; }
  get steps(): readonly DebugStep[] { return this.stepItems; }
  get context(): WebTestContextSnapshot | undefined { return this.currentContext; }
  get summary(): DebugSessionSummary | undefined { return this.currentSummary; }

  start(runId: string, startedAt = new Date().toISOString(), testName?: string): void {
    this.nextRequestId = 1;
    this.nextStepId = 1;
    this.requestItems.length = 0;
    this.stepItems.length = 0;
    this.currentStatus = "running";
    this.currentRunId = runId;
    this.currentTestName = testName;
    this.currentStartedAt = startedAt;
    this.currentFinishedAt = undefined;
    this.currentContext = undefined;
    this.currentSummary = undefined;
    this.totalRequestCount = 0;
    this.successfulRequestCount = 0;
    this.failedRequestCount = 0;
    this.requestDurationValues.length = 0;
  }

  clearRequests(): void {
    this.requestItems.length = 0;
    this.stepItems.length = 0;
  }

  prepare(testName?: string): void {
    this.nextRequestId = 1;
    this.nextStepId = 1;
    this.requestItems.length = 0;
    this.stepItems.length = 0;
    this.currentStatus = "idle";
    this.currentRunId = undefined;
    this.currentTestName = testName;
    this.currentStartedAt = undefined;
    this.currentFinishedAt = undefined;
    this.currentContext = undefined;
    this.currentSummary = undefined;
    this.totalRequestCount = 0;
    this.successfulRequestCount = 0;
    this.failedRequestCount = 0;
    this.requestDurationValues.length = 0;
  }

  applyExecutionEvent(event: WebTestExecutionEvent): void {
    if (event.stepType && event.stepName) this.applyStepEvent(event);
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
      case "BreakpointReached":
        this.currentStatus = "paused";
        this.pauseRequest(event.requestId);
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

  resume(): void {
    if (this.currentStatus !== "paused") return;
    this.currentStatus = "running";
    for (let index = 0; index < this.requestItems.length; index++) {
      const request = this.requestItems[index];
      if (request.status === "paused") {
        this.requestItems[index] = { ...request, status: "pending" };
      }
    }
  }

  private pauseRequest(engineRequestId?: string): void {
    if (!engineRequestId) return;
    const index = this.requestItems.findIndex(request =>
      request.engineRequestId === engineRequestId && request.status === "pending"
    );
    if (index < 0) return;
    this.requestItems[index] = { ...this.requestItems[index], status: "paused" };
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
    this.totalRequestCount++;
    this.requestItems.push({
      id: this.nextRequestId++,
      name: event.requestName,
      engineRequestId: event.requestId,
      startedAt: event.timestampUtc,
      method: event.method,
      url: event.url,
      requestHeaders: event.requestHeaders,
      requestBody: event.requestBody,
      contextAtStart: event.contextSnapshot,
      status: "pending"
    });
    const request = this.requestItems[this.requestItems.length - 1];
    this.addStep("Request", event.requestName ?? "HTTP request", "pending", event.timestampUtc, undefined, event, request.id);
  }

  private applyStepEvent(event: WebTestExecutionEvent): void {
    const type = event.stepType!;
    const name = event.stepName ?? event.requestName ?? type;
    const parentRequest = event.requestId
      ? this.requestItems.find(request => request.engineRequestId === event.requestId)
      : undefined;
    const localRequestId = parentRequest?.id;
    if (type === "Request") {
      const index = this.findPendingStep("Request", name, event.requestId);
      if (index >= 0) this.updateStep(index, event);
      return;
    }
    if (event.stepStatus === "Started") {
      this.addStep(type, name, "pending", event.timestampUtc, undefined, event, localRequestId);
      return;
    }
    const index = this.findPendingStep(type, name, event.requestId, localRequestId);
    if (index >= 0) this.updateStep(index, event);
    else this.addStep(type, name, event.stepStatus === "Failed" ? "failed" : "succeeded", event.timestampUtc, event.timestampUtc, event, localRequestId);
  }

  private addStep(type: DebugStepType, name: string, status: DebugStep["status"], startedAt?: string, completedAt?: string, event?: WebTestExecutionEvent, requestId?: number): void {
    this.stepItems.push({ id: this.nextStepId++, type, name, status, startedAt, completedAt, details: event?.stepDetails, requestId, engineRequestId: event?.requestId, method: event?.method, url: event?.url });
  }

  private findPendingStep(type: DebugStepType, name: string, engineRequestId?: string, requestId?: number): number {
    for (let i = this.stepItems.length - 1; i >= 0; i--) {
      const step = this.stepItems[i];
      if (step.type !== type || step.name !== name || step.status !== "pending") continue;
      if (engineRequestId && step.engineRequestId !== engineRequestId) continue;
      if (requestId !== undefined && step.requestId !== requestId) continue;
      return i;
    }
    return -1;
  }

  private updateStep(index: number, event: WebTestExecutionEvent): void {
    const previous = this.stepItems[index];
    const status = event.stepStatus === "Failed" ? "failed" : event.stepStatus === "Succeeded" ? "succeeded" : previous.status;
    this.stepItems[index] = { ...previous, status, completedAt: event.stepStatus === "Started" ? previous.completedAt : event.timestampUtc, details: event.stepDetails ?? previous.details, method: event.method ?? previous.method, url: event.url ?? previous.url };
  }

  private completeRequest(event: WebTestExecutionEvent, status: "succeeded" | "failed"): void {
    let index = -1;
    for (let i = this.requestItems.length - 1; i >= 0; i--) {
      const item = this.requestItems[i];
      if (item.status === "pending" && (event.requestId ? item.engineRequestId === event.requestId : item.name === event.requestName)) {
        index = i;
        break;
      }
    }

    if (index < 0) {
      this.startRequest(event);
      index = this.requestItems.length - 1;
    }

    const previous = this.requestItems[index];
    if (status === "succeeded") this.successfulRequestCount++;
    else this.failedRequestCount++;
    if (typeof event.elapsedMicros === "number") this.requestDurationValues.push(event.elapsedMicros / 1000);
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
      contextAtStart: previous.contextAtStart ?? event.contextSnapshot,
      contextAtEnd: event.contextSnapshot,
      status
    };
  }

  private updateVariable(name?: string, value?: string): void {
    if (!name) return;
    const variables = { ...(this.currentContext?.variables ?? {}), [name]: value ?? "" };
    this.currentContext = { ...(this.currentContext ?? {}), variables };
  }
}
