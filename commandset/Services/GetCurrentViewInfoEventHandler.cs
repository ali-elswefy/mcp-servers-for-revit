using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Services
{
    public class GetCurrentViewInfoEventHandler : QueuedExternalEventHandler<string, CurrentViewInfo>
    {
        private const string CommandName = "get_current_view_info";

        // The request is the JSON-RPC request ID, used only for error correlation.
        protected override CurrentViewInfo Handle(UIApplication app, string requestId)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                throw CommandErrors.NoActiveDocument(CommandName, requestId);

            var activeView = doc.ActiveView;
            if (activeView == null)
                throw CommandErrors.NoActiveView(CommandName, requestId);

            return new CurrentViewInfo
            {
#if REVIT2024_OR_GREATER
                Id = (int)activeView.Id.Value,
#else
                Id = activeView.Id.IntegerValue,
#endif
                UniqueId = activeView.UniqueId,
                Name = activeView.Name,
                ViewType = activeView.ViewType.ToString(),
                IsTemplate = activeView.IsTemplate,
                Scale = activeView.Scale,
                DetailLevel = activeView.DetailLevel.ToString(),
            };
        }

        public override string GetName()
        {
            return "Get Current View Information";
        }
    }
}
