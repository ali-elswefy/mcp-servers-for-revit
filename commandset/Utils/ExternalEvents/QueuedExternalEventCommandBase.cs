using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Autodesk.Revit.UI;
using RevitMcp.Interop;
using RevitMCPSDK.API.Base;
using RevitMCPSDK.Exceptions;

namespace RevitMCPCommandSet.Utils.ExternalEvents
{
    /// <summary>
    /// Command base that runs each request as its own invocation on the Revit UI thread.
    /// Replaces <c>RaiseAndWaitForCompletion</c>, whose shared handler state allowed a late
    /// callback or a concurrent request to overwrite another request's parameters or result.
    /// </summary>
    public abstract class QueuedExternalEventCommandBase<TRequest, TResult> : ExternalEventCommandBase
    {
        private readonly QueuedExternalEventHandler<TRequest, TResult> _queuedHandler;

        protected QueuedExternalEventCommandBase(QueuedExternalEventHandler<TRequest, TResult> handler, UIApplication uiApp)
            : base(handler, uiApp)
        {
            _queuedHandler = handler;
        }

        /// <summary>
        /// Whether the command can change the model. Decides whether a timeout while running is
        /// reported as possibly having modified the model (and therefore not safe to retry).
        /// </summary>
        protected virtual bool MayModifyModel => true;

        /// <summary>
        /// Queues <paramref name="request"/> for the Revit UI thread and waits up to
        /// <paramref name="timeoutMs"/>. Throws a structured <see cref="CommandExecutionException"/>
        /// on timeout or failure.
        /// </summary>
        protected TResult RunOnRevitThread(TRequest request, string requestId, int timeoutMs)
        {
            var invocation = new ExternalEventInvocation<TRequest, TResult>(CommandName, requestId, request);
            _queuedHandler.Enqueue(invocation);

            // Pending means an earlier raise has not been serviced yet; that callback drains this invocation too.
            ExternalEventRequest raiseStatus = Event.Raise();
            CommandLog.Info("{0} queued (raise {1}, {2} waiting, wait limit {3}ms)", invocation.LogTag, raiseStatus, _queuedHandler.QueueDepth, timeoutMs);

            if ((raiseStatus == ExternalEventRequest.Denied || raiseStatus == ExternalEventRequest.TimedOut) &&
                invocation.TryCancelBeforeStart())
            {
                CommandLog.Warning("{0} rejected by Revit (raise status {1}); not executed", invocation.LogTag, raiseStatus);
                throw CommandErrors.EventRejected(invocation, raiseStatus.ToString());
            }

            WaitResult wait = WaitWatchingForDialogs(invocation, timeoutMs, out RevitUiState uiState);
            if (wait != WaitResult.Completed)
            {
                InvocationState state = invocation.ResolveTimeout();
                if (state != InvocationState.Completed)
                {
                    if (wait == WaitResult.BlockedByDialog)
                    {
                        CommandLog.Warning("{0} gave up after {1}ms in state {2}: Revit is blocked by {3}; {4}", invocation.LogTag,
                            invocation.ElapsedMilliseconds, state, uiState.DescribeDialogs(),
                            state == InvocationState.CancelledBeforeStart
                                ? "cancelled, it will never run"
                                : "it is running; the caller was told the outcome is unknown");
                        throw CommandErrors.UserInputRequired(invocation, state, uiState, MayModifyModel);
                    }

                    CommandLog.Warning("{0} wait expired after {1}ms in state {2}: {3}", invocation.LogTag, timeoutMs, state,
                        state == InvocationState.CancelledBeforeStart
                            ? "cancelled, it will never run"
                            : "it is running; the caller was told the outcome is unknown");
                    throw CommandErrors.Timeout(invocation, state, timeoutMs, MayModifyModel);
                }
                // The callback finished in the race window; deliver its result normally.
            }

            if (invocation.Error is CommandExecutionException commandError)
            {
                CommandLog.Info("{0} returning structured error to the caller after {1}ms", invocation.LogTag, invocation.ElapsedMilliseconds);
                ExceptionDispatchInfo.Capture(commandError).Throw();
            }

            if (invocation.Error != null)
            {
                CommandLog.Info("{0} returning execution error to the caller after {1}ms: {2}", invocation.LogTag,
                    invocation.ElapsedMilliseconds, invocation.Error.Message);
                throw CommandErrors.Execution(invocation, invocation.Error);
            }

            CommandLog.Info("{0} returning result to the caller after {1}ms", invocation.LogTag, invocation.ElapsedMilliseconds);
            return invocation.Result;
        }

        private enum WaitResult { Completed, TimedOut, BlockedByDialog }

        /// <summary>How often the wait checks for a modal dialog.</summary>
        private const int DialogPollMs = 250;

        /// <summary>How long a dialog must stay open before the caller gives up, so a dialog that is closing does not count.</summary>
        private const int DialogGraceMs = 1500;

        /// <summary>
        /// Waits for the invocation, but stops early when a modal dialog keeps Revit blocked: while
        /// one is open the request cannot start (or, if it opened the dialog itself, cannot finish)
        /// until the user acts, so waiting out the full timeout only delays telling them.
        /// </summary>
        private static WaitResult WaitWatchingForDialogs(ExternalEventInvocation<TRequest, TResult> invocation, int timeoutMs, out RevitUiState uiState)
        {
            uiState = null;
            var clock = Stopwatch.StartNew();
            long blockedSinceMs = -1;

            while (true)
            {
                long remainingMs = timeoutMs - clock.ElapsedMilliseconds;
                if (remainingMs <= 0)
                    return WaitResult.TimedOut;

                if (invocation.WaitForCompletion((int)Math.Min(DialogPollMs, remainingMs)))
                    return WaitResult.Completed;

                RevitUiState state = RevitDialogDetector.Detect();
                if (!state.ModalDialogOpen)
                {
                    blockedSinceMs = -1;
                    continue;
                }

                if (blockedSinceMs < 0)
                {
                    blockedSinceMs = clock.ElapsedMilliseconds;
                    CommandLog.Info("{0} Revit is blocked by {1} while waiting in state {2}", invocation.LogTag, state.DescribeDialogs(), invocation.State);
                }
                else if (clock.ElapsedMilliseconds - blockedSinceMs >= DialogGraceMs)
                {
                    uiState = state;
                    return WaitResult.BlockedByDialog;
                }
            }
        }
    }
}
