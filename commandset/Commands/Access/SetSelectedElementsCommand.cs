using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPSDK.API.Base;
using System;
using System.Collections.Generic;

namespace RevitMCPCommandSet.Commands.Access
{
    public class SetSelectedElementsCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private SetSelectedElementsEventHandler _handler => (SetSelectedElementsEventHandler)Handler;

        public override string CommandName => "set_selected_elements";

        public SetSelectedElementsCommand(UIApplication uiApp)
            : base(new SetSelectedElementsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    if (parameters == null || parameters["elementIds"] == null ||
                        parameters["elementIds"].Type == JTokenType.Null)
                    {
                        throw new ArgumentException("The 'elementIds' parameter is required.");
                    }

                    List<long> elementIds = parameters["elementIds"].ToObject<List<long>>();
                    if (elementIds == null)
                    {
                        throw new ArgumentException("The 'elementIds' parameter must be an array of element IDs.");
                    }

                    _handler.SetElementIds(elementIds);

                    if (RaiseAndWaitForCompletion(15000))
                    {
                        if (!_handler.IsSuccess)
                        {
                            throw new Exception(_handler.ErrorMessage ?? "Failed to set the selected elements.");
                        }

                        return new
                        {
                            success = true,
                            count = _handler.SelectedCount,
                            elementIds = _handler.SelectedElementIds
                        };
                    }

                    throw new TimeoutException("Setting the selected elements timed out.");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to set selected elements: {ex.Message}", ex);
                }
            }
        }
    }
}
