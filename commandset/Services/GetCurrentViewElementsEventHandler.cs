using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Category selection:
    /// - Both lists omitted: the default model and annotation categories below.
    /// - Any list provided: only the provided lists are used; an omitted list contributes nothing.
    /// - Every provided list empty: no category filter (all element categories in the view).
    /// Unknown category names are a validation error rather than being silently dropped.
    /// Title blocks are not in the defaults; request OST_TitleBlocks explicitly on sheet views.
    /// </summary>
    public class GetCurrentViewElementsEventHandler : QueuedExternalEventHandler<ViewElementsRequest, ViewElementsResult>
    {
        private const string CommandName = "get_current_view_elements";

        // Default model category list
        public static readonly IReadOnlyList<string> DefaultModelCategories = new List<string>
        {
            "OST_Walls",
            "OST_Doors",
            "OST_Windows",
            "OST_Furniture",
            "OST_Columns",
            "OST_Floors",
            "OST_Roofs",
            "OST_Stairs",
            "OST_StructuralFraming",
            "OST_Ceilings",
            "OST_MEPSpaces",
            "OST_Rooms",
            "OST_IOSModelGroups"
        };
        // Default annotation category list
        public static readonly IReadOnlyList<string> DefaultAnnotationCategories = new List<string>
        {
            "OST_Dimensions",
            "OST_TextNotes",
            "OST_GenericAnnotation",
            "OST_WallTags",
            "OST_DoorTags",
            "OST_WindowTags",
            "OST_RoomTags",
            "OST_AreaTags",
            "OST_SpaceTags",
            "OST_ViewportLabels"
        };

        protected override ViewElementsResult Handle(UIApplication app, ViewElementsRequest request)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                throw CommandErrors.NoActiveDocument(CommandName, request.RequestId);

            var activeView = doc.ActiveView;
            if (activeView == null)
                throw CommandErrors.NoActiveView(CommandName, request.RequestId);

            List<ResolvedCategory> categories = ResolveCategories(doc, request);

            // Filter by category inside the collector so unrelated elements are never materialized.
            var collector = new FilteredElementCollector(doc, activeView.Id)
                .WhereElementIsNotElementType();
            if (categories.Count > 0)
            {
                collector.WherePasses(new ElementMulticategoryFilter(categories.Select(c => c.Category.Id).ToList()));
            }

            IEnumerable<Element> matches = collector;
            if (!request.IncludeHidden)
            {
                matches = matches.Where(e => !e.IsHidden(activeView));
            }

            // Stop enumerating once the limit is reached (plus one element to detect truncation),
            // before any property or relationship extraction.
            List<Element> elements = request.Limit > 0
                ? matches.Take(request.Limit + 1).ToList()
                : matches.ToList();
            bool truncated = request.Limit > 0 && elements.Count > request.Limit;
            if (truncated)
            {
                elements.RemoveAt(elements.Count - 1);
            }

            // Build the result
            var hostedElementIndex = request.IncludeRelationships
                ? ElementRelationshipUtils.BuildHostedElementIndex(doc)
                : null;
            var elementInfos = elements.Select(e => new ElementInfo
            {
#if REVIT2024_OR_GREATER
                Id = e.Id.Value,
#else
                Id = e.Id.IntegerValue,
#endif
                UniqueId = e.UniqueId,
                Name = e.Name,
                Category = e.Category?.Name ?? "unknow",
                Properties = GetElementProperties(e),
                Relationships = request.IncludeRelationships
                    ? ElementRelationshipUtils.GetRelationships(doc, e, false, hostedElementIndex)
                    : null
            }).ToList();

            return new ViewElementsResult
            {
#if REVIT2024_OR_GREATER
                ViewId = activeView.Id.Value,
#else
                ViewId = activeView.Id.IntegerValue,
#endif
                ViewName = activeView.Name,
                TotalElementsInView = new FilteredElementCollector(doc, activeView.Id).GetElementCount(),
                FilteredElementCount = elementInfos.Count,
                Truncated = truncated,
                CategoryFilter = categories.Count > 0
                    ? categories.Select(c => c.BuiltInCategoryName ?? c.Category.Name).ToList()
                    : null,
                Elements = elementInfos
            };
        }

        private static List<ResolvedCategory> ResolveCategories(Document doc, ViewElementsRequest request)
        {
            if (request.ModelCategoryList == null && request.AnnotationCategoryList == null)
            {
                // Defaults are not user input: skip any category this Revit version or document lacks.
                return CategoryResolver.Resolve(doc, DefaultModelCategories.Concat(DefaultAnnotationCategories), out _);
            }

            var requested = (request.ModelCategoryList ?? new List<string>())
                .Concat(request.AnnotationCategoryList ?? new List<string>())
                .ToList();
            var resolved = CategoryResolver.Resolve(doc, requested, out var invalidNames);
            if (invalidNames.Count > 0)
            {
                throw CommandErrors.Validation(
                    $"Unknown category name(s): {string.Join(", ", invalidNames)}. Use BuiltInCategory names such as 'OST_Walls' or display names such as 'Walls'.",
                    CommandName, request.RequestId, new { invalidCategories = invalidNames });
            }
            return resolved;
        }

        private Dictionary<string, string> GetElementProperties(Element element)
        {
            var properties = new Dictionary<string, string>();

            // Add common properties
#if REVIT2024_OR_GREATER
            properties.Add("ElementId", element.Id.Value.ToString());
#else
            properties.Add("ElementId", element.Id.IntegerValue.ToString());
#endif
            if (element.Location != null)
            {
                if (element.Location is LocationPoint locationPoint)
                {
                    var point = locationPoint.Point;
                    properties.Add("LocationX", point.X.ToString("F2"));
                    properties.Add("LocationY", point.Y.ToString("F2"));
                    properties.Add("LocationZ", point.Z.ToString("F2"));
                }
                else if (element.Location is LocationCurve locationCurve)
                {
                    var curve = locationCurve.Curve;
                    properties.Add("Start", $"{curve.GetEndPoint(0).X:F2}, {curve.GetEndPoint(0).Y:F2}, {curve.GetEndPoint(0).Z:F2}");
                    properties.Add("End", $"{curve.GetEndPoint(1).X:F2}, {curve.GetEndPoint(1).Y:F2}, {curve.GetEndPoint(1).Z:F2}");
                    properties.Add("Length", curve.Length.ToString("F2"));
                }
            }

            // Get commonly used parameter values
            var commonParams = new[] { "Comments", "Mark", "Level", "Family", "Type" };
            foreach (var paramName in commonParams)
            {
                Parameter param = element.LookupParameter(paramName);
                if (param != null && !param.IsReadOnly)
                {
                    if (param.StorageType == StorageType.String)
                        properties.Add(paramName, param.AsString() ?? "");
                    else if (param.StorageType == StorageType.Double)
                        properties.Add(paramName, param.AsDouble().ToString("F2"));
                    else if (param.StorageType == StorageType.Integer)
                        properties.Add(paramName, param.AsInteger().ToString());
                    else if (param.StorageType == StorageType.ElementId)
#if REVIT2024_OR_GREATER
                        properties.Add(paramName, param.AsElementId().Value.ToString());
#else
                        properties.Add(paramName, param.AsElementId().IntegerValue.ToString());
#endif
                }
            }

            return properties;
        }


        public override string GetName()
        {
            return "Get Current View Elements";
        }
    }
}
