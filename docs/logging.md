# Logging

The add-in writes two log streams to `<Revit addins>\revit_mcp_plugin\Logs\`, so the MCP side and the add-in side can be read on their own or together. Files are daily and kept for 14 days. `health_check` returns the exact paths and the current level.

| Stream | File | What it shows |
| --- | --- | --- |
| MCP protocol | `mcp_protocol_yyyyMMdd.log` | The wire: server start and stop, each connection, each request received and response sent (method, bytes, duration, error code, `kind`, timeout `outcome`), unparseable requests, responses that could not be delivered |
| Add-in | `mcp_addin_yyyyMMdd.log` | Plugin startup and shutdown, config and command loading, command dispatch with timing, and each queued command's lifecycle on the Revit UI thread: queued, started, finished, cancelled, timed out |

Every line is `date time LEVEL stream Tthread message`, for example:

```
2026-10-04 11:10:59.745 WARN  protocol T9   conn=1 req=req-B method=echo sent error code=-33001 kind=timeout outcome=outcome_unknown bytes=410 elapsed=221ms
2026-10-04 11:11:00.350 WARN  addin    T4   req=req-B cmd=echo inv=0b814f4c result was NOT delivered: the caller timed out and was told the outcome is unknown. The command ran to completion (ok).
```

`req=` is the JSON-RPC request ID the client sent, and it is the same in both streams, so one request can be followed across both. `inv=` identifies the single invocation inside the add-in. `T` is the thread: socket threads and the Revit UI thread have different numbers.

## Reading the logs

```powershell
.\scripts\trace-log.ps1                          # both streams merged by time, today
.\scripts\trace-log.ps1 -Channel protocol -Last 40
.\scripts\trace-log.ps1 -RequestId 1759574400123 # one request across both streams
.\scripts\trace-log.ps1 -Follow -MinLevel Warn   # live, warnings and errors only
```

Use `-RevitVersion 2024` or `-LogDir` if more than one Revit version has logs.

## Level

| Level | Records |
| --- | --- |
| `Info` (default) | One line per event: connections, requests, responses, command lifecycle |
| `Debug` | Also request parameters, response bodies and `send_code_to_revit` code, each truncated |
| `Warning`, `Error` | Problems only |

Set `"settings": { "logLevel": "Debug" }` in `Commands\commandRegistry.json` (applied the next time the service is switched on), or set the `REVIT_MCP_LOG_LEVEL` environment variable before starting Revit (it wins over the file). Debug logs contain model data and code, so turn it off when you are done.

## Reading a request's story

- **`sent … kind=timeout outcome=cancelled_before_start`** in the protocol log, with `skipped … not executed` in the add-in log: Revit was busy and the request never ran.
- **`outcome=outcome_unknown`**, then later `finished ok … after its caller had already timed out` and `result was NOT delivered`: the command was running when the caller gave up, and it completed anyway. The model changed.
- **`recv unparseable JSON bytes=8192`**: a request larger than one 8 KB socket read was cut off. Requests are not framed yet, so large `send_code_to_revit` snippets fail this way.
- **`sent ok` then `closed reason=read_failed` or `client_closed`**: "sent" means the response was handed to the network stack. A client that had already given up can look like this.
- **No `queued` line in the add-in log for a request that has a `dispatch` line**: the command has not been moved to the queued execution base yet, so only its dispatch and total time are logged.
