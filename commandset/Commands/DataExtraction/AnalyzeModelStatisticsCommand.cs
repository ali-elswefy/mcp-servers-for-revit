using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Commands.DataExtraction
{
    /// <summary>
    /// Parameters:
    /// - categories (optional string[]): omitted or null returns full-model statistics (unchanged
    ///   behavior). A non-empty list returns only those categories' instance counts. An empty list is
    ///   rejected, so "no filter" and "filter to nothing" cannot be confused.
    /// - includeDetailedTypes (optional bool): family/type breakdown. Default true for full-model
    ///   statistics, false for a category request.
    /// - includeLevels (optional bool): per-level distribution. Default true for full-model
    ///   statistics, false for a category request.
    /// </summary>
    public class AnalyzeModelStatisticsCommand : QueuedExternalEventCommandBase<ModelStatisticsRequest, object>
    {
        public override string CommandName => "analyze_model_statistics";

        protected override bool MayModifyModel => false;

        public AnalyzeModelStatisticsCommand(UIApplication uiApp)
            : base(new AnalyzeModelStatisticsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            List<string> categories = ParseCategories(parameters?["categories"], requestId);
            bool fullModel = categories == null;

            var request = new ModelStatisticsRequest
            {
                RequestId = requestId,
                Categories = categories,
                IncludeDetailedTypes = ParseBool(parameters, "includeDetailedTypes", requestId) ?? fullModel,
                IncludeLevels = ParseBool(parameters, "includeLevels", requestId) ?? fullModel
            };

            int timeoutMs = CommandTimeouts.Resolve(CommandName, requestId, CommandTimeouts.ModelStatisticsDefaultMs, parameters);
            return RunOnRevitThread(request, requestId, timeoutMs);
        }

        private List<string> ParseCategories(JToken token, string requestId)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (!(token is JArray array) || array.Any(item => item.Type != JTokenType.String || string.IsNullOrWhiteSpace(item.Value<string>())))
                throw CommandErrors.Validation("'categories' must be an array of non-empty category names.", CommandName, requestId);

            if (array.Count == 0)
            {
                throw CommandErrors.Validation(
                    "'categories' must not be empty. Omit it for full-model statistics, or list the categories to count.",
                    CommandName, requestId);
            }

            return array.Select(item => item.Value<string>()).ToList();
        }

        private bool? ParseBool(JObject parameters, string name, string requestId)
        {
            JToken token = parameters?[name];
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type != JTokenType.Boolean)
                throw CommandErrors.Validation($"'{name}' must be a boolean.", CommandName, requestId);

            return token.Value<bool>();
        }
    }
}
