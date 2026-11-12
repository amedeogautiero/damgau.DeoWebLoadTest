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
      "progressNotifications":true,
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

Request:

```json
{"jsonrpc":"2.0","id":2,"method":"run.start","params":{"loadTestFile":"C:/tests/sample.loadtest.yaml","seed":12345}}
```

Response:

```json
{"jsonrpc":"2.0","id":2,"result":{"runId":"<run-id>"}}
```

Only one run can be active in this first version. A second start request returns error `-32001`.

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

This protocol exposes **load-test-level progress**, using the existing `RunProgressSnapshot` contract. It does not yet expose per-request HTTP events, request/response bodies, WebTest Debug Mode, HAR import, or language features. Those require separate engine/host APIs and are not implied by the current progress contract.
