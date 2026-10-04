import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { revitErrorResult } from "../utils/errors.js";

export function registerAnalyzeModelStatisticsTool(server: McpServer) {
  server.tool(
    "analyze_model_statistics",
    "Analyze model complexity with element counts. " +
      "To count specific categories (e.g. 'how many walls?'), pass `categories` (e.g. ['OST_Walls'] or ['Walls']); this returns only those categories' instance counts and is fast. " +
      "Without `categories`, returns full-model statistics: total element counts, total types, total families, views, sheets, counts by category (with type/family breakdown), and level-by-level element distribution. " +
      "Useful for model auditing, performance analysis, and understanding model composition.",
    {
      categories: z
        .array(z.string().min(1))
        .min(1)
        .optional()
        .describe(
          "Categories to count, as BuiltInCategory names ('OST_Walls') or display names ('Walls'). Omit for full-model statistics. Unknown names are rejected."
        ),
      includeDetailedTypes: z
        .boolean()
        .optional()
        .describe(
          "Include a breakdown by family and type. Defaults to true for full-model statistics and false when `categories` is given."
        ),
      includeLevels: z
        .boolean()
        .optional()
        .describe(
          "Include element counts per level. Defaults to true for full-model statistics and false when `categories` is given."
        ),
    },
    async (args, extra) => {
      const params = {
        categories: args.categories,
        includeDetailedTypes: args.includeDetailedTypes,
        includeLevels: args.includeLevels,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("analyze_model_statistics", params);
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
        return revitErrorResult("analyze_model_statistics", error);
      }
    }
  );
}
