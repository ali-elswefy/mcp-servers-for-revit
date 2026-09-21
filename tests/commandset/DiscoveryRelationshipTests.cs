using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests;

public class DiscoveryRelationshipTests : RevitApiTest
{
    private static Document _doc;
    private static Group _group;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Discovery Relationship Test");
        tx.Start();

        var level = Level.Create(_doc, 0.0);
        var firstWall = Wall.Create(
            _doc,
            Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0)),
            level.Id,
            false);
        var secondWall = Wall.Create(
            _doc,
            Line.CreateBound(new XYZ(0, 5, 0), new XYZ(10, 5, 0)),
            level.Id,
            false);

        _group = _doc.Create.NewGroup(new List<ElementId>
        {
            firstWall.Id,
            secondWall.Id
        });

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task GroupRelationships_ReturnMemberIdsAndReferences()
    {
        var relationships = ElementRelationshipUtils.GetRelationships(_doc, _group, true);

        await Assert.That(relationships).IsNotNull();
        await Assert.That(relationships.GroupMemberIds).IsNotNull();
        await Assert.That(relationships.GroupMemberIds.Count).IsEqualTo(2);
        await Assert.That(relationships.GroupMembers).IsNotNull();
        await Assert.That(relationships.GroupMembers.Count).IsEqualTo(2);

        var memberIds = _group.GetMemberIds().Select(id => id.GetValue()).ToHashSet();
        await Assert.That(relationships.GroupMemberIds.All(memberIds.Contains)).IsTrue();
    }

    [Test]
    public async Task GroupMemberRelationships_ReturnContainingGroup()
    {
        Element member = _doc.GetElement(_group.GetMemberIds().First());
        var relationships = ElementRelationshipUtils.GetRelationships(_doc, member);

        await Assert.That(relationships).IsNotNull();
        await Assert.That(relationships.Group).IsNotNull();
        await Assert.That(relationships.Group.Id).IsEqualTo(_group.Id.GetValue());
    }

    [Test]
    public async Task ExpandGroups_ReturnsGroupAndAllMembers()
    {
        var expanded = ElementRelationshipUtils.ExpandGroups(_doc, new[] { _group });

        await Assert.That(expanded.Count).IsEqualTo(3);
        await Assert.That(expanded.Select(element => element.Id.GetValue()).Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task DetailedDiscovery_GroupResultIncludesRelationshipPayload()
    {
        var results = AIElementFilterEventHandler.GetElementFullInfo(
            _doc,
            new List<Element> { _group },
            includeRelationships: true,
            includeGroupMembers: true);
        var groupInfo = results.OfType<GroupOrLinkInfo>().Single();

        await Assert.That(groupInfo.Relationships).IsNotNull();
        await Assert.That(groupInfo.Relationships.GroupMemberIds.Count).IsEqualTo(2);
        await Assert.That(groupInfo.Relationships.GroupMembers.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FilteredDiscovery_GroupFilterReturnsOnlyDirectMembers()
    {
        var settings = new FilterSetting
        {
            FilterGroupId = _group.Id.GetValue(),
            IncludeInstances = true,
            MaxElements = 50
        };

        var results = AIElementFilterEventHandler.GetFilteredElements(_doc, settings);

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results.All(element => element.GroupId.GetValue() == _group.Id.GetValue())).IsTrue();
    }

    [Test]
    public async Task FilteredDiscovery_HostFilterWithUnhostedWallReturnsEmpty()
    {
        // The fixture contains plain walls with no wall-hosted FamilyInstance,
        // so filtering by a wall host must return an empty set without throwing.
        var settings = new FilterSetting
        {
            FilterHostElementId = _group.GetMemberIds().First().GetValue(),
            IncludeInstances = true,
            MaxElements = 50
        };

        var results = AIElementFilterEventHandler.GetFilteredElements(_doc, settings);

        await Assert.That(results).IsNotNull();
        await Assert.That(results.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RelationshipHelpers_HandleUnhostedElements()
    {
        Element member = _doc.GetElement(_group.GetMemberIds().First());

        await Assert.That(ElementRelationshipUtils.HasHost(member, _group.Id.GetValue())).IsFalse();
        await Assert.That(ElementRelationshipUtils.BelongsToGroup(member, _group.Id.GetValue())).IsTrue();
        await Assert.That(ElementRelationshipUtils.BelongsToGroup(_group, _group.Id.GetValue())).IsFalse();
    }

    [Test]
    public async Task FilterSetting_AllowsHostAndGroupCriteria()
    {
        var groupSettings = new FilterSetting
        {
            FilterGroupId = _group.Id.GetValue(),
            IncludeInstances = true
        };
        var hostSettings = new FilterSetting
        {
            FilterHostElementId = _group.GetMemberIds().First().GetValue(),
            IncludeInstances = true
        };

        await Assert.That(groupSettings.Validate(out string groupError)).IsTrue();
        await Assert.That(groupError).IsNull();
        await Assert.That(hostSettings.Validate(out string hostError)).IsTrue();
        await Assert.That(hostError).IsNull();
    }

    [Test]
    public async Task HostedElementIndex_IndexesNativeFamilyInstanceHosts()
    {
        var index = ElementRelationshipUtils.BuildHostedElementIndex(_doc);
        var hostedInstances = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>()
            .Where(instance => instance.Host != null)
            .ToList();

        await Assert.That(index).IsNotNull();
        foreach (FamilyInstance hostedInstance in hostedInstances)
        {
            await Assert.That(index.ContainsKey(hostedInstance.Host.Id.GetValue())).IsTrue();
            await Assert.That(index[hostedInstance.Host.Id.GetValue()]
                .Any(reference => reference.Id == hostedInstance.Id.GetValue())).IsTrue();
        }
    }
}
