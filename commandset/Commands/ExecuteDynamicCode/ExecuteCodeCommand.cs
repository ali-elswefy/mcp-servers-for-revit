using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// Command that handles code execution.
    ///
    /// Outcomes:
    /// - JSON-RPC success with success=true: the code ran (and the automatic transaction committed).
    /// - JSON-RPC success with success=false and errorType compilation/runtime/transaction: nothing was committed.
    /// - JSON-RPC error with data.kind="timeout": see data.outcome. "cancelled_before_start" never ran;
    ///   "outcome_unknown" may still commit, so the caller must inspect the model and must not replay it.
    /// </summary>
    public class ExecuteCodeCommand : QueuedExternalEventCommandBase<CodeExecutionRequest, ExecutionResultInfo>
    {
        public override string CommandName => "send_code_to_revit";

        public ExecuteCodeCommand(UIApplication uiApp)
            : base(new ExecuteCodeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            string code = parameters?["code"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(code))
                throw CommandErrors.Validation("Missing required parameter: 'code'", CommandName, requestId);

            JArray parametersArray = parameters["parameters"] as JArray;
            object[] executionParameters = parametersArray?.ToObject<object[]>() ?? Array.Empty<object>();

            string transactionMode = parameters["transactionMode"]?.Value<string>() ?? ExecuteCodeEventHandler.TransactionModeAuto;
            transactionMode = transactionMode.Trim().ToLowerInvariant();
            if (transactionMode != ExecuteCodeEventHandler.TransactionModeAuto &&
                transactionMode != ExecuteCodeEventHandler.TransactionModeNone)
            {
                throw CommandErrors.Validation(
                    $"Invalid transactionMode '{transactionMode}'. Use 'auto' or 'none'.", CommandName, requestId);
            }

            int timeoutMs = CommandTimeouts.Resolve(CommandName, requestId, CommandTimeouts.ExecuteCodeDefaultMs, parameters);

            var request = new CodeExecutionRequest
            {
                RequestId = requestId,
                Code = code,
                Parameters = executionParameters,
                TransactionMode = transactionMode
            };

            return RunOnRevitThread(request, requestId, timeoutMs);
        }
    }
}
