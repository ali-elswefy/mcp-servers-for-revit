using System.Collections.Concurrent;
using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitMCPSDK.Exceptions;

namespace RevitMCPCommandSet.Utils.ExternalEvents
{
    /// <summary>
    /// External event handler that processes a queue of independent invocations instead of
    /// holding request/result state in shared fields. Each socket request enqueues its own
    /// <see cref="ExternalEventInvocation{TRequest,TResult}"/>; the Revit callback drains the
    /// queue, skips invocations whose caller already cancelled them, and writes each result
    /// only to its own invocation.
    /// </summary>
    public abstract class QueuedExternalEventHandler<TRequest, TResult> : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ConcurrentQueue<ExternalEventInvocation<TRequest, TResult>> _queue =
            new ConcurrentQueue<ExternalEventInvocation<TRequest, TResult>>();

        internal void Enqueue(ExternalEventInvocation<TRequest, TResult> invocation) => _queue.Enqueue(invocation);

        internal int QueueDepth => _queue.Count;

        /// <summary>Runs on the Revit UI thread. Throw to report a failure to the caller.</summary>
        protected abstract TResult Handle(UIApplication app, TRequest request);

        public abstract string GetName();

        public void Execute(UIApplication app)
        {
            if (CommandLog.DebugEnabled)
                CommandLog.Debug("{0}: Revit callback entered, {1} queued", GetName(), _queue.Count);

            // ExternalEvent.Raise() coalesces while an event is pending, so one callback may
            // have to serve several queued invocations.
            while (_queue.TryDequeue(out var invocation))
            {
                if (!invocation.TryStart())
                {
                    CommandLog.Info("{0} skipped in state {1}: cancelled before it started, not executed", invocation.LogTag, invocation.State);
                    continue;
                }

                CommandLog.Info("{0} started on Revit thread after waiting {1}ms", invocation.LogTag, invocation.QueueWaitMilliseconds);

                var runClock = Stopwatch.StartNew();
                bool delivered;
                string outcome;
                try
                {
                    delivered = invocation.TryComplete(Handle(app, invocation.Request));
                    outcome = "ok";
                }
                catch (Exception ex)
                {
                    delivered = invocation.TryFail(ex);
                    if (ex is CommandExecutionException structured)
                    {
                        outcome = $"rejected code={structured.ErrorCode}: {structured.Message}";
                    }
                    else
                    {
                        outcome = "failed with an unexpected exception";
                        CommandLog.Error("{0} {1}", invocation.LogTag, ex);
                    }
                }

                CommandLog.Info("{0} finished {1} in {2}ms (total {3}ms){4}", invocation.LogTag, outcome, runClock.ElapsedMilliseconds,
                    invocation.ElapsedMilliseconds, delivered ? string.Empty : " after its caller had already timed out");

                if (!delivered)
                {
                    CommandLog.Warning(
                        "{0} result was NOT delivered: the caller timed out and was told the outcome is unknown. The command ran to completion ({1}).",
                        invocation.LogTag, outcome);
                }
            }
        }

        /// <summary>
        /// Not supported: waiting is per invocation. Use
        /// <see cref="QueuedExternalEventCommandBase{TRequest,TResult}"/> to run requests.
        /// </summary>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            throw new NotSupportedException(
                $"{GetType().Name} tracks completion per invocation; use QueuedExternalEventCommandBase.RunOnRevitThread.");
        }
    }
}
