using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Commands.Access
{
    public class GetCurrentViewElementsCommand : QueuedExternalEventCommandBase<ViewElementsRequest, ViewElementsResult>
    {
        public override string CommandName => "get_current_view_elements";

        protected override bool MayModifyModel => false;

        public GetCurrentViewElementsCommand(UIApplication uiApp)
            : base(new GetCurrentViewElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            var request = new ViewElementsRequest
            {
                RequestId = requestId,
                // Keep null (omitted) distinct from [] (explicitly empty); see GetCurrentViewElementsEventHandler.
                ModelCategoryList = ParseCategoryList(parameters, "modelCategoryList", requestId),
                AnnotationCategoryList = ParseCategoryList(parameters, "annotationCategoryList", requestId),
                IncludeHidden = parameters?["includeHidden"]?.Value<bool>() ?? false,
                Limit = parameters?["limit"]?.Value<int>() ?? 100,
                IncludeRelationships = parameters?["includeRelationships"]?.Value<bool>() ?? false
            };

            int timeoutMs = CommandTimeouts.Resolve(CommandName, requestId, CommandTimeouts.CurrentViewElementsDefaultMs, parameters);
            return RunOnRevitThread(request, requestId, timeoutMs);
        }

        private List<string> ParseCategoryList(JObject parameters, string name, string requestId)
        {
            JToken token = parameters?[name];
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (!(token is JArray array) || array.Any(item => item.Type != JTokenType.String))
                throw CommandErrors.Validation($"'{name}' must be an array of category names.", CommandName, requestId);

            return array.Select(item => item.Value<string>()).ToList();
        }
    }
}
