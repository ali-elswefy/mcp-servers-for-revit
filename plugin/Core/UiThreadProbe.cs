using Autodesk.Revit.UI;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Measures whether the Revit UI thread can currently service an external event, without
    /// touching the model. Each probe has its own token, so a callback that arrives after a
    /// probe timed out cannot be mistaken for the result of a later probe.
    /// </summary>
    public class UiThreadProbe : IExternalEventHandler
    {
        private readonly ConcurrentQueue<ProbeToken> _pending = new ConcurrentQueue<ProbeToken>();
        private ExternalEvent _event;

        /// <summary>Must be called from a valid Revit API context (command or Idling handler).</summary>
        public void EnsureCreated()
        {
            if (_event == null)
                _event = ExternalEvent.Create(this);
        }

        public object Probe(int timeoutMs)
        {
            if (_event == null)
            {
                return new { probed = false, responsive = (bool?)null, reason = "probe_not_initialized" };
            }

            var token = new ProbeToken();
            _pending.Enqueue(token);
            ExternalEventRequest raiseStatus = _event.Raise();

            bool responded = raiseStatus != ExternalEventRequest.Denied &&
                             raiseStatus != ExternalEventRequest.TimedOut &&
                             token.Done.Wait(timeoutMs);

            return new
            {
                probed = true,
                responsive = responded,
                latencyMs = responded ? token.LatencyMs : (long?)null,
                timeoutMs,
                raiseStatus = raiseStatus.ToString(),
                hasActiveDocument = responded ? token.HasActiveDocument : (bool?)null
            };
        }

        public void Execute(UIApplication app)
        {
            bool hasActiveDocument = app.ActiveUIDocument?.Document != null;
            while (_pending.TryDequeue(out var token))
            {
                token.Complete(hasActiveDocument);
            }
        }

        public string GetName() => "MCP UI Thread Probe";

        private class ProbeToken
        {
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public ManualResetEventSlim Done { get; } = new ManualResetEventSlim(false);
            public long LatencyMs { get; private set; }
            public bool HasActiveDocument { get; private set; }

            public void Complete(bool hasActiveDocument)
            {
                LatencyMs = _clock.ElapsedMilliseconds;
                HasActiveDocument = hasActiveDocument;
                Done.Set();
            }
        }
    }
}
