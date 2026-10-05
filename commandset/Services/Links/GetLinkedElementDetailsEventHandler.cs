using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Links;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.ExternalEvents;
using RevitMCPCommandSet.Utils.Links;
using RevitMCPSDK.API.Models.JsonRPC;

namespace RevitMCPCommandSet.Services.Links
{
    public class GetLinkedElementDetailsEventHandler : QueuedExternalEventHandler<LinkedElementDetailsRequest, LinkedElementDetailsResult>
    {
        private const string Command = LinkedRequestParser.DetailsCommand;

        protected override LinkedElementDetailsResult Handle(UIApplication app, LinkedElementDetailsRequest request)
        {
            Document host = app.ActiveUIDocument?.Document;
            if (host == null)
                throw CommandErrors.NoActiveDocument(Command, request.RequestId);

            List<LinkTarget> selected = LinkUtils.SelectTargets(host, request.LinkInstanceId, request.LinkName, Command, request.RequestId);
            List<LinkTarget> loaded = selected.Where(t => t.IsLoaded).ToList();
            if (loaded.Count == 0)
            {
                throw CommandErrors.RevitState(
                    selected.Count == 0
                        ? "The host model has no Revit links."
                        : "The selected linked model is not loaded: " + string.Join("; ", selected.Select(t => $"{t.LinkName} ({t.Status})")),
                    JsonRPCErrorCodes.ResourceUnavailable, "no_loaded_links", Command, request.RequestId);
            }

            LinkTarget target;
            Element element = FindElement(loaded, request, out target);

            Document linkDocument = target.Document;
            var reader = new LinkedElementReader(linkDocument);

            var result = new LinkedElementDetailsResult
            {
                Success = true,
                Element = LinkedElementInfoBuilder.Build(element, target, reader, null, null),
                ElementClass = element.GetType().Name,
                LinkName = target.LinkName,
                DocumentTitle = target.DocumentTitle,
                InstanceParameters = LinkedElementInfoBuilder.DescribeParameters(element)
            };

            if (request.IncludeTypeParameters)
            {
                ElementType type = reader.GetElementType(element);
                result.TypeParameters = type == null ? new List<LinkedParameterInfo>() : LinkedElementInfoBuilder.DescribeParameters(type);
            }

            if (request.IncludeRelationships)
            {
                result.Relationships = ElementRelationshipUtils.GetRelationships(
                    linkDocument, element, false, ElementRelationshipUtils.BuildHostedElementIndex(linkDocument));
            }

            return result;
        }

        private static Element FindElement(List<LinkTarget> loaded, LinkedElementDetailsRequest request, out LinkTarget target)
        {
            target = null;
            Element element = null;

            if (request.ElementId.HasValue)
            {
                // An ElementId only identifies an element inside one document, so the link must be unambiguous.
                if (loaded.Count != 1)
                {
                    throw CommandErrors.Validation(
                        "An elementId is only unique within one linked model. Pass 'linkInstanceId' (see list_linked_models) or a 'linkName' that matches a single link, or use 'uniqueId'.",
                        Command, request.RequestId,
                        new
                        {
                            reason = "link_ambiguous",
                            loadedLinks = loaded.Select(t => new { linkInstanceId = t.InstanceId, linkName = t.LinkName })
                        });
                }

                target = loaded[0];
                try
                {
                    element = target.Document.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.Create(request.ElementId.Value));
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Id outside this Revit version's range: not found.
                }
            }
            else
            {
                foreach (LinkTarget candidate in loaded)
                {
                    element = candidate.Document.GetElement(request.UniqueId);
                    if (element != null)
                    {
                        target = candidate;
                        break;
                    }
                }
            }

            if (element == null)
            {
                string what = request.ElementId.HasValue ? $"element {request.ElementId.Value}" : $"element with uniqueId '{request.UniqueId}'";
                throw CommandErrors.Validation(
                    $"The {what} was not found in {(loaded.Count == 1 ? $"link '{loaded[0].LinkName}'" : "the selected linked models")}.",
                    Command, request.RequestId, new { reason = "element_not_found" });
            }

            return element;
        }

        public override string GetName()
        {
            return "Get Linked Element Details";
        }
    }
}
