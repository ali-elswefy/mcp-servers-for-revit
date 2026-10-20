using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class DeleteElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        // Execution result
        public bool IsSuccess { get; private set; }

        // Number of successfully deleted elements (including dependent elements Revit deleted with them)
        public int DeletedCount { get; private set; }

        // IDs that are not element IDs, and IDs with no element in the document; neither was deleted
        public List<string> InvalidIds { get; } = new List<string>();
        public List<string> MissingIds { get; } = new List<string>();

        // Why nothing was deleted when IsSuccess is false; returned to the caller instead of shown in a dialog
        public string ErrorMessage { get; private set; }
        public Exception Error { get; private set; }

        // State synchronization object
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        // IDs of elements to delete
        public string[] ElementIds { get; set; }
        // IWaitableExternalEventHandler implementation
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
        }
        public void Execute(UIApplication app)
        {
            IsSuccess = false;
            DeletedCount = 0;
            InvalidIds.Clear();
            MissingIds.Clear();
            ErrorMessage = null;
            Error = null;
            try
            {
                var doc = app.ActiveUIDocument.Document;
                if (ElementIds == null || ElementIds.Length == 0)
                {
                    ErrorMessage = "The element ID list is empty.";
                    return;
                }
                // Build the collection of element IDs to delete
                List<ElementId> elementIdsToDelete = new List<ElementId>();
                foreach (var idStr in ElementIds)
                {
                    if (int.TryParse(idStr, out int elementIdValue))
                    {
                        var elementId = new ElementId(elementIdValue);
                        // Check whether the element exists
                        if (doc.GetElement(elementId) != null)
                        {
                            elementIdsToDelete.Add(elementId);
                        }
                        else
                        {
                            MissingIds.Add(idStr);
                        }
                    }
                    else
                    {
                        InvalidIds.Add(idStr);
                    }
                }
                if (InvalidIds.Count > 0 || MissingIds.Count > 0)
                {
                    CommandLog.Info("delete_element skipping ids: invalid=[{0}] missing=[{1}]",
                        string.Join(", ", InvalidIds), string.Join(", ", MissingIds));
                }
                // Delete any valid elements
                if (elementIdsToDelete.Count > 0)
                {
                    using (var transaction = new Transaction(doc, "Delete Elements"))
                    {
                        transaction.Start();

                        // Delete elements in a batch
                        ICollection<ElementId> deletedIds = doc.Delete(elementIdsToDelete);

                        // Revit rolls the transaction back when the deletion raises errors (see revitFailures).
                        TransactionStatus status = transaction.Commit();
                        if (status != TransactionStatus.Committed)
                        {
                            ErrorMessage = $"Revit did not commit the deletion (transaction status: {status}); nothing was deleted.";
                            return;
                        }
                        DeletedCount = deletedIds.Count;
                    }
                    IsSuccess = true;
                }
                else
                {
                    ErrorMessage = "None of the given IDs identifies an element in the active document; nothing was deleted.";
                }
            }
            catch (Exception ex)
            {
                Error = ex;
                ErrorMessage = $"Failed to delete elements: {ex.Message}";
                CommandLog.Warning("delete_element failed (returned to the caller as an error): {0}", ex);
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }
        public string GetName()
        {
            return "Delete Elements";
        }
    }
}
