using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// Statistics for a category
    /// </summary>
    public class CategoryStatistics
    {
        [JsonProperty("categoryName")]
        public string CategoryName { get; set; }

        [JsonProperty("elementCount")]
        public int ElementCount { get; set; }

        [JsonProperty("typeCount")]
        public int TypeCount { get; set; }

        [JsonProperty("familyCount")]
        public int FamilyCount { get; set; }

        [JsonProperty("types")]
        public List<TypeStatistics> Types { get; set; } = new List<TypeStatistics>();
    }

    /// <summary>
    /// Statistics for a type
    /// </summary>
    public class TypeStatistics
    {
        [JsonProperty("typeName")]
        public string TypeName { get; set; }

        [JsonProperty("familyName")]
        public string FamilyName { get; set; }

        [JsonProperty("instanceCount")]
        public int InstanceCount { get; set; }
    }

    /// <summary>
    /// Statistics by level
    /// </summary>
    public class LevelStatistics
    {
        [JsonProperty("levelName")]
        public string LevelName { get; set; }

        [JsonProperty("elevation")]
        public double Elevation { get; set; }

        [JsonProperty("elementCount")]
        public int ElementCount { get; set; }
    }

    /// <summary>
    /// Result container for model statistics
    /// </summary>
    public class AnalyzeModelStatisticsResult
    {
        [JsonProperty("projectName")]
        public string ProjectName { get; set; }

        [JsonProperty("totalElements")]
        public int TotalElements { get; set; }

        [JsonProperty("totalTypes")]
        public int TotalTypes { get; set; }

        [JsonProperty("totalFamilies")]
        public int TotalFamilies { get; set; }

        [JsonProperty("totalViews")]
        public int TotalViews { get; set; }

        [JsonProperty("totalSheets")]
        public int TotalSheets { get; set; }

        [JsonProperty("categories")]
        public List<CategoryStatistics> Categories { get; set; } = new List<CategoryStatistics>();

        [JsonProperty("levels")]
        public List<LevelStatistics> Levels { get; set; } = new List<LevelStatistics>();

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// Parameters for analyze_model_statistics. <see cref="Categories"/> null means full-model
    /// statistics; otherwise only the listed categories are counted.
    /// </summary>
    public class ModelStatisticsRequest
    {
        public string RequestId { get; set; }
        public List<string> Categories { get; set; }
        public bool IncludeDetailedTypes { get; set; }
        public bool IncludeLevels { get; set; }
    }

    /// <summary>
    /// Compact result for a category-targeted request. Detail fields stay null (and are omitted)
    /// unless explicitly requested.
    /// </summary>
    public class CategoryCountResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("mode")]
        public string Mode { get; set; } = "categories";

        [JsonProperty("projectName")]
        public string ProjectName { get; set; }

        [JsonProperty("categories")]
        public List<CategoryCount> Categories { get; set; } = new List<CategoryCount>();

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public class CategoryCount
    {
        [JsonProperty("requestedName")]
        public string RequestedName { get; set; }

        [JsonProperty("categoryName")]
        public string CategoryName { get; set; }

        [JsonProperty("builtInCategory", NullValueHandling = NullValueHandling.Ignore)]
        public string BuiltInCategory { get; set; }

        /// <summary>Number of element instances (element types excluded).</summary>
        [JsonProperty("elementCount")]
        public int ElementCount { get; set; }

        [JsonProperty("typeCount", NullValueHandling = NullValueHandling.Ignore)]
        public int? TypeCount { get; set; }

        [JsonProperty("familyCount", NullValueHandling = NullValueHandling.Ignore)]
        public int? FamilyCount { get; set; }

        [JsonProperty("types", NullValueHandling = NullValueHandling.Ignore)]
        public List<TypeStatistics> Types { get; set; }

        [JsonProperty("levels", NullValueHandling = NullValueHandling.Ignore)]
        public List<LevelStatistics> Levels { get; set; }
    }
}
