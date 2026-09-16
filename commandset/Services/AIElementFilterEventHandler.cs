using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace RevitMCPCommandSet.Services
{
    public class AIElementFilterEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;
        private Autodesk.Revit.ApplicationServices.Application app => uiApp.Application;
        /// <summary>
        /// Event wait handle.
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        /// <summary>
        /// Input filter settings.
        /// </summary>
        public FilterSetting FilterSetting { get; private set; }
        /// <summary>
        /// Execution result.
        /// </summary>
        public AIResult<List<object>> Result { get; private set; }

        /// <summary>
        /// Sets the filter parameters.
        /// </summary>
        public void SetParameters(FilterSetting data)
        {
            FilterSetting = data;
            _resetEvent.Reset();
        }
        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                var elementInfoList = new List<object>();
                // Validate the filter settings.
                if (!FilterSetting.Validate(out string errorMessage))
                    throw new Exception(errorMessage);
                // Get elements matching the specified criteria.
                var elementList = GetFilteredElements(doc, FilterSetting);
                if (elementList == null || !elementList.Any())
                    throw new Exception("No matching elements were found in the project. Check the filter settings.");
                // Enforce the maximum result count.
                string message = "";
                if (FilterSetting.MaxElements > 0)
                {
                    if (elementList.Count > FilterSetting.MaxElements)
                    {
                        elementList = elementList.Take(FilterSetting.MaxElements).ToList();
                        message = $" In addition, {elementList.Count} elements match the filter; only the first {FilterSetting.MaxElements} are shown.";
                    }
                }

                // Get information for the matching elements.
                elementInfoList = GetElementFullInfo(doc, elementList);

                Result = new AIResult<List<object>>
                {
                    Success = true,
                    Message = $"Successfully retrieved information for {elementInfoList.Count} elements. Details are stored in the Response property."+ message,
                    Response = elementInfoList,
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<object>>
                {
                    Success = false,
                    Message = $"Error retrieving element information: {ex.Message}",
                };
            }
            finally
            {
                _resetEvent.Set(); // Notify the waiting thread that the operation is complete.
            }
        }

        /// <summary>
        /// Waits for the operation to complete.
        /// </summary>
        /// <param name="timeoutMilliseconds">Timeout in milliseconds.</param>
        /// <returns>Whether the operation completed before the timeout.</returns>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// Implements IExternalEventHandler.GetName.
        /// </summary>
        public string GetName()
        {
            return "Get Element Information";
        }

        /// <summary>
        /// Gets elements from a Revit document that match all filter settings.
        /// </summary>
        /// <param name="doc">The Revit document.</param>
        /// <param name="settings">The filter settings.</param>
        /// <returns>Elements matching all filter conditions.</returns>
        public static IList<Element> GetFilteredElements(Document doc, FilterSetting settings)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            // Validate the filter settings.
            if (!settings.Validate(out string errorMessage))
            {
                System.Diagnostics.Trace.WriteLine($"Invalid filter settings: {errorMessage}");
                return new List<Element>();
            }
            // Track applied filter conditions.
            List<string> appliedFilters = new List<string>();
            List<Element> result = new List<Element>();
            // Filter types and instances separately, then combine the results.
            if (settings.IncludeTypes && settings.IncludeInstances)
            {
                // Collect element types.
                result.AddRange(GetElementsByKind(doc, settings, true, appliedFilters));

                // Collect element instances.
                result.AddRange(GetElementsByKind(doc, settings, false, appliedFilters));
            }
            else if (settings.IncludeInstances)
            {
                // Collect only element instances.
                result = GetElementsByKind(doc, settings, false, appliedFilters);
            }
            else if (settings.IncludeTypes)
            {
                // Collect only element types.
                result = GetElementsByKind(doc, settings, true, appliedFilters);
            }

            // Log applied filters.
            if (appliedFilters.Count > 0)
            {
                System.Diagnostics.Trace.WriteLine($"Applied {appliedFilters.Count} filter conditions: {string.Join(", ", appliedFilters)}");
                System.Diagnostics.Trace.WriteLine($"Final filter result: {result.Count} elements found");
            }
            return result;

        }

        /// <summary>
        /// Gets matching elements by kind (type or instance).
        /// </summary>
        private static List<Element> GetElementsByKind(Document doc, FilterSetting settings, bool isElementType, List<string> appliedFilters)
        {
            // Create the base FilteredElementCollector.
            FilteredElementCollector collector;
            // Restrict instances to elements visible in the active view when requested.
            if (!isElementType && settings.FilterVisibleInCurrentView && doc.ActiveView != null)
            {
                collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
                appliedFilters.Add("Elements visible in the active view");
            }
            else
            {
                collector = new FilteredElementCollector(doc);
            }
            // Filter by element kind.
            if (isElementType)
            {
                collector = collector.WhereElementIsElementType();
                appliedFilters.Add("Element types only");
            }
            else
            {
                collector = collector.WhereElementIsNotElementType();
                appliedFilters.Add("Element instances only");
            }
            // Build the filter list.
            List<ElementFilter> filters = new List<ElementFilter>();
            // 1. Category filter.
            if (!string.IsNullOrWhiteSpace(settings.FilterCategory))
            {
                BuiltInCategory category;
                if (!Enum.TryParse(settings.FilterCategory, true, out category))
                {
                    throw new ArgumentException($"'{settings.FilterCategory}' is not a valid Revit category.");
                }
                ElementCategoryFilter categoryFilter = new ElementCategoryFilter(category);
                filters.Add(categoryFilter);
                appliedFilters.Add($"Category: {settings.FilterCategory}");
            }
            // 2. Element class filter.
            if (!string.IsNullOrWhiteSpace(settings.FilterElementType))
            {

                Type elementType = null;
                // Try the supported forms of the type name.
                string[] possibleTypeNames = new string[]
                {
                    settings.FilterElementType,                                    // Original input.
                    $"Autodesk.Revit.DB.{settings.FilterElementType}, RevitAPI",  // Revit API namespace.
                    $"{settings.FilterElementType}, RevitAPI"                      // Assembly-qualified form.
                };
                foreach (string typeName in possibleTypeNames)
                {
                    elementType = Type.GetType(typeName);
                    if (elementType != null)
                        break;
                }
                if (elementType != null)
                {
                    ElementClassFilter classFilter = new ElementClassFilter(elementType);
                    filters.Add(classFilter);
                    appliedFilters.Add($"Element type: {elementType.Name}");
                }
                else
                {
                    throw new Exception($"Warning: Type '{settings.FilterElementType}' was not found");
                }
            }
            // 3. Family symbol filter (instances only).
            if (!isElementType && settings.FilterFamilySymbolId > 0)
            {
                ElementId symbolId = new ElementId(settings.FilterFamilySymbolId);
                // Verify that the element exists and is a family symbol.
                Element symbolElement = doc.GetElement(symbolId);
                if (symbolElement != null && symbolElement is FamilySymbol)
                {
                    FamilyInstanceFilter familyFilter = new FamilyInstanceFilter(doc, symbolId);
                    filters.Add(familyFilter);
                    // Add detailed family information to the filter log.
                    FamilySymbol symbol = symbolElement as FamilySymbol;
                    string familyName = symbol.Family?.Name ?? "Unknown family";
                    string symbolName = symbol.Name ?? "Unknown type";
                    appliedFilters.Add($"Family type: {familyName} - {symbolName} (ID: {settings.FilterFamilySymbolId})");
                }
                else
                {
                    string elementType = symbolElement != null ? symbolElement.GetType().Name : "Not found";
                    System.Diagnostics.Trace.WriteLine($"Warning: Element ID {settings.FilterFamilySymbolId} {(symbolElement == null ? "does not exist" : "is not a valid FamilySymbol")} (actual type: {elementType})");
                }
            }
            // 4. Spatial bounds filter.
            if (settings.BoundingBoxMin != null && settings.BoundingBoxMax != null)
            {
                // Convert millimeters to Revit internal XYZ coordinates.
                XYZ minXYZ = JZPoint.ToXYZ(settings.BoundingBoxMin);
                XYZ maxXYZ = JZPoint.ToXYZ(settings.BoundingBoxMax);
                // Create the spatial Outline.
                Outline outline = new Outline(minXYZ, maxXYZ);
                // Create the intersection filter.
                BoundingBoxIntersectsFilter boundingBoxFilter = new BoundingBoxIntersectsFilter(outline);
                filters.Add(boundingBoxFilter);
                appliedFilters.Add($"Spatial bounds: Min({settings.BoundingBoxMin.X:F2}, {settings.BoundingBoxMin.Y:F2}, {settings.BoundingBoxMin.Z:F2}), " +
                                  $"Max({settings.BoundingBoxMax.X:F2}, {settings.BoundingBoxMax.Y:F2}, {settings.BoundingBoxMax.Z:F2}) mm");
            }
            // Apply the combined filter.
            if (filters.Count > 0)
            {
                ElementFilter combinedFilter = filters.Count == 1
                    ? filters[0]
                    : new LogicalAndFilter(filters);
                collector = collector.WherePasses(combinedFilter);
                if (filters.Count > 1)
                {
                    System.Diagnostics.Trace.WriteLine($"Applied a combined filter with {filters.Count} conditions (logical AND)");
                }
            }
            return collector.ToElements().ToList();
        }

        /// <summary>
        /// Gets model element information.
        /// </summary>
        public static List<object> GetElementFullInfo(Document doc, IList<Element> elementCollector)
        {
            List<object> infoList = new List<object>();

            // Process each element.
            foreach (var element in elementCollector)
            {
                // Get information for physical model instances.
                if (element?.Category?.HasMaterialQuantities ?? false)
                {
                    var info = CreateElementFullInfo(doc, element);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
                // Get element type information.
                else if (element is ElementType elementType)
                {
                    var info = CreateTypeFullInfo(doc, elementType);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
                // 3. Datum elements (common).
                else if (element is Level || element is Grid)
                {
                    var info = CreatePositioningElementInfo(doc, element);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
                // 4. Spatial elements (fairly common).
                else if (element is SpatialElement) // Room, Area, etc.
                {
                    var info = CreateSpatialElementInfo(doc, element);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
                // 5. View elements (common).
                else if (element is View)
                {
                    var info = CreateViewInfo(doc, element);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
                // 6. Annotation elements (moderately common).
                else if (element is TextNote || element is Dimension ||
                         element is IndependentTag || element is AnnotationSymbol ||
                         element is SpotDimension)
                {
                    var info = CreateAnnotationInfo(doc, element);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
                // 7. Groups and links.
                else if (element is Group || element is RevitLinkInstance)
                {
                    var info = CreateGroupOrLinkInfo(doc, element);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
                // 8. Basic element information as a fallback.
                else
                {
                    var info = CreateElementBasicInfo(doc, element);
                    if (info != null)
                    {
                        infoList.Add(info);
                    }
                }
            }

            return infoList;
        }

        /// <summary>
        /// Creates a complete ElementInfo object for one element.
        /// </summary>
        public static ElementInstanceInfo CreateElementFullInfo(Document doc, Element element)
        {
            try
            {
                if (element?.Category == null)
                    return null;

                ElementInstanceInfo elementInfo = new ElementInstanceInfo();        // Create the custom class for complete element information.
                // ID
                elementInfo.Id = element.Id.GetIntValue();
                // UniqueId
                elementInfo.UniqueId = element.UniqueId;
                // Type name.
                elementInfo.Name = element.Name;
                // Family name.
                elementInfo.FamilyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString();
                // Category.
                elementInfo.Category = element.Category.Name;
                // Built-in category.
                elementInfo.BuiltInCategory = Enum.GetName(typeof(BuiltInCategory), element.Category.Id.GetIntValue());
                // Type ID.
                elementInfo.TypeId = element.GetTypeId().GetIntValue();
                // Containing room ID.
                if (element is FamilyInstance instance)
                    elementInfo.RoomId = instance.Room?.Id.GetIntValue() ?? -1;
                // Level.
                elementInfo.Level = GetElementLevel(doc, element);
                // Bounding box.
                BoundingBoxInfo boundingBoxInfo = new BoundingBoxInfo();
                elementInfo.BoundingBox = GetBoundingBoxInfo(element);
                // Parameters.
                //elementInfo.Parameters = GetDimensionParameters(element);
                ParameterInfo thicknessParam = GetThicknessInfo(element);      // Thickness parameter.
                if (thicknessParam != null)
                {
                    elementInfo.Parameters.Add(thicknessParam);
                }
                ParameterInfo heightParam = GetBoundingBoxHeight(elementInfo.BoundingBox);      // Height parameter.
                if (heightParam != null)
                {
                    elementInfo.Parameters.Add(heightParam);
                }

                return elementInfo;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Creates a complete TypeFullInfo object for one element type.
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="elementType"></param>
        /// <returns></returns>
        public static ElementTypeInfo CreateTypeFullInfo(Document doc, ElementType elementType)
        {
            ElementTypeInfo typeInfo = new ElementTypeInfo();
            // Id
            typeInfo.Id = elementType.Id.GetIntValue();
            // UniqueId
            typeInfo.UniqueId = elementType.UniqueId;
            // Type name.
            typeInfo.Name = elementType.Name;
            // Family name.
            typeInfo.FamilyName = elementType.FamilyName;
            // Category.
            typeInfo.Category = elementType.Category.Name;
            // Built-in category.
            typeInfo.BuiltInCategory = Enum.GetName(typeof(BuiltInCategory), elementType.Category.Id.GetIntValue());
            // Parameter dictionary.
            typeInfo.Parameters = GetDimensionParameters(elementType);
            ParameterInfo thicknessParam = GetThicknessInfo(elementType);      // Thickness parameter.
            if (thicknessParam != null)
            {
                typeInfo.Parameters.Add(thicknessParam);
            }
            return typeInfo;
        }

        /// <summary>
        /// Creates information for a datum element.
        /// </summary>
        public static PositioningElementInfo CreatePositioningElementInfo(Document doc, Element element)
        {
            try
            {
                if (element == null)
                    return null;
                PositioningElementInfo info = new PositioningElementInfo
                {
                    Id = element.Id.GetIntValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name,
                    FamilyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString(),
                    Category = element.Category?.Name,
                    BuiltInCategory = element.Category != null ?
                        Enum.GetName(typeof(BuiltInCategory), element.Category.Id.GetIntValue()) : null,
                    ElementClass = element.GetType().Name,
                    BoundingBox = GetBoundingBoxInfo(element)
                };

                // Process levels.
                if (element is Level level)
                {
                    // Convert to millimeters.
                    info.Elevation = level.Elevation * 304.8;
                }
                // Process grids.
                else if (element is Grid grid)
                {
                    Curve curve = grid.Curve;
                    if (curve != null)
                    {
                        XYZ start = curve.GetEndPoint(0);
                        XYZ end = curve.GetEndPoint(1);
                        // Create a JZLine in millimeters.
                        info.GridLine = new JZLine(
                            start.X * 304.8, start.Y * 304.8, start.Z * 304.8,
                            end.X * 304.8, end.Y * 304.8, end.Z * 304.8);
                    }
                }

                // Get level information.
                info.Level = GetElementLevel(doc, element);

                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error creating datum element information: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// Creates information for a spatial element.
        /// </summary>
        public static SpatialElementInfo CreateSpatialElementInfo(Document doc, Element element)
        {
            try
            {
                if (element == null || !(element is SpatialElement))
                    return null;
                SpatialElement spatialElement = element as SpatialElement;
                SpatialElementInfo info = new SpatialElementInfo
                {
                    Id = element.Id.GetIntValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name,
                    FamilyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString(),
                    Category = element.Category?.Name,
                    BuiltInCategory = element.Category != null ?
                        Enum.GetName(typeof(BuiltInCategory), element.Category.Id.GetIntValue()) : null,
                    ElementClass = element.GetType().Name,
                    BoundingBox = GetBoundingBoxInfo(element)
                };

                // Get the room or area number.
                if (element is Room room)
                {
                    info.Number = room.Number;
                    // Convert to cubic millimeters.
                    info.Volume = room.Volume * Math.Pow(304.8, 3);
                }
                else if (element is Area area)
                {
                    info.Number = area.Number;
                }

                // Get the area.
                Parameter areaParam = element.get_Parameter(BuiltInParameter.ROOM_AREA);
                if (areaParam != null && areaParam.HasValue)
                {
                    // Convert to square millimeters.
                    info.Area = areaParam.AsDouble() * Math.Pow(304.8, 2);
                }

                // Get the perimeter.
                Parameter perimeterParam = element.get_Parameter(BuiltInParameter.ROOM_PERIMETER);
                if (perimeterParam != null && perimeterParam.HasValue)
                {
                    // Convert to millimeters.
                    info.Perimeter = perimeterParam.AsDouble() * 304.8;
                }

                // Get level information.
                info.Level = GetElementLevel(doc, element);

                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error creating spatial element information: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// Creates information for a view element.
        /// </summary>
        public static ViewInfo CreateViewInfo(Document doc, Element element)
        {
            try
            {
                if (element == null || !(element is View))
                    return null;
                View view = element as View;

                ViewInfo info = new ViewInfo
                {
                    Id = element.Id.GetIntValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name,
                    FamilyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString(),
                    Category = element.Category?.Name,
                    BuiltInCategory = element.Category != null ?
                        Enum.GetName(typeof(BuiltInCategory), element.Category.Id.GetIntValue()) : null,
                    ElementClass = element.GetType().Name,
                    ViewType = view.ViewType.ToString(),
                    Scale = view.Scale,
                    IsTemplate = view.IsTemplate,
                    DetailLevel = view.DetailLevel.ToString(),
                    BoundingBox = GetBoundingBoxInfo(element)
                };

                // Get the level associated with the view.
                if (view is ViewPlan viewPlan && viewPlan.GenLevel != null)
                {
                    Level level = viewPlan.GenLevel;
                    info.AssociatedLevel = new LevelInfo
                    {
                        Id = level.Id.GetIntValue(),
                        Name = level.Name,
                        Height = level.Elevation * 304.8 // Convert to millimeters.
                    };
                }

                // Determine whether the view is open and active.
                UIDocument uidoc = new UIDocument(doc);

                // Get all open views.
                IList<UIView> openViews = uidoc.GetOpenUIViews();

                foreach (UIView uiView in openViews)
                {
                    // Check whether the view is open.
                    if (uiView.ViewId.GetValue() == view.Id.GetValue())
                    {
                        info.IsOpen = true;

                        // Check whether the view is currently active.
                        if (uidoc.ActiveView.Id.GetValue() == view.Id.GetValue())
                        {
                            info.IsActive = true;
                        }
                        break;
                    }
                }

                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error creating view element information: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// Creates information for an annotation element.
        /// </summary>
        public static AnnotationInfo CreateAnnotationInfo(Document doc, Element element)
        {
            try
            {
                if (element == null)
                    return null;
                AnnotationInfo info = new AnnotationInfo
                {
                    Id = element.Id.GetIntValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name,
                    FamilyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString(),
                    Category = element.Category?.Name,
                    BuiltInCategory = element.Category != null ?
                        Enum.GetName(typeof(BuiltInCategory), element.Category.Id.GetIntValue()) : null,
                    ElementClass = element.GetType().Name,
                    BoundingBox = GetBoundingBoxInfo(element)
                };

                // Get the owning view.
                Parameter viewParam = element.get_Parameter(BuiltInParameter.VIEW_NAME);
                if (viewParam != null && viewParam.HasValue)
                {
                    info.OwnerView = viewParam.AsString();
                }
                else if (element.OwnerViewId != ElementId.InvalidElementId)
                {
                    View ownerView = doc.GetElement(element.OwnerViewId) as View;
                    info.OwnerView = ownerView?.Name;
                }

                // Process text notes.
                if (element is TextNote textNote)
                {
                    info.TextContent = textNote.Text;
                    XYZ position = textNote.Coord;
                    // Convert to millimeters.
                    info.Position = new JZPoint(
                        position.X * 304.8,
                        position.Y * 304.8,
                        position.Z * 304.8);
                }
                // Process dimensions.
                else if (element is Dimension dimension)
                {
                    info.DimensionValue = dimension.Value.ToString();
                    XYZ origin = dimension.Origin;
                    // Convert to millimeters.
                    info.Position = new JZPoint(
                        origin.X * 304.8,
                        origin.Y * 304.8,
                        origin.Z * 304.8);
                }
                // Process other annotation elements.
                else if (element is AnnotationSymbol annotationSymbol)
                {
                    if (annotationSymbol.Location is LocationPoint locationPoint)
                    {
                        XYZ position = locationPoint.Point;
                        // Convert to millimeters.
                        info.Position = new JZPoint(
                            position.X * 304.8,
                            position.Y * 304.8,
                            position.Z * 304.8);
                    }
                }
                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error creating annotation element information: {ex.Message}");
                return null;
            }
        }
        /// <summary>
        /// Creates information for a group or link.
        /// </summary>
        public static GroupOrLinkInfo CreateGroupOrLinkInfo(Document doc, Element element)
        {
            try
            {
                if (element == null)
                    return null;
                GroupOrLinkInfo info = new GroupOrLinkInfo
                {
                    Id = element.Id.GetIntValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name,
                    FamilyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString(),
                    Category = element.Category?.Name,
                    BuiltInCategory = element.Category != null ?
                        Enum.GetName(typeof(BuiltInCategory), element.Category.Id.GetIntValue()) : null,
                    ElementClass = element.GetType().Name,
                    BoundingBox = GetBoundingBoxInfo(element)
                };

                // Process groups.
                if (element is Group group)
                {
                    ICollection<ElementId> memberIds = group.GetMemberIds();
                    info.MemberCount = memberIds?.Count;
                    info.GroupType = group.GroupType?.Name;
                }
                // Process links.
                else if (element is RevitLinkInstance linkInstance)
                {
                    RevitLinkType linkType = doc.GetElement(linkInstance.GetTypeId()) as RevitLinkType;
                    if (linkType != null)
                    {
                        ExternalFileReference extFileRef = linkType.GetExternalFileReference();
                        // Get the absolute path.
                        string absPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extFileRef.GetAbsolutePath());
                        info.LinkPath = absPath;

                        // Get the link status using GetLinkedFileStatus.
                        LinkedFileStatus linkStatus = linkType.GetLinkedFileStatus();
                        info.LinkStatus = linkStatus.ToString();
                    }
                    else
                    {
                        info.LinkStatus = LinkedFileStatus.Invalid.ToString();
                    }

                    // Get the location.
                    LocationPoint location = linkInstance.Location as LocationPoint;
                    if (location != null)
                    {
                        XYZ point = location.Point;
                        // Convert to millimeters.
                        info.Position = new JZPoint(
                            point.X * 304.8,
                            point.Y * 304.8,
                            point.Z * 304.8);
                    }
                }

                return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error creating group or link information: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates enhanced basic information for an element.
        /// </summary>
        public static ElementBasicInfo CreateElementBasicInfo(Document doc, Element element)
        {
            try
            {
                if (element == null)
                    return null;
                ElementBasicInfo basicInfo = new ElementBasicInfo
                {
                    Id = element.Id.GetIntValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name,
                    FamilyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString(),
                    Category = element.Category?.Name,
                    BuiltInCategory = element.Category != null ?
                        Enum.GetName(typeof(BuiltInCategory), element.Category.Id.GetIntValue()) : null,
                    BoundingBox = GetBoundingBoxInfo(element)
                };
                return basicInfo;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Error creating basic element information: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets thickness parameter information for a system family component.
        /// </summary>
        /// <param name="element">A system family component, such as a wall, floor, or door.</param>
        /// <returns>The parameter information, or null if unavailable.</returns>
        public static ParameterInfo GetThicknessInfo(Element element)
        {
            if (element == null)
            {
                return null;
            }

            // Get the component type.
            ElementType elementType = element.Document.GetElement(element.GetTypeId()) as ElementType;
            if (elementType == null)
            {
                return null;
            }

            // Get the built-in thickness parameter for the component type.
            Parameter thicknessParam = null;

            if (elementType is WallType)
            {
                thicknessParam = elementType.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
            }
            else if (elementType is FloorType)
            {
                thicknessParam = elementType.get_Parameter(BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM);
            }
            else if (elementType is FamilySymbol familySymbol)
            {
                switch (familySymbol.Category?.Id.GetIntValue())
                {
                    case (int)BuiltInCategory.OST_Doors:
                    case (int)BuiltInCategory.OST_Windows:
                        thicknessParam = elementType.get_Parameter(BuiltInParameter.FAMILY_THICKNESS_PARAM);
                        break;
                }
            }
            else if (elementType is CeilingType)
            {
                thicknessParam = elementType.get_Parameter(BuiltInParameter.CEILING_THICKNESS);
            }

            if (thicknessParam != null && thicknessParam.HasValue)
            {
                return new ParameterInfo
                {
                    Name = "Thickness",
                    Value = $"{thicknessParam.AsDouble() * 304.8}"
                };
            }
            return null;
        }

        /// <summary>
        /// Gets the level information for an element.
        /// </summary>
        public static LevelInfo GetElementLevel(Document doc, Element element)
        {
            try
            {
                Level level = null;

                // Get the level according to the element type.
                if (element is Wall wall) // Wall.
                {
                    level = doc.GetElement(wall.LevelId) as Level;
                }
                else if (element is Floor floor) // Floor.
                {
                    Parameter levelParam = floor.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                    if (levelParam != null && levelParam.HasValue)
                    {
                        level = doc.GetElement(levelParam.AsElementId()) as Level;
                    }
                }
                else if (element is FamilyInstance familyInstance) // Family instance, including generic models.
                {
                    // Try the family instance level parameter.
                    Parameter levelParam = familyInstance.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                    if (levelParam != null && levelParam.HasValue)
                    {
                        level = doc.GetElement(levelParam.AsElementId()) as Level;
                    }
                    // If that fails, try SCHEDULE_LEVEL_PARAM.
                    if (level == null)
                    {
                        levelParam = familyInstance.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                        if (levelParam != null && levelParam.HasValue)
                        {
                            level = doc.GetElement(levelParam.AsElementId()) as Level;
                        }
                    }
                }
                else // Other elements.
                {
                    // Try the general level parameter.
                    Parameter levelParam = element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
                    if (levelParam != null && levelParam.HasValue)
                    {
                        level = doc.GetElement(levelParam.AsElementId()) as Level;
                    }
                }

                if (level != null)
                {
                    LevelInfo levelInfo = new LevelInfo
                    {
                        Id = level.Id.GetIntValue(),
                        Name = level.Name,
                        Height = level.Elevation * 304.8
                    };
                    return levelInfo;
                }
                else
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets bounding box information for an element.
        /// </summary>
        public static BoundingBoxInfo GetBoundingBoxInfo(Element element)
        {
            try
            {
                BoundingBoxXYZ bbox = element.get_BoundingBox(null);
                if (bbox == null)
                    return null;
                return new BoundingBoxInfo
                {
                    Min = new JZPoint(
                        bbox.Min.X * 304.8,
                        bbox.Min.Y * 304.8,
                        bbox.Min.Z * 304.8),
                    Max = new JZPoint(
                        bbox.Max.X * 304.8,
                        bbox.Max.Y * 304.8,
                        bbox.Max.Z * 304.8)
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the height parameter from a bounding box.
        /// </summary>
        /// <param name="boundingBoxInfo">Bounding box information.</param>
        /// <returns>The parameter information, or null if unavailable.</returns>
        public static ParameterInfo GetBoundingBoxHeight(BoundingBoxInfo boundingBoxInfo)
        {
            try
            {
                // Validate the input.
                if (boundingBoxInfo?.Min == null || boundingBoxInfo?.Max == null)
                {
                    return null;
                }

                // The Z-axis difference is the height.
                double height = Math.Abs(boundingBoxInfo.Max.Z - boundingBoxInfo.Min.Z);

                return new ParameterInfo
                {
                    Name = "Height",
                    Value = $"{height}"
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the names and values of all non-empty element parameters.
        /// </summary>
        /// <param name="element">A Revit element.</param>
        /// <returns>A list of parameter information.</returns>
        public static List<ParameterInfo> GetDimensionParameters(Element element)
        {
            // Check for a null element.
            if (element == null)
            {
                return new List<ParameterInfo>();
            }

            var parameters = new List<ParameterInfo>();

            // Get all element parameters.
            foreach (Parameter param in element.Parameters)
            {
                try
                {
                    // Skip invalid parameters.
                    if (!param.HasValue || param.IsReadOnly)
                    {
                        continue;
                    }

                    // Keep dimension-related parameters only.
                    if (IsDimensionParameter(param))
                    {
                        // Get the string representation of the parameter value.
                        string value = param.AsValueString();

                        // Add non-empty values to the list.
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            parameters.Add(new ParameterInfo
                            {
                                Name = param.Definition.Name,
                                Value = value
                            });
                        }
                    }
                }
                catch
                {
                    // Continue if a parameter value cannot be read.
                    continue;
                }
            }

            // Sort by parameter name before returning.
            return parameters.OrderBy(p => p.Name).ToList();
        }

        /// <summary>
        /// Determines whether a parameter is a dimension parameter.
        /// </summary>
        public static bool IsDimensionParameter(Parameter param)
        {

#if REVIT2023_OR_GREATER
            // Revit 2023 and later use Definition.GetDataType() to get the parameter type.
            ForgeTypeId paramTypeId = param.Definition.GetDataType();

            // Check whether the parameter is dimension-related.
            bool isDimensionType = paramTypeId.Equals(SpecTypeId.Length) ||
                                   paramTypeId.Equals(SpecTypeId.Angle) ||
                                   paramTypeId.Equals(SpecTypeId.Area) ||
                                   paramTypeId.Equals(SpecTypeId.Volume);
            // Keep dimension-type parameters only.
            return isDimensionType;
#else
            // Check whether the parameter is dimension-related.
            bool isDimensionType = param.Definition.ParameterType == ParameterType.Length ||
                                   param.Definition.ParameterType == ParameterType.Angle ||
                                   param.Definition.ParameterType == ParameterType.Area ||
                                   param.Definition.ParameterType == ParameterType.Volume;

            // Keep dimension-type parameters only.
            return isDimensionType;
#endif
        }

    }

    /// <summary>
    /// Stores complete element instance information.
    /// </summary>
    public class ElementInstanceInfo
    {
        /// <summary>
        /// Id
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Id
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Type ID.
        /// </summary>
        public int TypeId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Category.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Built-in category.
        /// </summary>
        public string BuiltInCategory { get; set; }
        /// <summary>
        /// Containing room ID.
        /// </summary>
        public int RoomId { get; set; }
        /// <summary>
        /// Associated level.
        /// </summary>
        public LevelInfo Level { get; set; }
        /// <summary>
        /// Location information.
        /// </summary>
        public BoundingBoxInfo BoundingBox { get; set; }
        /// <summary>
        /// Instance parameters.
        /// </summary>
        public List<ParameterInfo> Parameters { get; set; } = new List<ParameterInfo>();

    }

    /// <summary>
    /// Stores complete element type information.
    /// </summary>
    public class ElementTypeInfo
    {
        /// <summary>
        /// ID
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Id
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Category name.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Built-in category ID.
        /// </summary>
        public string BuiltInCategory { get; set; }
        /// <summary>
        /// Type parameters.
        /// </summary>
        public List<ParameterInfo> Parameters { get; set; } = new List<ParameterInfo>();

    }

    /// <summary>
    /// Stores basic information for datum elements such as levels and grids.
    /// </summary>
    public class PositioningElementInfo
    {
        /// <summary>
        /// Element ID.
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Element unique ID.
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Category name.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Optional built-in category.
        /// </summary>
        public string BuiltInCategory { get; set; }
        /// <summary>
        /// Element .NET class name.
        /// </summary>
        public string ElementClass { get; set; }
        /// <summary>
        /// Elevation in millimeters, for levels.
        /// </summary>
        public double? Elevation { get; set; }
        /// <summary>
        /// Associated level.
        /// </summary>
        public LevelInfo Level { get; set; }
        /// <summary>
        /// Location information.
        /// </summary>
        public BoundingBoxInfo BoundingBox { get; set; }
        /// <summary>
        /// Grid line, for grids.
        /// </summary>
        public JZLine GridLine { get; set; }
    }
    /// <summary>
    /// Stores basic information for spatial elements such as rooms and areas.
    /// </summary>
    public class SpatialElementInfo
    {
        /// <summary>
        /// Element ID.
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Element unique ID.
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Number.
        /// </summary>
        public string Number { get; set; }
        /// <summary>
        /// Category name.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Optional built-in category.
        /// </summary>
        public string BuiltInCategory { get; set; }
        /// <summary>
        /// Element .NET class name.
        /// </summary>
        public string ElementClass { get; set; }
        /// <summary>
        /// Area in square millimeters.
        /// </summary>
        public double? Area { get; set; }
        /// <summary>
        /// Volume in cubic millimeters.
        /// </summary>
        public double? Volume { get; set; }
        /// <summary>
        /// Perimeter in millimeters.
        /// </summary>
        public double? Perimeter { get; set; }
        /// <summary>
        /// Associated level.
        /// </summary>
        public LevelInfo Level { get; set; }

        /// <summary>
        /// Location information.
        /// </summary>
        public BoundingBoxInfo BoundingBox { get; set; }
    }
    /// <summary>
    /// Stores basic view element information.
    /// </summary>
    public class ViewInfo
    {
        /// <summary>
        /// Element ID.
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Element unique ID.
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Category name.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Optional built-in category.
        /// </summary>
        public string BuiltInCategory { get; set; }
        /// <summary>
        /// Element .NET class name.
        /// </summary>
        public string ElementClass { get; set; }

        /// <summary>
        /// View type.
        /// </summary>
        public string ViewType { get; set; }

        /// <summary>
        /// View scale.
        /// </summary>
        public int? Scale { get; set; }

        /// <summary>
        /// Whether this is a view template.
        /// </summary>
        public bool IsTemplate { get; set; }

        /// <summary>
        /// Detail level.
        /// </summary>
        public string DetailLevel { get; set; }

        /// <summary>
        /// Associated level.
        /// </summary>
        public LevelInfo AssociatedLevel { get; set; }

        /// <summary>
        /// Location information.
        /// </summary>
        public BoundingBoxInfo BoundingBox { get; set; }

        /// <summary>
        /// Whether the view is open.
        /// </summary>
        public bool IsOpen { get; set; }

        /// <summary>
        /// Whether the view is currently active.
        /// </summary>
        public bool IsActive { get; set; }
    }
    /// <summary>
    /// Stores basic annotation element information.
    /// </summary>
    public class AnnotationInfo
    {
        /// <summary>
        /// Element ID.
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Element unique ID.
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Category name.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Optional built-in category.
        /// </summary>
        public string BuiltInCategory { get; set; }
        /// <summary>
        /// Element .NET class name.
        /// </summary>
        public string ElementClass { get; set; }
        /// <summary>
        /// Owning view.
        /// </summary>
        public string OwnerView { get; set; }
        /// <summary>
        /// Text content, for text notes.
        /// </summary>
        public string TextContent { get; set; }
        /// <summary>
        /// Position in millimeters.
        /// </summary>
        public JZPoint Position { get; set; }

        /// <summary>
        /// Location information.
        /// </summary>
        public BoundingBoxInfo BoundingBox { get; set; }
        /// <summary>
        /// Dimension value, for dimensions.
        /// </summary>
        public string DimensionValue { get; set; }
    }
    /// <summary>
    /// Stores basic group and link information.
    /// </summary>
    public class GroupOrLinkInfo
    {
        /// <summary>
        /// Element ID.
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Element unique ID.
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Category name.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Optional built-in category.
        /// </summary>
        public string BuiltInCategory { get; set; }
        /// <summary>
        /// Element .NET class name.
        /// </summary>
        public string ElementClass { get; set; }
        /// <summary>
        /// Group member count.
        /// </summary>
        public int? MemberCount { get; set; }
        /// <summary>
        /// Group type.
        /// </summary>
        public string GroupType { get; set; }
        /// <summary>
        /// Link status.
        /// </summary>
        public string LinkStatus { get; set; }
        /// <summary>
        /// Link path.
        /// </summary>
        public string LinkPath { get; set; }
        /// <summary>
        /// Position in millimeters.
        /// </summary>
        public JZPoint Position { get; set; }

        /// <summary>
        /// Location information.
        /// </summary>
        public BoundingBoxInfo BoundingBox { get; set; }
    }
    /// <summary>
    /// Stores enhanced basic element information.
    /// </summary>
    public class ElementBasicInfo
    {
        /// <summary>
        /// Element ID.
        /// </summary>
        public int Id { get; set; }
        /// <summary>
        /// Element unique ID.
        /// </summary>
        public string UniqueId { get; set; }
        /// <summary>
        /// Name.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Family name.
        /// </summary>
        public string FamilyName { get; set; }
        /// <summary>
        /// Category name.
        /// </summary>
        public string Category { get; set; }
        /// <summary>
        /// Optional built-in category.
        /// </summary>
        public string BuiltInCategory { get; set; }

        /// <summary>
        /// Location information.
        /// </summary>
        public BoundingBoxInfo BoundingBox { get; set; }
    }



    /// <summary>
    /// Stores parameter information.
    /// </summary>
    public class ParameterInfo
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// Stores bounding box information.
    /// </summary>
    public class BoundingBoxInfo
    {
        public JZPoint Min { get; set; }
        public JZPoint Max { get; set; }
    }

    /// <summary>
    /// Stores level information.
    /// </summary>
    public class LevelInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public double Height { get; set; }
    }



}
