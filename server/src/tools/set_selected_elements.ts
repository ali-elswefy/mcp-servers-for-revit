import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSetSelectedElementsTool(server: McpServer) {
  server.tool(
    "set_selected_elements",
    "Set the elements selected in Revit by their element IDs. Pass an empty array to clear the selection.",
    {
      elementIds: z
        .array(z.number().int())
        .describe("The Revit element IDs to select; use an empty array to clear the selection"),
    },
    async (args, extra) => {
      const params = {
        elementIds: args.elementIds,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_selected_elements", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `set selected elements failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
