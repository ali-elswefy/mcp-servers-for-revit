import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { revitErrorResult } from "../utils/errors.js";

export function registerListLinkedModelsTool(server: McpServer) {
  server.tool(
    "list_linked_models",
    "List the Revit links in the host model: link instance id, name, load status, file path, and where the link sits (origin in mm and rotation about Z). " +
      "Use the linkInstanceId it returns with query_linked_elements and get_linked_element_details. " +
      "Links that are not loaded cannot be queried. Coordinates are millimeters in the host model's coordinate system.",
    {
      includeCounts: z
        .boolean()
        .optional()
        .describe("Also count the elements in each loaded link. Slower on large links. Defaults to false."),
    },
    async (args, extra) => {
      const params = { includeCounts: args.includeCounts };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("list_linked_models", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return revitErrorResult("list_linked_models", error);
      }
    }
  );
}
