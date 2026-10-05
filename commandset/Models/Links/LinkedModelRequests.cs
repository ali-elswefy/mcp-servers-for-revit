using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Links
{
    // Request models for the linked-model tools. Free of Revit types.

    /// <summary>Point in millimeters. Same JSON shape as JZPoint (x, y, z).</summary>
    public class MmPoint
    {
        public MmPoint() { }

        public MmPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("z")] public double Z { get; set; }
    }

    public class MmBox
    {
        [JsonProperty("min")] public MmPoint Min { get; set; }
        [JsonProperty("max")] public MmPoint Max { get; set; }

        public bool Intersects(MmBox other) =>
            Min.X <= other.Max.X && Max.X >= other.Min.X &&
            Min.Y <= other.Max.Y && Max.Y >= other.Min.Y &&
            Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;
    }

    public class LinkedModelsRequest
    {
        public string RequestId { get; set; }

        /// <summary>Count elements in each loaded link (reads every element id, so it is slower on large links).</summary>
        public bool IncludeCounts { get; set; }
    }

    public enum ParameterOperator
    {
        Equals,
        NotEquals,
        Contains,
        StartsWith,
        IsEmpty,
        HasValue
    }

    public class ParameterFilterRule
    {
        public string Name { get; set; }
        public ParameterOperator Operator { get; set; }
        public string Value { get; set; }

        /// <summary>
        /// Compares the parameter's display string, case-insensitively. A parameter that does not exist
        /// on the element only matches IsEmpty.
        /// </summary>
        public bool Matches(bool parameterExists, string displayValue)
        {
            string value = displayValue ?? string.Empty;
            switch (Operator)
            {
                case ParameterOperator.IsEmpty:
                    return !parameterExists || value.Length == 0;
                case ParameterOperator.HasValue:
                    return parameterExists && value.Length > 0;
            }

            if (!parameterExists)
                return false;

            switch (Operator)
            {
                case ParameterOperator.Equals:
                    return string.Equals(value, Value, StringComparison.OrdinalIgnoreCase);
                case ParameterOperator.NotEquals:
                    return !string.Equals(value, Value, StringComparison.OrdinalIgnoreCase);
                case ParameterOperator.Contains:
                    return value.IndexOf(Value, StringComparison.OrdinalIgnoreCase) >= 0;
                case ParameterOperator.StartsWith:
                    return value.StartsWith(Value, StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }

    public class LinkedElementQueryRequest
    {
        public string RequestId { get; set; }

        /// <summary>Restrict to one link instance by id, or by name; both null means every loaded link.</summary>
        public long? LinkInstanceId { get; set; }
        public string LinkName { get; set; }

        public List<string> Categories { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string NameContains { get; set; }
        public string LevelName { get; set; }

        /// <summary>Host-coordinate box, mm.</summary>
        public MmBox BoundingBox { get; set; }

        /// <summary>Instead of an explicit box: the bounding box of this host-model element, grown by NearDistanceMm.</summary>
        public long? NearHostElementId { get; set; }
        public double NearDistanceMm { get; set; }

        public List<ParameterFilterRule> ParameterFilters { get; set; } = new List<ParameterFilterRule>();

        /// <summary>Parameters whose values are returned with each element.</summary>
        public List<string> ParameterNames { get; set; } = new List<string>();

        public int Limit { get; set; } = DefaultLimit;
        public bool CountOnly { get; set; }

        public const int DefaultLimit = 100;
        public const int MaxLimit = 1000;

        public bool HasCriteria =>
            Categories != null || FamilyName != null || TypeName != null || NameContains != null ||
            LevelName != null || BoundingBox != null || NearHostElementId.HasValue || ParameterFilters.Count > 0;
    }
    public class LinkedElementDetailsRequest
    {
        public string RequestId { get; set; }
        public long? LinkInstanceId { get; set; }
        public string LinkName { get; set; }

        /// <summary>ElementId inside the link (needs the link to be identifiable) or the element's UniqueId.</summary>
        public long? ElementId { get; set; }
        public string UniqueId { get; set; }

        public bool IncludeTypeParameters { get; set; } = true;
        public bool IncludeRelationships { get; set; }
    }
}
