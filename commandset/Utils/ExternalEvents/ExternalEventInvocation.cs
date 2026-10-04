using System.Diagnostics;

namespace RevitMCPCommandSet.Utils.ExternalEvents
{
    /// <summary>
    /// Lifecycle of one request queued for the Revit UI thread.
    /// Pending -> Running -> Completed is the normal path. A caller whose wait expires moves
    /// Pending -> CancelledBeforeStart (the request will never run) or
    /// Running -> OutcomeUnknown (the request may still finish and commit changes).
    /// </summary>
    public enum InvocationState
    {
        Pending = 0,
        Running = 1,
        Completed = 2,
        CancelledBeforeStart = 3,
        OutcomeUnknown = 4
    }

    /// <summary>
    /// Owns the request, result and state of a single command invocation, so concurrent
    /// requests never share mutable state. All transitions are atomic; whichever side
    /// (Revit callback or waiting caller) wins a transition decides the outcome.
    /// </summary>
    public sealed class ExternalEventInvocation<TRequest, TResult>
    {
        private int _state = (int)InvocationState.Pending;
        private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public ExternalEventInvocation(string commandName, string requestId, TRequest request)
        {
            CommandName = commandName;
            RequestId = requestId;
            Request = request;
        }

        public string InvocationId { get; } = Guid.NewGuid().ToString("N");
        public string CommandName { get; }
        public string RequestId { get; }
        public TRequest Request { get; }

        /// <summary>Set by the Revit callback; only meaningful once the callback has finished.</summary>
        public TResult Result { get; private set; }

        /// <summary>Set by the Revit callback when the handler threw.</summary>
        public Exception Error { get; private set; }

        /// <summary>Tag used on every log line about this invocation, e.g. "req=u1 cmd=send_code_to_revit inv=7f59bcac".</summary>
        public string LogTag =>
            $"req={RequestId ?? "-"} cmd={CommandName} inv={InvocationId.Substring(0, 8)}";

        public InvocationState State => (InvocationState)Volatile.Read(ref _state);

        public long ElapsedMilliseconds => _clock.ElapsedMilliseconds;

        /// <summary>Time spent queued before the Revit callback picked the request up.</summary>
        public long? QueueWaitMilliseconds { get; private set; }

        // ---- Revit callback side ----

        /// <summary>Pending -> Running. Returns false for a cancelled (stale) request, which must not be executed.</summary>
        public bool TryStart()
        {
            if (!TryTransition(InvocationState.Pending, InvocationState.Running))
                return false;

            QueueWaitMilliseconds = _clock.ElapsedMilliseconds;
            return true;
        }

        /// <summary>
        /// Running -> Completed with a result. Returns false when the caller already gave up
        /// (OutcomeUnknown); the late result stays on this invocation and is seen by nobody else.
        /// </summary>
        public bool TryComplete(TResult result)
        {
            Result = result;
            return Finish();
        }

        /// <summary>Running -> Completed with an error. Returns false when the caller already gave up.</summary>
        public bool TryFail(Exception error)
        {
            Error = error;
            return Finish();
        }

        private bool Finish()
        {
            // Result/Error are written before the interlocked transition, which publishes them.
            bool deliveredToCaller = TryTransition(InvocationState.Running, InvocationState.Completed);
            _finished.Set();
            return deliveredToCaller;
        }

        // ---- Caller side ----

        public bool WaitForCompletion(int timeoutMilliseconds) => _finished.Wait(timeoutMilliseconds);

        /// <summary>Pending -> CancelledBeforeStart. Succeeds only if the callback has not started.</summary>
        public bool TryCancelBeforeStart() =>
            TryTransition(InvocationState.Pending, InvocationState.CancelledBeforeStart);

        /// <summary>
        /// Called when the caller's wait expired. Cancels the request if it has not started,
        /// otherwise marks the outcome unknown. Returns the resulting state; Completed means
        /// the callback finished in the race window and its result can be used normally.
        /// </summary>
        public InvocationState ResolveTimeout()
        {
            if (TryCancelBeforeStart())
                return InvocationState.CancelledBeforeStart;

            if (TryTransition(InvocationState.Running, InvocationState.OutcomeUnknown))
                return InvocationState.OutcomeUnknown;

            return State;
        }

        private bool TryTransition(InvocationState from, InvocationState to) =>
            Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;
    }
}
