using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Commands;
using RevitMCPCommandSet.Models.Common;
using System.IO;
using System.Reflection;

namespace RevitMCPCommandSet.Utils
{
    public static class ProjectUtils
    {
        /// <summary>
        /// General-purpose method for creating a family instance
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="familySymbol">Family type</param>
        /// <param name="locationPoint">Placement point</param>
        /// <param name="locationLine">Reference line</param>
        /// <param name="baseLevel">Base level</param>
        /// <param name="topLevel">Second level (used for TwoLevelsBased families)</param>
        /// <param name="baseOffset">Base offset (ft)</param>
        /// <param name="topOffset">Top offset (ft)</param>
        /// <param name="faceDirection">Facing direction</param>
        /// <param name="handDirection">Hand direction</param>
        /// <param name="view">View</param>
        /// <returns>The created family instance, or null if creation fails</returns>
        public static FamilyInstance CreateInstance(
            this Document doc,
            FamilySymbol familySymbol,
            XYZ locationPoint = null,
            Line locationLine = null,
            Level baseLevel = null,
            Level topLevel = null,
            double baseOffset = -1,
            double topOffset = -1,
            XYZ faceDirection = null,
            XYZ handDirection = null,
            View view = null,
            Element explicitHost = null,
            bool snapToHostCenter = true)
        {
            // Validate required parameters
            if (doc == null)
                throw new ArgumentNullException($"Required parameter {typeof(Document)} {nameof(doc)} is missing!");
            if (familySymbol == null)
                throw new ArgumentNullException($"Required parameter {typeof(FamilySymbol)} {nameof(familySymbol)} is missing!");

            // Activate the family type
            if (!familySymbol.IsActive)
                familySymbol.Activate();

            FamilyInstance instance = null;

            // Select the creation method based on the family placement type
            switch (familySymbol.Family.FamilyPlacementType)
            {
                // Families based on a single level (for example, Metric Generic Model)
                case FamilyPlacementType.OneLevelBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required parameter {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    // With level information
                    if (baseLevel != null)
                    {
                        instance = doc.Create.NewFamilyInstance(
                            locationPoint,                  // Physical location where the instance will be placed
                            familySymbol,                   // FamilySymbol representing the type of instance to insert
                            baseLevel,                      // Level used as the object's base level
                            StructuralType.NonStructural);  // Component type when the instance is structural
                    }
                    // Without level information
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            locationPoint,                  // Physical location where the instance will be placed
                            familySymbol,                   // FamilySymbol representing the type of instance to insert
                            StructuralType.NonStructural);  // Component type when the instance is structural
                    }
                    break;

                // Families based on a single level and host (for example, doors and windows)
                case FamilyPlacementType.OneLevelBasedHosted:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required parameter {typeof(XYZ)} {nameof(locationPoint)} is missing!");

                    Element host = explicitHost;
                    XYZ placementPoint = locationPoint;

                    // If explicit host provided and it's a wall, snap to its centerline
                    if (host != null && snapToHostCenter && host is Wall explicitWall)
                    {
                        LocationCurve eLoc = explicitWall.Location as LocationCurve;
                        if (eLoc != null)
                        {
                            IntersectionResult eIr = eLoc.Curve.Project(locationPoint);
                            if (eIr != null)
                                placementPoint = new XYZ(eIr.XYZPoint.X, eIr.XYZPoint.Y, locationPoint.Z);
                        }
                    }

                    // Auto-detect host wall if not explicitly provided
                    if (host == null)
                    {
                        // Try geometric wall-centerline proximity first
                        var wallResult = doc.GetNearestWallByLocationLine(locationPoint, baseLevel);
                        if (wallResult.HasValue)
                        {
                            host = wallResult.Value.wall;
                            if (snapToHostCenter)
                                placementPoint = wallResult.Value.projectedPoint;
                        }
                        else
                        {
                            // Fall back to original ray-casting method
                            host = doc.GetNearestHostElement(locationPoint, familySymbol);
                        }
                    }

                    if (host == null)
                        throw new ArgumentNullException($"No valid host could be found!");

                    if (baseLevel != null)
                    {
                        instance = doc.Create.NewFamilyInstance(
                            placementPoint,
                            familySymbol,
                            host,
                            baseLevel,
                            StructuralType.NonStructural);
                    }
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            placementPoint,
                            familySymbol,
                            host,
                            StructuralType.NonStructural);
                    }

