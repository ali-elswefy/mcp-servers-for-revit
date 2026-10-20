using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitMcp.Logging;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Application-wide failure preprocessor for transactions committed by MCP commands.
    ///
    /// Revit shows failure dialogs when a transaction ends with warnings or errors. A dialog
    /// raised inside an MCP command blocks the Revit UI thread until a user answers it, so the
    /// agent's request hangs until it times out. This handler resolves those failures without
    /// UI and reports them back to the caller (see <see cref="CommandExecutor"/>):
    ///   warnings: deleted (policy "suppress") or left to Revit (policy "keep");
    ///   errors:   the transaction is rolled back (policy "rollback") or left to Revit's dialog (policy "ask_user").
    ///
    /// It is a FailuresProcessing handler rather than a per-transaction IFailuresPreprocessor so it
    /// also covers transactions opened by send_code_to_revit snippets and by every handler without
    /// each one opting in. Per-transaction preprocessors (e.g. create_room's) still run first.
    ///
    /// Only transactions committed by command-set code are touched: the handler checks whether a
    /// method from a command-set assembly is on the call stack. Command sets run on the UI thread
    /// only inside their external event callbacks, so a user's own edits never match.
    /// </summary>
    public class RevitFailureMonitor
    {
        public const string WarningsSuppress = "suppress";
        public const string WarningsKeep = "keep";
        public const string ErrorsRollback = "rollback";
        public const string ErrorsAskUser = "ask_user";

        public const string ActionWarningSuppressed = "warning_suppressed";
        public const string ActionWarningKept = "warning_kept";
        public const string ActionRolledBack = "rolled_back";
        public const string ActionLeftToUser = "left_to_user";

        private const int MaxElementIdsPerFailure = 50;
        private const int MaxFailuresPerRequest = 100;

        private static readonly RevitFailureMonitor _instance = new RevitFailureMonitor();
        public static RevitFailureMonitor Instance => _instance;

        private readonly object _lock = new object();
        private readonly HashSet<Assembly> _commandSetAssemblies = new HashSet<Assembly>();
        private readonly List<RequestFailures> _activeRequests = new List<RequestFailures>();
        private readonly FailureLog _undelivered = new FailureLog();
        private bool _subscribed;

        private RevitFailureMonitor() { }

        public string WarningPolicy { get; private set; } = WarningsSuppress;
        public string ErrorPolicy { get; private set; } = ErrorsRollback;

        /// <summary>Must be called from a valid Revit API context (OnStartup).</summary>
        public void Subscribe(ControlledApplication application)
        {
            if (_subscribed) return;
            application.FailuresProcessing += OnFailuresProcessing;
            _subscribed = true;
        }

        public void Unsubscribe(ControlledApplication application)
        {
            if (!_subscribed) return;
            application.FailuresProcessing -= OnFailuresProcessing;
            _subscribed = false;
        }

        public void Configure(FailureHandlingSettings settings)
        {
            string warnings = settings?.Warnings?.Trim().ToLowerInvariant();
            string errors = settings?.Errors?.Trim().ToLowerInvariant();

            WarningPolicy = warnings == WarningsKeep ? WarningsKeep : WarningsSuppress;
            ErrorPolicy = errors == ErrorsAskUser ? ErrorsAskUser : ErrorsRollback;

            if (warnings != null && warnings != WarningPolicy)
                Log(LogSeverity.Warning, "Unknown failureHandling.warnings '{0}'; using '{1}'.", settings.Warnings, WarningPolicy);
            if (errors != null && errors != ErrorPolicy)
                Log(LogSeverity.Warning, "Unknown failureHandling.errors '{0}'; using '{1}'.", settings.Errors, ErrorPolicy);

            Log(LogSeverity.Info, "Failure handling for MCP transactions: warnings={0} errors={1}", WarningPolicy, ErrorPolicy);
        }

        /// <summary>Marks an assembly whose code runs MCP commands, so its transactions are handled here.</summary>
        public void RegisterCommandSetAssembly(Assembly assembly)
        {
            lock (_lock)
                _commandSetAssemblies.Add(assembly);
        }

        /// <summary>
        /// Collects failures for one request until disposed. Failures are matched to requests by
        /// time: with several requests in flight at once (clients that do not serialize), each of
        /// them receives every failure recorded while it was running.
        /// </summary>
        public RequestFailures BeginRequest(string requestId, string method)
        {
            var request = new RequestFailures(this, requestId, method);
            lock (_lock)
                _activeRequests.Add(request);
            return request;
        }

        private void EndRequest(RequestFailures request)
        {
            lock (_lock)
                _activeRequests.Remove(request);
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            // Never let an exception escape into Revit's failure processing.
            try
            {
                FailuresAccessor accessor = e.GetFailuresAccessor();
                IList<FailureMessageAccessor> messages = accessor.GetFailureMessages();
                if (messages.Count == 0 || !accessor.IsTransactionBeingCommitted() || !IsCommandSetOnStack())
                    return;

                Process(e, accessor, messages);
            }
            catch (Exception ex)
            {
                Log(LogSeverity.Error, "Failure handling for an MCP transaction threw; Revit will handle the failures itself: {0}", ex);
            }
        }

        private void Process(FailuresProcessingEventArgs e, FailuresAccessor accessor, IList<FailureMessageAccessor> messages)
        {
            string transactionName = accessor.GetTransactionName();
            bool hasErrors = messages.Any(m => m.GetSeverity() != FailureSeverity.Warning);
            bool rollBack = hasErrors && ErrorPolicy == ErrorsRollback;

            var records = new List<FailureRecord>();
            foreach (FailureMessageAccessor message in messages)
            {
                FailureSeverity severity = message.GetSeverity();
                string action;
                if (rollBack)
                {
                    action = ActionRolledBack;
                }
                else if (severity == FailureSeverity.Warning)
                {
                    action = WarningPolicy == WarningsSuppress ? ActionWarningSuppressed : ActionWarningKept;
                    if (WarningPolicy == WarningsSuppress)
                        accessor.DeleteWarning(message);
                }
                else
                {
                    action = ActionLeftToUser;
                }

                records.Add(new FailureRecord
                {
                    Transaction = transactionName,
                    Severity = severity.ToString(),
                    Description = message.GetDescriptionText(),
                    FailureId = message.GetFailureDefinitionId()?.Guid.ToString(),
                    ElementIds = message.GetFailingElementIds()
                        .Concat(message.GetAdditionalElementIds())
                        .Select(ToNumber)
                        .Where(id => id.HasValue)
                        .Select(id => id.Value)
                        .Distinct()
                        .Take(MaxElementIdsPerFailure)
                        .ToList(),
                    Action = action
                });
            }

            if (rollBack)
            {
                // Clear the failures after rolling back, or Revit shows them in a dialog anyway.
                FailureHandlingOptions options = accessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                accessor.SetFailureHandlingOptions(options);
                e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            }

            Record(records, rollBack);
        }

        private void Record(List<FailureRecord> records, bool rolledBack)
        {
            List<RequestFailures> targets;
            lock (_lock)
            {
                targets = _activeRequests.ToList();
                if (targets.Count == 0)
                {
                    // The caller already stopped waiting (e.g. a command that finished after its timeout).
                    // Keep the failures for the next response so the agent still learns about them.
                    _undelivered.Add(records, rolledBack);
                }
            }

            string transaction = records[0].Transaction;
            string requests = targets.Count == 0 ? "none (caller already gone; reported with the next response)" : string.Join(",", targets.Select(t => t.RequestId ?? "-"));
            Log(rolledBack ? LogSeverity.Warning : LogSeverity.Info,
                "MCP transaction '{0}' ended with {1} failure(s): {2}; requests={3}",
                transaction, records.Count, rolledBack ? "rolled back because of errors" : string.Join(",", records.Select(r => r.Action).Distinct()), requests);
            foreach (var record in records)
                Log(LogSeverity.Info, "  {0} [{1}] {2}", record.Severity, record.Action, record.Description);

            foreach (var target in targets)
                target.Add(records, rolledBack);
        }

        private bool IsCommandSetOnStack()
        {
            HashSet<Assembly> assemblies;
            lock (_lock)
            {
                if (_commandSetAssemblies.Count == 0) return false;
                assemblies = new HashSet<Assembly>(_commandSetAssemblies);
            }

            foreach (StackFrame frame in new StackTrace(1, false).GetFrames() ?? new StackFrame[0])
            {
                Type type = frame.GetMethod()?.DeclaringType;
                if (type != null && assemblies.Contains(type.Assembly))
                    return true;
            }
            return false;
        }

        // ElementId.IntegerValue was replaced by Value (long) in Revit 2024; ToString() is the id on every version.
        private static long? ToNumber(ElementId id)
        {
            return id != null && long.TryParse(id.ToString(), out long value) && value >= 0 ? value : (long?)null;
        }

        private static void Log(LogSeverity severity, string message, params object[] args)
        {
            TraceLog.Write(LogChannel.Addin, severity, message, args);
        }

        /// <summary>
        /// Failures recorded while no request was waiting, as a "revitFailures" report, or null.
        /// Taking them hands them to the caller: each one is reported once.
        /// </summary>
        public object TakeUndeliveredReport()
        {
            FailureLog earlier;
            lock (_lock)
                earlier = _undelivered.TakeAll();
            return BuildReport(new FailureLog(), earlier);
        }

        public class FailureRecord
        {
            public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
            public string Transaction { get; set; }
            public string Severity { get; set; }
            public string Description { get; set; }
            public string FailureId { get; set; }
            public List<long> ElementIds { get; set; }
            public string Action { get; set; }
        }

        /// <summary>A bounded list of failure records plus the number of transactions they rolled back.</summary>
        private class FailureLog
        {
            public readonly List<FailureRecord> Records = new List<FailureRecord>();
            public int RolledBackTransactions;
            public int Dropped;

            public bool IsEmpty => Records.Count == 0 && Dropped == 0 && RolledBackTransactions == 0;

            public void Add(List<FailureRecord> records, bool rolledBack)
            {
                if (rolledBack) RolledBackTransactions++;
                foreach (var record in records)
                {
                    if (Records.Count < MaxFailuresPerRequest) Records.Add(record);
                    else Dropped++;
                }
            }

            public FailureLog TakeAll()
            {
                var taken = new FailureLog();
                taken.Records.AddRange(Records);
                taken.RolledBackTransactions = RolledBackTransactions;
                taken.Dropped = Dropped;
                Records.Clear();
                RolledBackTransactions = 0;
                Dropped = 0;
                return taken;
            }
        }

        /// <summary>
        /// The "revitFailures" object: failures from the current request, plus failures recorded
        /// earlier while no request was waiting (marked <c>earlier: true</c>). Null when both are empty.
        /// </summary>
        private static object BuildReport(FailureLog current, FailureLog earlier)
        {
            if (current.IsEmpty && earlier.IsEmpty)
                return null;

            var all = current.Records.Select(r => new { record = r, earlier = false })
                .Concat(earlier.Records.Select(r => new { record = r, earlier = true }))
                .ToList();
            int rolledBack = current.RolledBackTransactions + earlier.RolledBackTransactions;
            int suppressed = all.Count(r => r.record.Action == ActionWarningSuppressed);
            int kept = all.Count(r => r.record.Action == ActionWarningKept);
            int leftToUser = all.Count(r => r.record.Action == ActionLeftToUser);
            bool silenced = rolledBack > 0 || suppressed > 0;

            var summary = new List<string>();
            if (silenced)
                summary.Add("Revit reported failures that the MCP failure handler resolved without showing the user a dialog.");
            if (rolledBack > 0)
                summary.Add($"{rolledBack} transaction(s) had errors and were rolled back: their changes were NOT applied to the model, even if the result reports them.");
            if (suppressed > 0)
                summary.Add($"{suppressed} warning(s) were suppressed: the changes were applied, but the warnings are not kept in the model, so tell the user about them if they matter.");
            if (kept > 0)
                summary.Add($"{kept} warning(s) were left to Revit and are kept in the model's warning list.");
            if (leftToUser > 0)
                summary.Add("Errors were left for the user to resolve in Revit's failure dialog. Ask the user what they chose before continuing.");
            if (earlier.Records.Count > 0)
                summary.Add($"{earlier.Records.Count} of these (earlier: true) were raised after an earlier request had stopped waiting, for example a command that finished after its timeout; they did not come from this request.");

            return new
            {
                silenced,
                rolledBackTransactions = rolledBack,
                warningsSuppressed = suppressed,
                warningsKept = kept,
                errorsLeftToUser = leftToUser,
                message = string.Join(" ", summary),
                failures = all.Select(r => new
                {
                    transaction = r.record.Transaction,
                    severity = r.record.Severity,
                    description = r.record.Description,
                    action = r.record.Action,
                    failureId = r.record.FailureId,
                    elementIds = r.record.ElementIds,
                    recordedUtc = r.record.RecordedUtc,
                    earlier = r.earlier ? true : (bool?)null
                }).ToList(),
                failuresNotListed = current.Dropped + earlier.Dropped > 0 ? current.Dropped + earlier.Dropped : (int?)null
            };
        }

        /// <summary>Failures recorded while one request was running.</summary>
        public sealed class RequestFailures : IDisposable
        {
            private readonly RevitFailureMonitor _owner;
            private readonly object _lock = new object();
            private readonly FailureLog _log = new FailureLog();

            internal RequestFailures(RevitFailureMonitor owner, string requestId, string method)
            {
                _owner = owner;
                RequestId = requestId;
                Method = method;
            }

            public string RequestId { get; }
            public string Method { get; }

            internal void Add(List<FailureRecord> records, bool rolledBack)
            {
                lock (_lock)
                    _log.Add(records, rolledBack);
            }

            /// <summary>
            /// The "revitFailures" object for the response, or null when nothing was recorded. Also
            /// takes failures left over from earlier requests, so call it once, when the response is built.
            /// </summary>
            public object BuildReport()
            {
                FailureLog earlier;
                lock (_owner._lock)
                    earlier = _owner._undelivered.TakeAll();

                lock (_lock)
                    return RevitFailureMonitor.BuildReport(_log, earlier);
            }

            public void Dispose() => _owner.EndRequest(this);
        }
    }
}
