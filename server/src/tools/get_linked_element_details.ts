import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { revitErrorResult } from "../utils/errors.js";

export function registerGetLinkedElementDetailsTool(server: McpServer) {
  server.tool(
    "get_linked_element_details",
    "Get everything about one element inside a linked Revit model: its instance and type parameters (values as Revit displays them), " +
      "category, family, type, level, bounding box and location in host coordinates (mm), and optionally its host, group and hosted elements. " +
      "Identify the element by elementId together with the link (linkInstanceId or linkName), or by uniqueId alone. " +
      "An elementId is only unique within one link, so it is rejected when the link is ambiguous.",
    {
      linkInstanceId: z
        .number()
        .int()
        .optional()
        .describe("The link instance (from list_linked_models or query_linked_elements)."),
      linkName: z
        .string()
        .optional()
        .describe("Alternative to linkInstanceId: link name or part of it. Must identify a single link when used with elementId."),
      elementId: z
        .number()
        .int()
        .optional()
        .describe("ElementId inside the linked model. Give exactly one of elementId and uniqueId."),
      uniqueId: z
        .string()
        .optional()
        .describe("UniqueId of the element; searched in every loaded link when no link is given."),
      includeTypeParameters: z
        .boolean()
        .optional()
        .describe("Also return the type's parameters. Defaults to true."),
      includeRelationships: z
        .boolean()
        .optional()
        .describe("Also return host, group and hosted element references (ids are inside the linked model). Defaults to false."),
    },
    async (args, extra) => {
      const params = {
        linkInstanceId: args.linkInstanceId,
        linkName: args.linkName,
        elementId: args.elementId,
        uniqueId: args.uniqueId,
        includeTypeParameters: args.includeTypeParameters,
        includeRelationships: args.includeRelationships,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_linked_element_details", params);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return revitErrorResult("get_linked_element_details", error);
      }
    }
  );
}
