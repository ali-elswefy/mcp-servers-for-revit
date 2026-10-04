using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace RevitMcp.Logging
{
    /// <summary>Which stream a line belongs to. Each channel has its own file.</summary>
    public enum LogChannel
    {
        /// <summary>Add-in activity: startup, command loading, dispatch, execution on the Revit UI thread.</summary>
        Addin,

        /// <summary>MCP wire activity: connections, requests received, responses sent.</summary>
        Protocol
    }

    public enum LogSeverity
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }

    /// <summary>
    /// File logger shared by the plugin and the command set. This single source file is compiled
    /// into both assemblies (they cannot reference each other), so the settings that must agree are
    /// exchanged through AppDomain data and every write is a single atomic append, which lets both
    /// assemblies write the same file.
    ///
    /// Files: Logs\mcp_addin_yyyyMMdd.log and Logs\mcp_protocol_yyyyMMdd.log, kept for 14 days.
    /// Line: "2026-10-04 11:02:03.123 INFO  protocol T9   conn=1 req=u1 ...".
    ///
    /// Level (Debug, Info, Warning, Error): the REVIT_MCP_LOG_LEVEL environment variable if set, else
    /// "settings.logLevel" in Commands\commandRegistry.json (applied when the service is switched on),
    /// else Info. Debug also records request and result bodies, truncated.
    /// </summary>
    public static class TraceLog
    {
        public const string LevelDataKey = "RevitMcp.LogLevel";
        public const string DirectoryDataKey = "RevitMcp.LogDirectory";
        public const string LevelEnvironmentVariable = "REVIT_MCP_LOG_LEVEL";
        public const int RetentionDays = 14;
        public const int DefaultPreviewLength = 1000;

        private const int MutexWaitMilliseconds = 2000;
        private static readonly Mutex WriteMutex = new Mutex(false, "Local\\RevitMcp.TraceLog.Write");
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly string EnvironmentLevel = Environment.GetEnvironmentVariable(LevelEnvironmentVariable);
        private static string _derivedDirectory;
        private static int _retentionDone;

        // ---- configuration ----

        /// <summary>Sets the log folder (shared with the other assembly through AppDomain data) and prunes old files once.</summary>
        public static void UseDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            AppDomain.CurrentDomain.SetData(DirectoryDataKey, directory);
        }

        /// <summary>Sets the level from configuration. The environment variable still wins.</summary>
        public static void SetLevel(string level)
        {
            AppDomain.CurrentDomain.SetData(LevelDataKey, level);
        }

        public static LogSeverity CurrentLevel
        {
            get
            {
                string value = string.IsNullOrWhiteSpace(EnvironmentLevel)
                    ? AppDomain.CurrentDomain.GetData(LevelDataKey) as string
                    : EnvironmentLevel;
                return ParseLevel(value);
            }
        }

        public static LogSeverity ParseLevel(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "debug":
                case "trace":
                case "verbose":
                    return LogSeverity.Debug;
                case "warn":
                case "warning":
                    return LogSeverity.Warning;
                case "error":
                    return LogSeverity.Error;
                default:
                    return LogSeverity.Info;
            }
        }

        public static string LogDirectory
        {
            get
            {
                string configured = AppDomain.CurrentDomain.GetData(DirectoryDataKey) as string;
                if (!string.IsNullOrWhiteSpace(configured))
                    return EnsureDirectory(configured);

                if (_derivedDirectory == null)
                    _derivedDirectory = EnsureDirectory(DeriveDirectory());
                return _derivedDirectory;
            }
        }

        public static string GetLogPath(LogChannel channel, DateTime? day = null)
        {
            return Path.Combine(LogDirectory,
                $"mcp_{channel.ToString().ToLowerInvariant()}_{(day ?? DateTime.Now):yyyyMMdd}.log");
        }

        public static bool IsEnabled(LogSeverity severity) => severity >= CurrentLevel;

        // ---- writing ----

        public static void Debug(LogChannel channel, string message, params object[] args) => Write(channel, LogSeverity.Debug, message, args);

        public static void Info(LogChannel channel, string message, params object[] args) => Write(channel, LogSeverity.Info, message, args);

        public static void Warning(LogChannel channel, string message, params object[] args) => Write(channel, LogSeverity.Warning, message, args);

        public static void Error(LogChannel channel, string message, params object[] args) => Write(channel, LogSeverity.Error, message, args);

        public static void Write(LogChannel channel, LogSeverity severity, string message, params object[] args)
        {
            if (!IsEnabled(severity))
                return;

            try
            {
                string line = FormatLine(channel, severity, FormatMessage(message, args));
                System.Diagnostics.Debug.WriteLine(line);
                Append(GetLogPath(channel), Utf8.GetBytes(line + Environment.NewLine));
            }
            catch
            {
                // Logging must never break a command or the socket service.
            }
        }

        /// <summary>string.Format that never throws; a message without arguments is used verbatim.</summary>
        public static string FormatMessage(string message, object[] args)
        {
            if (message == null)
                return string.Empty;
            if (args == null || args.Length == 0)
                return message;

            try
            {
                return string.Format(CultureInfo.InvariantCulture, message, args);
            }
            catch (FormatException)
            {
                return message + " | " + string.Join(", ", args.Select(a => a?.ToString() ?? "null"));
            }
        }

        private static string FormatLine(LogChannel channel, LogSeverity severity, string message)
        {
            string label;
            switch (severity)
            {
                case LogSeverity.Debug: label = "DEBUG"; break;
                case LogSeverity.Warning: label = "WARN "; break;
                case LogSeverity.Error: label = "ERROR"; break;
                default: label = "INFO "; break;
            }

            // Keep one entry per line start: continuation lines (stack traces) are indented.
            string body = message.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\n    | ");
            return string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff} {1} {2,-8} T{3,-3} {4}",
                DateTime.Now, label, channel.ToString().ToLowerInvariant(), Thread.CurrentThread.ManagedThreadId, body);
        }

        // .NET's FileMode.Append only seeks to the end of the file when it opens it, so two writers with the file open at the
        // same time overwrite each other's lines. The plugin and the command set each carry their own copy of this class (so
        // a static lock cannot protect them from each other), hence a named mutex, which is shared across assemblies and
        // processes. Each write reopens the file under the mutex, so it always starts at the current end.
        private static void Append(string path, byte[] bytes)
        {
            bool owned = false;
            try
            {
                try
                {
                    owned = WriteMutex.WaitOne(MutexWaitMilliseconds);
                }
                catch (AbandonedMutexException)
                {
                    owned = true; // a previous owner died while holding it; the file is still consistent.
                }

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        {
                            stream.Write(bytes, 0, bytes.Length);
                        }
                        return;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            finally
            {
                if (owned)
                    WriteMutex.ReleaseMutex();
            }
        }

        // ---- helpers for message content ----

        /// <summary>Single-line, length-limited rendering of a body for Debug traces.</summary>
        public static string Preview(string text, int maxLength = DefaultPreviewLength)
        {
            if (text == null)
                return "null";

            string flat = text.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\r");
            return flat.Length <= maxLength ? flat : flat.Substring(0, maxLength) + $"...(+{flat.Length - maxLength} chars)";
        }

        /// <summary>First 8 hex characters of the SHA-256, to correlate a snippet without logging it.</summary>
        public static string ShortHash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Utf8.GetBytes(text ?? string.Empty));
                return BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        // ---- folder handling ----

        // Plugin: <plugin dir>\Logs. Command set: <plugin dir>\Commands\RevitMCPCommandSet\<version>\x.dll -> <plugin dir>\Logs.
        private static string DeriveDirectory()
        {
            string assemblyDirectory = Path.GetDirectoryName(typeof(TraceLog).Assembly.Location);
            for (string directory = assemblyDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                if (string.Equals(Path.GetFileName(directory), "Commands", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(Path.GetDirectoryName(directory), "Logs");
            }

            return string.IsNullOrEmpty(assemblyDirectory)
                ? Path.Combine(Path.GetTempPath(), "revit_mcp", "Logs")
                : Path.Combine(assemblyDirectory, "Logs");
        }

        private static string EnsureDirectory(string directory)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                if (Interlocked.Exchange(ref _retentionDone, 1) == 0)
                    PruneOldLogs(directory);
            }
            catch
            {
                // A missing folder only means lines are dropped.
            }
            return directory;
        }

        private static void PruneOldLogs(string directory)
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (string file in System.IO.Directory.GetFiles(directory, "mcp_*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }
    }
}
