import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { revitErrorResult } from "../utils/errors.js";

export function registerGetCurrentViewElementsTool(server: McpServer) {
  server.tool(
    "get_current_view_elements",
    "Get elements from the current active view in Revit. You can filter by model categories (like Walls, Floors) or annotation categories (like Dimensions, Text). " +
      "Omit both category lists to use a default set of common model and annotation categories (title blocks excluded). " +
      "If you pass either list, only the categories you pass are returned. Passing only empty lists returns every category in the view. " +
      "Unknown category names are rejected. Use includeHidden to show/hide invisible elements and limit to control the number of returned elements.",
    {
      modelCategoryList: z
        .array(z.string())
        .optional()
        .describe(
          "List of Revit model category names (e.g., 'OST_Walls', 'OST_Doors', 'OST_Floors')"
        ),
      annotationCategoryList: z
        .array(z.string())
        .optional()
        .describe(
          "List of Revit annotation category names (e.g., 'OST_Dimensions', 'OST_WallTags', 'OST_TextNotes', 'OST_TitleBlocks')"
        ),
      includeHidden: z
        .boolean()
        .optional()
        .describe("Whether to include hidden elements in the results"),
      limit: z
        .number()
        .optional()
        .describe("Maximum number of elements to return"),
      includeRelationships: z
        .boolean()
        .optional()
        .default(false)
        .describe("Include native host and group relationship metadata for each returned element"),
    },
    async (args, extra) => {
      const params = {
        // Leave omitted lists undefined: omitted and empty mean different things to the add-in.
        modelCategoryList: args.modelCategoryList,
        annotationCategoryList: args.annotationCategoryList,
        includeHidden: args.includeHidden || false,
        limit: args.limit || 100,
        includeRelationships: args.includeRelationships || false,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "get_current_view_elements",
            params
          );
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
        return revitErrorResult("get_current_view_elements", error);
      }
    }
  );
}
