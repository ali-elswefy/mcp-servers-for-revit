using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;
using System.Reflection;
using System.Windows.Media.Imaging;



namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        private UIControlledApplication _controlledApplication;
        private Logger _logger;

        public Result OnStartup(UIControlledApplication application)
        {
            RibbonPanel mcpPanel = application.CreateRibbonPanel("Revit MCP Plugin");

            PushButtonData pushButtonData = new PushButtonData("ID_EXCMD_TOGGLE_REVIT_MCP", "Revit MCP\r\n Switch",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.MCPServiceConnection");
            pushButtonData.ToolTip = "Open / Close mcp server";
            pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-16.png", UriKind.RelativeOrAbsolute));
            pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(pushButtonData);

            PushButtonData mcp_settings_pushButtonData = new PushButtonData("ID_EXCMD_MCP_SETTINGS", "Settings",
                Assembly.GetExecutingAssembly().Location, "revit_mcp_plugin.Core.Settings");
            mcp_settings_pushButtonData.ToolTip = "MCP Settings";
            mcp_settings_pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-16.png", UriKind.RelativeOrAbsolute));
            mcp_settings_pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(mcp_settings_pushButtonData);

            _logger = new Logger();
            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();

            if (configManager.Config.Settings.AutoStart)
            {
                _controlledApplication = application;
                application.Idling += StartServerOnIdle;
            }

            return Result.Succeeded;
        }

        private void StartServerOnIdle(object sender, IdlingEventArgs e)
        {
            _controlledApplication.Idling -= StartServerOnIdle;

            try
            {
                UIApplication uiApplication = sender as UIApplication;
                if (uiApplication == null)
                {
                    _logger.Error("Unable to auto-start the MCP server because the Revit UI application is unavailable.");
                    return;
                }

                SocketService.Instance.Initialize(uiApplication);
                SocketService.Instance.Start();

                if (SocketService.Instance.IsRunning)
                {
                    _logger.Info("MCP server started automatically.");
                }
                else
                {
                    _logger.Error("MCP server could not be started automatically.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to auto-start the MCP server: {0}", ex.Message);
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (_controlledApplication != null)
                {
                    _controlledApplication.Idling -= StartServerOnIdle;
                }

                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch { }

            return Result.Succeeded;
        }
    }
}
