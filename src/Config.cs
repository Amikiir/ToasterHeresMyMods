// Config.cs
// Configuration management for the blacklist system

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ToasterHeresMyMods
{
    [Serializable]
    public class BlacklistConfig
    {
        // Config version for migration
        public int ConfigVersion = 1;

        // Enable the blacklist system
        public bool Enabled = true;

        // List of blacklisted Steam Workshop mod IDs
        public List<ulong> BlacklistedModIds = new List<ulong>();

        // Broadcast a message to chat when a player is kicked for blacklisted mod
        public bool BroadcastKicks = true;

        // Enable debug logging for blacklist system
        public bool EnableDebugLogs = false;

        // Kick players with local mods (mod ID 0 or < 2500000000)
        public bool KickPlayersWithLocalMods = false;

        // Only kick players when they try to join Red or Blue team (if false, kicks on connection)
        public bool KickOnTeamJoin = true;
    }

    public static class ConfigManager
    {
        public static BlacklistConfig Config { get; private set; } = new BlacklistConfig();

        private static string ConfigDir
        {
            get
            {
                string gameRoot = Application.dataPath;
                if (gameRoot.EndsWith("Puck_Data"))
                {
                    gameRoot = Directory.GetParent(gameRoot).FullName;
                }
                string configFolder = Path.Combine(gameRoot, "config");
                if (!Directory.Exists(configFolder))
                {
                    Directory.CreateDirectory(configFolder);
                }
                return configFolder;
            }
        }

        private static string ConfigFile => Path.Combine(ConfigDir, "mod_blacklist.json");

        public static void LoadConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                if (File.Exists(ConfigFile))
                {
                    string json = File.ReadAllText(ConfigFile);
                    Config = JsonUtility.FromJson<BlacklistConfig>(json) ?? new BlacklistConfig();
                    Log($"Config loaded from {ConfigFile}");
                }
                else
                {
                    Config = new BlacklistConfig();
                    SaveConfig();
                    Log($"Created new config at {ConfigFile}");
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to load config: {ex.Message}");
                Config = new BlacklistConfig();
            }
        }

        public static void SaveConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                string json = JsonUtility.ToJson(Config, true);
                File.WriteAllText(ConfigFile, json);
                Debug.Log($"[{Plugin.MOD_NAME}/Config] Config saved to {ConfigFile}");
            }
            catch (Exception ex)
            {
                LogError($"Failed to save config: {ex.Message}");
            }
        }

        public static void ReloadConfig()
        {
            LoadConfig();
            Log("Config reloaded.");
        }

        private static void Log(string message)
        {
            Debug.Log($"[{Plugin.MOD_NAME}/Config] {message}");
        }

        private static void LogError(string message)
        {
            Debug.LogError($"[{Plugin.MOD_NAME}/Config] {message}");
        }
    }
}
