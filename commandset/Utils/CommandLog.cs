using RevitMcp.Logging;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Add-in stream logger for the command set (Logs\mcp_addin_yyyyMMdd.log). Shares its file and
    /// level with the plugin; see <see cref="TraceLog"/>.
    /// </summary>
    public static class CommandLog
    {
        public static bool DebugEnabled => TraceLog.IsEnabled(LogSeverity.Debug);

        public static void Debug(string message, params object[] args) => TraceLog.Write(LogChannel.Addin, LogSeverity.Debug, message, args);

        public static void Info(string message, params object[] args) => TraceLog.Write(LogChannel.Addin, LogSeverity.Info, message, args);

        public static void Warning(string message, params object[] args) => TraceLog.Write(LogChannel.Addin, LogSeverity.Warning, message, args);

        public static void Error(string message, params object[] args) => TraceLog.Write(LogChannel.Addin, LogSeverity.Error, message, args);
    }
}
