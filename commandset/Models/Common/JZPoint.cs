using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

/// <summary>
///     Three-dimensional point
/// </summary>
public class JZPoint
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public JZPoint()
    {
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    public JZPoint(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>
    ///     Constructor
    /// </summary>
    public JZPoint(double x, double y)
    {
        X = x;
        Y = y;
        Z = 0;
    }

    [JsonProperty("x")] public double X { get; set; }

    [JsonProperty("y")] public double Y { get; set; }

    [JsonProperty("z")] public double Z { get; set; }

    /// <summary>
    ///     Converts to a Revit XYZ point
    ///     Converts units from millimeters to feet
    /// </summary>
    public static XYZ ToXYZ(JZPoint jzPoint)
    {
        return new XYZ(jzPoint.X / 304.8, jzPoint.Y / 304.8, jzPoint.Z / 304.8);
    }
}
