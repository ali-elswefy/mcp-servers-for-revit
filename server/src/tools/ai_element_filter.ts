import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerAIElementFilterTool(server: McpServer) {
  server.tool(
    "ai_element_filter",
    "An intelligent Revit element querying tool designed specifically for AI assistants to retrieve detailed element information from Revit projects. Filter by category, type, family symbol, visibility, spatial location, host element, or containing group. Set includeRelationships to return native host/group references; set expandGroups to add group members to the result set and include member references. Example: {\"filterCategory\": \"OST_Doors\", \"includeRelationships\": true} finds doors and reports their hosts and groups.",
    {
      data: z.object({
        filterCategory: z
          .string()
          .optional()
          .describe("Enumeration of built-in element categories in Revit used for filtering and identifying specific element types (e.g., OST_Walls, OST_Floors, OST_GenericModel). Note that furniture elements may be classified as either OST_Furniture or OST_GenericModel categories, requiring flexible selection approaches"),
        filterElementType: z
          .string()
          .optional()
          .describe("The Revit element type name used for filtering specific elements by their class or type (e.g., 'Wall', 'Floor', 'Autodesk.Revit.DB.Wall'). Gets or sets the name of the Revit element type to be filtered."),
        filterFamilySymbolId: z
          .number()
          .optional()
          .describe("The ElementId of a specific FamilySymbol (type) in Revit used for filtering elements by their type (e.g., '123456', '789012'). Gets or sets the ElementId of the FamilySymbol to be used as a filter criterion. Use '-1' if no specific FamilySymbol filtering is needed."),
        filterHostElementId: z
          .number()
          .int()
          .positive()
          .optional()
          .describe("Return only hosted family instances whose native host has this Revit element ID."),
        filterGroupId: z
          .number()
          .int()
          .positive()
          .optional()
          .describe("Return only direct members of the group instance with this Revit element ID."),
        includeTypes: z
          .boolean()
          .default(false)
          .describe("Determines whether to include element types (such as wall types, door types, etc.) in the selection results. When set to true, element types will be included; when false, they will be excluded."),
        includeInstances: z
          .boolean()
          .default(true)
          .describe("Determines whether to include element instances (such as placed walls, doors, etc.) in the selection results. When set to true, element instances will be included; when false, they will be excluded."),
        filterVisibleInCurrentView: z
          .boolean()
          .optional()
          .describe("Determines whether to only return elements that are visible in the current view. When set to true, only elements visible in the current view will be returned. Note: This filter only applies to element instances, not type elements."),
        includeRelationships: z
          .boolean()
          .optional()
          .default(false)
          .describe("Include native host and group relationship metadata on each result. Group results include direct member IDs."),
        expandGroups: z
          .boolean()
          .optional()
          .default(false)
          .describe("When a returned result is a group, add its direct and nested members to the result set and include member references."),
        boundingBoxMin: z
          .object({
            x: z.number().describe("X coordinate in mm"),
            y: z.number().describe("Y coordinate in mm"),
            z: z.number().describe("Z coordinate in mm"),
          })
          .optional()
          .describe("The minimum point coordinates (in mm) for spatial bounding box filtering. When set along with boundingBoxMax, only elements that intersect with this bounding box will be returned. Set to null to disable this filter."),
        boundingBoxMax: z
          .object({
            x: z.number().describe("X coordinate in mm"),
            y: z.number().describe("Y coordinate in mm"),
            z: z.number().describe("Z coordinate in mm"),
          })
          .optional()
          .describe("The maximum point coordinates (in mm) for spatial bounding box filtering. When set along with boundingBoxMin, only elements that intersect with this bounding box will be returned. Set to null to disable this filter."),
          maxElements: z
          .number()
          .optional()
          .describe("The maximum number of elements to find in a single tool invocation. Default is 50. Values exceeding 50 are not recommended for performance reasons."),
      })
        .describe("Configuration parameters for the Revit element filter tool. These settings determine which elements will be selected from the Revit project based on various filtering criteria. Multiple filters can be combined to achieve precise element selection. All spatial coordinates should be provided in millimeters."),
    },
    async (args, extra) => {
      const params = args;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "ai_element_filter",
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
        return {
          content: [
            {
              type: "text",
              text: `Get element information failed: ${error instanceof Error ? error.message : String(error)
                }`,
            },
          ],
        };
      }
    }
  );
}
