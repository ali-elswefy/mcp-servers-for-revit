using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services;
using RevitMCPCommandSet.Utils.ExternalEvents;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Delete
{
    public class DeleteElementCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private DeleteElementEventHandler _handler => (DeleteElementEventHandler)Handler;

        public override string CommandName => "delete_element";

        public DeleteElementCommand(UIApplication uiApp)
            : base(new DeleteElementEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    // Parse the array parameter
                    var elementIds = parameters?["elementIds"]?.ToObject<string[]>();
                    if (elementIds == null || elementIds.Length == 0)
                    {
                        throw new ArgumentException("The element ID list cannot be empty.");
                    }

                    // Set the array of element IDs to delete
                    _handler.ElementIds = elementIds;

                    // Raise the external event and wait for completion
                    if (RaiseAndWaitForCompletion(15000))
                    {
                        List<string> invalidIds = _handler.InvalidIds.ToList();
                        List<string> missingIds = _handler.MissingIds.ToList();
                        bool skipped = invalidIds.Count > 0 || missingIds.Count > 0;

                        if (_handler.IsSuccess)
                        {
                            return new
                            {
                                deleted = true,
                                count = _handler.DeletedCount,
                                invalidIds = invalidIds.Count > 0 ? invalidIds : null,
                                missingIds = missingIds.Count > 0 ? missingIds : null,
                                message = skipped
                                    ? $"Deleted {_handler.DeletedCount} element(s). Skipped {invalidIds.Count + missingIds.Count} ID(s) that are not valid element IDs (invalidIds) or have no element in the document (missingIds)."
                                    : null
                            };
                        }

                        if (_handler.Error == null && skipped)
                        {
                            throw CommandErrors.Validation(
                                $"{_handler.ErrorMessage} Not element IDs: [{string.Join(", ", invalidIds)}]. " +
                                $"No element with ID: [{string.Join(", ", missingIds)}].",
                                CommandName, requestId, new { invalidIds, missingIds });
                        }

                        throw CommandErrors.Execution(CommandName, requestId,
                            _handler.ErrorMessage ?? "Failed to delete elements.", _handler.Error);
                    }
                    else
                    {
                        throw new TimeoutException("The operation to delete elements timed out.");
                    }
                }
                catch (Exception ex)
                {
                    // Keep the inner exception: CommandExecutor returns a structured error found in it as is.
                    throw new Exception($"Failed to delete elements: {ex.Message}", ex);
                }
            }
        }
    }
}
