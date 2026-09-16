using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Generic;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Manages the creation and lifecycle of external events.
    /// </summary>
    public class ExternalEventManager
    {
        private static ExternalEventManager _instance;
        private Dictionary<string, ExternalEventWrapper> _events = new Dictionary<string, ExternalEventWrapper>();
        private bool _isInitialized = false;
        private UIApplication _uiApp;
        private ILogger _logger;

        /// <summary>
        /// Manages the creation and lifecycle of external events.
        /// </summary>
        public static ExternalEventManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new ExternalEventManager();
                return _instance;
            }
        }

        private ExternalEventManager() { }

        public void Initialize(UIApplication uiApp, ILogger logger)
        {
            _uiApp = uiApp;
            _logger = logger;
            _isInitialized = true;
        }

        /// <summary>
        /// Gets or creates an external event.
        /// </summary>
        public ExternalEvent GetOrCreateEvent(IWaitableExternalEventHandler handler, string key)
        {
            if (!_isInitialized)
                throw new InvalidOperationException($"{nameof(ExternalEventManager)} has not been initialized.");

            // Return the existing event when its handler matches.
            if (_events.TryGetValue(key, out var wrapper) &&
                wrapper.Handler == handler)
            {
                return wrapper.Event;
            }

            // Events must be created on the UI thread.
            ExternalEvent externalEvent = null;

            // Create the event in the active document context.
            _uiApp.ActiveUIDocument.Document.Application.ExecuteCommand(
                (uiApp) => {
                    externalEvent = ExternalEvent.Create(handler);
                }
            );

            if (externalEvent == null)
                throw new InvalidOperationException("Unable to create the external event.");

            // Store the event.
            _events[key] = new ExternalEventWrapper
            {
                Event = externalEvent,
                Handler = handler
            };

            _logger.Info($"Created a new external event for key {key}.");

            return externalEvent;
        }

        /// <summary>
        /// <para>Clears the event cache.</para>
        /// </summary>
        public void ClearEvents()
        {
            _events.Clear();
        }

        private class ExternalEventWrapper
        {
            public ExternalEvent Event { get; set; }
            public IWaitableExternalEventHandler Handler { get; set; }
        }
    }
}

namespace Autodesk.Revit.DB
{
    public static class ApplicationExtensions
    {
        public delegate void CommandDelegate(UIApplication uiApp);

        /// <summary>
        /// <para>Execute commands in the Revit context.</para>
        /// </summary>
        public static void ExecuteCommand(this Autodesk.Revit.ApplicationServices.Application app, CommandDelegate command)
        {
            // This method is called in the Revit context and can safely create an ExternalEvent.
            command?.Invoke(new UIApplication(app));
        }
    }
}
