using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitMCPCommandSet.Models.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Services
{
    public class OperateElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;
        private Autodesk.Revit.ApplicationServices.Application app => uiApp.Application;

        /// <summary>
        /// Event wait handle
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        /// <summary>
        /// Operation data (input)
        /// </summary>
        public OperationSetting OperationData { get; private set; }
        /// <summary>
        /// Execution result (output)
        /// </summary>
        public AIResult<string> Result { get; private set; }

        /// <summary>
        /// Sets the operation parameters
        /// </summary>
        public void SetParameters(OperationSetting data)
        {
            OperationData = data;
            _resetEvent.Reset();
        }
        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                bool result = ExecuteElementOperation(uiDoc, OperationData);

                Result = new AIResult<string>
                {
                    Success = true,
                    Message = $"Operation completed successfully.",
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<string>
                {
                    Success = false,
                    Message = $"Error operating on elements: {ex.Message}",
                };
            }
            finally
            {
                _resetEvent.Set(); // Notify the waiting thread that the operation is complete
            }
        }

        /// <summary>
        /// Waits for the operation to complete
        /// </summary>
        /// <param name="timeoutMilliseconds">Timeout in milliseconds</param>
        /// <returns>Whether the operation completed before the timeout</returns>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName implementation
        /// </summary>
        public string GetName()
        {
            return "Operate on Elements";
        }

        /// <summary>
        /// Executes the element operation specified by the settings
        /// </summary>
        /// <param name="uidoc">Current UI document</param>
        /// <param name="setting">Operation settings</param>
        /// <returns>Whether the operation succeeded</returns>
        public static bool ExecuteElementOperation(UIDocument uidoc, OperationSetting setting)
        {
            // Validate parameters
            if (uidoc == null || uidoc.Document == null || setting == null || setting.ElementIds == null ||
                (setting.ElementIds.Count == 0 && setting.Action.ToLower() != "resetisolate"))
                throw new Exception("Invalid parameters: the document is null or no elements were specified.");

            Document doc = uidoc.Document;

            // Convert integer element IDs to ElementId values
            ICollection<ElementId> elementIds = setting.ElementIds.Select(id => new ElementId(id)).ToList();

            // Parse the operation type
            ElementOperationType action;
            if (!Enum.TryParse(setting.Action, true, out action))
            {
                throw new Exception($"Unsupported operation type: {setting.Action}");
            }

            // Execute the requested operation
            switch (action)
            {
                case ElementOperationType.Select:
                    // Select elements
                    uidoc.Selection.SetElementIds(elementIds);
                    return true;

                case ElementOperationType.SelectionBox:
                    // Create a section box in a 3D view

                    // Check whether the active view is a 3D view
                    View3D targetView;

                    if (doc.ActiveView is View3D)
                    {
                        // Use the active view when it is a 3D view
                        targetView = doc.ActiveView as View3D;
                    }
                    else
                    {
                        // Find a default 3D view when the active view is not 3D
                        FilteredElementCollector collector = new FilteredElementCollector(doc);
                        collector.OfClass(typeof(View3D));

                        // Try to find the default or another suitable 3D view
                        targetView = collector
                            .Cast<View3D>()
                            .FirstOrDefault(v => !v.IsTemplate && !v.IsLocked && (v.Name.Contains("{3D}") || v.Name.Contains("Default 3D")));

                        if (targetView == null)
                        {
                            // Throw if no suitable 3D view was found
                            throw new Exception("No suitable 3D view was found for creating a section box.");
                        }

                        // Activate the 3D view
                        uidoc.ActiveView = targetView;
                    }

                    // Calculate the bounding box of the selected elements
                    BoundingBoxXYZ boundingBox = null;

                    foreach (ElementId id in elementIds)
                    {
                        Element elem = doc.GetElement(id);
                        BoundingBoxXYZ elemBox = elem.get_BoundingBox(null);

                        if (elemBox != null)
                        {
                            if (boundingBox == null)
                            {
                                boundingBox = new BoundingBoxXYZ
                                {
                                    Min = new XYZ(elemBox.Min.X, elemBox.Min.Y, elemBox.Min.Z),
                                    Max = new XYZ(elemBox.Max.X, elemBox.Max.Y, elemBox.Max.Z)
                                };
                            }
                            else
                            {
                                // Expand the bounding box to include the current element
                                boundingBox.Min = new XYZ(
                                    Math.Min(boundingBox.Min.X, elemBox.Min.X),
                                    Math.Min(boundingBox.Min.Y, elemBox.Min.Y),
                                    Math.Min(boundingBox.Min.Z, elemBox.Min.Z));

                                boundingBox.Max = new XYZ(
                                    Math.Max(boundingBox.Max.X, elemBox.Max.X),
                                    Math.Max(boundingBox.Max.Y, elemBox.Max.Y),
                                    Math.Max(boundingBox.Max.Z, elemBox.Max.Z));
                            }
                        }
                    }

                    if (boundingBox == null)
                    {
                        throw new Exception("Could not create a bounding box for the selected elements.");
                    }

                    // Expand the bounding box slightly beyond the elements
                    double offset = 1.0; // One-foot offset
                    boundingBox.Min = new XYZ(boundingBox.Min.X - offset, boundingBox.Min.Y - offset, boundingBox.Min.Z - offset);
                    boundingBox.Max = new XYZ(boundingBox.Max.X + offset, boundingBox.Max.Y + offset, boundingBox.Max.Z + offset);

                    // Enable and set the section box in the 3D view
                    using (Transaction trans = new Transaction(doc, "Create Section Box"))
                    {
                        trans.Start();
                        targetView.IsSectionBoxActive = true;
                        targetView.SetSectionBox(boundingBox);
                        trans.Commit();
                    }

                    // Center the view on the elements
                    uidoc.ShowElements(elementIds);
                    return true;

                case ElementOperationType.SetColor:
                    // Set the elements to the specified color
                    using (Transaction trans = new Transaction(doc, "Set Element Color"))
                    {
                        trans.Start();
                        SetElementsColor(doc, elementIds, setting.ColorValue);
                        trans.Commit();
                    }
                    // Bring the elements into view
                    uidoc.ShowElements(elementIds);
                    return true;


                case ElementOperationType.SetTransparency:
                    // Set element transparency in the active view
                    using (Transaction trans = new Transaction(doc, "Set Element Transparency"))
                    {
                        trans.Start();

                        // Create graphic override settings
                        OverrideGraphicSettings overrideSettings = new OverrideGraphicSettings();

                        // Clamp transparency to the 0-100 range
                        int transparencyValue = Math.Max(0, Math.Min(100, setting.TransparencyValue));

                        // Set surface transparency
                        overrideSettings.SetSurfaceTransparency(transparencyValue);

                        // Apply transparency to each element
                        foreach (ElementId id in elementIds)
                        {
                            doc.ActiveView.SetElementOverrides(id, overrideSettings);
                        }

                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Delete:
                    // Delete elements in a transaction
                    using (Transaction trans = new Transaction(doc, "Delete Elements"))
                    {
                        trans.Start();
                        doc.Delete(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Hide:
                    // Hide elements in the active view within a transaction
                    using (Transaction trans = new Transaction(doc, "Hide Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.HideElements(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.TempHide:
                    // Temporarily hide elements in the active view within a transaction
                    using (Transaction trans = new Transaction(doc, "Temporarily Hide Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.HideElementsTemporary(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Isolate:
                    // Isolate elements in the active view within a transaction
                    using (Transaction trans = new Transaction(doc, "Isolate Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.IsolateElementsTemporary(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.Unhide:
                    // Unhide elements in the active view within a transaction
                    using (Transaction trans = new Transaction(doc, "Unhide Elements"))
                    {
                        trans.Start();
                        doc.ActiveView.UnhideElements(elementIds);
                        trans.Commit();
                    }
                    return true;

                case ElementOperationType.ResetIsolate:
                    // Reset temporary isolation in the active view within a transaction
                    using (Transaction trans = new Transaction(doc, "Reset Isolation"))
                    {
                        trans.Start();
                        doc.ActiveView.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                        trans.Commit();
                    }
                    return true;

                default:
                    throw new Exception($"Unsupported operation type: {setting.Action}");
            }
        }

        /// <summary>
        /// Sets the specified elements to a color in the active view
        /// </summary>
        /// <param name="doc">Document</param>
        /// <param name="elementIds">IDs of the elements to color</param>
        /// <param name="elementColor">RGB color value</param>
        private static void SetElementsColor(Document doc, ICollection<ElementId> elementIds, int[] elementColor)
        {
            // Validate the color array
            if (elementColor == null || elementColor.Length < 3)
            {
                elementColor = new int[] { 255, 0, 0 }; // Default to red
            }
            // Clamp RGB values to the 0-255 range
            int r = Math.Max(0, Math.Min(255, elementColor[0]));
            int g = Math.Max(0, Math.Min(255, elementColor[1]));
            int b = Math.Max(0, Math.Min(255, elementColor[2]));
            // Create a Revit color using byte values
            Color color = new Color((byte)r, (byte)g, (byte)b);
            // Create graphic override settings
            OverrideGraphicSettings overrideSettings = new OverrideGraphicSettings();
            // Set the requested color
            overrideSettings.SetProjectionLineColor(color);
            overrideSettings.SetCutLineColor(color);
            overrideSettings.SetSurfaceForegroundPatternColor(color);
            overrideSettings.SetSurfaceBackgroundPatternColor(color);

            // Try to set the fill pattern
            try
            {
                // Try to get the default fill pattern
                FilteredElementCollector patternCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement));

                // Prefer a solid fill pattern
                FillPatternElement solidPattern = patternCollector
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);

                if (solidPattern != null)
                {
                    overrideSettings.SetSurfaceForegroundPatternId(solidPattern.Id);
                    overrideSettings.SetSurfaceForegroundPatternVisible(true);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to set the fill pattern: {ex.Message}");
            }

            // Apply the override settings to each element
            foreach (ElementId id in elementIds)
            {
                doc.ActiveView.SetElementOverrides(id, overrideSettings);
            }
        }

    }
}
