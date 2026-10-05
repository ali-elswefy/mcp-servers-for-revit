using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Links
{
    // All coordinates in these models are millimeters in the HOST model's coordinate system
    // (the link instance transform is already applied), like the other tools.

    public class LinkTransformInfo
    {
        /// <summary>Origin of the link's coordinate system in host coordinates, mm.</summary>
        [JsonProperty("origin")] public MmPoint Origin { get; set; }

        /// <summary>Rotation about the Z axis, degrees, counter-clockwise.</summary>
        [JsonProperty("rotationZDegrees")] public double RotationZDegrees { get; set; }

        [JsonProperty("isIdentity")] public bool IsIdentity { get; set; }

        [JsonProperty("hasReflection")] public bool HasReflection { get; set; }
    }

    public class LinkInstanceInfo
    {
        /// <summary>ElementId of the RevitLinkInstance in the host model. Pass this to the other linked-model tools.</summary>
        [JsonProperty("linkInstanceId")] public long LinkInstanceId { get; set; }

        /// <summary>Name of the link type, usually the linked file name.</summary>
        [JsonProperty("linkName")] public string LinkName { get; set; }

        [JsonProperty("instanceName")] public string InstanceName { get; set; }

        [JsonProperty("documentTitle", NullValueHandling = NullValueHandling.Ignore)] public string DocumentTitle { get; set; }

        /// <summary>Revit's LinkedFileStatus: Loaded, Unloaded, NotFound, InClosedWorkset, ...</summary>
        [JsonProperty("status")] public string Status { get; set; }

        [JsonProperty("isLoaded")] public bool IsLoaded { get; set; }

        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)] public string Path { get; set; }

        [JsonProperty("transform", NullValueHandling = NullValueHandling.Ignore)] public LinkTransformInfo Transform { get; set; }

        /// <summary>Non-type elements in the link; only with includeCounts.</summary>
        [JsonProperty("elementCount", NullValueHandling = NullValueHandling.Ignore)] public int? ElementCount { get; set; }
    }

    public class LinkedModelsResult
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("hostDocument")] public string HostDocument { get; set; }
        [JsonProperty("links")] public List<LinkInstanceInfo> Links { get; set; } = new List<LinkInstanceInfo>();

        /// <summary>Link types that are not placed in the host model (no instance), with their status.</summary>
        [JsonProperty("unplacedLinkTypes")] public List<string> UnplacedLinkTypes { get; set; } = new List<string>();

        [JsonProperty("message")] public string Message { get; set; }
    }

    // ---------------------------------------------------------------- query_linked_elements

    public class LinkedLocation
    {
        [JsonProperty("point", NullValueHandling = NullValueHandling.Ignore)] public MmPoint Point { get; set; }
        [JsonProperty("start", NullValueHandling = NullValueHandling.Ignore)] public MmPoint Start { get; set; }
        [JsonProperty("end", NullValueHandling = NullValueHandling.Ignore)] public MmPoint End { get; set; }
    }

    public class LinkedElementInfo
    {
        [JsonProperty("linkInstanceId")] public long LinkInstanceId { get; set; }

        /// <summary>ElementId inside the linked document. Only unique together with linkInstanceId.</summary>
        [JsonProperty("elementId")] public long ElementId { get; set; }

        [JsonProperty("uniqueId")] public string UniqueId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("builtInCategory", NullValueHandling = NullValueHandling.Ignore)] public string BuiltInCategory { get; set; }
        [JsonProperty("familyName", NullValueHandling = NullValueHandling.Ignore)] public string FamilyName { get; set; }
        [JsonProperty("typeName", NullValueHandling = NullValueHandling.Ignore)] public string TypeName { get; set; }
        [JsonProperty("typeId", NullValueHandling = NullValueHandling.Ignore)] public long? TypeId { get; set; }
        [JsonProperty("level", NullValueHandling = NullValueHandling.Ignore)] public string Level { get; set; }

        /// <summary>Axis-aligned box in host coordinates, mm. Null when the element has no geometry box.</summary>
        [JsonProperty("boundingBox", NullValueHandling = NullValueHandling.Ignore)] public MmBox BoundingBox { get; set; }

        [JsonProperty("location", NullValueHandling = NullValueHandling.Ignore)] public LinkedLocation Location { get; set; }

        /// <summary>Requested parameter values as displayed by Revit; null when the element has no such parameter.</summary>
        [JsonProperty("parameters", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string> Parameters { get; set; }
    }

    public class LinkedCategoryCount
    {
        [JsonProperty("categoryName")] public string CategoryName { get; set; }
        [JsonProperty("builtInCategory", NullValueHandling = NullValueHandling.Ignore)] public string BuiltInCategory { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
    }

    public class LinkQueryLinkResult
    {
        [JsonProperty("linkInstanceId")] public long LinkInstanceId { get; set; }
        [JsonProperty("linkName")] public string LinkName { get; set; }
        [JsonProperty("documentTitle", NullValueHandling = NullValueHandling.Ignore)] public string DocumentTitle { get; set; }

        /// <summary>Elements in this link that matched all criteria.</summary>
        [JsonProperty("matched")] public int Matched { get; set; }

        [JsonProperty("returned")] public int Returned { get; set; }

        [JsonProperty("elements", NullValueHandling = NullValueHandling.Ignore)] public List<LinkedElementInfo> Elements { get; set; }
        [JsonProperty("categoryCounts", NullValueHandling = NullValueHandling.Ignore)] public List<LinkedCategoryCount> CategoryCounts { get; set; }
    }

    public class LinkedElementQueryResult
    {
        [JsonProperty("success")] public bool Success { get; set; }

        /// <summary>"elements" or "counts".</summary>
        [JsonProperty("mode")] public string Mode { get; set; }

        [JsonProperty("links")] public List<LinkQueryLinkResult> Links { get; set; } = new List<LinkQueryLinkResult>();
        [JsonProperty("totalMatched")] public int TotalMatched { get; set; }
        [JsonProperty("totalReturned")] public int TotalReturned { get; set; }

        /// <summary>True when more elements matched than the limit allowed to be returned.</summary>
        [JsonProperty("truncated")] public bool Truncated { get; set; }

        [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new List<string>();
        [JsonProperty("message")] public string Message { get; set; }
    }

    // ---------------------------------------------------------------- get_linked_element_details
    public class LinkedParameterInfo
    {
        [JsonProperty("name")] public string Name { get; set; }

        /// <summary>The value as Revit displays it, in the linked document's units.</summary>
        [JsonProperty("value")] public string Value { get; set; }

        [JsonProperty("storageType")] public string StorageType { get; set; }
        [JsonProperty("isReadOnly")] public bool IsReadOnly { get; set; }
        [JsonProperty("isShared")] public bool IsShared { get; set; }
    }

    public class LinkedElementDetailsResult
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("element")] public LinkedElementInfo Element { get; set; }
        [JsonProperty("elementClass")] public string ElementClass { get; set; }
        [JsonProperty("linkName")] public string LinkName { get; set; }
        [JsonProperty("documentTitle", NullValueHandling = NullValueHandling.Ignore)] public string DocumentTitle { get; set; }
        [JsonProperty("instanceParameters")] public List<LinkedParameterInfo> InstanceParameters { get; set; } = new List<LinkedParameterInfo>();
        [JsonProperty("typeParameters", NullValueHandling = NullValueHandling.Ignore)] public List<LinkedParameterInfo> TypeParameters { get; set; }

        /// <summary>Host, group and hosted-element references; ids are inside the linked document.</summary>
        [JsonProperty("relationships", NullValueHandling = NullValueHandling.Ignore)] public ElementRelationshipInfo Relationships { get; set; }
    }
}
