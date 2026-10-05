using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Links;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.ExternalEvents;
using RevitMCPCommandSet.Utils.Links;
using RevitMCPSDK.API.Models.JsonRPC;

namespace RevitMCPCommandSet.Services.Links
{
    /// <summary>
    /// Queries the elements of linked models. Filters are applied inside a collector first (category and box),
    /// then per element (family, type, name, level, parameters, exact host-coordinate box). Matches are counted
    /// for the whole query, but element details are built only up to the request's limit.
    /// </summary>
    public class QueryLinkedElementsEventHandler : QueuedExternalEventHandler<LinkedElementQueryRequest, LinkedElementQueryResult>
    {
        private const string Command = LinkedRequestParser.QueryCommand;
        private const int MaxListedLevels = 30;

        protected override LinkedElementQueryResult Handle(UIApplication app, LinkedElementQueryRequest request)
        {
            Document host = app.ActiveUIDocument?.Document;
            if (host == null)
                throw CommandErrors.NoActiveDocument(Command, request.RequestId);

            var result = new LinkedElementQueryResult { Success = true, Mode = request.CountOnly ? "counts" : "elements" };

            List<LinkTarget> loaded = SelectLoadedTargets(host, request, result);
            MmBox hostBox = ResolveHostBox(host, request);
            Dictionary<long, List<ResolvedCategory>> categoriesByLink = ResolveCategories(loaded, request, result);

            int remaining = request.Limit;
            foreach (LinkTarget target in loaded)
            {
                List<ResolvedCategory> categories = null;
                categoriesByLink?.TryGetValue(target.InstanceId, out categories);

                result.Links.Add(QueryLink(target, request, categories, hostBox, ref remaining, result.Warnings));
            }

            result.TotalMatched = result.Links.Sum(link => link.Matched);
            result.TotalReturned = result.Links.Sum(link => link.Returned);
            result.Truncated = !request.CountOnly && result.TotalMatched > result.TotalReturned;
            result.Message = request.CountOnly
                ? $"{result.TotalMatched} matching elements counted in {loaded.Count} link(s)."
                : $"{result.TotalReturned} of {result.TotalMatched} matching elements returned from {loaded.Count} link(s)." +
                  (result.Truncated ? $" Raise 'limit' (max {LinkedElementQueryRequest.MaxLimit}) or narrow the filters to see the rest." : string.Empty);
            return result;
        }

        // ---------------------------------------------------------------- request resolution

        private static List<LinkTarget> SelectLoadedTargets(Document host, LinkedElementQueryRequest request, LinkedElementQueryResult result)
        {
            List<LinkTarget> selected = LinkUtils.SelectTargets(host, request.LinkInstanceId, request.LinkName, Command, request.RequestId);

            var loaded = new List<LinkTarget>();
            foreach (LinkTarget target in selected)
            {
                if (target.IsLoaded)
                    loaded.Add(target);
                else
                    result.Warnings.Add($"Link '{target.LinkName}' (instance {target.InstanceId}) is not loaded (status: {target.Status}) and was skipped.");
            }

            if (loaded.Count == 0)
            {
                throw CommandErrors.RevitState(
                    selected.Count == 0
                        ? "The host model has no Revit links."
                        : "None of the selected linked models is loaded: " + string.Join("; ", result.Warnings),
                    JsonRPCErrorCodes.ResourceUnavailable, "no_loaded_links", Command, request.RequestId);
            }

            return loaded;
        }

        /// <summary>The box to filter by, in host coordinates mm: the explicit box, or a host element's box grown by a distance.</summary>
        private static MmBox ResolveHostBox(Document host, LinkedElementQueryRequest request)
        {
            if (request.BoundingBox != null)
                return request.BoundingBox;

            if (!request.NearHostElementId.HasValue)
                return null;

            Element hostElement = null;
            try
            {
                hostElement = host.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.Create(request.NearHostElementId.Value));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Id outside this Revit version's range: not found.
            }

            if (hostElement == null)
            {
                throw CommandErrors.Validation(
                    $"Host element {request.NearHostElementId.Value} was not found in the active document.",
                    Command, request.RequestId, new { reason = "host_element_not_found" });
            }

