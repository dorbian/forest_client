using Dalamud.Configuration;
using Dalamud.Plugin;
using Forest.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Forest
{
    [Serializable]
    public class ForestConfig : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        // Add the PlayerDatabase property that ForestWindow expects
        public Dictionary<string, PlayerData> PlayerDatabase { get; set; } = new();

        // Murder Mystery games list and current game
        public List<MurderMysteryData> MurderMysteryGames { get; set; } = new();

        // Store the index of the current game instead of the object reference
        public int CurrentGameIndex { get; set; } = -1;

        [NonSerialized]
        private MurderMysteryData? _cachedCurrentGame;

        public MurderMysteryData? CurrentGame
        {
            get
            {
                if (_cachedCurrentGame != null && MurderMysteryGames.Contains(_cachedCurrentGame))
                    return _cachedCurrentGame;

                if (CurrentGameIndex >= 0 && CurrentGameIndex < MurderMysteryGames.Count)
                {
                    _cachedCurrentGame = MurderMysteryGames[CurrentGameIndex];
                    return _cachedCurrentGame;
                }

                _cachedCurrentGame = MurderMysteryGames.FirstOrDefault();
                CurrentGameIndex = _cachedCurrentGame != null ? MurderMysteryGames.IndexOf(_cachedCurrentGame) : -1;
                return _cachedCurrentGame;
            }
            set
            {
                _cachedCurrentGame = value;
                CurrentGameIndex = value != null ? MurderMysteryGames.IndexOf(value) : -1;
            }
        }

        [NonSerialized]
        public IDalamudPluginInterface? PluginInterface;

        // Properties expected by ConfigWindow
        public bool SomePropertyToBeSavedAndWithADefault { get; set; } = false;
        public bool IsConfigWindowMovable { get; set; } = true;

        // Example setting you can store
        public int MaxColumnWidth { get; set; } = 200;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.PluginInterface = pluginInterface;

        }

        public void Save()
        {
            try
            {
                PluginInterface?.SavePluginConfig(this);
                Plugin.Log?.Information($"Config saved successfully. Games: {MurderMysteryGames.Count}, Players: {PlayerDatabase.Count}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"Failed to save config: {ex.Message}");
            }
        }

        // Static method to load config with fallback handling
        public static ForestConfig LoadConfig(IDalamudPluginInterface pluginInterface)
        {
            Plugin.Log?.Information("=== Starting config loading process ===");

            // Check what config directory we're using
            var configDir = pluginInterface.GetPluginConfigDirectory();
            Plugin.Log?.Information($"Config directory: {configDir}");

            // List all files in config directory for debugging
            try
            {
                if (System.IO.Directory.Exists(configDir))
                {
                    var files = System.IO.Directory.GetFiles(configDir);
                    Plugin.Log?.Information($"Files in config directory: {string.Join(", ", files.Select(System.IO.Path.GetFileName))}");
                }
                else
                {
                    Plugin.Log?.Warning("Config directory does not exist!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"Error listing config directory: {ex.Message}");
            }

            try
            {
                // First, try to load normally
                Plugin.Log?.Information("Attempting normal Dalamud config loading...");
                var config = pluginInterface.GetPluginConfig() as ForestConfig;
                if (config != null)
                {
                    Plugin.Log?.Information($"SUCCESS: Loaded existing config via Dalamud. Games: {config.MurderMysteryGames.Count}, Players: {config.PlayerDatabase.Count}");
                    config.Initialize(pluginInterface);
                    return config;
                }
                else
                {
                    Plugin.Log?.Warning("Normal Dalamud config loading returned null");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"Normal config loading failed with exception: {ex}");
            }

            // If normal loading failed, try to load and migrate from raw JSON
            // Try multiple possible config file names
            string[] possibleConfigFiles = {
                System.IO.Path.Combine(configDir, "Forest.json"),
            };

            string configFilePath = null;
            foreach (var path in possibleConfigFiles)
            {
                if (System.IO.File.Exists(path))
                {
                    configFilePath = path;
                    break;
                }
            }

            Plugin.Log?.Information($"Attempting manual JSON loading from: {configFilePath ?? "no config file found"}");

            try
            {
                if (configFilePath != null)
                {
                    Plugin.Log?.Information("Config file exists, reading content...");
                    var jsonString = System.IO.File.ReadAllText(configFilePath);
                    Plugin.Log?.Information($"Config file size: {jsonString.Length} characters");

                    // Log first 500 chars for debugging (be careful not to log sensitive data)
                    var preview = jsonString.Length > 500 ? jsonString.Substring(0, 500) + "..." : jsonString;
                    Plugin.Log?.Information($"Config content preview: {preview}");

                    // Try to deserialize with more permissive settings
                    var options = new System.Text.Json.JsonSerializerOptions
                    {
                        IgnoreReadOnlyProperties = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true
                    };

                    ForestConfig rawConfig = null;

                    // First try System.Text.Json
                    try
                    {
                        rawConfig = System.Text.Json.JsonSerializer.Deserialize<ForestConfig>(jsonString, options);
                    }
                    catch (Exception jsonEx)
                    {
                        Plugin.Log?.Warning($"System.Text.Json failed: {jsonEx.Message}");

                        // The file has $type metadata, which suggests it was saved with Newtonsoft.Json
                        // Try to clean the JSON by removing $type fields
                        try
                        {
                            var cleanedJson = System.Text.RegularExpressions.Regex.Replace(jsonString, @"""?\$type""?\s*:\s*""[^""]*"",?\s*", "");
                            Plugin.Log?.Information("Attempting to deserialize cleaned JSON (removed $type fields)");
                            rawConfig = System.Text.Json.JsonSerializer.Deserialize<ForestConfig>(cleanedJson, options);
                        }
                        catch (Exception cleanEx)
                        {
                            Plugin.Log?.Error($"Even cleaned JSON failed: {cleanEx.Message}");
                        }
                    }
                    if (rawConfig != null)
                    {
                        Plugin.Log?.Information($"SUCCESS: Loaded config from JSON. Games: {rawConfig.MurderMysteryGames.Count}, Players: {rawConfig.PlayerDatabase.Count}");
                        rawConfig.Initialize(pluginInterface);

                        // Force save in new format
                        Plugin.Log?.Information("Saving migrated config...");
                        rawConfig.Save();
                        Plugin.Log?.Information("Migrated and saved config in new format");
                        return rawConfig;
                    }
                    else
                    {
                        Plugin.Log?.Warning("JSON deserialization returned null");
                    }
                }
                else
                {
                    Plugin.Log?.Information("Forest.json does not exist");

                    // Check for other potential config files
                    var altConfigPath = System.IO.Path.Combine(configDir, "Forest.json");
                    if (System.IO.File.Exists(altConfigPath))
                    {
                        Plugin.Log?.Information($"Found alternative config file: {altConfigPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"JSON migration failed with exception: {ex}");

                // Backup the old config file so user doesn't lose data
                try
                {
                    var backupPath = System.IO.Path.Combine(configDir, $"ForestConfig_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                    if (System.IO.File.Exists(configFilePath))
                    {
                        System.IO.File.Copy(configFilePath, backupPath);
                        Plugin.Log?.Information($"Backed up old config to: {backupPath}");
                    }
                }
                catch (Exception backupEx)
                {
                    Plugin.Log?.Error($"Failed to backup old config: {backupEx.Message}");
                }
            }

            // Create new config if everything else failed
            Plugin.Log?.Warning("All config loading methods failed - creating new config as fallback");
            var newConfig = new ForestConfig();
            newConfig.Initialize(pluginInterface);
            Plugin.Log?.Information("Saving new config...");
            newConfig.Save();
            Plugin.Log?.Information("=== Config loading process completed with new config ===");
            return newConfig;
        }
    }
}
