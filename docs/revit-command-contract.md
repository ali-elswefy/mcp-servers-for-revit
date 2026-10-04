# Revit add-in command contract

This is the contract between the Revit add-in (JSON-RPC over TCP, port 8080) and MCP clients:
the Node server in `server/` and the Python service (`AI_RevitMCP/app/revit_mcp/service/tool_manifest.json`).
Keep client schemas in sync with this file.

## Error envelope

Every JSON-RPC error carries `error.data.kind`. Clients should branch on `kind` (and `outcome`) and
return a deterministic message, without asking the model to interpret a known failure.

| `kind` | Produced by | Meaning | Safe to retry? |
| --- | --- | --- | --- |
| `connection` | client | The add-in could not be reached, or the socket dropped mid-request | Yes if nothing was sent (`executed: false`); otherwise treat as `outcome_unknown` |
| `timeout` | add-in or client | See `outcome` below | See `retrySafe` |
| `validation` | add-in | Bad parameters, e.g. an unknown category name (`details.invalidCategories`) | After fixing the input |
| `revit_state` | add-in | No active document (`reason: no_active_document`) or view (`no_active_view`) | After the user fixes Revit's state |
| `revit_unavailable` | add-in | Revit refused to queue the external event; not executed | Yes |
| `execution` | add-in | The command threw inside Revit | Depends on the command |
| `invalid_request`, `method_not_found`, `internal` | add-in | Protocol or unexpected errors | No |

### Timeouts

The add-in waits a bounded time for the Revit UI thread. When the wait expires:

| `outcome` | Meaning | `retrySafe` |
| --- | --- | --- |
| `cancelled_before_start` | Revit had not started the request. It was cancelled atomically and will never run. | `true` |
| `outcome_unknown` | The request was already running. It may still complete and commit changes. | `false` for commands that can change the model (`modelMayHaveChanged: true`); `true` for read-only commands |

Timeout errors also include `command`, `requestId`, `invocationId`, `timeoutMs`, `elapsedMs` and `started`.
The add-in logs a late completion of an `outcome_unknown` request against the same `requestId`
(add-in log, see [logging.md](logging.md)).

**Clients must never retry or replay a request automatically after `outcome_unknown` or a client-side
timeout.** Tell the user the model may have changed and should be inspected first.

### Timeout alignment

Defined in `commandset/Utils/ExternalEvents/CommandTimeouts.cs`:

| Command | Default wait |
| --- | --- |
| `send_code_to_revit` | 120 s |
| `analyze_model_statistics` | 120 s |
| `get_current_view_elements` | 60 s |
| `get_current_view_info` | 10 s |

Every wait is capped at 135 s. A client's outer timeout must be at least 15 s longer than the add-in's
wait so the structured add-in timeout arrives first (Node server: 150 s; Python service: 150 s).
Waits can be overridden per request with a `timeoutMs` parameter, or per command with the environment
variable `REVIT_MCP_TIMEOUT_MS_<COMMAND_NAME>` (e.g. `REVIT_MCP_TIMEOUT_MS_SEND_CODE_TO_REVIT`); both are
clamped to 1 s to 135 s. A client with a shorter budget should pass `timeoutMs` = its budget minus 15 s.

## `send_code_to_revit`

Success responses keep their shape: `{ success, result, errorMessage }`, plus `requestId`, and
`errorType` when `success` is `false`:

- `compilation`: the snippet did not compile; nothing ran.
- `runtime`: the snippet threw. With `transactionMode: "auto"` the transaction was rolled back.
- `transaction`: the snippet ran but the automatic transaction did not commit.

Timeouts are JSON-RPC errors (`kind: "timeout"`), never `success: false`.
`transactionMode` must be `auto` or `none`; anything else is a `validation` error.

## `analyze_model_statistics`

| Parameter | Default | Notes |
| --- | --- | --- |
| `categories` | omitted | Omitted or `null`: full-model statistics, with the same response as before. Non-empty array: count only these categories. `[]` is a `validation` error. Names may be `OST_Walls` or display names like `Walls`; unknown names are a `validation` error. |
| `includeDetailedTypes` | `true` (full), `false` (categories) | Family/type breakdown |
| `includeLevels` | `true` (full), `false` (categories) | Per-level counts |

Category response:

```json
{
  "success": true,
  "mode": "categories",
  "projectName": "...",
  "categories": [
    { "requestedName": "Walls", "categoryName": "Walls", "builtInCategory": "OST_Walls", "elementCount": 42 }
  ],
  "message": "Walls: 42"
}
```

`typeCount`, `familyCount`, `types` and `levels` appear on a category only when requested.

## `get_current_view_elements`

- Both `modelCategoryList` and `annotationCategoryList` omitted: default categories (title blocks excluded).
- Either list provided: only the provided lists are used; an omitted list adds nothing.
- Every provided list empty: no category filter (all categories in the view).
- Unknown category names: `validation` error.
- The response adds `Truncated` (more matches than `limit`) and `CategoryFilter` (applied categories, or `null`).

## `get_current_view_info`

No active document or view returns a `revit_state` error. The add-in never opens a dialog for MCP requests.

## `health_check` (built into the plugin)

Handled by the socket service itself, so it works even if no command set loaded. It never queries the model.

Parameters: `probeUiThread` (default `true`), `uiProbeTimeoutMs` (default 2000, range 100 to 30000).

```json
{
  "plugin": {
    "reachable": true, "socketRunning": true, "port": 8080, "startedUtc": "...",
    "registeredCommandCount": 30, "lastIdlingUtc": "...", "secondsSinceLastIdling": 4.2
  },
  "revitUiThread": {
    "probed": true, "responsive": true, "latencyMs": 12, "timeoutMs": 2000,
    "raiseStatus": "Accepted", "hasActiveDocument": true
  },
  "logging": {
    "level": "Info", "directory": "...\\revit_mcp_plugin\\Logs",
    "protocolLog": "...\\mcp_protocol_20261004.log", "addinLog": "...\\mcp_addin_20261004.log"
  }
}
```

`plugin.reachable` only shows that the socket answered. It does not show that Revit API work can run;
`revitUiThread.responsive` does. Revit raises Idling only when it becomes idle, so an old `lastIdlingUtc`
alone does not mean Revit is blocked. Run health checks on their own connection so they do not queue
behind a long-running command.
