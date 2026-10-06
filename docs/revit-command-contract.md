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
| `query_linked_elements` | 120 s |
| `list_linked_models`, `get_linked_element_details` | 60 s |

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
It is listed under `builtInMethods` in `command.json` (not under `commands`, so Settings does not offer it as a toggle).

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

## Linked models

Three read-only commands query the Revit links of the host model. Coordinates are millimeters in the
**host** model's coordinate system: each link instance's placement (including shared coordinates) is applied,
so results can be compared with host elements directly. An `elementId` returned for a linked element is only
unique inside its link; the pair `linkInstanceId` + `elementId` identifies it (`uniqueId` identifies it on its own).

Errors use the usual envelope. `validation` errors carry `details.reason` where useful:
`link_not_found` (with `availableLinks`), `link_ambiguous`, `element_not_found`, `host_element_not_found`,
`host_element_has_no_box`, or `invalidCategories`. `revit_state` with `reason: no_loaded_links` means every
selected link is unloaded or the host has none. Timeouts follow the usual `outcome` rules and are always
retry-safe, because the commands do not change the model.

### `list_linked_models`

| Parameter | Default | Notes |
| --- | --- | --- |
| `includeCounts` | `false` | Counts the non-type elements of each loaded link; slower on large links |

Returns `links[]` (one per `RevitLinkInstance`): `linkInstanceId`, `linkName`, `instanceName`, `documentTitle`,
`status` (Revit's `LinkedFileStatus`), `isLoaded`, `path`, `transform` (`origin` mm, `rotationZDegrees`,
`isIdentity`, `hasReflection`) and `elementCount` when requested. `unplacedLinkTypes[]` lists link types that
have no instance in the host. Unloaded links are listed with their status and have no `transform`.

### `query_linked_elements`

| Parameter | Notes |
| --- | --- |
| `linkInstanceId` or `linkName` | One link by id, or links whose name contains the text. Both omitted: every loaded link. Giving both is a validation error. Unloaded links are skipped with a warning. |
| `categories` | `OST_*` names or display names, resolved in each link. A name unknown in every link is a `validation` error; unknown in only some links is skipped there with a warning. `[]` is a validation error. |
| `familyName`, `typeName` | Exact, case-insensitive |
| `nameContains` | Substring of the element, family or type name, case-insensitive |
| `levelName` | Exact, case-insensitive; matches only elements that have a level. When nothing matches, a warning lists the link's levels. |
| `boundingBoxMin` + `boundingBoxMax` | `{ x, y, z }` mm in host coordinates, both required, min <= max. Matches elements whose host-coordinate bounding box intersects it (touching counts). |
| `nearHostElementId` (+ `nearDistanceMm`, default 0) | Alternative to a box: the host element's bounding box grown by the distance. Not combinable with a box. |
| `parameterFilters` | `[{ name, operator, value }]`, all must match. Operators: `equals`, `notEquals`, `contains`, `startsWith`, `isEmpty`, `hasValue`. Compared as the displayed string, case-insensitively; instance parameter first, then type parameter. A missing parameter matches only `isEmpty`. There are no numeric comparisons. |
| `parameterNames` | Parameter values to return with each element; `null` where the element lacks the parameter |
| `limit` | 1 to 1000, default 100, shared across all links |
| `countOnly` | Return `categoryCounts` per link instead of elements. With no other filter this summarizes the whole link. |

Without `countOnly`, at least one filter is required: listing a whole link is rejected.

Result: `mode` (`elements` or `counts`), `links[]` with `matched`, `returned`, `elements[]` or `categoryCounts[]`,
plus `totalMatched`, `totalReturned`, `truncated` and `warnings[]`. `matched` always counts every match, even
beyond `limit`. Each element has `linkInstanceId`, `elementId`, `uniqueId`, `name`, `category`,
`builtInCategory`, `familyName`, `typeName`, `typeId`, `level`, `boundingBox` (host mm), `location`
(`point`, or `start` and `end`, host mm) and optional `parameters`. The order of elements is unspecified.

### `get_linked_element_details`

| Parameter | Default | Notes |
| --- | --- | --- |
| `linkInstanceId` or `linkName` | | The link; with `elementId` it must identify exactly one loaded link |
| `elementId` or `uniqueId` | | Exactly one. A `uniqueId` is searched in every loaded link when no link is given. |
| `includeTypeParameters` | `true` | |
| `includeRelationships` | `false` | Host, group and hosted elements; ids are inside the link. Builds an index of the link's family instances, so it is slower. |

Returns `element` (as above, without `parameters`), `elementClass`, `linkName`, `documentTitle`,
`instanceParameters[]` and `typeParameters[]` (`name`, `value`, `storageType`, `isReadOnly`, `isShared`), and
`relationships`. Values are strings as Revit displays them, in the linked document's units.

### Limits

- Only Revit links of the host are covered. Nested links (links inside a linked model) and CAD links are not.
- Queries run on the whole linked document and ignore the host view, view filters and link visibility settings.
- Elements without a category are skipped.
- The bounding box filter is on axis-aligned boxes. For a link rotated about Z, an element's host box is the
  box of its rotated link box, so it can be larger than the element's real footprint.
- Query cost grows with the number of elements that pass the category and box filters, since the other
  filters are checked per element. Use `categories` and a box or level where possible.
