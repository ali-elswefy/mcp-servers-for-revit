using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Models.Common
{
    /// <summary>
    /// Filter settings that support combining multiple criteria
    /// </summary>
    public class FilterSetting
    {
        /// <summary>
        /// Gets or sets the name of the Revit built-in category to filter, such as "OST_Walls".
        /// If null or empty, elements are not filtered by category.
        /// </summary>
        [JsonProperty("filterCategory")]
        public string FilterCategory { get; set; } = null;
        /// <summary>
        /// Gets or sets the Revit element type name to filter, such as "Wall" or "Autodesk.Revit.DB.Wall".
        /// If null or empty, elements are not filtered by type.
        /// </summary>
        [JsonProperty("filterElementType")]
        public string FilterElementType { get; set; } = null;
        /// <summary>
        /// Gets or sets the ElementId value of the family type (FamilySymbol) to filter.
        /// If zero or negative, elements are not filtered by family type.
        /// This filter applies only to element instances, not element types.
        /// </summary>
        [JsonProperty("filterFamilySymbolId")]
        public int FilterFamilySymbolId { get; set; } = -1;
        /// <summary>
        /// Gets or sets whether to include element types, such as wall types and door types.
        /// </summary>
        [JsonProperty("includeTypes")]
        public bool IncludeTypes { get; set; } = false;
        /// <summary>
        /// Gets or sets whether to include element instances, such as placed walls and doors.
        /// </summary>
        [JsonProperty("includeInstances")]
        public bool IncludeInstances { get; set; } = true;
        /// <summary>
        /// Gets or sets whether to return only elements visible in the current view.
        /// This filter applies only to element instances, not element types.
        /// </summary>
        [JsonProperty("filterVisibleInCurrentView")]
        public bool FilterVisibleInCurrentView { get; set; }
        /// <summary>
        /// Gets or sets the minimum point of the spatial filter, in millimeters.
        /// When this value and BoundingBoxMax are set, elements intersecting the bounding box are returned.
        /// </summary>
        [JsonProperty("boundingBoxMin")]
        public JZPoint BoundingBoxMin { get; set; } = null;
        /// <summary>
        /// Gets or sets the maximum point of the spatial filter, in millimeters.
        /// When this value and BoundingBoxMin are set, elements intersecting the bounding box are returned.
        /// </summary>
        [JsonProperty("boundingBoxMax")]
        public JZPoint BoundingBoxMax { get; set; } = null;
        /// <summary>
        /// Maximum number of elements to return
        /// </summary>
        [JsonProperty("maxElements")]
        public int MaxElements { get; set; } = 50; 
        /// <summary>
        /// Validates the filter settings and checks for potential conflicts.
        /// </summary>
        /// <returns>True if the settings are valid; otherwise, false.</returns>
        public bool Validate(out string errorMessage)
        {
            errorMessage = null;

            // Ensure that at least one element kind is included
            if (!IncludeTypes && !IncludeInstances)
            {
                errorMessage = "Invalid filter settings: include at least one of element types or element instances.";
                return false;
            }

            // Ensure that at least one filter criterion is specified
            if (string.IsNullOrWhiteSpace(FilterCategory) &&
                string.IsNullOrWhiteSpace(FilterElementType) &&
                FilterFamilySymbolId <= 0)
            {
                errorMessage = "Invalid filter settings: specify at least one filter criterion (category, element type, or family type).";
                return false;
            }

            // Check for filters that conflict with element types
            if (IncludeTypes && !IncludeInstances)
            {
                List<string> invalidFilters = new List<string>();
                if (FilterFamilySymbolId > 0)
                    invalidFilters.Add("family instance filter");
                if (FilterVisibleInCurrentView)
                    invalidFilters.Add("view visibility filter");
                if (invalidFilters.Count > 0)
                {
                    errorMessage = $"The following filters do not apply when filtering only element types: {string.Join(", ", invalidFilters)}";
                    return false;
                }
            }
            // Validate the spatial filter
            if (BoundingBoxMin != null && BoundingBoxMax != null)
            {
                // Ensure that the minimum coordinates do not exceed the maximum coordinates
                if (BoundingBoxMin.X > BoundingBoxMax.X ||
                    BoundingBoxMin.Y > BoundingBoxMax.Y ||
                    BoundingBoxMin.Z > BoundingBoxMax.Z)
                {
                    errorMessage = "Invalid spatial filter settings: each minimum coordinate must be less than or equal to its maximum coordinate.";
                    return false;
                }
            }
            else if (BoundingBoxMin != null || BoundingBoxMax != null)
            {
                errorMessage = "Invalid spatial filter settings: both minimum and maximum points must be specified.";
                return false;
            }
            return true;
        }
    }
}
