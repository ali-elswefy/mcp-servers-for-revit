using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.Exceptions;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace revit_mcp_plugin.Core
{
    /// <summary>What a request ended in, for logging. Filled in by <see cref="CommandExecutor"/>.</summary>
    public class CommandOutcome
    {
        public bool Success { get; set; } = true;
        public int? ErrorCode { get; set; }

        /// <summary>The "kind" in the JSON-RPC error data (timeout, validation, revit_state, ...).</summary>
        public string Kind { get; set; }

        /// <summary>The "outcome" in timeout error data (cancelled_before_start, outcome_unknown).</summary>
        public string TimeoutOutcome { get; set; }

        public string Message { get; set; }

        public override string ToString()
        {
            if (Success)
                return "ok";

            string text = "error code=" + ErrorCode;
            if (Kind != null) text += " kind=" + Kind;
            if (TimeoutOutcome != null) text += " outcome=" + TimeoutOutcome;
            return text;
        }
    }

    public class CommandExecutor
    {
        // Values of the "kind" field on JSON-RPC error data produced here. Command sets add their
        // own kinds (timeout, validation, revit_state, ...) through CommandExecutionException data.
        public const string ErrorKindMethodNotFound = "method_not_found";
        public const string ErrorKindInternal = "internal";

        private readonly ICommandRegistry _commandRegistry;
        private readonly ILogger _logger;

        public CommandExecutor(ICommandRegistry commandRegistry, ILogger logger)
        {
            _commandRegistry = commandRegistry;
            _logger = logger;
        }

        public string ExecuteCommand(JsonRPCRequest request) => ExecuteCommand(request, out _);

        /// <summary>
        /// Executes a Revit command declared inside a JSON-RPC request.
        /// </summary>
        /// <param name="request">A JSON-RPC request.</param>
        /// <param name="outcome">How the request ended, for logging.</param>
        /// <returns>The JSON-RPC response.</returns>
        public string ExecuteCommand(JsonRPCRequest request, out CommandOutcome outcome)
        {
            outcome = new CommandOutcome();
            var clock = Stopwatch.StartNew();

            try
            {
                // Find the command.
                if (!_commandRegistry.TryGetCommand(request.Method, out var command))
                {
                    _logger.Warning("req={0} cmd={1} dispatch failed: command not found", request.Id, request.Method);
                    return Fail(outcome, request.Id,
                        JsonRPCErrorCodes.MethodNotFound,
                        $"Method not found: '{request.Method}'",
                        new { kind = ErrorKindMethodNotFound });
                }

                _logger.Info("req={0} cmd={1} dispatch", request.Id, request.Method);

                // Execute the command. Revit failures raised by its transactions are reported with the response.
                using (var failures = RevitFailureMonitor.Instance.BeginRequest(request.Id, request.Method))
                {
                    try
                    {
                        object result = command.Execute(request.GetParamsObject(), request.Id);
                        _logger.Info("req={0} cmd={1} dispatch done ok in {2}ms", request.Id, request.Method, clock.ElapsedMilliseconds);

                        return WithFailureReport(CreateSuccessResponse(request.Id, result), failures, request);
                    }
                    catch (Exception ex) when (FindCommandExecutionException(ex) is CommandExecutionException commandEx)
                    {
                        object data = commandEx.ErrorData ?? new { kind = ErrorKindInternal };
                        string response = WithFailureReport(Fail(outcome, request.Id, commandEx.ErrorCode, commandEx.Message, data),
                            failures, request);
                        _logger.Warning("req={0} cmd={1} dispatch done in {2}ms: {3}: {4}",
                            request.Id, request.Method, clock.ElapsedMilliseconds, outcome, commandEx.Message);
                        return response;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("req={0} cmd={1} dispatch failed in {2}ms with an unexpected exception: {3}",
                            request.Id, request.Method, clock.ElapsedMilliseconds, ex);
                        return WithFailureReport(Fail(outcome, request.Id,
                            JsonRPCErrorCodes.InternalError,
                            ex.Message,
                            new { kind = ErrorKindInternal }), failures, request);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("req={0} dispatch failed: {1}", request.Id, ex);
                return Fail(outcome, request.Id,
                    JsonRPCErrorCodes.InternalError,
                    $"Internal error: {ex.Message}",
                    new { kind = ErrorKindInternal });
            }
        }

        private string Fail(CommandOutcome outcome, string id, int code, string message, object data)
        {
            outcome.Success = false;
            outcome.ErrorCode = code;
            outcome.Message = message;

            if (data != null)
            {
                JToken token = JToken.FromObject(data);
                outcome.Kind = token["kind"]?.Value<string>();
                outcome.TimeoutOutcome = token["outcome"]?.Value<string>();
            }

            return CreateErrorResponse(id, code, message, data);
        }

        /// <summary>
        /// Adds "revitFailures" to the response envelope (next to "result" or "error") when Revit
        /// reported failures in the command's transactions, including ones the failure handler
        /// silenced. On the envelope it works for every result shape and for errors alike.
        /// </summary>
        private string WithFailureReport(string response, RevitFailureMonitor.RequestFailures failures, JsonRPCRequest request)
        {
            object report = failures.BuildReport();
            if (report == null)
                return response;

            _logger.Info("req={0} cmd={1} response carries revitFailures", request.Id, request.Method);
            return AttachFailureReport(response, report);
        }

        /// <summary>Adds a "revitFailures" report to a serialized JSON-RPC response.</summary>
        public static string AttachFailureReport(string response, object report)
        {
            if (report == null)
                return response;

            JObject envelope = JObject.Parse(response);
            envelope["revitFailures"] = JToken.FromObject(report);
            return envelope.ToString(Formatting.None);
        }

        // Commands sometimes wrap a structured error in a generic exception; keep the structure.
        private static CommandExecutionException FindCommandExecutionException(Exception ex)
        {
            var seen = new HashSet<Exception>();
            for (var current = ex; current != null && seen.Add(current); current = current.InnerException)
            {
                if (current is CommandExecutionException commandEx)
                    return commandEx;
            }
            return null;
        }

        public string CreateSuccessResponse(string id, object result)
        {
            var response = new JsonRPCSuccessResponse
            {
                Id = id,
                Result = result is JToken jToken ? jToken : JToken.FromObject(result)
            };

            return response.ToJson();
        }

        public string CreateErrorResponse(string id, int code, string message, object data = null)
        {
            var response = new JsonRPCErrorResponse
            {
                Id = id,
                Error = new JsonRPCError
                {
                    Code = code,
                    Message = message,
                    Data = data != null ? JToken.FromObject(data) : null
                }
            };

            return response.ToJson();
        }
    }
}
