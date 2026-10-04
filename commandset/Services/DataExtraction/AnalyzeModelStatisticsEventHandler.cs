using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class AnalyzeModelStatisticsEventHandler : QueuedExternalEventHandler<ModelStatisticsRequest, object>
    {
        private const string CommandName = "analyze_model_statistics";

        protected override object Handle(UIApplication app, ModelStatisticsRequest request)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                throw CommandErrors.NoActiveDocument(CommandName, request.RequestId);

            if (request.Categories != null)
                return AnalyzeCategories(doc, request);

            try
            {
                return AnalyzeFullModel(doc, request);
            }
            catch (Exception ex)
            {
                return new AnalyzeModelStatisticsResult
                {
                    Success = false,
                    Message = $"Error analyzing model statistics: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Counts only the requested categories with category-scoped collectors. Types and levels
        /// are enumerated per category only when explicitly requested.
        /// </summary>
        private static CategoryCountResult AnalyzeCategories(Document doc, ModelStatisticsRequest request)
        {
            var resolved = CategoryResolver.Resolve(doc, request.Categories, out var invalidNames);
            if (invalidNames.Count > 0)
            {
                throw CommandErrors.Validation(
                    $"Unknown category name(s): {string.Join(", ", invalidNames)}. Use a BuiltInCategory name such as 'OST_Walls' or a category display name such as 'Walls'.",
                    CommandName, request.RequestId, new { invalidCategories = invalidNames });
            }

            var levelNames = request.IncludeLevels
                ? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList()
                : null;

            var counts = new List<CategoryCount>();
            var seen = new HashSet<long>();
            foreach (var category in resolved)
            {
                if (!seen.Add(category.Category.Id.GetValue()))
                    continue;

                var count = new CategoryCount
                {
                    RequestedName = category.RequestedName,
                    CategoryName = category.Category.Name,
                    BuiltInCategory = category.BuiltInCategoryName,
                    ElementCount = InstancesOf(doc, category.Category).GetElementCount()
                };

                if (request.IncludeDetailedTypes || request.IncludeLevels)
                    AddCategoryDetail(doc, category.Category, count, request.IncludeDetailedTypes, levelNames);

                counts.Add(count);
            }

            return new CategoryCountResult
            {
                Success = true,
                ProjectName = doc.Title,
                Categories = counts,
                Message = string.Join("; ", counts.Select(c => $"{c.CategoryName}: {c.ElementCount}"))
            };
        }

        private static FilteredElementCollector InstancesOf(Document doc, Category category) =>
            new FilteredElementCollector(doc)
                .WherePasses(new ElementCategoryFilter(category.Id))
                .WhereElementIsNotElementType();

        private static void AddCategoryDetail(Document doc, Category category, CategoryCount count, bool includeTypes, List<Level> levels)
        {
            var types = new Dictionary<(string Family, string Type), TypeStatistics>();
            var typeOrder = new List<TypeStatistics>();
            var levelCounts = new Dictionary<ElementId, int>();

            foreach (Element elem in InstancesOf(doc, category))
            {
                if (levels != null)
                {
                    levelCounts.TryGetValue(elem.LevelId, out int n);
                    levelCounts[elem.LevelId] = n + 1;
                }

                if (includeTypes && elem is FamilyInstance fi && !string.IsNullOrEmpty(fi.Symbol?.Name))
                    CountType(types, typeOrder, fi.Symbol.Family?.Name, fi.Symbol.Name);
            }

            if (includeTypes)
            {
                count.Types = typeOrder;
                count.TypeCount = typeOrder.Select(t => t.TypeName).Distinct().Count();
                count.FamilyCount = typeOrder.Select(t => t.FamilyName).Distinct().Count();
            }

            if (levels != null)
            {
                count.Levels = levels.Select(level => new LevelStatistics
                {
                    LevelName = level.Name,
                    Elevation = level.Elevation,
                    ElementCount = levelCounts.TryGetValue(level.Id, out int n) ? n : 0
                }).ToList();
            }
        }

        private static void CountType(Dictionary<(string Family, string Type), TypeStatistics> types,
            List<TypeStatistics> typeOrder, string familyName, string typeName)
        {
            if (types.TryGetValue((familyName, typeName), out var existing))
            {
                existing.InstanceCount++;
                return;
            }

            var stats = new TypeStatistics { TypeName = typeName, FamilyName = familyName, InstanceCount = 1 };
            types[(familyName, typeName)] = stats;
            typeOrder.Add(stats);
        }

        /// <summary>Full-model statistics; the response shape is unchanged from earlier versions.</summary>
        private static AnalyzeModelStatisticsResult AnalyzeFullModel(Document doc, ModelStatisticsRequest request)
        {
            // Get project name
            string projectName = doc.Title;

            // Count total elements
            int totalElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount();

            // Count total types
            int totalTypes = new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .GetElementCount();

            // Count views
            int totalViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Where(v => !(v as View).IsTemplate)
                .Count();

            // Count sheets
            int totalSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .GetElementCount();

            // Analyze by category, collecting per-level counts in the same pass
            var categoryStats = new Dictionary<string, CategoryStatistics>();
            var categoryTypes = new Dictionary<string, Dictionary<(string Family, string Type), TypeStatistics>>();
            var familyNames = new HashSet<string>();
            var levelCounts = new Dictionary<ElementId, int>();

            foreach (Element elem in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (request.IncludeLevels)
                {
                    levelCounts.TryGetValue(elem.LevelId, out int n);
                    levelCounts[elem.LevelId] = n + 1;
                }

                if (elem.Category == null) continue;

                string catName = elem.Category.Name;

                if (!categoryStats.TryGetValue(catName, out var stats))
                {
                    stats = new CategoryStatistics { CategoryName = catName };
                    categoryStats[catName] = stats;
                    categoryTypes[catName] = new Dictionary<(string Family, string Type), TypeStatistics>();
                }

                stats.ElementCount++;

                // Track type information
                if (elem is FamilyInstance fi)
                {
                    string familyName = fi.Symbol?.Family?.Name;
                    string typeName = fi.Symbol?.Name;

                    if (!string.IsNullOrEmpty(familyName))
                    {
                        familyNames.Add(familyName);
                    }

                    if (request.IncludeDetailedTypes && !string.IsNullOrEmpty(typeName))
                    {
                        CountType(categoryTypes[catName], stats.Types, familyName, typeName);
                    }
                }
            }

            // Calculate type and family counts per category
            foreach (var stat in categoryStats.Values)
            {
                stat.TypeCount = stat.Types.Select(t => t.TypeName).Distinct().Count();
                stat.FamilyCount = stat.Types.Select(t => t.FamilyName).Distinct().Count();
            }

            // Analyze by level
            var levelStats = new List<LevelStatistics>();
            if (request.IncludeLevels)
            {
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation);

                foreach (Level level in levels)
                {
                    levelStats.Add(new LevelStatistics
                    {
                        LevelName = level.Name,
                        Elevation = level.Elevation,
                        ElementCount = levelCounts.TryGetValue(level.Id, out int n) ? n : 0
                    });
                }
            }

            return new AnalyzeModelStatisticsResult
            {
                ProjectName = projectName,
                TotalElements = totalElements,
                TotalTypes = totalTypes,
                TotalFamilies = familyNames.Count,
                TotalViews = totalViews,
                TotalSheets = totalSheets,
                Categories = categoryStats.Values.OrderByDescending(c => c.ElementCount).ToList(),
                Levels = levelStats,
                Success = true,
                Message = $"Successfully analyzed model with {totalElements} elements across {categoryStats.Count} categories"
            };
        }

        public override string GetName()
        {
            return "Analyze Model Statistics";
        }
    }
}
