using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

/// <summary>
///     Point-based element
/// </summary>
public class PointElement
{
    public PointElement()
    {
        Parameters = new Dictionary<string, double>();
    }

    /// <summary>
    ///     Element category
    /// </summary>
    [JsonProperty("category")]
    public string Category { get; set; } = "INVALID";

    /// <summary>
    ///     Type ID
    /// </summary>
    [JsonProperty("typeId")]
    public int TypeId { get; set; } = -1;

    /// <summary>
    ///     Location point coordinates
    /// </summary>
    [JsonProperty("locationPoint")]
    public JZPoint LocationPoint { get; set; }

    /// <summary>
    ///     Width
    /// </summary>
    [JsonProperty("width")]
    public double Width { get; set; } = -1;

    /// <summary>
    ///     Depth
    /// </summary>
    [JsonProperty("depth")]
    public double Depth { get; set; }

    /// <summary>
    ///     Height
    /// </summary>
    [JsonProperty("height")]
    public double Height { get; set; }

    /// <summary>
    ///     Base level
    /// </summary>
    [JsonProperty("baseLevel")]
    public double BaseLevel { get; set; }

    /// <summary>
    ///     Base offset
    /// </summary>
    [JsonProperty("baseOffset")]
    public double BaseOffset { get; set; }

    /// <summary>
    ///     Rotation angle in degrees for non-hosted elements, such as furniture
    /// </summary>
    [JsonProperty("rotation")]
    public double Rotation { get; set; } = 0;

    /// <summary>
    ///     Explicit host wall ElementId; -1 enables automatic detection
    /// </summary>
    [JsonProperty("hostWallId")]
    public int HostWallId { get; set; } = -1;

    /// <summary>
    ///     Whether to flip the facing orientation of doors or windows
    /// </summary>
    [JsonProperty("facingFlipped")]
    public bool FacingFlipped { get; set; } = false;

    /// <summary>
    ///     Parameter values
    /// </summary>
    [JsonProperty("parameters")]
    public Dictionary<string, double> Parameters { get; set; }
}
