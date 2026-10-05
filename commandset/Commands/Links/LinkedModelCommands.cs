using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Links;
using RevitMCPCommandSet.Services.Links;
using RevitMCPCommandSet.Utils.ExternalEvents;
using RevitMCPCommandSet.Utils.Links;

namespace RevitMCPCommandSet.Commands.Links
{
    /// <summary>
    /// Lists the Revit links in the host model: load status, file path, placement and (optionally) element counts.
    /// Parameters: includeCounts (bool, default false).
    /// </summary>
    public class ListLinkedModelsCommand : QueuedExternalEventCommandBase<LinkedModelsRequest, LinkedModelsResult>
    {
        public override string CommandName => LinkedRequestParser.ListCommand;

        protected override bool MayModifyModel => false;

        public ListLinkedModelsCommand(UIApplication uiApp)
            : base(new ListLinkedModelsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            LinkedModelsRequest request = LinkedRequestParser.ParseList(parameters, requestId);
            int timeoutMs = CommandTimeouts.Resolve(CommandName, requestId, CommandTimeouts.ListLinkedModelsDefaultMs, parameters);
            return RunOnRevitThread(request, requestId, timeoutMs);
        }
    }

    /// <summary>
    /// Queries elements inside linked models. See <see cref="LinkedRequestParser.ParseQuery"/> for the parameters and
    /// docs/revit-command-contract.md for the result.
    /// </summary>
    public class QueryLinkedElementsCommand : QueuedExternalEventCommandBase<LinkedElementQueryRequest, LinkedElementQueryResult>
    {
        public override string CommandName => LinkedRequestParser.QueryCommand;

        protected override bool MayModifyModel => false;

        public QueryLinkedElementsCommand(UIApplication uiApp)
            : base(new QueryLinkedElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            LinkedElementQueryRequest request = LinkedRequestParser.ParseQuery(parameters, requestId);
            int timeoutMs = CommandTimeouts.Resolve(CommandName, requestId, CommandTimeouts.QueryLinkedElementsDefaultMs, parameters);
            return RunOnRevitThread(request, requestId, timeoutMs);
        }
    }

    /// <summary>
    /// Returns every parameter, location and optional relationships of one element in a linked model.
    /// </summary>
    public class GetLinkedElementDetailsCommand : QueuedExternalEventCommandBase<LinkedElementDetailsRequest, LinkedElementDetailsResult>
    {
        public override string CommandName => LinkedRequestParser.DetailsCommand;

        protected override bool MayModifyModel => false;

        public GetLinkedElementDetailsCommand(UIApplication uiApp)
            : base(new GetLinkedElementDetailsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            LinkedElementDetailsRequest request = LinkedRequestParser.ParseDetails(parameters, requestId);
            int timeoutMs = CommandTimeouts.Resolve(CommandName, requestId, CommandTimeouts.LinkedElementDetailsDefaultMs, parameters);
            return RunOnRevitThread(request, requestId, timeoutMs);
        }
    }
}
