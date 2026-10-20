using RevitMcp.Interop;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.Exceptions;

namespace RevitMCPCommandSet.Utils.ExternalEvents
{
    /// <summary>
    /// Values of the <c>kind</c> field carried in JSON-RPC error data. Clients branch on this
    /// field instead of parsing messages. "connection" is produced by clients, never by the add-in.
    /// </summary>
    public static class ErrorKinds
    {
        public const string Timeout = "timeout";
        public const string Validation = "validation";
        public const string RevitState = "revit_state";
        public const string RevitUnavailable = "revit_unavailable";
        public const string Execution = "execution";

        /// <summary>A modal dialog in Revit needs the user's answer; see <see cref="RevitDialogDetector"/>.</summary>
        public const string UserInputRequired = RevitDialogDetector.ErrorKind;
    }

    /// <summary>Values of the <c>outcome</c> field on timeout errors.</summary>
    public static class TimeoutOutcomes
    {
        /// <summary>The request was cancelled before Revit started it; it will never run.</summary>
        public const string CancelledBeforeStart = "cancelled_before_start";

        /// <summary>The request was already running; it may still complete and change the model.</summary>
        public const string OutcomeUnknown = "outcome_unknown";
    }

    /// <summary>
    /// Builds <see cref="CommandExecutionException"/>s with structured data. The plugin's
    /// CommandExecutor copies the code and data into the JSON-RPC error response.
    /// </summary>
    public static class CommandErrors
    {
        public static CommandExecutionException Validation(string message, string command, string requestId, object details = null)
        {
            return Create(message, JsonRPCErrorCodes.InvalidParams, new Dictionary<string, object>
            {
                ["kind"] = ErrorKinds.Validation,
                ["command"] = command,
                ["requestId"] = requestId,
                ["details"] = details
            });
        }

        public static CommandExecutionException NoActiveDocument(string command, string requestId)
        {
            return RevitState(
                "No active Revit document. Open or activate a project in Revit and try again.",
                JsonRPCErrorCodes.DocumentNotAvailable, "no_active_document", command, requestId);
        }

        public static CommandExecutionException NoActiveView(string command, string requestId)
        {
            return RevitState(
                "The active Revit document has no active view. Activate a view in Revit and try again.",
                JsonRPCErrorCodes.ViewNotFound, "no_active_view", command, requestId);
        }

        public static CommandExecutionException RevitState(string message, int code, string reason, string command, string requestId)
        {
            return Create(message, code, new Dictionary<string, object>
            {
                ["kind"] = ErrorKinds.RevitState,
                ["reason"] = reason,
                ["command"] = command,
                ["requestId"] = requestId
            });
        }

        public static CommandExecutionException Execution<TRequest, TResult>(
            ExternalEventInvocation<TRequest, TResult> invocation, Exception error)
        {
            return Create($"{invocation.CommandName} failed: {error.Message}", JsonRPCErrorCodes.RevitApiError,
                new Dictionary<string, object>
                {
                    ["kind"] = ErrorKinds.Execution,
                    ["command"] = invocation.CommandName,
                    ["requestId"] = invocation.RequestId,
                    ["invocationId"] = invocation.InvocationId,
                    ["exceptionType"] = error.GetType().FullName
                });
        }

        /// <summary>
        /// For commands on the SDK's ExternalEventCommandBase, which have no invocation: the handler
        /// failed inside Revit and the failure is returned to the caller instead of shown in a dialog.
        /// </summary>
        public static CommandExecutionException Execution(string command, string requestId, string message, Exception error = null, object details = null)
        {
            return Create(message, JsonRPCErrorCodes.RevitApiError, new Dictionary<string, object>
            {
                ["kind"] = ErrorKinds.Execution,
                ["command"] = command,
                ["requestId"] = requestId,
                ["exceptionType"] = error?.GetType().FullName,
                ["details"] = details
            });
        }

        public static CommandExecutionException EventRejected<TRequest, TResult>(
            ExternalEventInvocation<TRequest, TResult> invocation, string raiseStatus)
        {
            return Create(
                $"Revit refused to queue {invocation.CommandName} (external event status: {raiseStatus}). The request was not executed and is safe to retry.",
                JsonRPCErrorCodes.ExternalEventExecutionFailed,
                new Dictionary<string, object>
                {
                    ["kind"] = ErrorKinds.RevitUnavailable,
                    ["raiseStatus"] = raiseStatus,
                    ["command"] = invocation.CommandName,
                    ["requestId"] = invocation.RequestId,
                    ["invocationId"] = invocation.InvocationId,
                    ["executed"] = false,
                    ["retrySafe"] = true
                });
        }

