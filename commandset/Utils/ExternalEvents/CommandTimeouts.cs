using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Utils.ExternalEvents
{
    /// <summary>
    /// Central definition of how long a command waits for the Revit UI thread.
    ///
    /// Every wait is capped at <see cref="MaximumMs"/>, which stays below the outer timeout used by
    /// MCP clients (Node server and Python service: 150 s), so a client always receives the add-in's
    /// structured timeout result instead of hitting its own generic timeout first. If a client's
    /// outer timeout changes, keep it at least <see cref="ClientMarginMs"/> above the wait it requests.
    ///
    /// Resolution order: per-request <c>timeoutMs</c> parameter, then the environment variable
    /// <c>REVIT_MCP_TIMEOUT_MS_&lt;COMMAND_NAME&gt;</c> (e.g. REVIT_MCP_TIMEOUT_MS_SEND_CODE_TO_REVIT),
    /// then the command default. The result is clamped to [<see cref="MinimumMs"/>, <see cref="MaximumMs"/>].
    /// </summary>
    public static class CommandTimeouts
    {
        public const int MinimumMs = 1_000;
        public const int MaximumMs = 135_000;
        public const int ClientMarginMs = 15_000;

        public const int ExecuteCodeDefaultMs = 120_000;
        public const int CurrentViewInfoDefaultMs = 10_000;
        public const int CurrentViewElementsDefaultMs = 60_000;
        public const int ModelStatisticsDefaultMs = 120_000;
        public const int ListLinkedModelsDefaultMs = 60_000;
        public const int QueryLinkedElementsDefaultMs = 120_000;
        public const int LinkedElementDetailsDefaultMs = 60_000;

        public const string TimeoutParameterName = "timeoutMs";
        public const string EnvironmentVariablePrefix = "REVIT_MCP_TIMEOUT_MS_";

        public static int Resolve(string commandName, string requestId, int defaultMs, JObject parameters)
        {
            int? requested = ReadRequestTimeout(commandName, requestId, parameters) ?? ReadEnvironmentTimeout(commandName);
            return Clamp(requested ?? defaultMs);
        }

        private static int? ReadRequestTimeout(string commandName, string requestId, JObject parameters)
        {
            JToken token = parameters?[TimeoutParameterName];
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw CommandErrors.Validation($"'{TimeoutParameterName}' must be a number of milliseconds.", commandName, requestId);

            return (int)Math.Min(int.MaxValue, Math.Max(0, token.Value<double>()));
        }

        private static int? ReadEnvironmentTimeout(string commandName)
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentVariablePrefix + commandName.ToUpperInvariant());
            return int.TryParse(value, out int ms) && ms > 0 ? ms : (int?)null;
        }

        private static int Clamp(int ms) => Math.Min(MaximumMs, Math.Max(MinimumMs, ms));
    }
}
