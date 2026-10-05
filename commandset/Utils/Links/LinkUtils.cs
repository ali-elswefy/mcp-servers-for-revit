using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Links;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Utils.Links
{
    /// <summary>One RevitLinkInstance in the host model with what is needed to query it.</summary>
    public class LinkTarget
    {
        public RevitLinkInstance Instance { get; set; }
        public RevitLinkType Type { get; set; }

        /// <summary>Null when the link is not loaded.</summary>
        public Document Document { get; set; }

        /// <summary>Link coordinates to host coordinates (shared coordinates and placement included).</summary>
        public Transform ToHost { get; set; }
        public Transform FromHost { get; set; }

        public long InstanceId { get; set; }
        public string LinkName { get; set; }
        public string InstanceName { get; set; }
        public string Status { get; set; }
        public string DocumentTitle { get; set; }

        public bool IsLoaded => Document != null;
    }

    /// <summary>Helpers for working with Revit links. Everything runs on the Revit thread.</summary>
    public static class LinkUtils
    {
        public const double MmPerFoot = 304.8;

        // ---------------------------------------------------------------- finding links

        public static List<LinkTarget> GetTargets(Document host)
        {
            var targets = new List<LinkTarget>();

            foreach (RevitLinkInstance instance in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)))
            {
                var type = host.GetElement(instance.GetTypeId()) as RevitLinkType;

                Document linkDocument = null;
                try
                {
                    linkDocument = instance.GetLinkDocument();
                }
                catch (Exception)
                {
                    // Treated as not loaded; the status below says why when Revit knows.
                }

                Transform toHost = linkDocument != null ? instance.GetTotalTransform() : null;
                targets.Add(new LinkTarget
                {
                    Instance = instance,
                    Type = type,
                    Document = linkDocument,
                    ToHost = toHost,
                    FromHost = toHost?.Inverse,
                    InstanceId = instance.Id.GetValue(),
                    LinkName = type?.Name ?? instance.Name,
                    InstanceName = instance.Name,
                    Status = type != null ? SafeStatus(type) : "Unknown",
                    DocumentTitle = linkDocument?.Title
                });
            }

            return targets.OrderBy(t => t.LinkName, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.InstanceId).ToList();
        }

        private static string SafeStatus(RevitLinkType type)
        {
            try
            {
                return type.GetLinkedFileStatus().ToString();
            }
            catch (Exception)
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Picks the link instances a request refers to: one by id, those whose name contains the given text,
        /// or all. A selector that matches nothing is a validation error that lists the available links.
        /// </summary>
        public static List<LinkTarget> SelectTargets(Document host, long? linkInstanceId, string linkName, string command, string requestId)
        {
            List<LinkTarget> all = GetTargets(host);
            List<LinkTarget> selected;

            if (linkInstanceId.HasValue)
            {
                selected = all.Where(t => t.InstanceId == linkInstanceId.Value).ToList();
            }
            else if (linkName != null)
            {
                selected = all.Where(t => Contains(t.LinkName, linkName) || Contains(t.InstanceName, linkName) || Contains(t.DocumentTitle, linkName)).ToList();
            }
            else
            {
                return all;
            }

            if (selected.Count == 0)
            {
                string wanted = linkInstanceId.HasValue ? $"link instance {linkInstanceId.Value}" : $"a link named '{linkName}'";
                throw CommandErrors.Validation(
                    all.Count == 0
                        ? $"The host model has no Revit links, so {wanted} cannot exist."
                        : $"No {wanted} was found. Use list_linked_models to see the available links.",
                    command, requestId,
                    new
                    {
                        reason = "link_not_found",
                        availableLinks = all.Select(t => new { linkInstanceId = t.InstanceId, linkName = t.LinkName, status = t.Status })
                    });
            }

            return selected;
        }

        private static bool Contains(string text, string part) =>
            text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        // ---------------------------------------------------------------- link information

        public static LinkTransformInfo DescribeTransform(Transform toHost)
        {
            if (toHost == null)
                return null;

            return new LinkTransformInfo
            {
                Origin = ToMm(toHost.Origin),
                RotationZDegrees = Math.Round(Math.Atan2(toHost.BasisX.Y, toHost.BasisX.X) * 180.0 / Math.PI, 6),
                IsIdentity = toHost.IsIdentity,
                HasReflection = toHost.HasReflection
            };
        }

        public static string GetLinkPath(Document host, LinkTarget target)
        {
            try
            {
                if (target.Type != null)
                {
                    ExternalFileReference reference = ExternalFileUtils.GetExternalFileReference(host, target.Type.Id);
                    if (reference != null)
                        return ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
                }
            }
            catch (Exception)
            {
                // Cloud or otherwise unresolvable paths; fall back below.
            }

            return string.IsNullOrEmpty(target.Document?.PathName) ? null : target.Document.PathName;
        }

        // ---------------------------------------------------------------- coordinates

        public static MmPoint ToMm(XYZ point) => new MmPoint(point.X * MmPerFoot, point.Y * MmPerFoot, point.Z * MmPerFoot);

        public static MmPoint ToHostMm(XYZ linkPoint, Transform toHost) => ToMm(toHost.OfPoint(linkPoint));

        /// <summary>Axis-aligned box of the element in host coordinates, mm; null when the element has no box.</summary>
        public static MmBox GetHostBox(Element element, Transform toHost)
        {
            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box == null)
                return null;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (bool useMaxX in new[] { false, true })
            foreach (bool useMaxY in new[] { false, true })
            foreach (bool useMaxZ in new[] { false, true })
            {
                var corner = new XYZ(
                    useMaxX ? box.Max.X : box.Min.X,
                    useMaxY ? box.Max.Y : box.Min.Y,
                    useMaxZ ? box.Max.Z : box.Min.Z);
                XYZ hostPoint = toHost.OfPoint(box.Transform.OfPoint(corner));

                minX = Math.Min(minX, hostPoint.X); maxX = Math.Max(maxX, hostPoint.X);
                minY = Math.Min(minY, hostPoint.Y); maxY = Math.Max(maxY, hostPoint.Y);
                minZ = Math.Min(minZ, hostPoint.Z); maxZ = Math.Max(maxZ, hostPoint.Z);
            }

            return new MmBox
            {
                Min = new MmPoint(minX * MmPerFoot, minY * MmPerFoot, minZ * MmPerFoot),
                Max = new MmPoint(maxX * MmPerFoot, maxY * MmPerFoot, maxZ * MmPerFoot)
            };
        }

        /// <summary>
        /// The link-coordinate box that surrounds a host-coordinate box. For a rotated link this is larger than
        /// the host box, so it is only a pre-filter: callers still check each element's host box.
        /// </summary>
        public static Outline ToLinkOutline(MmBox hostBox, Transform fromHost)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (bool useMaxX in new[] { false, true })
            foreach (bool useMaxY in new[] { false, true })
            foreach (bool useMaxZ in new[] { false, true })
            {
                var hostPoint = new XYZ(
                    (useMaxX ? hostBox.Max.X : hostBox.Min.X) / MmPerFoot,
                    (useMaxY ? hostBox.Max.Y : hostBox.Min.Y) / MmPerFoot,
                    (useMaxZ ? hostBox.Max.Z : hostBox.Min.Z) / MmPerFoot);
                XYZ linkPoint = fromHost.OfPoint(hostPoint);

                minX = Math.Min(minX, linkPoint.X); maxX = Math.Max(maxX, linkPoint.X);
                minY = Math.Min(minY, linkPoint.Y); maxY = Math.Max(maxY, linkPoint.Y);
                minZ = Math.Min(minZ, linkPoint.Z); maxZ = Math.Max(maxZ, linkPoint.Z);
            }

            // A degenerate (zero-size) outline is rejected by Revit, so give it a hair of thickness.
            const double epsilon = 1e-6;
            return new Outline(new XYZ(minX - epsilon, minY - epsilon, minZ - epsilon), new XYZ(maxX + epsilon, maxY + epsilon, maxZ + epsilon));
        }

        public static MmBox Grow(MmBox box, double distanceMm) => new MmBox
        {
            Min = new MmPoint(box.Min.X - distanceMm, box.Min.Y - distanceMm, box.Min.Z - distanceMm),
            Max = new MmPoint(box.Max.X + distanceMm, box.Max.Y + distanceMm, box.Max.Z + distanceMm)
        };

        public static LinkedLocation GetHostLocation(Element element, Transform toHost)
        {
            Location location = element.Location;

            if (location is LocationPoint point)
                return new LinkedLocation { Point = ToHostMm(point.Point, toHost) };

            if (location is LocationCurve curve && curve.Curve != null && curve.Curve.IsBound)
            {
                return new LinkedLocation
                {
                    Start = ToHostMm(curve.Curve.GetEndPoint(0), toHost),
                    End = ToHostMm(curve.Curve.GetEndPoint(1), toHost)
                };
            }

            return null;
        }

        // ---------------------------------------------------------------- categories and parameters

        public static string BuiltInCategoryName(Category category)
        {
            long value = category.Id.GetValue();
            return value < 0 ? ((BuiltInCategory)(int)value).ToString() : null;
        }

        /// <summary>The value as Revit displays it, in the document's units. Empty when the parameter has no value.</summary>
        public static string DisplayValue(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
                return string.Empty;

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString() ?? string.Empty;
                case StorageType.Integer:
                    return parameter.AsValueString() ?? parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return parameter.AsValueString() ?? parameter.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    ElementId id = parameter.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId)
                        return string.Empty;
                    return parameter.AsValueString() ?? id.GetValue().ToString(CultureInfo.InvariantCulture);
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// Per-link lookups for the type, level and parameter of elements, cached for the duration of one request
    /// because a category of thousands of elements shares a handful of types and levels.
    /// </summary>
    public class LinkedElementReader
    {
        private readonly Document _document;
        private readonly Dictionary<long, ElementType> _types = new Dictionary<long, ElementType>();
        private readonly Dictionary<long, string> _levelNames = new Dictionary<long, string>();

        public LinkedElementReader(Document linkDocument)
        {
            _document = linkDocument;
        }

        public ElementType GetElementType(Element element)
        {
            ElementId typeId = element.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId)
                return null;

            long key = typeId.GetValue();
            if (!_types.TryGetValue(key, out ElementType type))
            {
                type = _document.GetElement(typeId) as ElementType;
                _types[key] = type;
            }
            return type;
        }

        public string GetFamilyName(Element element) => GetElementType(element)?.FamilyName;

        public string GetTypeName(Element element) => GetElementType(element)?.Name;

        public string GetLevelName(Element element)
        {
            ElementId levelId = element.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
                return null;

            long key = levelId.GetValue();
            if (!_levelNames.TryGetValue(key, out string name))
            {
                name = (_document.GetElement(levelId) as Level)?.Name;
                _levelNames[key] = name;
            }
            return name;
        }

        /// <summary>Instance parameter first, then the same-named type parameter.</summary>
        public Parameter FindParameter(Element element, string name)
        {
            return element.LookupParameter(name) ?? GetElementType(element)?.LookupParameter(name);
        }
    }
}
