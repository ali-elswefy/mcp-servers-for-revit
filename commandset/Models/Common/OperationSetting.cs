using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Models.Common
{
    /// <summary>
    /// Defines the types of operations that can be performed on elements
    /// </summary>
    public enum ElementOperationType
    {
        /// <summary>
        /// Select elements
        /// </summary>
        Select,

        /// <summary>
        /// Zoom to the selection box
        /// </summary>
        SelectionBox,

        /// <summary>
        /// Set element color and fill
        /// </summary>
        SetColor,

        /// <summary>
        /// Set element transparency
        /// </summary>
        SetTransparency,

        /// <summary>
        /// Delete elements
        /// </summary>
        Delete,

        /// <summary>
        /// Hide elements
        /// </summary>
        Hide,

        /// <summary>
        /// Temporarily hide elements
        /// </summary>
        TempHide,

        /// <summary>
        /// Isolate elements (display them exclusively)
        /// </summary>
        Isolate,

        /// <summary>
        /// Unhide elements
        /// </summary>
        Unhide,

        /// <summary>
        /// Reset isolation (display all elements)
        /// </summary>
        ResetIsolate,
    }


    /// <summary>
    /// Settings for element operations
    /// </summary>
    public class OperationSetting
    {
        /// <summary>
        /// IDs of the elements to operate on
        /// </summary>
        [JsonProperty("elementIds")]
        public List<int> ElementIds = new List<int>();

        /// <summary>
        /// Action to perform, stored as the string value of the ElementOperationType enum
        /// </summary>
        [JsonProperty("action")]
        public string Action { get; set; } = "Select";

        /// <summary>
        /// Transparency value from 0 to 100; higher values are more transparent
        /// </summary>
        [JsonProperty("transparencyValue")]
        public int TransparencyValue { get; set; } = 50;

        /// <summary>
        /// Element color in RGB format; defaults to red
        /// </summary>
        [JsonProperty("colorValue")]
        public int[] ColorValue { get; set; } = new int[] { 255, 0, 0 }; // Red by default
    }
}
