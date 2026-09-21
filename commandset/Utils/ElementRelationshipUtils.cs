using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Utils;

/// <summary>
/// Resolves native Revit host and group relationships for discovery results.
/// </summary>
public static class ElementRelationshipUtils
{
    public static Dictionary<long, List<ElementReferenceInfo>> BuildHostedElementIndex(Document document)
    {
        var hostedElementIndex = new Dictionary<long, List<ElementReferenceInfo>>();
        if (document == null)
            return hostedElementIndex;

        foreach (FamilyInstance familyInstance in new FilteredElementCollector(document)
                     .OfClass(typeof(FamilyInstance))
                     .Cast<FamilyInstance>())
        {
            Element host = familyInstance.Host;
            if (host == null)
                continue;

            long hostId = host.Id.GetValue();
            if (!hostedElementIndex.TryGetValue(hostId, out List<ElementReferenceInfo> hostedElements))
            {
                hostedElements = new List<ElementReferenceInfo>();
                hostedElementIndex[hostId] = hostedElements;
            }

            hostedElements.Add(CreateReference(familyInstance));
        }

        return hostedElementIndex;
    }

    public static List<Element> ExpandGroups(Document document, IEnumerable<Element> elements)
    {
        if (document == null || elements == null)
            return new List<Element>();

        var expandedElements = new List<Element>();
        var seenElementIds = new HashSet<long>();
        var groupsToExpand = new Queue<Group>();

        foreach (Element element in elements)
        {
            if (element == null || !seenElementIds.Add(element.Id.GetValue()))
                continue;

            expandedElements.Add(element);
            if (element is Group group)
                groupsToExpand.Enqueue(group);
        }

        while (groupsToExpand.Count > 0)
        {
            Group group = groupsToExpand.Dequeue();
            foreach (ElementId memberId in group.GetMemberIds())
            {
                Element member = document.GetElement(memberId);
                if (member == null || !seenElementIds.Add(member.Id.GetValue()))
                    continue;

                expandedElements.Add(member);
                if (member is Group nestedGroup)
                    groupsToExpand.Enqueue(nestedGroup);
            }
        }

        return expandedElements;
    }

    public static ElementRelationshipInfo GetRelationships(
        Document document,
        Element element,
        bool includeGroupMembers = false,
        IReadOnlyDictionary<long, List<ElementReferenceInfo>> hostedElementIndex = null)
    {
        if (document == null || element == null)
            return null;

        ElementRelationshipInfo relationships = new ElementRelationshipInfo();

        if (element is FamilyInstance familyInstance)
        {
            relationships.Host = CreateReference(familyInstance.Host);
        }

        ElementId groupId = element.GroupId;
        if (groupId != null && groupId != ElementId.InvalidElementId)
        {
            relationships.Group = CreateReference(document.GetElement(groupId));
        }

        if (hostedElementIndex != null &&
            hostedElementIndex.TryGetValue(element.Id.GetValue(), out List<ElementReferenceInfo> hostedElements) &&
            hostedElements.Count > 0)
        {
            relationships.HostedElementIds = hostedElements.Select(hosted => hosted.Id).ToList();
            relationships.HostedElements = hostedElements;
        }

        if (element is Group group)
        {
            ICollection<ElementId> memberIds = group.GetMemberIds();
            relationships.GroupMemberIds = memberIds
                .Select(id => id.GetValue())
                .ToList();
            if (includeGroupMembers)
            {
                relationships.GroupMembers = memberIds
                    .Select(document.GetElement)
                    .Where(member => member != null)
                    .Select(CreateReference)
                    .ToList();
            }
        }

        if (relationships.Host == null &&
            relationships.Group == null &&
            relationships.HostedElementIds == null &&
            relationships.GroupMemberIds == null)
        {
            return null;
        }

        return relationships;
    }

    public static ElementReferenceInfo CreateReference(Element element)
    {
        if (element == null)
            return null;

        return new ElementReferenceInfo
        {
            Id = element.Id.GetValue(),
            UniqueId = element.UniqueId,
            Name = element.Name,
            Category = element.Category?.Name,
            ElementClass = element.GetType().Name
        };
    }

    public static bool HasHost(Element element, long hostElementId)
    {
        return element is FamilyInstance familyInstance &&
               familyInstance.Host != null &&
               familyInstance.Host.Id.GetValue() == hostElementId;
    }

    public static bool BelongsToGroup(Element element, long groupId)
    {
        return element != null &&
               element.GroupId != null &&
               element.GroupId != ElementId.InvalidElementId &&
               element.GroupId.GetValue() == groupId;
    }
}