        public static CommandExecutionException Timeout<TRequest, TResult>(
            ExternalEventInvocation<TRequest, TResult> invocation, InvocationState state, int timeoutMs, bool mayModifyModel)
        {
            bool started = state == InvocationState.OutcomeUnknown;
            bool modelMayHaveChanged = started && mayModifyModel;

            string message;
            if (!started)
            {
                message = $"{invocation.CommandName} timed out after {timeoutMs} ms before Revit started it " +
                          "(the Revit UI thread was busy). The request was cancelled and will not run; it is safe to retry.";
            }
            else if (modelMayHaveChanged)
            {
                message = $"{invocation.CommandName} was still running in Revit when the {timeoutMs} ms wait expired. " +
                          "Its outcome is unknown: it may still complete and commit changes to the model. " +
                          "Do not retry automatically; inspect the model before running it again.";
            }
            else
            {
                message = $"{invocation.CommandName} was still running in Revit when the {timeoutMs} ms wait expired. " +
                          "It does not modify the model, so it is safe to retry once Revit is responsive.";
            }

            return Create(message, JsonRPCErrorCodes.CommandExecutionTimeout, new Dictionary<string, object>
            {
                ["kind"] = ErrorKinds.Timeout,
                ["outcome"] = started ? TimeoutOutcomes.OutcomeUnknown : TimeoutOutcomes.CancelledBeforeStart,
                ["command"] = invocation.CommandName,
                ["requestId"] = invocation.RequestId,
                ["invocationId"] = invocation.InvocationId,
                ["timeoutMs"] = timeoutMs,
                ["elapsedMs"] = invocation.ElapsedMilliseconds,
                ["started"] = started,
                ["modelMayHaveChanged"] = modelMayHaveChanged,
                ["retrySafe"] = !modelMayHaveChanged
            });
        }

        /// <summary>
        /// The caller stopped waiting because a modal dialog blocks Revit. Same envelope as the
        /// plugin's pre-dispatch check, plus the timeout fields that say whether the request ran.
        /// </summary>
        public static CommandExecutionException UserInputRequired<TRequest, TResult>(
            ExternalEventInvocation<TRequest, TResult> invocation, InvocationState state, RevitUiState uiState, bool mayModifyModel)
        {
            bool started = state == InvocationState.OutcomeUnknown;
            bool modelMayHaveChanged = started && mayModifyModel;
            string dialogs = uiState.DescribeDialogs();

            string message = started
                ? $"Revit is showing a dialog that needs the user's input ({dialogs}) while {invocation.CommandName} was running. " +
                  "It continues once the user answers the dialog, so its outcome is unknown" +
                  (modelMayHaveChanged ? " and it may still change the model. " : ". ")
                : $"Revit is showing a dialog that needs the user's input ({dialogs}). {invocation.CommandName} was cancelled before it started and will not run. ";
            message += "Stop and ask the user to respond to the dialog in Revit; do not call any more Revit tools until the user confirms it is closed.";

            return Create(message, JsonRPCErrorCodes.ResourceUnavailable, new Dictionary<string, object>
            {
                ["kind"] = ErrorKinds.UserInputRequired,
                ["reason"] = RevitDialogDetector.ErrorReason,
                ["outcome"] = started ? TimeoutOutcomes.OutcomeUnknown : TimeoutOutcomes.CancelledBeforeStart,
                ["command"] = invocation.CommandName,
                ["requestId"] = invocation.RequestId,
                ["invocationId"] = invocation.InvocationId,
                ["dialogs"] = uiState.DialogsData(),
                ["elapsedMs"] = invocation.ElapsedMilliseconds,
                ["executed"] = started,
                ["started"] = started,
                ["modelMayHaveChanged"] = modelMayHaveChanged,
                ["retrySafe"] = !modelMayHaveChanged
            });
        }

        private static CommandExecutionException Create(string message, int code, Dictionary<string, object> data)
        {
            foreach (var key in data.Where(pair => pair.Value == null).Select(pair => pair.Key).ToList())
                data.Remove(key);

            return new CommandExecutionException(message, code, data);
        }
    }
}
