namespace RevitMCPCommandSet.Models.Common
{
    public class ViewElementsResult
    {
        public long ViewId { get; set; }
        public string ViewName { get; set; }
        public int TotalElementsInView { get; set; }
        public int FilteredElementCount { get; set; }

        /// <summary>True when more elements matched than the requested limit.</summary>
        public bool Truncated { get; set; }

        /// <summary>BuiltInCategory or display names that were applied; null when no category filter was applied.</summary>
        public List<string> CategoryFilter { get; set; }

        public List<ElementInfo> Elements { get; set; } = new List<ElementInfo>();
    }

    /// <summary>
    /// Parameters for get_current_view_elements. A null list means the caller omitted it; an
    /// empty list means the caller explicitly asked for no categories from that list.
    /// </summary>
    public class ViewElementsRequest
    {
        public string RequestId { get; set; }
        public List<string> ModelCategoryList { get; set; }
        public List<string> AnnotationCategoryList { get; set; }
        public bool IncludeHidden { get; set; }
        public int Limit { get; set; }
        public bool IncludeRelationships { get; set; }
    }
}
