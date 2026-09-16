using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class CreateLineElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
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
        /// Creation data (input)
        /// </summary>
        public List<LineElement> CreatedInfo { get; private set; }
        /// <summary>
        /// Execution result (output)
        /// </summary>
        public AIResult<List<int>> Result { get; private set; }
        private List<string> _warnings = new List<string>();

        public string _wallName = "Generic - ";
        public string _ductName = "Rectangular Duct - ";

        /// <summary>
        /// Sets the creation parameters
        /// </summary>
        public void SetParameters(List<LineElement> data)
        {
            CreatedInfo = data;
            _resetEvent.Reset();
        }
        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                var elementIds = new List<int>();
                _warnings.Clear();
                foreach (var data in CreatedInfo)
                {
                    int requestedTypeId = data.TypeId;

                    // Step 0: Get the element type
                    BuiltInCategory builtInCategory = BuiltInCategory.INVALID;
                    Enum.TryParse(data.Category.Replace(".", ""), true, out builtInCategory);

                    // Step 1: Get levels and offsets
                    Level baseLevel = null;
                    Level topLevel = null;
                    double topOffset = -1;  // ft
                    double baseOffset = -1; // ft
                    baseLevel = doc.FindNearestLevel(data.BaseLevel / 304.8);
                    baseOffset = (data.BaseOffset + data.BaseLevel) / 304.8 - baseLevel.Elevation;
                    topLevel = doc.FindNearestLevel((data.BaseLevel + data.BaseOffset + data.Height) / 304.8);
                    topOffset = (data.BaseLevel + data.BaseOffset + data.Height) / 304.8 - topLevel.Elevation;
                    if (baseLevel == null)
                        continue;

                    // Step 2: Get the family type
                    FamilySymbol symbol = null;
                    WallType wallType = null;
                    DuctType ductType = null;

                    if (data.TypeId != -1 && data.TypeId != 0)
                    {
                        ElementId typeELeId = new ElementId(data.TypeId);
                        if (typeELeId != null)
                        {
                            Element typeEle = doc.GetElement(typeELeId);
                            if (typeEle != null && typeEle is FamilySymbol)
                            {
                                symbol = typeEle as FamilySymbol;
                                // Get the symbol category and convert it to BuiltInCategory
                                builtInCategory = (BuiltInCategory)symbol.Category.Id.GetIntValue();
                            }
                            else if (typeEle != null && typeEle is WallType)
                            {
                                wallType = typeEle as WallType;
                                builtInCategory = (BuiltInCategory)wallType.Category.Id.GetIntValue();
                            }
                            else if (typeEle != null && typeEle is DuctType)
                            {
                                ductType = typeEle as DuctType;
                                builtInCategory = (BuiltInCategory)ductType.Category.Id.GetIntValue();
                            }
                        }
                    }
                    if (builtInCategory == BuiltInCategory.INVALID)
                        continue;
                    switch (builtInCategory)
                    {
                        case BuiltInCategory.OST_Walls:
                            if (wallType == null)
                            {
                                // Requested typeId was invalid or not provided, fall back to first available
                                wallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault();
                                if (wallType == null)
                                {
                                    _warnings.Add($"No wall types available in project.");
                                    continue;
                                }
                                if (requestedTypeId != -1 && requestedTypeId != 0)
                                {
                                    _warnings.Add($"Requested wall typeId {requestedTypeId} not found. Defaulted to '{wallType.Name}' (ID: {wallType.Id.GetValue()})");
                                }
                            }
                            break;
                        case BuiltInCategory.OST_DuctCurves:
                            if (ductType == null)
                            {
                                // Requested typeId was invalid or not provided, fall back to first available rectangular duct
                                ductType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(DuctType))
                                    .Cast<DuctType>()
                                    .FirstOrDefault(d => d.Shape == ConnectorProfileType.Rectangular);
                                if (ductType == null)
                                {
                                    _warnings.Add($"No rectangular duct types available in project.");
                                    continue;
                                }
                                if (requestedTypeId != -1 && requestedTypeId != 0)
                                {
                                    _warnings.Add($"Requested duct typeId {requestedTypeId} not found. Defaulted to '{ductType.Name}' (ID: {ductType.Id.GetValue()})");
                                }
                            }
                            break;
                        default:
                            if (symbol == null)
                            {
                                symbol = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FamilySymbol))
                                    .OfCategory(builtInCategory)
                                    .Cast<FamilySymbol>()
                                    .FirstOrDefault(fs => fs.IsActive); // Use an active type as the default
                                if (symbol == null)
                                {
                                    symbol = new FilteredElementCollector(doc)
                                    .OfClass(typeof(FamilySymbol))
                                    .OfCategory(builtInCategory)
                                    .Cast<FamilySymbol>()
                                    .FirstOrDefault();
                                }
                                if (symbol == null)
                                {
                                    _warnings.Add($"No family types available for category {builtInCategory}.");
                                    continue;
                                }
                                if (requestedTypeId != -1 && requestedTypeId != 0)
                                {
                                    _warnings.Add($"Requested typeId {requestedTypeId} not found. Defaulted to '{symbol.FamilyName}: {symbol.Name}' (ID: {symbol.Id.GetValue()})");
                                }
                            }
                            break;
                    }

                    // Step 3: Call the general method to create a family instance
                    using (Transaction transaction = new Transaction(doc, "Create Line-Based Element"))
                    {
                        transaction.Start();
                        switch (builtInCategory)
                        {
                            case BuiltInCategory.OST_Walls:
                                Wall wall = null;
                                wall = Wall.Create
                                (
                                  doc,
                                  JZLine.ToLine(data.LocationLine),
                                  wallType.Id,
                                  baseLevel.Id,
                                  data.Height / 304.8,
                                  baseOffset,
                                  false,
                                  false
                                );
                                if (wall != null)
                                {
                                    elementIds.Add(wall.Id.GetIntValue());
                                }
                                break;
                            case BuiltInCategory.OST_DuctCurves:
                                Duct duct = null;
                                // Get the required MEP system type
                                MEPSystemType mepSystemType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(MEPSystemType))
                                    .Cast<MEPSystemType>()
                                    .FirstOrDefault(m => m.SystemClassification == MEPSystemClassification.SupplyAir);

                                if (mepSystemType != null)
                                {
                                    duct = Duct.Create(
                                        doc,
                                        mepSystemType.Id,
                                        ductType.Id,
                                        baseLevel.Id,
                                        JZLine.ToLine(data.LocationLine).GetEndPoint(0),
                                        JZLine.ToLine(data.LocationLine).GetEndPoint(1)
                                    );

                                    if (duct != null)
                                    {
                                        // Set the elevation offset
                                        Parameter offsetParam = duct.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
                                        if (offsetParam != null)
                                            offsetParam.Set(baseOffset);
                                        elementIds.Add(duct.Id.GetIntValue());
                                    }
                                }
                                break;
                            default:
                                if (!symbol.IsActive)
                                    symbol.Activate();

                                // Call the general FamilyInstance creation method
                                var instance = doc.CreateInstance(symbol, null, JZLine.ToLine(data.LocationLine), baseLevel, topLevel, baseOffset, topOffset);
                                if (instance != null)
                                {
                                    elementIds.Add(instance.Id.GetIntValue());
                                }
                                break;
                        }
                        //doc.Refresh();
                        transaction.Commit();
                    }
                }
                string message = $"Successfully created {elementIds.Count} element(s).";
                if (_warnings.Count > 0)
                {
                    message += "\n\n⚠ Warnings:\n  • " + string.Join("\n  • ", _warnings);
                }
                Result = new AIResult<List<int>>
                {
                    Success = true,
                    Message = message,
                    Response = elementIds,
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<int>>
                {
                    Success = false,
                    Message = $"Error creating line-based element: {ex.Message}",
                };
                TaskDialog.Show("Error", $"Error creating line-based element: {ex.Message}");
            }
            finally
            {
                _resetEvent.Set(); // Notify the waiting thread that the operation is complete
            }
        }

        /// <summary>
        /// Waits for creation to complete
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
            return "Create Line-Based Element";
        }

        /// <summary>
        /// Creates or gets a wall type with the specified thickness
        /// </summary>
        /// <param name="doc">Revit document</param>
        /// <param name="width">Width in feet</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private WallType CreateOrGetWallType(Document doc, double width = 200 / 304.8)
        {
            // First look for a wall type with the specified thickness
            string legacyTypeName = $"\u5E38\u89C4 - {width * 304.8}mm";
            WallType existingType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(w => w.Name == $"{_wallName}{width * 304.8}mm" ||
                                                         w.Name == legacyTypeName);
            if (existingType != null)
                return existingType;

            // Create a new wall type from a basic wall type if none exists
            WallType baseWallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(w => w.Name.Contains("Generic") ||
                                                         w.Name.Contains("\u5E38\u89C4")); ;
            if (baseWallType == null)
            {
                baseWallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(); ;
            }

            if (baseWallType == null)
                throw new InvalidOperationException("No suitable base wall type was found.");

            // Duplicate the wall type
            WallType newWallType = null;
            newWallType = baseWallType.Duplicate($"{_wallName}{width * 304.8}mm") as WallType;

            // Set the wall thickness
            CompoundStructure cs = newWallType.GetCompoundStructure();
            if (cs != null)
            {
                // Get the original layer's material ID
                ElementId materialId = cs.GetLayers().First().MaterialId;

                // Create a new single-layer structure
                CompoundStructureLayer newLayer = new CompoundStructureLayer(
                    width,  // Width in feet
                    MaterialFunctionAssignment.Structure,  // Function assignment
                    materialId  // Material ID
                );

                // Create a new compound structure
                IList<CompoundStructureLayer> newLayers = new List<CompoundStructureLayer> { newLayer };
                cs.SetLayers(newLayers);

                // Apply the new compound structure
                newWallType.SetCompoundStructure(cs);
            }
            return newWallType;
        }

        /// <summary>
        /// Creates or gets a duct type with the specified dimensions
        /// </summary>
        /// <param name="doc">Revit document</param>
        /// <param name="width">Width in feet</param>
        /// <param name="height">Height in feet</param>
        /// <returns>Duct type</returns>
        private DuctType CreateOrGetDuctType(Document doc, double width, double height)
        {
            string typeName = $"{_ductName}{width * 304.8}x{height * 304.8}mm";
            string legacyTypeName = $"\u77E9\u5F62\u98CE\u7BA1 - {width * 304.8}x{height * 304.8}mm";

            // First look for a duct type with the specified dimensions
            DuctType existingType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(DuctType))
                                    .Cast<DuctType>()
                                    .FirstOrDefault(d => (d.Name == typeName || d.Name == legacyTypeName) &&
                                                         d.Shape == ConnectorProfileType.Rectangular);

            if (existingType != null)
                return existingType;

            // Create a new duct type from an existing rectangular type if none exists
            DuctType baseDuctType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(DuctType))
                                    .Cast<DuctType>()
                                    .FirstOrDefault(d => d.Shape == ConnectorProfileType.Rectangular);

            if (baseDuctType == null)
                throw new InvalidOperationException("No suitable base rectangular duct type was found.");

            // Duplicate the duct type
            DuctType newDuctType = baseDuctType.Duplicate(typeName) as DuctType;

            // Set the duct dimension parameters
            Parameter widthParam = newDuctType.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            Parameter heightParam = newDuctType.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

            if (widthParam != null && heightParam != null)
            {
                widthParam.Set(width);
                heightParam.Set(height);
            }

            return newDuctType;
        }

    }
}
