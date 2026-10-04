import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { revitErrorResult, toRevitCommandError } from "../utils/errors.js";

export function registerHealthCheckTool(server: McpServer) {
  server.tool(
    "health_check",
    "Check the Revit connection without querying the model. Reports two separate facts: whether the Revit add-in's socket answered (plugin.reachable), " +
      "and whether Revit's UI thread processed a no-op API event within the probe timeout (revitUiThread.responsive). " +
      "A reachable plugin with an unresponsive UI thread means Revit is busy (for example a modal dialog or a long operation) and model commands will time out.",
    {
      probeUiThread: z
        .boolean()
        .optional()
        .default(true)
        .describe("Also test whether the Revit UI thread can process an API event. Defaults to true."),
      uiProbeTimeoutMs: z
        .number()
        .optional()
        .default(2000)
        .describe("How long to wait for the UI-thread probe, in milliseconds (100-30000)."),
    },
    async (args, extra) => {
      const params = {
        probeUiThread: args.probeUiThread,
        uiProbeTimeoutMs: args.uiProbeTimeoutMs,
      };

      try {
        const response = await withRevitConnection(
          async (revitClient) => {
            return await revitClient.sendCommand("health_check", params, params.uiProbeTimeoutMs + 5000);
          },
          { exclusive: false }
        );

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        const err = toRevitCommandError(error);
        if (err.kind !== "connection") {
          return revitErrorResult("health_check", err);
        }

        // An unreachable plugin is a health fact, not a tool failure.
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  plugin: { reachable: false, error: err.detail },
                  revitUiThread: { probed: false, responsive: null, reason: "plugin_unreachable" },
                },
                null,
                2
              ),
            },
          ],
        };
      }
    }
  );
}
