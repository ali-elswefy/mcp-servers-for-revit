import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { revitErrorResult } from "../utils/errors.js";

const pointSchema = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number(),
});

const parameterFilterSchema = z.object({
  name: z.string().min(1).describe("Parameter name, e.g. 'Fire Rating'. Instance parameters are checked first, then type parameters."),
  operator: z
    .enum(["equals", "notEquals", "contains", "startsWith", "isEmpty", "hasValue"])
    .describe("Comparison against the value as Revit displays it, case-insensitive. A parameter the element does not have only matches isEmpty."),
  value: z
    .union([z.string(), z.number(), z.boolean()])
    .optional()
    .describe("Value to compare with. Not used for isEmpty and hasValue."),
});

export function registerQueryLinkedElementsTool(server: McpServer) {
  server.tool(
    "query_linked_elements",
    "Query elements inside linked Revit models (not the host model). Filter by category, family, type, name, level, a box in host coordinates, nearness to a host element, or parameter values. " +
      "Returns elements, or set countOnly to get per-category counts (with no other filter, countOnly summarizes everything in the link). " +
      "Without countOnly at least one filter is required. Results are grouped by link; each element has linkInstanceId and elementId, which together identify it for get_linked_element_details. " +
      "Coordinates are millimeters in the HOST model's coordinate system (the link's placement is already applied). " +
      "Unknown category names are rejected. Elements in a link are queried as a whole, regardless of the host view.",
    {
      linkInstanceId: z
        .number()
        .int()
        .optional()
        .describe("Only this link instance (from list_linked_models). Omit to query every loaded link."),
      linkName: z
        .string()
        .optional()
        .describe("Only links whose name contains this text. Use either linkInstanceId or linkName."),
      categories: z
        .array(z.string().min(1))
        .min(1)
        .optional()
        .describe("Categories as BuiltInCategory names ('OST_Walls') or display names ('Walls')."),
      familyName: z.string().optional().describe("Exact family name, case-insensitive."),
      typeName: z.string().optional().describe("Exact type name, case-insensitive."),
      nameContains: z
        .string()
        .optional()
        .describe("Text contained in the element name, family name or type name, case-insensitive."),
      levelName: z
        .string()
        .optional()
        .describe("Exact level name inside the link, case-insensitive. Only matches elements that have a level."),
      boundingBoxMin: pointSchema
        .optional()
        .describe("Minimum corner of a search box in host coordinates, mm. Use together with boundingBoxMax."),
      boundingBoxMax: pointSchema
        .optional()
        .describe("Maximum corner of a search box in host coordinates, mm."),
      nearHostElementId: z
        .number()
        .int()
        .optional()
        .describe("Search around this element of the host model: its bounding box grown by nearDistanceMm. Do not combine with a bounding box."),
      nearDistanceMm: z
        .number()
        .min(0)
        .optional()
        .describe("How far around nearHostElementId to search, in mm. Defaults to 0."),
      parameterFilters: z
        .array(parameterFilterSchema)
        .optional()
        .describe("All of these must match."),
      parameterNames: z
        .array(z.string().min(1))
        .optional()
        .describe("Parameter values to return with each element (null where the element has no such parameter)."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(1000)
        .optional()
        .describe("Maximum elements to return across all links. Defaults to 100. The total number of matches is always reported."),
      countOnly: z
        .boolean()
        .optional()
        .describe("Return per-category counts instead of elements."),
    },
    async (args, extra) => {
      // Omitted values stay omitted: the add-in treats absent and empty differently.
      const params = {
        linkInstanceId: args.linkInstanceId,
        linkName: args.linkName,
        categories: args.categories,
        familyName: args.familyName,
        typeName: args.typeName,
        nameContains: args.nameContains,
        levelName: args.levelName,
        boundingBoxMin: args.boundingBoxMin,
        boundingBoxMax: args.boundingBoxMax,
        nearHostElementId: args.nearHostElementId,
        nearDistanceMm: args.nearDistanceMm,
        parameterFilters: args.parameterFilters,
        parameterNames: args.parameterNames,
        limit: args.limit,
        countOnly: args.countOnly,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("query_linked_elements", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return revitErrorResult("query_linked_elements", error);
      }
    }
  );
}
