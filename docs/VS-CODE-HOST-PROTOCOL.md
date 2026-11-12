# VS Code Host protocol

The first host protocol is JSON-RPC 2.0 over **newline-delimited JSON (NDJSON)**:
- one complete JSON-RPC message per line on stdin;
- one complete JSON-RPC message per line on stdout;
- stdout is reserved for protocol traffic; diagnostics are written to stderr;
- the host currently accepts request messages (notifications from the client are ignored).

This is a small host protocol, not the Language Server Protocol. It does not use LSP's Content-Length framing.

## Requests

### initialize

Request:

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
```

Response:

```json
{
  "jsonrpc":"2.0",
  "id":1,
  "result":{
    "protocolVersion":"1.0",
    "serverName":"LoadTest.VSCode.Host",
    "capabilities":{
      "localLoadTest":true,
      "standaloneWebTest":true,
      "progressNotifications":true,
      "executionEvents":true,
      "cancellation":true,
      "concurrentRuns":false
    }
  }
}
```

### run.start

Starts one local load test through `LocalLoadTestExecutor`. The response is an acknowledgement; progress and terminal state arrive as notifications.

Parameters:
- `loadTestFile` (required): path to the `.loadtest.yaml` file.
- `resultsDatabasePath` (optional): SQLite results database; defaults to `loadtest-results.db` in the host working directory.
- `runName` (optional): display name for the run.
- `seed` (optional): deterministic execution seed.
- `debugMode` (optional, default `false`): enables per-request execution event notifications and request/response payloads.

Request:

```json
{"jsonrpc":"2.0","id":2,"method":"run.start","params":{"loadTestFile":"C:/tests/sample.loadtest.yaml","seed":12345,"debugMode":true}}
```

Response:

```json
{"jsonrpc":"2.0","id":2,"result":{"runId":"<run-id>"}}
```

Only one local execution can be active in this first version. A second start request returns error `-32001`.

### webtest.run

Runs one `.webtest.yaml` directly through the shared `WebTestRunner`, without creating a temporary `.loadtest.yaml` file. This is a single-user, single-iteration execution intended for functional verification of a WebTest, not load generation.

Parameters:
- `webTestFile` (required): path to the `.webtest.yaml` file.
- `debugMode` (optional, default `false`): forwards structured request and test events as `webtest.event` notifications.

Request:

```json
{"jsonrpc":"2.0","id":4,"method":"webtest.run","params":{"webTestFile":"C:/tests/sample.webtest.yaml","debugMode":true}}
```

Response:

```json
{"jsonrpc":"2.0","id":4,"result":{"runId":"<run-id>"}}
```

The execution uses the WebTest's own defaults, context, steps and relative file paths. It uses one HTTP session and stops at the first failed step, following the normal `WebTestRunner` behavior.

Notifications:
- `webtest.event`: structured engine event, emitted only when `debugMode` is true.
- `webtest.completed`: includes `success`, `failures`, `elapsedMs` and the number of started HTTP requests. A completed WebTest may have `success: false` when a request or validation fails.
- `webtest.failed`: emitted when the Host cannot load or start the WebTest.
- `webtest.cancelled`: emitted when cancellation is requested and observed.

The same `run.cancel` method cancels either a load test or a standalone WebTest. Only one local execution of either kind may be active at a time.

### run.cancel

Requests cancellation of the active run. The `runId` must match the identifier returned by `run.start`.

```json
{"jsonrpc":"2.0","id":3,"method":"run.cancel","params":{"runId":"<run-id>"}}
```

Response:

```json
{"jsonrpc":"2.0","id":3,"result":{"accepted":true,"runId":"<run-id>"}}
```

## Server notifications

Every notification is a JSON-RPC message without an `id`.

### run.progress

Emitted whenever the execution service publishes a `RunProgressSnapshot`:

```json
{"jsonrpc":"2.0","method":"run.progress","params":{"runId":"<run-id>","snapshot":{}}}
```

The snapshot contains status, sequence, elapsed time, active users, completed/failed tests and requests, throughput, and aggregate series. Its property names are camelCase and enum values are camelCase strings.

### Debug execution notifications

These notifications are sent only when `run.start` specifies `debugMode: true`. The host forwards structured events raised by the shared engine. Each notification has the common envelope `{ "runId": "...", "event": { ... } }`. Event properties are camelCase.

- `debug.requestStarted`: emitted after request plugins and variable binding, immediately before the HTTP send. Includes scenario, test, virtual user, iteration, request name, method, final URL, headers, and request body.
- `debug.requestCompleted`: emitted after a successful HTTP request and its validations/extractions. Includes request and response metadata, response headers/body, status, and elapsed microseconds.
- `debug.requestFailed`: same context as `debug.requestCompleted`, plus error type/message. A failed request emits this event instead of `debug.requestCompleted`.
- `debug.variablesUpdated`: emitted for each successfully extracted variable, with its name and value.
- `debug.testCompleted`: emitted when a WebTest iteration ends, with success, elapsed microseconds, and failure details.

Events are generated by `WebTestRunner` in the engine and forwarded by the Host; they are not reconstructed from console output. They can arrive concurrently when multiple virtual users execute tests. Consumers should use `runId`, `userId`, `iteration`, and timestamps to correlate events. Request and response bodies can be large and may contain sensitive data; clients should avoid retaining or displaying them unless Debug Mode is enabled.

### run.completed

Emitted when the executor returns a result. The payload contains:
- `run`: persisted run metadata;
- `summary`: aggregated series summary;
- `csv`: summary CSV text.

```json
{"jsonrpc":"2.0","method":"run.completed","params":{"runId":"<run-id>","result":{"run":{},"summary":{},"csv":"..."}}}
```

A completed notification can represent a normally completed or engine-cancelled run; inspect `run.status`.

### run.failed

Emitted if execution throws:

```json
{"jsonrpc":"2.0","method":"run.failed","params":{"runId":"<run-id>","error":{"code":"execution_failed","message":"...","details":"..."}}}
```

### run.cancelled

Emitted only if cancellation escapes the execution service as an `OperationCanceledException`:

```json
{"jsonrpc":"2.0","method":"run.cancelled","params":{"runId":"<run-id>"}}
```

## JSON-RPC errors

Standard errors:
- `-32700`: parse error;
- `-32600`: invalid request;
- `-32601`: method not found;
- `-32602`: invalid params;
- `-32603`: internal error.

Host-specific errors:
- `-32001`: another local run is already active;
- `-32002`: no active run exists for the supplied run id.

## Scope of this version

This protocol exposes load-test-level progress and structured per-request WebTest execution events. The events provide the engine data needed by a VS Code Debug Mode client. HAR import and language features remain outside this protocol version.
