using Autodesk.Revit.DB;
using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

/// <summary>
/// Common relationship data attached to element discovery results.
/// </summary>
public class ElementRelationshipInfo
{
    /// <summary>
    /// Element hosting this element, when the element is hosted.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ElementReferenceInfo Host { get; set; }

    /// <summary>
    /// Group containing this element, when the element is a group member.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ElementReferenceInfo Group { get; set; }

    /// <summary>
    /// Hosted family instance IDs when this element is their host.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<long> HostedElementIds { get; set; }

    /// <summary>
    /// Hosted family instance summaries when relationship metadata is requested.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ElementReferenceInfo> HostedElements { get; set; }

    /// <summary>
    /// Direct member IDs when the discovered element is a group instance.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<long> GroupMemberIds { get; set; }

    /// <summary>
    /// Direct member summaries when group member expansion is requested.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<ElementReferenceInfo> GroupMembers { get; set; }
}

/// <summary>
/// Lightweight reference to another Revit element.
/// </summary>
public class ElementReferenceInfo
{
    public long Id { get; set; }
    public string UniqueId { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public string ElementClass { get; set; }
}

/// <summary>
/// Marks a discovery result that can carry relationship metadata.
/// </summary>
public interface IElementRelationshipContainer
{
    ElementRelationshipInfo Relationships { get; set; }
}

/// <summary>
/// Base class for detailed discovery records that can expose relationships.
/// </summary>
public abstract class RelationshipAwareInfo : IElementRelationshipContainer
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ElementRelationshipInfo Relationships { get; set; }
}
