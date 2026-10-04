using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMcp.Logging;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    public class SocketService
    {
        private static SocketService _instance;
        private TcpListener _listener;
        private Thread _listenerThread;
        private bool _isRunning;
        private int _port = 8080;
        private UIApplication _uiApp;
        private ICommandRegistry _commandRegistry;
        private ILogger _logger;
        private CommandExecutor _commandExecutor;

        // Built-in method answered by the socket service itself, even if no command set loaded.
        public const string HealthCheckMethod = "health_check";
        private const int DefaultUiProbeTimeoutMs = 2000;
        private const int MaxUiProbeTimeoutMs = 30000;

        private readonly UiThreadProbe _uiThreadProbe = new UiThreadProbe();
        private bool _idlingSubscribed;
        private long _lastIdlingUtcTicks;
        private DateTime? _startedUtc;

        public static SocketService Instance
        {
            get
            {
                if(_instance == null)
                    _instance = new SocketService();
                return _instance;
            }
        }

        private SocketService()
        {
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;

        public int Port
        {
            get => _port;
            set => _port = value;
        }

        // Initialize the service.
        public void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;

            // Initialize ExternalEventManager.
            ExternalEventManager.Instance.Initialize(uiApp, _logger);

            // Initialize runs in a Revit API context, which external events and Idling subscriptions require.
            _uiThreadProbe.EnsureCreated();
            if (!_idlingSubscribed)
            {
                _uiApp.Idling += OnIdling;
                _idlingSubscribed = true;
            }

            // Record the current Revit version.
            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            string currentVersion = versionAdapter.GetRevitVersion();
            _logger.Info("Current Revit version: {0}", currentVersion);



            // Create the command executor.
            _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

            // Load configuration and register commands.
            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();
            

            //// Read the service port from the configuration.
            //if (configManager.Config.Settings.Port > 0)
            //{
            //    _port = configManager.Config.Settings.Port;
            //}
            _port = 8080; // Hard-wired port number.

            // Load commands.
            CommandManager commandManager = new CommandManager(
                _commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            _logger.Info($"Socket service initialized on port {_port}");
        }

        private int _connectionCounter;

        // One request as seen on the wire, filled in while it is processed so the response can be logged with it.
        private class RequestTrace
        {
            public int ConnectionId;
            public int RequestBytes;
            public string Id;
            public string Method;
            public CommandOutcome Outcome = new CommandOutcome();
        }

        private static void Wire(LogSeverity severity, string message, params object[] args)
        {
            TraceLog.Write(LogChannel.Protocol, severity, message, args);
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                _isRunning = true;
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();

                _listenerThread = new Thread(ListenForClients)
                {
                    IsBackground = true
                };
                _listenerThread.Start();
                _startedUtc = DateTime.UtcNow;
                Wire(LogSeverity.Info, "server started port={0} level={1} log={2}", _port, TraceLog.CurrentLevel, TraceLog.LogDirectory);
            }
            catch (Exception ex)
            {
                _isRunning = false;
                Wire(LogSeverity.Error, "server failed to start on port {0}: {1}", _port, ex);
                _logger.Error("MCP server failed to start on port {0}: {1}", _port, ex.Message);
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                Wire(LogSeverity.Info, "server stopping");
                _isRunning = false;

                _listener?.Stop();
                _listener = null;

                if(_listenerThread!=null && _listenerThread.IsAlive)
                {
                    _listenerThread.Join(1000);
                }
                Wire(LogSeverity.Info, "server stopped");
            }
            catch (Exception ex)
            {
                Wire(LogSeverity.Error, "error while stopping the server: {0}", ex);
            }
        }

        private void ListenForClients()
        {
            try
            {
                while (_isRunning)
                {
                    TcpClient client = _listener.AcceptTcpClient();

                    Thread clientThread = new Thread(HandleClientCommunication)
                    {
                        IsBackground = true
                    };
                    clientThread.Start(client);
                }
            }
            catch (Exception ex)
            {
                // Stop() ends the blocking accept with a SocketException; only an unexpected end is worth a line.
                if (_isRunning)
                {
                    Wire(LogSeverity.Error, "listener stopped unexpectedly: {0}", ex);
                }
            }
        }

        private void HandleClientCommunication(object clientObj)
        {
            TcpClient tcpClient = (TcpClient)clientObj;
            int connectionId = Interlocked.Increment(ref _connectionCounter);
            var connectionClock = Stopwatch.StartNew();
            int requestCount = 0;
            string closeReason = "server_stopping";

            Wire(LogSeverity.Info, "conn={0} opened remote={1}", connectionId, SafeRemoteEndpoint(tcpClient));

            try
            {
                NetworkStream stream = tcpClient.GetStream();
                byte[] buffer = new byte[8192];

                while (_isRunning && tcpClient.Connected)
                {
                    // Read client messages.
                    int bytesRead;

                    try
                    {
                        bytesRead = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (IOException ex)
                    {
                        closeReason = "read_failed (" + ex.Message + ")";
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        closeReason = "client_closed";
                        break;
                    }

                    var clock = Stopwatch.StartNew();
                    requestCount++;
                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var trace = new RequestTrace { ConnectionId = connectionId, RequestBytes = bytesRead };

                    string response = ProcessJsonRPCRequest(message, trace);

                    // Send response.
                    byte[] responseData = Encoding.UTF8.GetBytes(response);
                    try
                    {
                        stream.Write(responseData, 0, responseData.Length);
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                    {
                        // The client gave up (for example after its own timeout). The command may still have run.
                        Wire(LogSeverity.Warning,
                            "conn={0} req={1} method={2} response NOT delivered ({3} bytes, {4}ms after receipt): client had disconnected ({5}). Result was: {6}",
                            connectionId, trace.Id ?? "-", trace.Method ?? "-", responseData.Length, clock.ElapsedMilliseconds, ex.Message, trace.Outcome);
                        closeReason = "write_failed";
                        break;
                    }

                    Wire(trace.Outcome.Success ? LogSeverity.Info : LogSeverity.Warning,
                        "conn={0} req={1} method={2} sent {3} bytes={4} elapsed={5}ms",
                        connectionId, trace.Id ?? "-", trace.Method ?? "-", trace.Outcome, responseData.Length, clock.ElapsedMilliseconds);
                    if (TraceLog.IsEnabled(LogSeverity.Debug))
                    {
                        Wire(LogSeverity.Debug, "conn={0} req={1} response={2}", connectionId, trace.Id ?? "-", TraceLog.Preview(response));
                    }
                }
            }
            catch (Exception ex)
            {
                closeReason = "error";
                Wire(LogSeverity.Error, "conn={0} failed: {1}", connectionId, ex);
            }
            finally
            {
                tcpClient.Close();
                Wire(LogSeverity.Info, "conn={0} closed reason={1} requests={2} duration={3}ms",
                    connectionId, closeReason, requestCount, connectionClock.ElapsedMilliseconds);
            }
        }

        private static string SafeRemoteEndpoint(TcpClient client)
        {
            try
            {
                return client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private string ProcessJsonRPCRequest(string requestJson, RequestTrace trace)
        {
            JsonRPCRequest request;

            try
            {
                // Parse JSON-RPC requests.
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);

                // Verify that the request format is valid.
                if (request == null || !request.IsValid())
                {
                    Wire(LogSeverity.Warning, "conn={0} recv invalid JSON-RPC request bytes={1} preview={2}",
                        trace.ConnectionId, trace.RequestBytes, TraceLog.Preview(requestJson, 200));
                    return Fail(trace, null, JsonRPCErrorCodes.InvalidRequest, "Invalid JSON-RPC request",
                        new { kind = "invalid_request" });
                }

                trace.Id = request.Id;
                trace.Method = request.Method;
                Wire(LogSeverity.Info, "conn={0} req={1} method={2} recv bytes={3}",
                    trace.ConnectionId, request.Id, request.Method, trace.RequestBytes);
                if (TraceLog.IsEnabled(LogSeverity.Debug))
                {
                    Wire(LogSeverity.Debug, "conn={0} req={1} params={2}", trace.ConnectionId, request.Id,
                        TraceLog.Preview(request.GetParamsObject()?.ToString(Formatting.None)));
                }

                if (request.Method == HealthCheckMethod)
                {
                    return CreateSuccessResponse(request.Id, BuildHealthReport(request.GetParamsObject()));
                }

                // Look up and execute the command, preserving structured error codes and data.
                string response = _commandExecutor.ExecuteCommand(request, out CommandOutcome outcome);
                trace.Outcome = outcome;
                return response;
            }
            catch (JsonException ex)
            {
                // JSON parsing error. Requests are read with a single 8 KB read, so a truncated or merged message ends up here.
                string head = TraceLog.Preview(requestJson, 200);
                string tail = requestJson != null && requestJson.Length > 200
                    ? TraceLog.Preview(requestJson.Substring(requestJson.Length - 60), 60)
                    : string.Empty;
                Wire(LogSeverity.Warning, "conn={0} recv unparseable JSON bytes={1} ({2}) head={3} tail={4}",
                    trace.ConnectionId, trace.RequestBytes, ex.Message, head, tail);
                return Fail(trace, null, JsonRPCErrorCodes.ParseError, "Invalid JSON",
                    new { kind = "invalid_request" });
            }
            catch (Exception ex)
            {
                // Catch other errors produced when processing requests.
                Wire(LogSeverity.Error, "conn={0} req={1} internal error while processing: {2}", trace.ConnectionId, trace.Id ?? "-", ex);
                return Fail(trace, trace.Id, JsonRPCErrorCodes.InternalError, $"Internal error: {ex.Message}",
                    new { kind = CommandExecutor.ErrorKindInternal });
            }
        }

        private string Fail(RequestTrace trace, string id, int code, string message, object data)
        {
            JToken token = JToken.FromObject(data);
            trace.Outcome = new CommandOutcome
            {
                Success = false,
                ErrorCode = code,
                Message = message,
                Kind = token["kind"]?.Value<string>()
            };
            return CreateErrorResponse(id, code, message, data);
        }

        private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            Interlocked.Exchange(ref _lastIdlingUtcTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Reports two separate facts. "plugin" proves only that this socket service answered;
        /// it says nothing about whether Revit API work can run. "revitUiThread" is measured by
        /// raising a no-op external event and waiting a bounded time for Revit to service it.
        /// </summary>
        private object BuildHealthReport(JObject parameters)
        {
            bool probeUiThread = parameters?["probeUiThread"]?.Value<bool?>() ?? true;
            int probeTimeoutMs = parameters?["uiProbeTimeoutMs"]?.Value<int?>() ?? DefaultUiProbeTimeoutMs;
            probeTimeoutMs = Math.Min(MaxUiProbeTimeoutMs, Math.Max(100, probeTimeoutMs));

            long idlingTicks = Interlocked.Read(ref _lastIdlingUtcTicks);
            DateTime? lastIdlingUtc = idlingTicks > 0 ? new DateTime(idlingTicks, DateTimeKind.Utc) : (DateTime?)null;

            var plugin = new
            {
                reachable = true,
                socketRunning = _isRunning,
                port = _port,
                startedUtc = _startedUtc,
                registeredCommandCount = (_commandRegistry as RevitCommandRegistry)?.GetRegisteredCommands().Count(),
                // Revit raises Idling when it enters an idle state, not continuously, so an old
                // timestamp alone does not mean the UI thread is blocked.
                lastIdlingUtc,
                secondsSinceLastIdling = lastIdlingUtc.HasValue ? Math.Round((DateTime.UtcNow - lastIdlingUtc.Value).TotalSeconds, 1) : (double?)null
            };

            object uiThread = probeUiThread
                ? _uiThreadProbe.Probe(probeTimeoutMs)
                : new { probed = false, responsive = (bool?)null, reason = "probe_not_requested" };

            var logging = new
            {
                level = TraceLog.CurrentLevel.ToString(),
                directory = TraceLog.LogDirectory,
                protocolLog = TraceLog.GetLogPath(LogChannel.Protocol),
                addinLog = TraceLog.GetLogPath(LogChannel.Addin)
            };

            return new { plugin, revitUiThread = uiThread, logging };
        }

        private string CreateSuccessResponse(string id, object result)
        {
            var response = new JsonRPCSuccessResponse
            {
                Id = id,
                Result = result is JToken jToken ? jToken : JToken.FromObject(result)
            };

            return response.ToJson();
        }

        private string CreateErrorResponse(string id, int code, string message, object data = null)
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
