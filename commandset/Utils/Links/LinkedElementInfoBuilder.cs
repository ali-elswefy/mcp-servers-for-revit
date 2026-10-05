using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Links;

namespace RevitMCPCommandSet.Utils.Links
{
    public static class LinkedElementInfoBuilder
    {
        /// <param name="hostBox">The element's box in host coordinates when the caller already computed it, else null to compute it here.</param>
        /// <param name="parameterNames">Parameters to read, or null for none.</param>
        public static LinkedElementInfo Build(
            Element element,
            LinkTarget target,
            LinkedElementReader reader,
            MmBox hostBox,
            IList<string> parameterNames)
        {
            ElementType type = reader.GetElementType(element);
            Category category = element.Category;

            var info = new LinkedElementInfo
            {
                LinkInstanceId = target.InstanceId,
                ElementId = element.Id.GetValue(),
                UniqueId = element.UniqueId,
                Name = SafeName(element),
                Category = category?.Name,
                BuiltInCategory = category != null ? LinkUtils.BuiltInCategoryName(category) : null,
                FamilyName = type?.FamilyName,
                TypeName = type != null ? SafeName(type) : null,
                TypeId = type?.Id.GetValue(),
                Level = reader.GetLevelName(element),
                BoundingBox = hostBox ?? LinkUtils.GetHostBox(element, target.ToHost),
                Location = LinkUtils.GetHostLocation(element, target.ToHost)
            };

            if (parameterNames != null && parameterNames.Count > 0)
            {
                info.Parameters = new Dictionary<string, string>();
                foreach (string name in parameterNames)
                {
                    Parameter parameter = reader.FindParameter(element, name);
                    info.Parameters[name] = parameter == null ? null : LinkUtils.DisplayValue(parameter);
                }
            }

            return info;
        }

        // Element.Name can throw for a few internal element classes.
        public static string SafeName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static List<LinkedParameterInfo> DescribeParameters(Element element)
        {
            var parameters = new List<LinkedParameterInfo>();
            foreach (Parameter parameter in element.Parameters)
            {
                string name = parameter.Definition?.Name;
                if (name == null)
                    continue;

                parameters.Add(new LinkedParameterInfo
                {
                    Name = name,
                    Value = LinkUtils.DisplayValue(parameter),
                    StorageType = parameter.StorageType.ToString(),
                    IsReadOnly = parameter.IsReadOnly,
                    IsShared = parameter.IsShared
                });
            }

            return parameters.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
