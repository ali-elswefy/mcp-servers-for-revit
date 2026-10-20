using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMcp.Interop;
using RevitMcp.Logging;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Application-wide failure handling for transactions committed by MCP commands.
    ///
    /// Revit shows failure dialogs when a transaction ends with warnings or errors. Inside an MCP
    /// command such a dialog blocks Revit until a user answers it, and a failure that is silently
    /// resolved lets the command carry on as if nothing happened. Instead, by default (policy
    /// "halt") the first failure in a command:
    ///   1. rolls back the transaction that failed, without a dialog;
    ///   2. halts the command: every later change it makes is blocked (see <see cref="HaltUpdater"/>);
    ///   3. is returned to the agent as a "revit_failure" error listing each failure, the fixes
    ///      Revit offers for it (e.g. "Unjoin Elements") and what the command had already committed.
    /// The agent asks the user, then repeats the call with "failureResolutions"; the chosen fix (or
    /// "accept" for a warning) is applied here when the same failure comes up again.
    ///
    /// It is a FailuresProcessing handler rather than a per-transaction IFailuresPreprocessor so it
    /// covers every handler and send_code_to_revit snippets without each one opting in.
    /// Per-transaction preprocessors (e.g. create_room's) still run first.
    ///
    /// Only transactions committed by command-set code are touched: the handler checks whether a
    /// method from a command-set assembly is on the call stack. Command sets run on the UI thread
    /// only inside their external event callbacks, so a user's own edits never match.
    /// </summary>
    public class RevitFailureMonitor
    {
        // Policies (settings.failureHandling).
        public const string PolicyHalt = "halt";
        public const string WarningsReport = "report";
        public const string WarningsKeep = "keep";
        public const string ErrorsRollback = "rollback";
        public const string ErrorsAskUser = "ask_user";

        // What happened to one failure ("action" in reports).
        public const string ActionHalted = "halted";
        public const string ActionRolledBack = "rolled_back";
        public const string ActionWarningReported = "warning_reported";
        public const string ActionWarningKept = "warning_kept";
        public const string ActionAccepted = "accepted";
        public const string ActionResolved = "resolved";
        public const string ActionLeftToUser = "left_to_user";

        /// <summary>Request parameter carrying the user's decisions: [{ failureId, resolution }].</summary>
        public const string ResolutionsParameter = "failureResolutions";
        public const string ResolutionAccept = "accept";

        public const string ErrorKind = "revit_failure";
        public const string ErrorReason = "failure_needs_user";

        private static readonly FailureDefinitionId HaltFailureId =
            new FailureDefinitionId(new Guid("6f0c9a52-3d1e-4b7a-9c3e-2a8f5d7e41b0"));
        private static readonly Guid HaltUpdaterGuid = new Guid("b4d6e1a3-7c25-4f80-8e9b-1d3c5a7f9e02");

        private const int MaxElementIds = 50;
        private const int MaxFailuresPerRequest = 100;

        private static readonly RevitFailureMonitor _instance = new RevitFailureMonitor();
        public static RevitFailureMonitor Instance => _instance;

        private readonly object _lock = new object();
        private readonly HashSet<Assembly> _commandSetAssemblies = new HashSet<Assembly>();
        private readonly List<RequestFailures> _activeRequests = new List<RequestFailures>();
        private readonly FailureLog _undelivered = new FailureLog();
        private volatile HaltState _halt;
        private UpdaterId _updaterId;
        private bool _subscribed;

        private RevitFailureMonitor() { }

        public string WarningPolicy { get; private set; } = PolicyHalt;
        public string ErrorPolicy { get; private set; } = PolicyHalt;

        /// <summary>False when the updater that blocks changes after a halt could not be registered.</summary>
        public bool CanBlockAfterHalt => _updaterId != null;

        // ---- setup ----

        /// <summary>Must be called from OnStartup: failure definitions can only be created there.</summary>
        public void Startup(UIControlledApplication application)
        {
            if (_subscribed) return;
            application.ControlledApplication.FailuresProcessing += OnFailuresProcessing;
            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
            _subscribed = true;

            try
            {
                FailureDefinition.CreateFailureDefinition(HaltFailureId, FailureSeverity.Error,
                    "Blocked by the MCP add-in: an earlier Revit failure in the same command is waiting for the user's decision.");

                var updaterId = new UpdaterId(application.ActiveAddInId, HaltUpdaterGuid);
                UpdaterRegistry.RegisterUpdater(new HaltUpdater(this, updaterId), true);
                var everyElement = new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true));
                UpdaterRegistry.AddTrigger(updaterId, everyElement, Element.GetChangeTypeAny());
                UpdaterRegistry.AddTrigger(updaterId, everyElement, Element.GetChangeTypeElementAddition());
                UpdaterRegistry.AddTrigger(updaterId, everyElement, Element.GetChangeTypeElementDeletion());
                _updaterId = updaterId;
            }
            catch (Exception ex)
            {
                Log(LogSeverity.Error, "Could not register the updater that blocks changes after a failure; a halted command can still change the model in later transactions: {0}", ex);
            }
        }

        public void Shutdown(UIControlledApplication application)
        {
            if (!_subscribed) return;
            application.ControlledApplication.FailuresProcessing -= OnFailuresProcessing;
            application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
            _subscribed = false;

            try
            {
                if (_updaterId != null)
                    UpdaterRegistry.UnregisterUpdater(_updaterId);
            }
            catch (Exception) { }
        }

        public void Configure(FailureHandlingSettings settings)
        {
            string warnings = settings?.Warnings?.Trim().ToLowerInvariant();
            string errors = settings?.Errors?.Trim().ToLowerInvariant();

            // "suppress" was the earlier name of "report".
            WarningPolicy = warnings == WarningsReport || warnings == "suppress" ? WarningsReport
                : warnings == WarningsKeep ? WarningsKeep
                : PolicyHalt;
            ErrorPolicy = errors == ErrorsRollback ? ErrorsRollback
                : errors == ErrorsAskUser ? ErrorsAskUser
                : PolicyHalt;

            if (warnings != null && warnings != WarningPolicy && warnings != "suppress")
                Log(LogSeverity.Warning, "Unknown failureHandling.warnings '{0}'; using '{1}'.", settings.Warnings, WarningPolicy);
            if (errors != null && errors != ErrorPolicy)
                Log(LogSeverity.Warning, "Unknown failureHandling.errors '{0}'; using '{1}'.", settings.Errors, ErrorPolicy);

            Log(LogSeverity.Info, "Failure handling for MCP transactions: warnings={0} errors={1} blockAfterHalt={2}",
                WarningPolicy, ErrorPolicy, CanBlockAfterHalt);
        }

        /// <summary>Marks an assembly whose code runs MCP commands, so its transactions are handled here.</summary>
        public void RegisterCommandSetAssembly(Assembly assembly)
        {
            lock (_lock)
                _commandSetAssemblies.Add(assembly);
        }

        /// <summary>
        /// Parses the "failureResolutions" request parameter: failure definition id -> the fix to
        /// apply (a resolution caption or type name, or "accept"). Throws FormatException when malformed.
        /// </summary>
        public static Dictionary<Guid, string> ParseResolutions(JToken token)
        {
            var resolutions = new Dictionary<Guid, string>();
            if (token == null || token.Type == JTokenType.Null)
                return resolutions;

            if (!(token is JArray array))
                throw new FormatException($"'{ResolutionsParameter}' must be an array of {{ failureId, resolution }}.");

            foreach (JToken entry in array)
            {
                string id = entry?["failureId"]?.Value<string>();
                string resolution = entry?["resolution"]?.Value<string>()?.Trim();
                if (!Guid.TryParse(id, out Guid failureId))
                    throw new FormatException($"Each '{ResolutionsParameter}' entry needs a 'failureId' (the GUID reported with the failure); got '{id}'.");
                if (string.IsNullOrEmpty(resolution))
                    throw new FormatException($"The '{ResolutionsParameter}' entry for {failureId} needs a 'resolution': one of the fixes reported for it, or 'accept' for a warning.");
                resolutions[failureId] = resolution;
            }
            return resolutions;
        }

        /// <summary>
        /// Collects failures for one request until disposed. Failures are matched to requests by
        /// time: with several requests in flight at once (clients that do not serialize), each of
        /// them receives every failure recorded while it was running.
        /// </summary>
        public RequestFailures BeginRequest(string requestId, string method, Dictionary<Guid, string> resolutions)
        {
            var request = new RequestFailures(this, requestId, method, resolutions ?? new Dictionary<Guid, string>());
            lock (_lock)
                _activeRequests.Add(request);
            return request;
        }

        private void EndRequest(RequestFailures request)
        {
            lock (_lock)
                _activeRequests.Remove(request);
        }

        /// <summary>Called when Revit becomes idle: the halted command has returned, so its halt ends.</summary>
        public void OnIdle()
        {
            HaltState halt = _halt;
            if (halt == null) return;
            _halt = null;
            Log(LogSeverity.Info, "Halt after the failure in '{0}' ended ({1} later change(s) blocked).", halt.Transaction, halt.Blocked);
        }

        // ---- failure processing ----

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            // Never let an exception escape into Revit's failure processing.
            try
            {
                FailuresAccessor accessor = e.GetFailuresAccessor();
                IList<FailureMessageAccessor> messages = accessor.GetFailureMessages();
                if (messages.Count == 0 || !accessor.IsTransactionBeingCommitted())
                    return;

                if (!IsCommandSetOnStack(out Type handlerType))
                    return;

                Process(e, accessor, messages, handlerType);
            }
            catch (Exception ex)
            {
                Log(LogSeverity.Error, "Failure handling for an MCP transaction threw; Revit will handle the failures itself: {0}", ex);
            }
        }

        private void Process(FailuresProcessingEventArgs e, FailuresAccessor accessor, IList<FailureMessageAccessor> messages, Type handlerType)
        {
            string transactionName = accessor.GetTransactionName();

            // A change made after the command halted (the updater posts HaltFailureId into it): block it.
            HaltState halt = _halt;
            bool blockedByUpdater = messages.Any(m => m.GetFailureDefinitionId()?.Guid == HaltFailureId.Guid);
            if (blockedByUpdater || (halt != null && halt.Matches(handlerType)))
            {
                RollBack(e, accessor);
                if (halt != null) halt.Blocked++;
                foreach (var request in ActiveRequests())
                    request.AddBlocked();
                Log(LogSeverity.Warning, "MCP transaction '{0}' blocked: the command halted after an earlier failure.", transactionName);
                return;
            }

            Dictionary<Guid, string> requested = ActiveResolutions();
            var records = new List<FailureRecord>();
            bool halted = false, rolledBack = false, resolved = false;

            foreach (FailureMessageAccessor message in messages)
            {
                FailureRecord record = Describe(message, transactionName);
                bool isWarning = message.GetSeverity() == FailureSeverity.Warning;

                if (requested.TryGetValue(message.GetFailureDefinitionId().Guid, out string choice))
                {
                    ApplyUserChoice(accessor, message, record, choice, isWarning);
                }
                else if (isWarning)
                {
                    record.Action = WarningPolicy == PolicyHalt ? ActionHalted
                        : WarningPolicy == WarningsReport ? ActionWarningReported
                        : ActionWarningKept;
                    if (record.Action == ActionWarningReported)
                        accessor.DeleteWarning(message);
                }
                else
                {
                    record.Action = ErrorPolicy == PolicyHalt ? ActionHalted
                        : ErrorPolicy == ErrorsRollback ? ActionRolledBack
                        : ActionLeftToUser;
                }

                halted |= record.Action == ActionHalted;
                rolledBack |= record.Action == ActionRolledBack;
                resolved |= record.Action == ActionResolved;
                records.Add(record);
            }

            if (halted || rolledBack)
            {
                // The whole transaction goes, including fixes applied to its other failures.
                foreach (var record in records.Where(r => r.Action == ActionResolved || r.Action == ActionAccepted))
                {
                    record.Note = $"The fix was applied, but the transaction was rolled back because of another failure in it ({record.Action}).";
                    record.Action = halted ? ActionHalted : ActionRolledBack;
                }

                RollBack(e, accessor);
                if (halted)
                    _halt = new HaltState(handlerType, McpInvocationMarker.Current, transactionName);
            }
            else if (resolved)
            {
                // Revit applies the resolutions and processes the transaction's failures again.
                e.SetProcessingResult(FailureProcessingResult.ProceedWithCommit);
            }

            Record(records, halted, rolledBack || halted);
        }

        private static void ApplyUserChoice(FailuresAccessor accessor, FailureMessageAccessor message, FailureRecord record, string choice, bool isWarning)
        {
            record.RequestedResolution = choice;

            if (string.Equals(choice, ResolutionAccept, StringComparison.OrdinalIgnoreCase))
            {
                if (isWarning)
                {
                    accessor.DeleteWarning(message);
                    record.Action = ActionAccepted;
                }
                else
                {
                    record.Action = ActionHalted;
                    record.Note = "Errors cannot be accepted: choose one of Revit's fixes or change the request.";
                }
                return;
            }

            ResolutionInfo fix = record.Resolutions.FirstOrDefault(r =>
                string.Equals(r.Caption, choice, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Type.ToString(), choice, StringComparison.OrdinalIgnoreCase));

            if (fix == null)
            {
                record.Action = ActionHalted;
                record.Note = $"The requested fix '{choice}' is not one Revit offers for this failure.";
            }
            else if (accessor.GetAttemptedResolutionTypes(message).Contains(fix.Type))
            {
                record.Action = ActionHalted;
                record.Note = $"Revit already tried '{fix.Caption}' and the failure remained.";
            }
            else if (!accessor.IsFailureResolutionPermitted(message, fix.Type))
            {
                record.Action = ActionHalted;
                record.Note = $"Revit does not permit '{fix.Caption}' here.";
            }
            else
            {
                message.SetCurrentResolutionType(fix.Type);
                accessor.ResolveFailure(message);
                record.Action = ActionResolved;
                record.ResolutionApplied = fix.Caption;
            }
        }

        private static void RollBack(FailuresProcessingEventArgs e, FailuresAccessor accessor)
        {
            // Clear the failures after rolling back, or Revit shows them in a dialog anyway.
            FailureHandlingOptions options = accessor.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            accessor.SetFailureHandlingOptions(options);
            e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
        }

        private static FailureRecord Describe(FailureMessageAccessor message, string transactionName)
        {
            FailureDefinitionId definitionId = message.GetFailureDefinitionId();
            var record = new FailureRecord
            {
                Transaction = transactionName,
                Severity = message.GetSeverity().ToString(),
                Description = message.GetDescriptionText(),
                FailureId = definitionId?.Guid.ToString(),
                ElementIds = message.GetFailingElementIds()
                    .Concat(message.GetAdditionalElementIds())
                    .Select(ToNumber)
                    .Where(id => id.HasValue)
                    .Select(id => id.Value)
                    .Distinct()
                    .Take(MaxElementIds)
                    .ToList()
            };

            // The fixes Revit offers for this failure, as its own dialog would list them.
            if (definitionId != null && message.HasResolutions())
            {
                FailureDefinitionAccessor definition = Autodesk.Revit.ApplicationServices.Application
                    .GetFailureDefinitionRegistry()?.FindFailureDefinition(definitionId);
                if (definition != null)
                {
                    FailureResolutionType defaultType = definition.GetDefaultResolutionType();
                    foreach (FailureResolutionType type in definition.GetApplicableResolutionTypes())
                    {
                        if (!message.HasResolutionOfType(type)) continue;
                        record.Resolutions.Add(new ResolutionInfo
                        {
                            Type = type,
                            Caption = definition.GetResolutionCaption(type),
                            IsDefault = type == defaultType
                        });
                    }
                }
            }
            return record;
        }

        private void Record(List<FailureRecord> records, bool halted, bool rolledBack)
        {
            List<RequestFailures> targets;
            lock (_lock)
            {
                targets = _activeRequests.ToList();
                if (targets.Count == 0)
                {
                    // The caller already stopped waiting (e.g. a command that finished after its timeout).
                    // Keep the failures for the next response so the agent still learns about them.
                    _undelivered.Add(records, rolledBack, halted);
                }
            }

            string transaction = records[0].Transaction;
            string requests = targets.Count == 0 ? "none (caller already gone; reported with the next response)" : string.Join(",", targets.Select(t => t.RequestId ?? "-"));
            Log(rolledBack ? LogSeverity.Warning : LogSeverity.Info,
                "MCP transaction '{0}' ended with {1} failure(s): {2}; requests={3}",
                transaction, records.Count,
                halted ? "rolled back and the command halted" : string.Join(",", records.Select(r => r.Action).Distinct()), requests);
            foreach (var record in records)
                Log(LogSeverity.Info, "  {0} [{1}] {2} (fixes: {3})", record.Severity, record.Action, record.Description,
                    record.Resolutions.Count == 0 ? "none" : string.Join("; ", record.Resolutions.Select(r => r.Caption)));

            foreach (var target in targets)
                target.Add(records, rolledBack, halted);
        }

        // ---- committed changes ----

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                if (e.Operation != Autodesk.Revit.DB.Events.UndoOperation.TransactionCommitted)
                    return;

                List<RequestFailures> targets = ActiveRequests();
                if (targets.Count == 0 || !IsCommandSetOnStack(out _))
                    return;

                var change = new CommittedChange
                {
                    Transactions = e.GetTransactionNames().ToList(),
                    Added = e.GetAddedElementIds().Select(ToNumber).Where(id => id.HasValue).Select(id => id.Value).Take(MaxElementIds).ToList(),
                    Deleted = e.GetDeletedElementIds().Select(ToNumber).Where(id => id.HasValue).Select(id => id.Value).Take(MaxElementIds).ToList(),
                    ModifiedCount = e.GetModifiedElementIds().Count
                };
                foreach (var target in targets)
                    target.AddCommitted(change);
            }
            catch (Exception ex)
            {
                Log(LogSeverity.Warning, "Could not record a committed MCP transaction: {0}", ex.Message);
            }
        }

        // ---- helpers ----

        private List<RequestFailures> ActiveRequests()
        {
            lock (_lock)
                return _activeRequests.ToList();
        }

        private Dictionary<Guid, string> ActiveResolutions()
        {
            var merged = new Dictionary<Guid, string>();
            foreach (var request in ActiveRequests())
                foreach (var pair in request.Resolutions)
                    merged[pair.Key] = pair.Value;
            return merged;
        }

        /// <summary>
        /// Whether command-set code is on the call stack, and which external event handler it runs
        /// in (the innermost command-set type that is an IExternalEventHandler).
        /// </summary>
        private bool IsCommandSetOnStack(out Type handlerType)
        {
            handlerType = null;
            HashSet<Assembly> assemblies;
            lock (_lock)
            {
                if (_commandSetAssemblies.Count == 0) return false;
                assemblies = new HashSet<Assembly>(_commandSetAssemblies);
            }

            bool found = false;
            foreach (StackFrame frame in new StackTrace(1, false).GetFrames() ?? new StackFrame[0])
            {
                Type type = frame.GetMethod()?.DeclaringType;
                if (type == null || !assemblies.Contains(type.Assembly))
                    continue;

                found = true;
                if (typeof(IExternalEventHandler).IsAssignableFrom(type))
                {
                    handlerType = type;
                    break;
                }
            }
            return found;
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

        // ---- types ----

        /// <summary>A command that halted: later changes from the same handler invocation are blocked until Revit is idle.</summary>
        private class HaltState
        {
            public HaltState(Type handlerType, long invocation, string transaction)
            {
                HandlerType = handlerType;
                Invocation = invocation;
                Transaction = transaction;
            }

            public Type HandlerType { get; }
            public long Invocation { get; }
            public string Transaction { get; }
            public int Blocked;

            // Queued commands run several invocations per Revit callback; each one bumps the marker.
            public bool Matches(Type handlerType) =>
                (HandlerType == null || handlerType == HandlerType) && McpInvocationMarker.Current == Invocation;
        }

        /// <summary>
        /// Dynamic model updater that triggers on every element change. While a command is halted
        /// it posts <see cref="HaltFailureId"/> into the command's later transactions, so they
        /// reach failure processing (even without failures of their own) and are rolled back.
        /// Registered as optional, so documents never ask for it when the add-in is missing.
        /// </summary>
        private class HaltUpdater : IUpdater
        {
            private readonly RevitFailureMonitor _owner;
            private readonly UpdaterId _id;

            public HaltUpdater(RevitFailureMonitor owner, UpdaterId id)
            {
                _owner = owner;
                _id = id;
            }

            public void Execute(UpdaterData data)
            {
                // An exception here would make Revit disable the updater and show a dialog.
                try
                {
                    HaltState halt = _owner._halt;
                    if (halt == null) return; // The common case: costs one field read per transaction.

                    if (_owner.IsCommandSetOnStack(out Type handlerType) && halt.Matches(handlerType))
                        data.GetDocument().PostFailure(new FailureMessage(HaltFailureId));
                }
                catch (Exception ex)
                {
                    Log(LogSeverity.Error, "Halt updater failed: {0}", ex);
                }
            }

            public UpdaterId GetUpdaterId() => _id;
            public ChangePriority GetChangePriority() => ChangePriority.Annotations;
            public string GetUpdaterName() => "MCP failure halt";
            public string GetAdditionalInformation() =>
                "Blocks further changes from an MCP command after a Revit failure, until the user decides how to proceed.";
        }

        public class ResolutionInfo
        {
            public FailureResolutionType Type { get; set; }
            public string Caption { get; set; }
            public bool IsDefault { get; set; }
        }

        public class FailureRecord
        {
            public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
            public string Transaction { get; set; }
            public string Severity { get; set; }
            public string Description { get; set; }
            public string FailureId { get; set; }
            public List<long> ElementIds { get; set; }
            public List<ResolutionInfo> Resolutions { get; } = new List<ResolutionInfo>();
            public string Action { get; set; }
            public string RequestedResolution { get; set; }
            public string ResolutionApplied { get; set; }
            public string Note { get; set; }

            public bool IsWarning => Severity == FailureSeverity.Warning.ToString();
        }

        public class CommittedChange
        {
            public List<string> Transactions { get; set; }
            public List<long> Added { get; set; }
            public List<long> Deleted { get; set; }
            public int ModifiedCount { get; set; }
        }

        /// <summary>A bounded list of failure records plus transaction counts.</summary>
        private class FailureLog
        {
            public readonly List<FailureRecord> Records = new List<FailureRecord>();
            public int RolledBackTransactions;
            public bool Halted;
            public int Dropped;

            public bool IsEmpty => Records.Count == 0 && Dropped == 0 && RolledBackTransactions == 0;

            public void Add(List<FailureRecord> records, bool rolledBack, bool halted)
            {
                if (rolledBack) RolledBackTransactions++;
                Halted |= halted;
                foreach (var record in records)
                {
                    if (Records.Count < MaxFailuresPerRequest) Records.Add(record);
                    else Dropped++;
                }
            }

            public FailureLog TakeAll()
            {
                var taken = new FailureLog { RolledBackTransactions = RolledBackTransactions, Halted = Halted, Dropped = Dropped };
                taken.Records.AddRange(Records);
                Records.Clear();
                RolledBackTransactions = 0;
                Halted = false;
                Dropped = 0;
                return taken;
            }
        }

        /// <summary>
        /// Failures from one request. Payload is the "revitFailures" object; Message is a complete,
        /// explicit description for clients that only show error messages.
        /// </summary>
        public class FailureReport
        {
            public bool Halted { get; set; }
            public JObject Payload { get; set; }
            public string Message { get; set; }
        }

        /// <summary>Failures recorded while one request was running.</summary>
        public sealed class RequestFailures : IDisposable
        {
            private readonly RevitFailureMonitor _owner;
            private readonly object _lock = new object();
            private readonly FailureLog _log = new FailureLog();
            private readonly List<CommittedChange> _committed = new List<CommittedChange>();
            private int _committedAfterHalt;
            private int _blocked;

            internal RequestFailures(RevitFailureMonitor owner, string requestId, string method, Dictionary<Guid, string> resolutions)
            {
                _owner = owner;
                RequestId = requestId;
                Method = method;
                Resolutions = resolutions;
            }

            public string RequestId { get; }
            public string Method { get; }
            internal Dictionary<Guid, string> Resolutions { get; }

            internal void Add(List<FailureRecord> records, bool rolledBack, bool halted)
            {
                lock (_lock)
                    _log.Add(records, rolledBack, halted);
            }

            internal void AddBlocked()
            {
                lock (_lock)
                    _blocked++;
            }

            internal void AddCommitted(CommittedChange change)
            {
                lock (_lock)
                {
                    if (_log.Halted) _committedAfterHalt++;
                    else if (_committed.Count < MaxFailuresPerRequest) _committed.Add(change);
                }
            }

            /// <summary>
            /// The report for the response, or null when nothing happened. Also takes failures left
            /// over from earlier requests, so call it once, when the response is built.
            /// </summary>
            public FailureReport Finish()
            {
                FailureLog earlier;
                lock (_owner._lock)
                    earlier = _owner._undelivered.TakeAll();

                lock (_lock)
                    return BuildReport(Method, _log, earlier, _committed, _blocked, _committedAfterHalt);
            }

            public void Dispose() => _owner.EndRequest(this);
        }

        /// <summary>Failures recorded while no request was waiting, as a report for health_check, or null.</summary>
        public FailureReport TakeUndeliveredReport()
        {
            FailureLog earlier;
            lock (_lock)
                earlier = _undelivered.TakeAll();
            return BuildReport(null, new FailureLog(), earlier, new List<CommittedChange>(), 0, 0);
        }

        private static FailureReport BuildReport(string method, FailureLog current, FailureLog earlier,
            List<CommittedChange> committed, int blocked, int committedAfterHalt)
        {
            if (current.IsEmpty && earlier.IsEmpty && blocked == 0)
                return null;

            var all = current.Records.Select(r => new { record = r, earlier = false })
                .Concat(earlier.Records.Select(r => new { record = r, earlier = true }))
                .ToList();
            bool halted = current.Halted;
            int count(string action) => all.Count(r => r.record.Action == action);

            var payload = new JObject
            {
                ["halted"] = halted,
                ["rolledBackTransactions"] = current.RolledBackTransactions + earlier.RolledBackTransactions,
                ["blockedTransactions"] = blocked,
                ["warningsReported"] = count(ActionWarningReported),
                ["warningsKept"] = count(ActionWarningKept),
                ["accepted"] = count(ActionAccepted),
                ["resolved"] = count(ActionResolved),
                ["errorsLeftToUser"] = count(ActionLeftToUser),
                ["failures"] = new JArray(all.Select(r => FailureToJson(r.record, r.earlier)))
            };
            if (halted)
            {
                payload["appliedBeforeFailure"] = new JArray(committed.Select(c => new JObject
                {
                    ["transactions"] = new JArray(c.Transactions),
                    ["addedElementIds"] = new JArray(c.Added),
                    ["deletedElementIds"] = new JArray(c.Deleted),
                    ["modifiedElementCount"] = c.ModifiedCount
                }));
                if (committedAfterHalt > 0)
                    payload["committedAfterFailure"] = committedAfterHalt;
                payload["retry"] = new JObject
                {
                    ["parameter"] = ResolutionsParameter,
                    ["example"] = new JArray(all.Where(r => r.record.Action == ActionHalted && r.record.FailureId != null)
                        .Select(r => new JObject
                        {
                            ["failureId"] = r.record.FailureId,
                            ["resolution"] = r.record.Resolutions.FirstOrDefault(x => x.IsDefault)?.Caption
                                             ?? r.record.Resolutions.FirstOrDefault()?.Caption
                                             ?? (r.record.IsWarning ? ResolutionAccept : "<no fix available: change the request>")
                        }))
                };
            }
            int dropped = current.Dropped + earlier.Dropped;
            if (dropped > 0)
                payload["failuresNotListed"] = dropped;

            string message = halted
                ? HaltMessage(method, all.Where(r => !r.earlier).Select(r => r.record).ToList(), committed, blocked, committedAfterHalt)
                : SummaryMessage(all.Select(r => r.record).ToList(), current.RolledBackTransactions + earlier.RolledBackTransactions, earlier.Records.Count);
            if (halted && earlier.Records.Count > 0)
                message += $" {earlier.Records.Count} more failure(s) (earlier: true) came from an earlier command that finished after its caller stopped waiting.";
            payload["message"] = message;

            return new FailureReport { Halted = halted, Payload = payload, Message = message };
        }

        private static JObject FailureToJson(FailureRecord r, bool earlier)
        {
            var json = new JObject
            {
                ["transaction"] = r.Transaction,
                ["severity"] = r.Severity,
                ["description"] = r.Description,
                ["action"] = r.Action,
                ["failureId"] = r.FailureId,
                ["elementIds"] = new JArray(r.ElementIds ?? new List<long>()),
                ["canAccept"] = r.IsWarning,
                ["resolutions"] = new JArray(r.Resolutions.Select(x => new JObject
                {
                    ["caption"] = x.Caption,
                    ["type"] = x.Type.ToString(),
                    ["isDefault"] = x.IsDefault
                })),
                ["recordedUtc"] = r.RecordedUtc
            };
            if (r.RequestedResolution != null) json["requestedResolution"] = r.RequestedResolution;
            if (r.ResolutionApplied != null) json["resolutionApplied"] = r.ResolutionApplied;
            if (r.Note != null) json["note"] = r.Note;
            if (earlier) json["earlier"] = true;
            return json;
        }

        private static string HaltMessage(string method, List<FailureRecord> records, List<CommittedChange> committed, int blocked, int committedAfterHalt)
        {
            var text = new StringBuilder();
            List<FailureRecord> stopping = records.Where(r => r.Action == ActionHalted).ToList();
            string failedTransaction = stopping.FirstOrDefault()?.Transaction ?? records.FirstOrDefault()?.Transaction;

            text.Append($"Revit reported {stopping.Count} failure(s) while running {method ?? "the command"}, so the add-in STOPPED the command. ");
            text.Append($"The transaction '{failedTransaction}' was rolled back and none of its changes were applied");
            text.Append(blocked > 0 ? $"; {blocked} later change(s) by the same command were blocked. " : ". ");

            if (committed.Count > 0)
            {
                text.Append($"{committed.Count} earlier transaction(s) of this command were committed before the failure and REMAIN in the model (");
                text.Append(string.Join(", ", committed.SelectMany(c => c.Transactions).Distinct().Take(10).Select(t => "'" + t + "'")));
                text.Append("; see appliedBeforeFailure). ");
            }
            else
            {
                text.Append("No earlier transaction of this command was recorded as committed. ");
            }
            if (committedAfterHalt > 0)
                text.Append($"{committedAfterHalt} transaction(s) still committed after the failure. ");

            text.Append("\nFailures:");
            foreach (var r in stopping)
            {
                text.Append($"\n- {r.Severity}: {r.Description} [failureId {r.FailureId}]");
                if (r.ElementIds != null && r.ElementIds.Count > 0)
                    text.Append($" Elements: {string.Join(", ", r.ElementIds.Take(10))}{(r.ElementIds.Count > 10 ? ", ..." : "")}.");
                text.Append(r.Resolutions.Count > 0
                    ? " Revit fixes: " + string.Join("; ", r.Resolutions.Select(x => $"'{x.Caption}'{(x.IsDefault ? " (default)" : "")}")) + "."
                    : " Revit offers no fix for it.");
                text.Append(r.IsWarning ? " As a warning it can also be accepted." : " As an error it cannot be accepted.");
                if (r.Note != null) text.Append(" " + r.Note);
            }

            text.Append("\nStop and ask the user how to handle each failure: apply one of Revit's fixes, accept a warning, or change the request. ");
            text.Append($"Then call the tool again with {ResolutionsParameter}: [{{\"failureId\": \"<id>\", \"resolution\": \"<fix caption or 'accept'>\"}}]");
            text.Append(committed.Count > 0 ? ", only for the parts that were not applied. " : ". ");
            text.Append("Do not retry without the user's decision.");
            return text.ToString();
        }

        private static string SummaryMessage(List<FailureRecord> records, int rolledBack, int earlierCount)
        {
            var summary = new List<string> { "Revit reported failures in this command that were handled without a dialog." };
            int count(string action) => records.Count(r => r.Action == action);

            if (count(ActionHalted) > 0)
                summary.Add($"{count(ActionHalted)} failure(s) stopped a command that had already returned (see failures[].resolutions): its failed transaction was rolled back and later changes were blocked. Tell the user and ask how to proceed.");
            if (rolledBack > 0)
                summary.Add($"{rolledBack} transaction(s) were rolled back: their changes were NOT applied, even if the result reports them.");
            if (count(ActionResolved) > 0)
                summary.Add($"{count(ActionResolved)} failure(s) were fixed with the requested Revit resolution ({string.Join(", ", records.Where(r => r.Action == ActionResolved).Select(r => "'" + r.ResolutionApplied + "'").Distinct())}).");
            if (count(ActionAccepted) > 0)
                summary.Add($"{count(ActionAccepted)} warning(s) were accepted as requested; the changes were applied.");
            if (count(ActionWarningReported) > 0)
                summary.Add($"{count(ActionWarningReported)} warning(s) were not shown to the user and are not kept in the model; the changes were applied. Tell the user about any that matter.");
            if (count(ActionWarningKept) > 0)
                summary.Add($"{count(ActionWarningKept)} warning(s) were left to Revit and are kept in the model's warning list.");
            if (count(ActionLeftToUser) > 0)
                summary.Add("Errors were left for the user to resolve in Revit's failure dialog. Ask the user what they chose before continuing.");
            if (earlierCount > 0)
                summary.Add($"{earlierCount} of these (earlier: true) came from an earlier command that finished after its caller stopped waiting.");
            return string.Join(" ", summary);
        }
    }
}
