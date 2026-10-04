using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcp.Logging;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.ExternalEvents;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// One code-execution request. Each socket request gets its own instance, so concurrent
    /// requests cannot exchange code, parameters or results.
    /// </summary>
    public class CodeExecutionRequest
    {
        public string RequestId { get; set; }
        public string Code { get; set; }
        public object[] Parameters { get; set; } = Array.Empty<object>();
        public string TransactionMode { get; set; } = ExecuteCodeEventHandler.TransactionModeAuto;
    }

    /// <summary>
    /// External event handler for code execution
    /// </summary>
    public class ExecuteCodeEventHandler : QueuedExternalEventHandler<CodeExecutionRequest, ExecutionResultInfo>
    {
        public const string TransactionModeAuto = "auto";
        public const string TransactionModeNone = "none";

        public const string ErrorTypeCompilation = "compilation";
        public const string ErrorTypeRuntime = "runtime";
        public const string ErrorTypeTransaction = "transaction";

        protected override ExecutionResultInfo Handle(UIApplication app, CodeExecutionRequest request)
        {
            string tag = $"req={request.RequestId ?? "-"} send_code";

            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
                throw CommandErrors.NoActiveDocument("send_code_to_revit", request.RequestId);

            CommandLog.Info("{0} document='{1}' view='{2}' mode={3} codeLength={4} codeHash={5}",
                tag, doc.Title, doc.ActiveView?.Name, request.TransactionMode, request.Code.Length, TraceLog.ShortHash(request.Code));
            if (CommandLog.DebugEnabled)
                CommandLog.Debug("{0} code={1}", tag, TraceLog.Preview(request.Code, 2000));

            // Compile before opening a transaction: a compile error never touches the model.
            MethodInfo entryPoint;
            try
            {
                entryPoint = DynamicCodeCompiler.Compile(request.Code, request.RequestId);
            }
            catch (CodeCompilationException ex)
            {
                CommandLog.Info("{0} compile failed, nothing was run: {1}", tag, TraceLog.Preview(ex.Message, 300));
                return ExecutionResultInfo.Failure(request.RequestId, ErrorTypeCompilation, ex.Message);
            }

            var runClock = Stopwatch.StartNew();
            object result;
            if (request.TransactionMode == TransactionModeNone)
            {
                try
                {
                    result = Invoke(entryPoint, doc, request.Parameters);
                }
                catch (Exception ex)
                {
                    CommandLog.Warning("{0} code threw after {1}ms with no transaction (changes the code made itself are NOT rolled back): {2}",
                        tag, runClock.ElapsedMilliseconds, Unwrap(ex));
                    return ExecutionResultInfo.Failure(request.RequestId, ErrorTypeRuntime,
                        $"Execution failed: {Unwrap(ex).Message}");
                }
            }
            else
            {
                using (var transaction = new Transaction(doc, "Execute AI Code"))
                {
                    transaction.Start();
                    try
                    {
                        result = Invoke(entryPoint, doc, request.Parameters);
                    }
                    catch (Exception ex)
                    {
                        if (transaction.HasStarted())
                            transaction.RollBack();
                        CommandLog.Warning("{0} code threw after {1}ms; transaction rolled back: {2}", tag, runClock.ElapsedMilliseconds, Unwrap(ex));
                        return ExecutionResultInfo.Failure(request.RequestId, ErrorTypeRuntime,
                            $"Execution failed: {Unwrap(ex).Message} (the automatic transaction was rolled back)");
                    }

                    TransactionStatus status;
                    try
                    {
                        status = transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        CommandLog.Error("{0} transaction commit threw after {1}ms: {2}", tag, runClock.ElapsedMilliseconds, ex);
                        return ExecutionResultInfo.Failure(request.RequestId, ErrorTypeTransaction,
                            $"Execution failed: the automatic transaction could not be committed ({ex.Message}).");
                    }

                    CommandLog.Info("{0} code ran in {1}ms; transaction {2}", tag, runClock.ElapsedMilliseconds, status);

                    if (status != TransactionStatus.Committed)
                    {
                        return ExecutionResultInfo.Failure(request.RequestId, ErrorTypeTransaction,
                            $"Execution failed: the automatic transaction was not committed (status: {status}).");
                    }
                }
            }

            if (request.TransactionMode == TransactionModeNone)
                CommandLog.Info("{0} code ran in {1}ms with no transaction", tag, runClock.ElapsedMilliseconds);

            return ExecutionResultInfo.Succeeded(request.RequestId, result);
        }

        private static object Invoke(MethodInfo entryPoint, Document doc, object[] parameters) =>
            entryPoint.Invoke(null, new object[] { doc, parameters });

        private static Exception Unwrap(Exception ex) =>
            ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;

        public override string GetName()
        {
            return "Execute AI Code";
        }
    }

    // Execution result data structure
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>"compilation", "runtime" or "transaction" when <see cref="Success"/> is false.</summary>
        [JsonProperty("errorType", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorType { get; set; }

        [JsonProperty("requestId", NullValueHandling = NullValueHandling.Ignore)]
        public string RequestId { get; set; }

        public static ExecutionResultInfo Failure(string requestId, string errorType, string message) =>
            new ExecutionResultInfo { Success = false, ErrorType = errorType, ErrorMessage = message, RequestId = requestId };

        public static ExecutionResultInfo Succeeded(string requestId, object result)
        {
            var info = new ExecutionResultInfo { Success = true, RequestId = requestId };
            try
            {
                info.Result = JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                // The code already ran (and any transaction committed), so this is still a success.
                info.Result = JsonConvert.SerializeObject(result?.ToString());
                info.ErrorMessage = $"The return value could not be serialized to JSON ({ex.Message}); returned its ToString() instead.";
            }
            return info;
        }
    }
}