                    // Set sill height for windows (baseOffset maps to sill height for hosted elements)
                    if (instance != null && baseOffset != -1)
                    {
                        Parameter sillParam = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sillParam != null && !sillParam.IsReadOnly)
                        {
                            sillParam.Set(baseOffset);
                        }
                    }
                    break;

                // Families based on two levels (for example, columns)
                case FamilyPlacementType.TwoLevelsBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required parameter {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    if (baseLevel == null)
                        throw new ArgumentNullException($"Required parameter {typeof(Level)} {nameof(baseLevel)} is missing!");
                    // Determine whether this is a structural or architectural column
                    StructuralType structuralType = StructuralType.NonStructural;
                    if (familySymbol.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_StructuralColumns)
                        structuralType = StructuralType.Column;
                    instance = doc.Create.NewFamilyInstance(
                        locationPoint,              // Physical location where the instance will be placed
                        familySymbol,               // FamilySymbol representing the type of instance to insert
                        baseLevel,                  // Level used as the object's base level
                        structuralType);            // Component type when the instance is structural
                    // Set the base level, top level, base offset, and top offset
                    if (instance != null)
                    {
                        // Set the column's base and top levels
                        if (baseLevel != null)
                        {
                            Parameter baseLevelParam = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                            if (baseLevelParam != null)
                                baseLevelParam.Set(baseLevel.Id);
                        }
                        if (topLevel != null)
                        {
                            Parameter topLevelParam = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
                            if (topLevelParam != null)
                                topLevelParam.Set(topLevel.Id);
                        }
                        // Get the base offset parameter
                        if (baseOffset != -1)
                        {
                            Parameter baseOffsetParam = instance.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM);
                            if (baseOffsetParam != null && baseOffsetParam.StorageType == StorageType.Double)
                            {
                                // Convert millimeters to Revit internal units
                                double baseOffsetInternal = baseOffset;
                                baseOffsetParam.Set(baseOffsetInternal);
                            }
                        }
                        // Get the top offset parameter
                        if (topOffset != -1)
                        {
                            Parameter topOffsetParam = instance.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM);
                            if (topOffsetParam != null && topOffsetParam.StorageType == StorageType.Double)
                            {
                                // Convert millimeters to Revit internal units
                                double topOffsetInternal = topOffset;
                                topOffsetParam.Set(topOffsetInternal);
                            }
                        }
                    }
                    break;

                // View-specific families (for example, detail annotations)
                case FamilyPlacementType.ViewBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required parameter {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    instance = doc.Create.NewFamilyInstance(
                        locationPoint,  // Family-instance origin; projected onto the view plane when created in a ViewPlan
                        familySymbol,   // FamilySymbol representing the type of instance to insert
                        view);          // 2D view in which to place the family instance
                    break;

                // Work-plane-based families (for example, face- or wall-based Metric Generic Models)
                case FamilyPlacementType.WorkPlaneBased:
                    if (locationPoint == null)
                        throw new ArgumentNullException($"Required parameter {typeof(XYZ)} {nameof(locationPoint)} is missing!");
                    // Get the nearest host face
                    Reference hostFace = doc.GetNearestFaceReference(locationPoint, 1000 / 304.8);
                    if (hostFace == null)
                        throw new ArgumentNullException($"No valid host could be found!");
                    if (faceDirection == null || faceDirection == XYZ.Zero)
                    {
                        var result = doc.GenerateDefaultOrientation(hostFace);
                        faceDirection = result.FacingOrientation;
                    }
                    // Create a family instance on the face using a point and direction
                    instance = doc.Create.NewFamilyInstance(
                        hostFace,               // Reference to the face
                        locationPoint,          // Point on the face where the instance will be placed
                        faceDirection,          // Vector defining instance orientation; it controls rotation on the face and cannot be parallel to the face normal
                        familySymbol);          // FamilySymbol representing the type to insert; it must belong to a WorkPlaneBased family
                    break;

                // Line-based families on a work plane (for example, line-based Metric Generic Models)
                case FamilyPlacementType.CurveBased:
                    if (locationLine == null)
                        throw new ArgumentNullException($"Required parameter {typeof(Line)} {nameof(locationLine)} is missing!");

                    // Get the nearest host face (no tolerance allowed)
                    Reference lineHostFace = doc.GetNearestFaceReference(locationLine.Evaluate(0.5, true), 1e-5);
                    if (lineHostFace != null)
                    {
                        instance = doc.Create.NewFamilyInstance(
                            lineHostFace,   // Reference to the face
                            locationLine,   // Curve on which the family instance is based
                            familySymbol);  // FamilySymbol representing the type to insert; its FamilyPlacementType must be WorkPlaneBased or CurveBased
                    }
                    else
                    {
                        instance = doc.Create.NewFamilyInstance(
                            locationLine,                   // Curve on which the family instance is based
                            familySymbol,                   // FamilySymbol representing the type to insert; its FamilyPlacementType must be WorkPlaneBased or CurveBased
                            baseLevel,                      // Level used as the object's base level
                            StructuralType.NonStructural);  // Component type when the instance is structural
                    }
                    if (instance != null)
                    {
                        // Get the base offset parameter
                        if (baseOffset != -1)
                        {
                            Parameter baseOffsetParam = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
                            if (baseOffsetParam != null && baseOffsetParam.StorageType == StorageType.Double)
                            {
                                // Convert millimeters to Revit internal units
                                double baseOffsetInternal = baseOffset;
                                baseOffsetParam.Set(baseOffsetInternal);
                            }
                        }
                    }
                    break;

                // Line-based families in a specific view (for example, detail components)
                case FamilyPlacementType.CurveBasedDetail:
                    if (locationLine == null)
                        throw new ArgumentNullException($"Required parameter {typeof(Line)} {nameof(locationLine)} is missing!");
                    if (view == null)
                        throw new ArgumentNullException($"Required parameter {typeof(View)} {nameof(view)} is missing!");
                    instance = doc.Create.NewFamilyInstance(
                        locationLine,   // Family instance line location; the line must lie in the view plane
                        familySymbol,   // FamilySymbol representing the type of instance to insert
                        view);          // 2D view in which to place the family instance
                    break;

                // Structural curve-driven families (for example, beams, braces, or slanted columns)
                case FamilyPlacementType.CurveDrivenStructural:
                    if (locationLine == null)
                        throw new ArgumentNullException($"Required parameter {typeof(Line)} {nameof(locationLine)} is missing!");
                    if (baseLevel == null)
                        throw new ArgumentNullException($"Required parameter {typeof(Level)} {nameof(baseLevel)} is missing!");
                    instance = doc.Create.NewFamilyInstance(
                        locationLine,                   // Curve on which the family instance is based
                        familySymbol,                   // FamilySymbol representing the type to insert; its FamilyPlacementType must be WorkPlaneBased or CurveBased
                        baseLevel,                      // Level used as the object's base level
                        StructuralType.Beam);           // Component type when the instance is structural
                    break;

                // Adaptive families (for example, Metric Generic Model Adaptive or curtain panels)
                case FamilyPlacementType.Adaptive:
                    throw new NotImplementedException("The creation method for FamilyPlacementType.Adaptive is not implemented!");

                default:
                    break;
            }
            return instance;
        }

        /// <summary>
        /// Generates default facing and hand orientations (the long edge defaults to HandOrientation and the short edge to FacingOrientation)
        /// </summary>
        /// <param name="hostFace"></param>
        /// <returns></returns>
        public static (XYZ FacingOrientation, XYZ HandOrientation) GenerateDefaultOrientation(this Document doc, Reference hostFace)
        {
            var facingOrientation = new XYZ();  // Facing direction: orientation of the family's positive Y-axis after loading
            var handOrientation = new XYZ();    // Hand direction: orientation of the family's positive X-axis after loading

            // Step 1: Get the face object from the Reference
            Face face = doc.GetElement(hostFace.ElementId).GetGeometryObjectFromReference(hostFace) as Face;

            // Step 2: Get the face profiles
            List<Curve> profile = null;
            // Collection of profiles; each sublist is a complete closed loop, usually with the outer loop first
            List<List<Curve>> profiles = new List<List<Curve>>();
            // Get all profile loops (the outer loop and any inner openings)
            EdgeArrayArray edgeLoops = face.EdgeLoops;
            // Iterate through each profile loop
            foreach (EdgeArray loop in edgeLoops)
            {
                List<Curve> currentLoop = new List<Curve>();
                // Get each edge in the loop
                foreach (Edge edge in loop)
                {
                    Curve curve = edge.AsCurve();
                    currentLoop.Add(curve);
                }
                // Add the current loop to the results if it contains edges
                if (currentLoop.Count > 0)
                {
                    profiles.Add(currentLoop);
                }
            }
            // The first loop is usually the outer profile
            if (profiles != null && profiles.Any())
                profile = profiles.FirstOrDefault();

            // Step 3: Get the face normal
            XYZ faceNormal = null;
            // For a planar face, obtain the normal directly from its property
            if (face is PlanarFace planarFace)
                faceNormal = planarFace.FaceNormal;

            // Step 4: Get two valid principal directions that follow the right-hand rule
            var result = face.GetMainDirections();
            var primaryDirection = result.PrimaryDirection;
            var secondaryDirection = result.SecondaryDirection;

            // By default, the long-edge direction is HandOrientation and the short-edge direction is FacingOrientation
            facingOrientation = primaryDirection;
            handOrientation = secondaryDirection;

            // Check the right-hand rule (thumb: HandOrientation, index finger: FacingOrientation, middle finger: FaceNormal)
            if (!facingOrientation.IsRightHandRuleCompliant(handOrientation, faceNormal))
            {
                var newHandOrientation = facingOrientation.GenerateIndexFinger(faceNormal);
                if (newHandOrientation != null)
                {
                    handOrientation = newHandOrientation;
                }
            }

            return (facingOrientation, handOrientation);
        }

        /// <summary>
        /// Gets the face Reference nearest to a point
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="location">Target point</param>
        /// <param name="radius">Search radius (internal units)</param>
        /// <returns>The nearest face Reference, or null if none is found</returns>
        public static Reference GetNearestFaceReference(this Document doc, XYZ location, double radius = 1000 / 304.8)
        {
            try
            {
                // Apply a small tolerance offset
                location = new XYZ(location.X, location.Y, location.Z + 0.1 / 304.8);

                // Create or retrieve a 3D view
                View3D view3D = null;
                FilteredElementCollector collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D));

                foreach (View3D v in collector)
                {
                    if (!v.IsTemplate)
                    {
                        view3D = v;
                        break;
                    }
                }

                if (view3D == null)
                {
                    using (Transaction trans = new Transaction(doc, "Create 3D View"))
                    {
                        trans.Start();
                        ViewFamilyType vft = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);

                        if (vft != null)
                        {
                            view3D = View3D.CreateIsometric(doc, vft.Id);
                        }
                        trans.Commit();
                    }
                }

                if (view3D == null)
                {
                    TaskDialog.Show("Error", "Unable to create or retrieve a 3D view");
                    return null;
                }

                // Define rays in six directions
                XYZ[] directions = new XYZ[]
                {
                  XYZ.BasisX,    // Positive X
                  -XYZ.BasisX,   // Negative X
                  XYZ.BasisY,    // Positive Y
                  -XYZ.BasisY,   // Negative Y
                  XYZ.BasisZ,    // Positive Z
                  -XYZ.BasisZ    // Negative Z
                };

                // Create filters
                ElementClassFilter wallFilter = new ElementClassFilter(typeof(Wall));
                ElementClassFilter floorFilter = new ElementClassFilter(typeof(Floor));
                ElementClassFilter ceilingFilter = new ElementClassFilter(typeof(Ceiling));
                ElementClassFilter instanceFilter = new ElementClassFilter(typeof(FamilyInstance));

                // Combine filters
                LogicalOrFilter categoryFilter = new LogicalOrFilter(
                    new ElementFilter[] { wallFilter, floorFilter, ceilingFilter, instanceFilter });


                // 1. Simplest option: a filter for all element instances
                //ElementFilter filter = new ElementIsElementTypeFilter(true);

                // Create the ray intersector
                ReferenceIntersector refIntersector = new ReferenceIntersector(categoryFilter,
                    FindReferenceTarget.Face, view3D);
                refIntersector.FindReferencesInRevitLinks = true; // Include faces in linked Revit models if needed

                double minDistance = double.MaxValue;
                Reference nearestFace = null;

                foreach (XYZ direction in directions)
                {
                    // Cast a ray from the current location
                    IList<ReferenceWithContext> references = refIntersector.Find(location, direction);

                    foreach (ReferenceWithContext rwc in references)
                    {
                        double distance = rwc.Proximity; // Get the distance to the face

                        // Keep the closer face if it is within the search radius
                        if (distance <= radius && distance < minDistance)
                        {
                            minDistance = distance;
                            nearestFace = rwc.GetReference();
                        }
                    }
                }

                return nearestFace;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred while finding the nearest face: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets the nearest element that can host the family at the specified point
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="location">Target point</param>
        /// <param name="familySymbol">Family type used to determine the host type</param>
        /// <param name="radius">Search radius (internal units)</param>
        /// <returns>The nearest host element, or null if none is found</returns>
        public static Element GetNearestHostElement(this Document doc, XYZ location, FamilySymbol familySymbol, double radius = 5.0)
        {
            try
            {
                // Validate required parameters
                if (doc == null || location == null || familySymbol == null)
                    return null;

                // Get the family's hosting behavior parameter
                Parameter hostParam = familySymbol.Family.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR);
                int hostingBehavior = hostParam?.AsInteger() ?? 0;

                // Create or retrieve a 3D view
                View3D view3D = null;
                FilteredElementCollector viewCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D));
                foreach (View3D v in viewCollector)
                {
                    if (!v.IsTemplate)
                    {
                        view3D = v;
                        break;
                    }
                }

                if (view3D == null)
                {
                    using (Transaction trans = new Transaction(doc, "Create 3D View"))
                    {
                        trans.Start();
                        ViewFamilyType vft = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);

                        if (vft != null)
                        {
                            view3D = View3D.CreateIsometric(doc, vft.Id);
                        }
                        trans.Commit();
                    }
                }

                if (view3D == null)
                {
                    TaskDialog.Show("Error", "Unable to create or retrieve a 3D view");
                    return null;
                }

                // Create a class filter based on the hosting behavior
                ElementFilter classFilter;
                switch (hostingBehavior)
                {
                    case 1: // Wall based
                        classFilter = new ElementClassFilter(typeof(Wall));
                        break;
                    case 2: // Floor based
                        classFilter = new ElementClassFilter(typeof(Floor));
                        break;
                    case 3: // Ceiling based
                        classFilter = new ElementClassFilter(typeof(Ceiling));
                        break;
                    case 4: // Roof based
                        classFilter = new ElementClassFilter(typeof(RoofBase));
                        break;
                    default:
                        return null; // Unsupported host type
                }

                // Define rays in six directions
                XYZ[] directions = new XYZ[]
                {
                    XYZ.BasisX,    // Positive X
                    -XYZ.BasisX,   // Negative X
                    XYZ.BasisY,    // Positive Y
                    -XYZ.BasisY,   // Negative Y
                    XYZ.BasisZ,    // Positive Z
                    -XYZ.BasisZ    // Negative Z
                };

                // Create the ray intersector
                ReferenceIntersector refIntersector = new ReferenceIntersector(classFilter,
                    FindReferenceTarget.Element, view3D);
                refIntersector.FindReferencesInRevitLinks = true; // Include elements in linked Revit models if needed

                double minDistance = double.MaxValue;
                Element nearestHost = null;

                foreach (XYZ direction in directions)
                {
                    // Cast a ray from the current location
                    IList<ReferenceWithContext> references = refIntersector.Find(location, direction);

                    foreach (ReferenceWithContext rwc in references)
                    {
                        double distance = rwc.Proximity; // Get the distance to the element

                        // Keep the closer element if it is within the search radius
                        if (distance <= radius && distance < minDistance)
                        {
                            minDistance = distance;
                            nearestHost = doc.GetElement(rwc.GetReference().ElementId);
                        }
                    }
                }

                return nearestHost;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An error occurred while finding the nearest host element: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the nearest wall to a point using wall location-line distance calculation.
        /// More reliable than ray-casting for door/window placement.
        /// </summary>
        /// <param name="doc">Current Revit document</param>
        /// <param name="point">Target point (internal units, feet)</param>
        /// <param name="level">Level to filter walls on</param>
        /// <param name="tolerance">Extra tolerance beyond half wall width (feet). Default ~5mm.</param>
        /// <returns>Tuple of (wall, projectedPoint, wallDirection, distance) or null</returns>
        public static (Wall wall, XYZ projectedPoint, XYZ wallDirection, double distance)?
            GetNearestWallByLocationLine(
                this Document doc,
                XYZ point,
                Level level,
                double tolerance = 5.0 / 304.8)
        {
            if (doc == null || point == null || level == null)
                return null;

            // Collect all walls on the given level
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w =>
                {
                    Parameter baseLevelParam = w.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                    return baseLevelParam != null && baseLevelParam.AsElementId() == level.Id;
                })
                .ToList();

            Wall bestWall = null;
            XYZ bestProjection = null;
            XYZ bestDirection = null;
            double bestDistance = double.MaxValue;

            foreach (Wall wall in walls)
            {
                LocationCurve locCurve = wall.Location as LocationCurve;
                if (locCurve == null) continue;

                Curve curve = locCurve.Curve;
                if (curve == null) continue;

                // Use Curve.Project() which handles both lines and arcs
                IntersectionResult ir = curve.Project(new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z));
                if (ir == null) continue;

                XYZ projectedPt = ir.XYZPoint;
                double distance = new XYZ(point.X - projectedPt.X, point.Y - projectedPt.Y, 0).GetLength();

                // Check if point is within half the wall width + tolerance
                double halfWidth = wall.Width / 2.0;
                if (distance <= halfWidth + tolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    bestWall = wall;
                    bestProjection = new XYZ(projectedPt.X, projectedPt.Y, point.Z);

                    // Compute wall direction from curve tangent at projected parameter
                    XYZ p0 = curve.GetEndPoint(0);
                    XYZ p1 = curve.GetEndPoint(1);
                    bestDirection = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0).Normalize();
                }
            }

            if (bestWall == null)
                return null;

            return (bestWall, bestProjection, bestDirection, bestDistance);
        }

        /// <summary>
        /// Highlights the specified face
        /// </summary>
        /// <param name="doc">Current document</param>
        /// <param name="faceRef">Reference to the face to highlight</param>
        /// <param name="duration">Highlight duration in milliseconds; defaults to 3000 milliseconds</param>
        public static void HighlightFace(this Document doc, Reference faceRef)
        {
            if (faceRef == null) return;

            // Get the solid fill pattern
            FillPatternElement solidFill = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(x => x.GetFillPattern().IsSolidFill);

            if (solidFill == null)
            {
                TaskDialog.Show("Error", "No solid fill pattern was found");
                return;
            }

            // Create highlight display settings
            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceForegroundPatternColor(new Color(255, 0, 0)); // Red
            ogs.SetSurfaceForegroundPatternId(solidFill.Id);
            ogs.SetSurfaceTransparency(0); // Opaque

            // Apply the highlight
            doc.ActiveView.SetElementOverrides(faceRef.ElementId, ogs);
        }

        /// <summary>
        /// Extracts the two principal direction vectors of a face
        /// </summary>
        /// <param name="face">Input face</param>
        /// <returns>A tuple containing the primary and secondary directions</returns>
        /// <exception cref="ArgumentNullException">Thrown when the face is null</exception>
        /// <exception cref="ArgumentException">Thrown when the face profile cannot form a valid shape</exception>
        /// <exception cref="InvalidOperationException">Thrown when no valid direction can be extracted</exception>
        public static (XYZ PrimaryDirection, XYZ SecondaryDirection) GetMainDirections(this Face face)
        {
            // 1. Validate the argument
            if (face == null)
                throw new ArgumentNullException(nameof(face), "Face cannot be null");

            // 2. Get the face normal for any subsequent perpendicular-vector calculation
            XYZ faceNormal = face.ComputeNormal(new UV(0.5, 0.5));

            // 3. Get the outer profile of the face
            EdgeArrayArray edgeLoops = face.EdgeLoops;
            if (edgeLoops.Size == 0)
                throw new ArgumentException("The face has no valid edge loop", nameof(face));

            // The first loop is usually the outer profile
            EdgeArray outerLoop = edgeLoops.get_Item(0);

            // 4. Calculate the direction vector and length of each edge
            List<XYZ> edgeDirections = new List<XYZ>();  // Unit direction vector for each edge
            List<double> edgeLengths = new List<double>(); // Length of each edge

            foreach (Edge edge in outerLoop)
            {
                Curve curve = edge.AsCurve();
                XYZ startPoint = curve.GetEndPoint(0);
                XYZ endPoint = curve.GetEndPoint(1);

                // Calculate the vector from the start point to the end point
                XYZ direction = endPoint - startPoint;
                double length = direction.GetLength();

                // Ignore edges that are too short, possibly due to coincident vertices or numerical precision
                if (length > 1e-10)
                {
                    edgeDirections.Add(direction.Normalize());  // Store the normalized direction vector
                    edgeLengths.Add(length);                    // Store the edge length
                }
            }

            if (edgeDirections.Count < 4) // Require at least four edges
            {
                throw new ArgumentException("The supplied face does not have enough edges to form a valid shape", nameof(face));
            }

            // 5. Group edges with similar directions
            List<List<int>> directionGroups = new List<List<int>>();  // Direction groups, each containing edge indices

            for (int i = 0; i < edgeDirections.Count; i++)
            {
                bool foundGroup = false;
                XYZ currentDirection = edgeDirections[i];

                // Try to add the current edge to an existing direction group
                for (int j = 0; j < directionGroups.Count; j++)
                {
                    var group = directionGroups[j];
                    // Calculate the current group's weighted average direction
                    XYZ groupAvgDir = CalculateWeightedAverageDirection(group, edgeDirections, edgeLengths);

                    // Check whether the current direction resembles the group average in either direction
                    double dotProduct = Math.Abs(groupAvgDir.DotProduct(currentDirection));
                    if (dotProduct > 0.8) // Directions within approximately 30 degrees are considered similar
                    {
                        group.Add(i);  // Add the current edge index to this direction group
                        foundGroup = true;
                        break;
                    }
                }

                // Create a new group if the current edge is not similar to any existing group
                if (!foundGroup)
                {
                    List<int> newGroup = new List<int> { i };
                    directionGroups.Add(newGroup);
                }
            }

            // 6. Calculate each direction group's total weight (sum of edge lengths) and average direction
            List<double> groupWeights = new List<double>();
            List<XYZ> groupDirections = new List<XYZ>();

            foreach (var group in directionGroups)
            {
                // Calculate the total length of all edges in this group
                double totalLength = 0;
                foreach (int edgeIndex in group)
                {
                    totalLength += edgeLengths[edgeIndex];
                }
                groupWeights.Add(totalLength);

                // Calculate the weighted average direction of this group
                groupDirections.Add(CalculateWeightedAverageDirection(group, edgeDirections, edgeLengths));
            }

            // 7. Sort by weight and extract the principal directions
            int[] sortedIndices = Enumerable.Range(0, groupDirections.Count)
                .OrderByDescending(i => groupWeights[i])
                .ToArray();

            // 8. Construct the result
            if (groupDirections.Count >= 2)
            {
                // Use the two highest-weight groups as the primary and secondary directions
                int primaryIndex = sortedIndices[0];
                int secondaryIndex = sortedIndices[1];

                return (
                    PrimaryDirection: groupDirections[primaryIndex],      // Primary direction
                    SecondaryDirection: groupDirections[secondaryIndex]   // Secondary direction
                );
            }
            else if (groupDirections.Count == 1)
            {
                // With only one direction group, construct a secondary direction perpendicular to the primary direction
                XYZ primaryDirection = groupDirections[0];
                // Use the cross product of the face normal and primary direction to create a perpendicular vector
                XYZ secondaryDirection = faceNormal.CrossProduct(primaryDirection).Normalize();

                return (
                    PrimaryDirection: primaryDirection,         // Primary direction
                    SecondaryDirection: secondaryDirection      // Constructed perpendicular secondary direction
                );
            }
            else
            {
                // No valid direction could be extracted (rare)
                throw new InvalidOperationException("Unable to extract a valid direction from the face");
            }
        }

        /// <summary>
        /// Calculates the weighted average direction of a group of edges based on edge length
        /// </summary>
        /// <param name="edgeIndices">List of edge indices</param>
        /// <param name="directions">Direction vectors of all edges</param>
        /// <param name="lengths">Lengths of all edges</param>
        /// <returns>The normalized weighted average direction vector</returns>
        public static XYZ CalculateWeightedAverageDirection(List<int> edgeIndices, List<XYZ> directions, List<double> lengths)
        {
            if (edgeIndices.Count == 0)
                return null;

            double sumX = 0, sumY = 0, sumZ = 0;
            XYZ referenceDir = directions[edgeIndices[0]];  // Use the first direction in the group as the reference

            foreach (int i in edgeIndices)
            {
                XYZ currentDir = directions[i];

                // Calculate the dot product with the reference direction to determine whether reversal is needed
                double dot = referenceDir.DotProduct(currentDir);

                // If the direction is opposite (negative dot product), reverse the vector when calculating its contribution
                // This aligns vectors within the group and prevents cancellation
                double factor = (dot >= 0) ? lengths[i] : -lengths[i];

                // Accumulate the weighted vector components
                sumX += currentDir.X * factor;
                sumY += currentDir.Y * factor;
                sumZ += currentDir.Z * factor;
            }

            // Create and normalize the resultant vector
            XYZ avgDir = new XYZ(sumX, sumY, sumZ);
            double magnitude = avgDir.GetLength();

            // Guard against a zero vector
            if (magnitude < 1e-10)
                return referenceDir;  // Fall back to the reference direction

            return avgDir.Normalize();  // Return the normalized direction vector
        }

        /// <summary>
        /// Determines whether three vectors follow the right-hand rule and are mutually perpendicular
        /// </summary>
        /// <param name="thumb">Thumb direction vector</param>
        /// <param name="indexFinger">Index-finger direction vector</param>
        /// <param name="middleFinger">Middle-finger direction vector</param>
        /// <param name="tolerance">Comparison tolerance; defaults to 1e-6</param>
        /// <returns>True if the vectors follow the right-hand rule and are mutually perpendicular; otherwise, false</returns>
        public static bool IsRightHandRuleCompliant(this XYZ thumb, XYZ indexFinger, XYZ middleFinger, double tolerance = 1e-6)
        {
            // Check whether the three vectors are mutually perpendicular (all dot products are near zero)
            double dotThumbIndex = Math.Abs(thumb.DotProduct(indexFinger));
            double dotThumbMiddle = Math.Abs(thumb.DotProduct(middleFinger));
            double dotIndexMiddle = Math.Abs(indexFinger.DotProduct(middleFinger));

            bool areOrthogonal = (dotThumbIndex <= tolerance) &&
                                  (dotThumbMiddle <= tolerance) &&
                                  (dotIndexMiddle <= tolerance);

            // Check the right-hand rule only when the three vectors are mutually perpendicular
            if (!areOrthogonal)
                return false;

            // Calculate the dot product of the cross-product vector and thumb to test the right-hand rule
            XYZ crossProduct = indexFinger.CrossProduct(middleFinger);
            double rightHandTest = crossProduct.DotProduct(thumb);

            // A positive dot product indicates compliance with the right-hand rule
            return rightHandTest > tolerance;
        }

        /// <summary>
        /// Generates an index-finger direction from the thumb and middle-finger directions according to the right-hand rule
        /// </summary>
        /// <param name="thumb">Thumb direction vector</param>
        /// <param name="middleFinger">Middle-finger direction vector</param>
        /// <param name="tolerance">Perpendicularity tolerance; defaults to 1e-6</param>
        /// <returns>The generated index-finger direction vector, or null if the input vectors are not perpendicular</returns>
        public static XYZ GenerateIndexFinger(this XYZ thumb, XYZ middleFinger, double tolerance = 1e-6)
        {
            // Normalize the input vectors first
            XYZ normalizedThumb = thumb.Normalize();
            XYZ normalizedMiddleFinger = middleFinger.Normalize();

            // Check whether the two vectors are perpendicular (dot product near zero)
            double dotProduct = normalizedThumb.DotProduct(normalizedMiddleFinger);

            // The vectors are not perpendicular if the absolute dot product exceeds the tolerance
            if (Math.Abs(dotProduct) > tolerance)
            {
                return null;
            }

            // Calculate and negate the cross product to obtain the index-finger direction
            XYZ indexFinger = normalizedMiddleFinger.CrossProduct(normalizedThumb).Negate();

            // Return the normalized index-finger direction vector
            return indexFinger.Normalize();
        }

        /// <summary>
        /// Creates or retrieves a level at the specified elevation
        /// </summary>
        /// <param name="doc">Revit document</param>
        /// <param name="elevation">Level elevation (ft)</param>
        /// <param name="levelName">Level name</param>
        /// <returns></returns>
        public static Level CreateOrGetLevel(this Document doc, double elevation, string levelName)
        {
            // First look for an existing level at the specified elevation
            Level existingLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 0.1 / 304.8);

            if (existingLevel != null)
                return existingLevel;

            // Create a new level
            Level newLevel = Level.Create(doc, elevation);
            // Set the level name
            Level namesakeLevel = new FilteredElementCollector(doc)
                 .OfClass(typeof(Level))
                 .Cast<Level>()
                 .FirstOrDefault(l => l.Name == levelName);
            if (namesakeLevel != null)
            {
                levelName = $"{levelName}_{newLevel.Id.GetValue()}";
            }
            newLevel.Name = levelName;

            return newLevel;
        }

        /// <summary>
        /// Finds the level nearest to the specified elevation
        /// </summary>
        /// <param name="doc">Current Revit document</param>
        /// <param name="height">Target elevation (Revit internal units)</param>
        /// <returns>The level nearest to the target elevation, or null if the document contains no levels</returns>
        public static Level FindNearestLevel(this Document doc, double height)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc), "Document cannot be null");

            // Use a LINQ query to get the nearest level directly
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => Math.Abs(level.Elevation - height))
                .FirstOrDefault();
        }

        ///// <summary>
        ///// Refreshes the view and adds a delay
        ///// </summary>
        //public static void Refresh(this Document doc, int waitingTime = 0, bool allowOperation = true)
        //{
        //    UIApplication uiApp = new UIApplication(doc.Application);
        //    UIDocument uiDoc = uiApp.ActiveUIDocument;

        //    // Check whether the document can be modified
        //    if (uiDoc.Document.IsModifiable)
        //    {
        //        // Update the model
        //        uiDoc.Document.Regenerate();
        //    }
        //    // Update the UI
        //    uiDoc.RefreshActiveView();

        //    // Wait for the specified delay
        //    if (waitingTime != 0)
        //    {
        //        System.Threading.Thread.Sleep(waitingTime);
        //    }

        //    // Allow the user to perform non-safe operations
        //    if (allowOperation)
        //    {
        //        System.Windows.Forms.Application.DoEvents();
        //    }
        //}

        /// <summary>
        /// Saves the specified message to a file on the desktop (overwriting by default)
        /// </summary>
        /// <param name="message">Message content to save</param>
        /// <param name="fileName">Target file name</param>
        public static void SaveToDesktop(this string message, string fileName = "temp.json", bool isAppend = false)
        {
            // Ensure fileName includes an extension
            if (!Path.HasExtension(fileName))
            {
                fileName += ".txt"; // Add the .txt extension by default
            }

            // Get the desktop path
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            // Build the complete file path
            string filePath = Path.Combine(desktopPath, fileName);

            // Write to the file (overwrite mode)
            using (StreamWriter sw = new StreamWriter(filePath, isAppend))
            {
                sw.WriteLine($"{message}");
            }
        }

    }
}
