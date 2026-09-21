using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Services
{
    public class SetSelectedElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public List<long> ElementIds { get; private set; } = new List<long>();
        public List<long> SelectedElementIds { get; private set; } = new List<long>();
        public int SelectedCount { get; private set; }
        public bool IsSuccess { get; private set; }
        public string ErrorMessage { get; private set; }

        public void SetElementIds(List<long> elementIds)
        {
            ElementIds = elementIds ?? new List<long>();
            SelectedElementIds = new List<long>();
            SelectedCount = 0;
            IsSuccess = false;
            ErrorMessage = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                UIDocument uiDoc = app?.ActiveUIDocument;
                if (uiDoc == null || uiDoc.Document == null)
                {
                    throw new InvalidOperationException("There is no active Revit document.");
                }

                Document document = uiDoc.Document;
                List<ElementId> validElementIds = new List<ElementId>();
                List<long> invalidElementIds = new List<long>();

                foreach (long requestedId in (ElementIds ?? new List<long>()).Distinct())
                {
#if REVIT2024_OR_GREATER
                    ElementId elementId = new ElementId(requestedId);
#else
                    if (requestedId < int.MinValue || requestedId > int.MaxValue)
                    {
                        invalidElementIds.Add(requestedId);
                        continue;
                    }

                    ElementId elementId = new ElementId((int)requestedId);
#endif

                    if (document.GetElement(elementId) == null)
                    {
                        invalidElementIds.Add(requestedId);
                        continue;
                    }

                    if (!validElementIds.Contains(elementId))
                    {
                        validElementIds.Add(elementId);
                    }
                }

                if (invalidElementIds.Count > 0)
                {
                    throw new ArgumentException(
                        $"The following element IDs do not exist in the active document: {string.Join(", ", invalidElementIds)}");
                }

                uiDoc.Selection.SetElementIds(validElementIds);
                SelectedElementIds = validElementIds.Select(GetElementIdValue).ToList();
                SelectedCount = SelectedElementIds.Count;
                IsSuccess = true;
            }
            catch (Exception ex)
            {
                IsSuccess = false;
                ErrorMessage = ex.Message;
                SelectedElementIds = new List<long>();
                SelectedCount = 0;
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public string GetName()
        {
            return "Set Selected Elements";
        }

        private static long GetElementIdValue(ElementId elementId)
        {
#if REVIT2024_OR_GREATER
            return elementId.Value;
#else
            return elementId.IntegerValue;
#endif
        }
    }
}
