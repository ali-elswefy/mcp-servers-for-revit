using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPCommandSet.Utils.ExternalEvents;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Access
{
    public class GetAvailableFamilyTypesCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private GetAvailableFamilyTypesEventHandler _handler => (GetAvailableFamilyTypesEventHandler)Handler;

        public override string CommandName => "get_available_family_types";

        public GetAvailableFamilyTypesCommand(UIApplication uiApp)
            : base(new GetAvailableFamilyTypesEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    // Parse parameters
                    List<string> categoryList = parameters?["categoryList"]?.ToObject<List<string>>() ?? new List<string>();
                    string familyNameFilter = parameters?["familyNameFilter"]?.Value<string>();
                    int? limit = parameters?["limit"]?.Value<int>();

                    // Set query parameters
                    _handler.CategoryList = categoryList;
                    _handler.FamilyNameFilter = familyNameFilter;
                    _handler.Limit = limit;

                    // Raise the external event and wait up to 15 seconds for completion
                    if (RaiseAndWaitForCompletion(15000))
                    {
                        if (_handler.Error != null)
                            throw CommandErrors.Execution(CommandName, requestId, $"Failed to retrieve available family types: {_handler.Error.Message}", _handler.Error);
                        return _handler.ResultFamilyTypes;
                    }
                    else
                    {
                        throw new TimeoutException("Retrieving available family types timed out.");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to retrieve available family types: {ex.Message}", ex);
                }
            }
        }
    }
}
