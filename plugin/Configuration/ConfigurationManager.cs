using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Utils;
using System;
using System.IO;

namespace revit_mcp_plugin.Configuration
{
    public class ConfigurationManager
    {
        private readonly ILogger _logger;
        private readonly string _configPath;

        public FrameworkConfig Config { get; private set; }

        public ConfigurationManager(ILogger logger)
        {
            _logger = logger;

            // Configuration file path.
            _configPath = PathManager.GetCommandRegistryFilePath();
        }

        /// <summary>
        /// <para>Load configuration from a JSON file.</para>
        /// </summary>
        public void LoadConfiguration()
        {
            Config = new FrameworkConfig();

            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    Config = JsonConvert.DeserializeObject<FrameworkConfig>(json) ?? new FrameworkConfig();
                    Config.Settings = Config.Settings ?? new ServiceSettings();
                    _logger.Info("Configuration file loaded: {0}", _configPath);
                }
                else
                {
                    _logger.Error("No configuration file found.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load configuration file: {0}", ex.Message);
            }

            // Record the load time.
            _lastConfigLoadTime = DateTime.Now;
        }

        ///// <summary>
        ///// <para>Reload configuration.</para>
        ///// </summary>
        //public void RefreshConfiguration()
        //{
        //    LoadConfiguration();
        //    _logger.Info("Configuration has been reloaded.");
        //}

        //public bool HasConfigChanged()
        //{
        //    if (!File.Exists(_configPath))
        //        return false;

        //    DateTime lastWrite = File.GetLastWriteTime(_configPath);
        //    return lastWrite > _lastConfigLoadTime;
        //}

        private DateTime _lastConfigLoadTime;
    }
}
