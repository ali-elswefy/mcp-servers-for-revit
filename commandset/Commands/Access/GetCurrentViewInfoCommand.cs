using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Commands.Access
{
    public class GetCurrentViewInfoCommand : QueuedExternalEventCommandBase<string, CurrentViewInfo>
    {
        public override string CommandName => "get_current_view_info";

        protected override bool MayModifyModel => false;

        public GetCurrentViewInfoCommand(UIApplication uiApp)
            : base(new GetCurrentViewInfoEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            int timeoutMs = CommandTimeouts.Resolve(CommandName, requestId, CommandTimeouts.CurrentViewInfoDefaultMs, parameters);
            return RunOnRevitThread(requestId, requestId, timeoutMs);
        }
    }
}
