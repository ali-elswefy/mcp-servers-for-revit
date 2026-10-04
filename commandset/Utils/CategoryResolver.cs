using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    public class ResolvedCategory
    {
        public string RequestedName { get; set; }
        public Category Category { get; set; }

        /// <summary>The OST_* name, or null for a non-built-in category (e.g. an imported CAD category).</summary>
        public string BuiltInCategoryName { get; set; }
    }

    /// <summary>
    /// Resolves user-supplied category names against a document. Accepts BuiltInCategory names
    /// ("OST_Walls") and category display names ("Walls", case-insensitive). Names that do not
    /// resolve to a category in the document are reported as invalid instead of being ignored.
    /// </summary>
    public static class CategoryResolver
    {
        public static List<ResolvedCategory> Resolve(Document doc, IEnumerable<string> names, out List<string> invalidNames)
        {
            var resolved = new List<ResolvedCategory>();
            invalidNames = new List<string>();
            Dictionary<string, Category> byDisplayName = null;

            foreach (string rawName in names)
            {
                string name = rawName?.Trim();
                Category category = null;

                if (!string.IsNullOrEmpty(name))
                {
                    if (name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse(name, true, out BuiltInCategory builtIn) && Enum.IsDefined(typeof(BuiltInCategory), builtIn))
                            category = TryGetCategory(doc, builtIn);
                    }
                    else
                    {
                        byDisplayName = byDisplayName ?? BuildDisplayNameIndex(doc);
                        byDisplayName.TryGetValue(name, out category);
                    }
                }

                if (category == null)
                {
                    invalidNames.Add(rawName ?? "(null)");
                    continue;
                }

                long idValue = category.Id.GetValue();
                resolved.Add(new ResolvedCategory
                {
                    RequestedName = rawName,
                    Category = category,
                    BuiltInCategoryName = idValue < 0 ? ((BuiltInCategory)(int)idValue).ToString() : null
                });
            }

            return resolved;
        }

        private static Category TryGetCategory(Document doc, BuiltInCategory builtIn)
        {
            try
            {
                return Category.GetCategory(doc, builtIn);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, Category> BuildDisplayNameIndex(Document doc)
        {
            var index = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
            foreach (Category category in doc.Settings.Categories)
            {
                if (!index.ContainsKey(category.Name))
                    index[category.Name] = category;
            }
            return index;
        }
    }
}
