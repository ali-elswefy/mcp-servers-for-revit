import { AsyncLocalStorage } from "node:async_hooks";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

/**
 * The add-in's failure handler resolves Revit failures in MCP transactions without a dialog
 * (warnings suppressed, transactions with errors rolled back) and reports them in a
 * `revitFailures` object on the JSON-RPC response envelope. This module makes sure the agent
 * sees that report on every tool, whatever the tool does with the command's result.
 */
export interface RevitFailureReport {
  silenced?: boolean;
  rolledBackTransactions?: number;
  warningsSuppressed?: number;
  warningsKept?: number;
  errorsLeftToUser?: number;
  message?: string;
  failures?: unknown[];
  [key: string]: unknown;
}

type Collected = { command: string; report: RevitFailureReport };

// One collector per tool call; it follows the call into socket callbacks.
const collector = new AsyncLocalStorage<Collected[]>();

/** Called by the socket client for each response that carries `revitFailures`. */
export function recordRevitFailures(command: string, report: RevitFailureReport): void {
  const store = collector.getStore();
  if (store) {
    store.push({ command, report });
  } else {
    // Outside a tool call there is nobody to tell; keep it visible in the server log.
    console.error(`Revit failures reported for ${command} outside a tool call:`, JSON.stringify(report));
  }
}

function noticeText(collected: Collected[]): string {
  const rolledBack = collected.reduce((n, c) => n + (c.report.rolledBackTransactions ?? 0), 0);
  const headline =
    rolledBack > 0
      ? "REVIT FAILURES: some changes were rolled back and NOT applied."
      : "REVIT FAILURES: Revit reported failures that were silenced (no dialog was shown to the user).";
  const details = collected.map((c) => ({ command: c.command, ...c.report }));
  return (
    `${headline}\n` +
    collected.map((c) => `- ${c.command}: ${c.report.message ?? "see details"}`).join("\n") +
    "\nCheck these against what you report to the user; mention suppressed warnings that matter.\n" +
    JSON.stringify({ revitFailures: details.length === 1 ? details[0] : details }, null, 2)
  );
}

type ToolResult = { content?: Array<{ type: string; [key: string]: unknown }>; [key: string]: unknown };

function withRevitFailureNotice<T extends (...args: any[]) => any>(handler: T): T {
  return (async (...args: unknown[]) => {
    const collected: Collected[] = [];
    const result: ToolResult = await collector.run(collected, () => handler(...args));
    if (collected.length === 0 || !result || typeof result !== "object") return result;

    // First, so the agent reads it before the data it qualifies.
    const notice = { type: "text" as const, text: noticeText(collected) };
    return { ...result, content: [notice, ...(result.content ?? [])] };
  }) as unknown as T;
}

/**
 * Wraps the handler of every tool registered on `server` from now on, so a `revitFailures`
 * report is always surfaced as the first content block of the tool result.
 */
export function surfaceRevitFailures(server: McpServer): void {
  const register = server.tool.bind(server) as (...args: unknown[]) => unknown;
  (server as unknown as { tool: (...args: unknown[]) => unknown }).tool = (...args: unknown[]) => {
    const last = args.length - 1;
    if (typeof args[last] === "function") {
      args[last] = withRevitFailureNotice(args[last] as (...a: unknown[]) => unknown);
    }
    return register(...args);
  };
}
