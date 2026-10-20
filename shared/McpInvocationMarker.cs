using System;

namespace RevitMcp.Interop
{
    /// <summary>
    /// Counts MCP invocations run on the Revit UI thread. The command set bumps it before each
    /// queued invocation; the plugin's failure monitor uses it to tell invocations apart when one
    /// Revit callback runs several of them, so a halt in one never blocks the next.
    ///
    /// This file is compiled into both assemblies; the value is shared through AppDomain data.
    /// Only the Revit UI thread writes it.
    /// </summary>
    public static class McpInvocationMarker
    {
        public const string DataKey = "RevitMcp.InvocationSequence";

        public static long Current
        {
            get
            {
                object value = AppDomain.CurrentDomain.GetData(DataKey);
                return value is long sequence ? sequence : 0;
            }
        }

        /// <summary>Marks the start of a new invocation. Call on the Revit UI thread.</summary>
        public static long Begin()
        {
            long next = Current + 1;
            AppDomain.CurrentDomain.SetData(DataKey, next);
            return next;
        }
    }
}