            MmBox box = LinkUtils.GetHostBox(hostElement, Transform.Identity);
            if (box == null)
            {
                throw CommandErrors.Validation(
                    $"Host element {request.NearHostElementId.Value} has no bounding box to search around.",
                    Command, request.RequestId, new { reason = "host_element_has_no_box" });
            }

            return LinkUtils.Grow(box, request.NearDistanceMm);
        }

        /// <summary>
        /// Resolves category names in every link document. A name that resolves in none of them is a validation
        /// error; one that resolves in only some is skipped for the others with a warning.
        /// </summary>
        private static Dictionary<long, List<ResolvedCategory>> ResolveCategories(
            List<LinkTarget> loaded, LinkedElementQueryRequest request, LinkedElementQueryResult result)
        {
            if (request.Categories == null)
                return null;

            var byLink = new Dictionary<long, List<ResolvedCategory>>();
            HashSet<string> invalidEverywhere = null;

            foreach (LinkTarget target in loaded)
            {
                List<ResolvedCategory> resolved = CategoryResolver.Resolve(target.Document, request.Categories, out List<string> invalid);
                byLink[target.InstanceId] = resolved
                    .GroupBy(c => c.Category.Id.GetValue())
                    .Select(group => group.First())
                    .ToList();

                invalidEverywhere = invalidEverywhere == null
                    ? new HashSet<string>(invalid)
                    : new HashSet<string>(invalidEverywhere.Intersect(invalid));

                if (invalid.Count > 0)
                    result.Warnings.Add($"Link '{target.LinkName}': unknown categories skipped: {string.Join(", ", invalid)}.");
            }

            if (invalidEverywhere != null && invalidEverywhere.Count > 0)
            {
                throw CommandErrors.Validation(
                    $"Unknown category name(s): {string.Join(", ", invalidEverywhere)}. Use a BuiltInCategory name such as 'OST_Walls' or a category display name such as 'Walls'.",
                    Command, request.RequestId, new { invalidCategories = invalidEverywhere.ToList() });
            }

            return byLink;
        }

        // ---------------------------------------------------------------- per-link query

