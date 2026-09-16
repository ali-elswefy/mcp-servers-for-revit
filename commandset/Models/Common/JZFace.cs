using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

/// <summary>
///     Three-dimensional face
/// </summary>
public class JZFace
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public JZFace()
    {
        InnerLoops = new List<List<JZLine>>();
        OuterLoop = new List<JZLine>();
    }

    /// <summary>
    ///     Outer loop (List&lt;JZLine&gt;)
    /// </summary>
    [JsonProperty("outerLoop")]
    public List<JZLine> OuterLoop { get; set; }

    /// <summary>
    ///     Inner loops (List&lt;List&lt;JZLine&gt;&gt;, representing one or more inner loops)
    /// </summary>
    [JsonProperty("innerLoops")]
    public List<List<JZLine>> InnerLoops { get; set; }
}
