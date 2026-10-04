using RevitMcp.Logging;
using RevitMCPSDK.API.Interfaces;

namespace revit_mcp_plugin.Utils
{
    /// <summary>
    /// SDK logger used by the plugin and its command loader. Writes to the add-in stream
    /// (Logs\mcp_addin_yyyyMMdd.log); the level comes from settings.logLevel.
    /// </summary>
    public class Logger : ILogger
    {
        public Logger()
        {
            TraceLog.UseDirectory(PathManager.GetLogsDirectoryPath());
        }

        public void Log(LogLevel level, string message, params object[] args)
        {
            TraceLog.Write(LogChannel.Addin, Map(level), message, args);
        }

        public void Debug(string message, params object[] args)
        {
            Log(LogLevel.Debug, message, args);
        }

        public void Info(string message, params object[] args)
        {
            Log(LogLevel.Info, message, args);
        }

        public void Warning(string message, params object[] args)
        {
            Log(LogLevel.Warning, message, args);
        }

        public void Error(string message, params object[] args)
        {
            Log(LogLevel.Error, message, args);
        }

        private static LogSeverity Map(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return LogSeverity.Debug;
                case LogLevel.Warning: return LogSeverity.Warning;
                case LogLevel.Error: return LogSeverity.Error;
                default: return LogSeverity.Info;
            }
        }
    }
}