        private static LinkQueryLinkResult QueryLink(
            LinkTarget target,
            LinkedElementQueryRequest request,
            List<ResolvedCategory> categories,
            MmBox hostBox,
            ref int remaining,
            List<string> warnings)
        {
            var linkResult = new LinkQueryLinkResult
            {
                LinkInstanceId = target.InstanceId,
                LinkName = target.LinkName,
                DocumentTitle = target.DocumentTitle
            };
            if (request.CountOnly)
                linkResult.CategoryCounts = new List<LinkedCategoryCount>();
            else
                linkResult.Elements = new List<LinkedElementInfo>();

            // Categories were requested but none exists in this link: nothing can match. This must not fall
            // through to an unfiltered query.
            if (categories != null && categories.Count == 0)
                return linkResult;

            Document linkDocument = target.Document;
            bool needsElementChecks = request.FamilyName != null || request.TypeName != null || request.NameContains != null ||
                                      request.LevelName != null || request.ParameterFilters.Count > 0 || hostBox != null;

            // Whole-category counts need no per-element work.
            if (request.CountOnly && categories != null && !needsElementChecks)
            {
                foreach (ResolvedCategory category in categories)
                {
                    int count = new FilteredElementCollector(linkDocument)
                        .WherePasses(new ElementCategoryFilter(category.Category.Id))
                        .WhereElementIsNotElementType()
                        .GetElementCount();
                    linkResult.CategoryCounts.Add(new LinkedCategoryCount
                    {
                        CategoryName = category.Category.Name,
                        BuiltInCategory = category.BuiltInCategoryName,
                        Count = count
                    });
                    linkResult.Matched += count;
                }

                linkResult.CategoryCounts = linkResult.CategoryCounts.Where(c => c.Count > 0).OrderByDescending(c => c.Count).ToList();
                return linkResult;
            }

            FilteredElementCollector collector = new FilteredElementCollector(linkDocument).WhereElementIsNotElementType();
            if (categories != null)
            {
                List<ElementId> ids = categories.Select(c => c.Category.Id).ToList();
                collector.WherePasses(ids.Count == 1 ? (ElementFilter)new ElementCategoryFilter(ids[0]) : new ElementMulticategoryFilter(ids));
            }
            if (hostBox != null)
            {
                // A pre-filter in link coordinates; the exact host-coordinate check is below.
                collector.WherePasses(new BoundingBoxIntersectsFilter(LinkUtils.ToLinkOutline(hostBox, target.FromHost)));
            }

            var reader = new LinkedElementReader(linkDocument);
            var counts = request.CountOnly ? new Dictionary<long, LinkedCategoryCount>() : null;

            foreach (Element element in collector)
            {
                Category category = element.Category;
                if (category == null)
                    continue;

                MmBox elementBox = null;
                if (!Matches(element, request, reader, target, hostBox, out elementBox))
                    continue;

                linkResult.Matched++;

                if (counts != null)
                {
                    long key = category.Id.GetValue();
                    if (!counts.TryGetValue(key, out LinkedCategoryCount entry))
                    {
                        entry = new LinkedCategoryCount { CategoryName = category.Name, BuiltInCategory = LinkUtils.BuiltInCategoryName(category) };
                        counts[key] = entry;
                    }
                    entry.Count++;
                }
                else if (remaining > 0)
                {
                    linkResult.Elements.Add(LinkedElementInfoBuilder.Build(element, target, reader, elementBox, request.ParameterNames));
                    linkResult.Returned++;
                    remaining--;
                }
            }

            if (counts != null)
                linkResult.CategoryCounts = counts.Values.OrderByDescending(c => c.Count).ThenBy(c => c.CategoryName).ToList();

            if (linkResult.Matched == 0 && request.LevelName != null)
                warnings.Add($"Link '{target.LinkName}': nothing matched level '{request.LevelName}'. Levels in this link: {ListLevels(linkDocument)}.");

            return linkResult;
        }

        /// <summary>Per-element checks, cheapest first. The element's host box is returned so it is computed once.</summary>
        private static bool Matches(
            Element element,
            LinkedElementQueryRequest request,
            LinkedElementReader reader,
            LinkTarget target,
            MmBox hostBox,
            out MmBox elementBox)
        {
            elementBox = null;

            if (request.LevelName != null &&
                !string.Equals(reader.GetLevelName(element), request.LevelName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (request.FamilyName != null &&
                !string.Equals(reader.GetFamilyName(element), request.FamilyName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (request.TypeName != null &&
                !string.Equals(reader.GetTypeName(element), request.TypeName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (request.NameContains != null &&
                !ContainsIgnoreCase(LinkedElementInfoBuilder.SafeName(element), request.NameContains) &&
                !ContainsIgnoreCase(reader.GetFamilyName(element), request.NameContains) &&
                !ContainsIgnoreCase(reader.GetTypeName(element), request.NameContains))
                return false;

            foreach (ParameterFilterRule rule in request.ParameterFilters)
            {
                Parameter parameter = reader.FindParameter(element, rule.Name);
                if (!rule.Matches(parameter != null, parameter == null ? null : LinkUtils.DisplayValue(parameter)))
                    return false;
            }

            if (hostBox != null)
            {
                elementBox = LinkUtils.GetHostBox(element, target.ToHost);
                if (elementBox == null || !elementBox.Intersects(hostBox))
                    return false;
            }

            return true;
        }

        private static bool ContainsIgnoreCase(string text, string part) =>
            text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string ListLevels(Document linkDocument)
        {
            List<string> names = new FilteredElementCollector(linkDocument).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(level => level.Elevation).Select(level => level.Name).ToList();
            string listed = string.Join(", ", names.Take(MaxListedLevels));
            return names.Count > MaxListedLevels ? listed + $", ... ({names.Count - MaxListedLevels} more)" : listed;
        }

        public override string GetName()
        {
            return "Query Linked Elements";
        }
    }
}
