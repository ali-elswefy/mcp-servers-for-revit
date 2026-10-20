import { AsyncLocalStorage } from "node:async_hooks";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

/**
 * The add-in's failure handler deals with Revit failures in MCP transactions without a dialog.
 * By default the first failure halts the command and comes back as a `revit_failure` error
 * listing the failures and Revit's fixes for them; the agent asks the user, then repeats the call
 * with `failureResolutions`. Failures that did not halt the command are reported in a
 * `revitFailures` object on the JSON-RPC response envelope.
 *
 * This module adds `failureResolutions` to every Revit tool and forwards it with each command,
 * and surfaces every `revitFailures` report at the top of the tool result.
 */
export interface RevitFailureReport {
  halted?: boolean;
  rolledBackTransactions?: number;
  message?: string;
  failures?: unknown[];
  [key: string]: unknown;
}

export interface FailureResolution {
  failureId: string;
  resolution: string;
}

type Collected = { command: string; report: RevitFailureReport };
type CallContext = { reports: Collected[]; failureResolutions?: FailureResolution[] };

// One context per tool call; it follows the call into socket callbacks.
const context = new AsyncLocalStorage<CallContext>();

// Tools that do not run Revit commands, so they have no failures to resolve.
const TOOLS_WITHOUT_REVIT_FAILURES = new Set(["health_check", "query_stored_data", "store_project_data", "store_room_data"]);

const failureResolutionsSchema = z
  .array(
    z.object({
      failureId: z.string().describe("The failureId reported with the failure in the revit_failure error."),
      resolution: z
        .string()
        .describe(
          "The user's choice: one of the failure's resolutions[].caption (e.g. 'Unjoin Elements') or resolutions[].type, or 'accept' to keep the change despite a warning."
        ),
    })
  )
  .optional()
  .describe(
    "Only when repeating a call that failed with a revit_failure error: the user's decision for each reported failure. " +
      "Never fill this in without asking the user. The add-in applies each choice when that failure comes up again."
  );

/** The `failureResolutions` of the tool call in progress, forwarded with each Revit command. */
export function currentFailureResolutions(): FailureResolution[] | undefined {
  return context.getStore()?.failureResolutions;
}

/** Called by the socket client for each response that carries `revitFailures`. */
export function recordRevitFailures(command: string, report: RevitFailureReport): void {
  const store = context.getStore();
  if (store) {
    store.reports.push({ command, report });
  } else {
    // Outside a tool call there is nobody to tell; keep it visible in the server log.
    console.error(`Revit failures reported for ${command} outside a tool call:`, JSON.stringify(report));
  }
}

function noticeText(collected: Collected[]): string {
  const halted = collected.some((c) => c.report.halted || (c.report.failures ?? []).some((f: any) => f?.action === "halted"));
  const rolledBack = collected.reduce((n, c) => n + (c.report.rolledBackTransactions ?? 0), 0);
  const headline = halted
    ? "REVIT FAILURES: a Revit failure stopped a command. Ask the user how to proceed before doing anything else."
    : rolledBack > 0
      ? "REVIT FAILURES: some changes were rolled back and NOT applied."
      : "REVIT FAILURES: Revit reported failures that were handled without showing the user a dialog.";
  const details = collected.map((c) => ({ command: c.command, ...c.report }));
  return (
    `${headline}\n` +
    collected.map((c) => `- ${c.command}: ${c.report.message ?? "see details"}`).join("\n") +
    "\nCheck these against what you report to the user; tell them about failures that matter.\n" +
    JSON.stringify({ revitFailures: details.length === 1 ? details[0] : details }, null, 2)
  );
}

type ToolResult = { content?: Array<{ type: string; [key: string]: unknown }>; [key: string]: unknown };

function withRevitFailureContext<T extends (...args: any[]) => any>(handler: T): T {
  return (async (...args: any[]) => {
    const toolArgs = args.length > 1 && args[0] && typeof args[0] === "object" ? args[0] : undefined;
    const call: CallContext = { reports: [], failureResolutions: toolArgs?.failureResolutions };
    const result: ToolResult = await context.run(call, () => handler(...args));
    if (call.reports.length === 0 || !result || typeof result !== "object") return result;

    // First, so the agent reads it before the data it qualifies.
    const notice = { type: "text" as const, text: noticeText(call.reports) };
    return { ...result, content: [notice, ...(result.content ?? [])] };
  }) as unknown as T;
}

/**
 * Wraps every tool registered on `server` from now on: Revit tools gain the optional
 * `failureResolutions` argument, and any `revitFailures` report is surfaced as the first content
 * block of the tool result.
 */
export function surfaceRevitFailures(server: McpServer): void {
  const register = server.tool.bind(server) as (...args: unknown[]) => unknown;
  (server as unknown as { tool: (...args: unknown[]) => unknown }).tool = (...args: unknown[]) => {
    const [name] = args;
    const last = args.length - 1;
    const shapeIndex = last - 1;

    // server.tool(name, description, shape, handler): add the argument to the shape.
    const shape = args[shapeIndex];
    if (
      typeof name === "string" &&
      !TOOLS_WITHOUT_REVIT_FAILURES.has(name) &&
      shapeIndex >= 1 &&
      shape &&
      typeof shape === "object" &&
      !Array.isArray(shape)
    ) {
      args[shapeIndex] = { ...(shape as object), failureResolutions: failureResolutionsSchema };
    }

    if (typeof args[last] === "function") {
      args[last] = withRevitFailureContext(args[last] as (...a: unknown[]) => unknown);
    }
    return register(...args);
  };
}
