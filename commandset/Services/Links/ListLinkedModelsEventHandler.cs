using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Links;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.ExternalEvents;
using RevitMCPCommandSet.Utils.Links;

namespace RevitMCPCommandSet.Services.Links
{
    public class ListLinkedModelsEventHandler : QueuedExternalEventHandler<LinkedModelsRequest, LinkedModelsResult>
    {
        protected override LinkedModelsResult Handle(UIApplication app, LinkedModelsRequest request)
        {
            Document host = app.ActiveUIDocument?.Document;
            if (host == null)
                throw CommandErrors.NoActiveDocument(LinkedRequestParser.ListCommand, request.RequestId);

            List<LinkTarget> targets = LinkUtils.GetTargets(host);
            var result = new LinkedModelsResult { Success = true, HostDocument = host.Title };

            foreach (LinkTarget target in targets)
            {
                var info = new LinkInstanceInfo
                {
                    LinkInstanceId = target.InstanceId,
                    LinkName = target.LinkName,
                    InstanceName = target.InstanceName,
                    DocumentTitle = target.DocumentTitle,
                    Status = target.Status,
                    IsLoaded = target.IsLoaded,
                    Path = LinkUtils.GetLinkPath(host, target),
                    Transform = LinkUtils.DescribeTransform(target.ToHost)
                };

                if (request.IncludeCounts && target.IsLoaded)
                {
                    info.ElementCount = new FilteredElementCollector(target.Document).WhereElementIsNotElementType().GetElementCount();
                }

                result.Links.Add(info);
            }

            // Link types with no instance in the host model (loaded in the file but not placed).
            var placedTypeIds = new HashSet<long>(targets.Where(t => t.Type != null).Select(t => t.Type.Id.GetValue()));
            foreach (RevitLinkType type in new FilteredElementCollector(host).OfClass(typeof(RevitLinkType)))
            {
                if (placedTypeIds.Contains(type.Id.GetValue()) || IsNestedLink(type))
                    continue;

                result.UnplacedLinkTypes.Add($"{type.Name} ({type.GetLinkedFileStatus()})");
            }

            int loaded = result.Links.Count(link => link.IsLoaded);
            result.Message = result.Links.Count == 0
                ? "The host model has no placed Revit links."
                : $"{result.Links.Count} link instance(s), {loaded} loaded.";
            return result;
        }

        // Nested links (links inside a linked model) show up as link types but are not placed in the host.
        private static bool IsNestedLink(RevitLinkType type)
        {
            try
            {
                return type.IsNestedLink;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override string GetName()
        {
            return "List Linked Models";
        }
    }
}
