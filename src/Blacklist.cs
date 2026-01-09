// Blacklist.cs
// Server-side mod blacklist system - kicks players with blacklisted mods
// Compatible with C# 7.3 / .NET Framework 4.8

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;


namespace ToasterHeresMyMods
{
    public static class BlacklistManager
    {
        // Cache of mod details we've fetched
        private static readonly Dictionary<ulong, ItemDetails> _modDetailsCache = new Dictionary<ulong, ItemDetails>();

        // Players pending blacklist check (waiting for mod details)
        private static readonly Dictionary<ulong, PendingPlayerCheck> _pendingChecks = new Dictionary<ulong, PendingPlayerCheck>();

        // Track recently checked players to prevent duplicate kicks
        private static readonly HashSet<ulong> _recentlyChecked = new HashSet<ulong>();
        private static readonly Dictionary<ulong, float> _checkTimestamps = new Dictionary<ulong, float>();
        private const float CHECK_COOLDOWN = 2f; // seconds before same player can be checked again

        // Track players with blacklisted mods (clientId -> player data)
        private static readonly Dictionary<ulong, BlacklistedPlayerData> _blacklistedPlayers = new Dictionary<ulong, BlacklistedPlayerData>();

        private class BlacklistedPlayerData
        {
            public ulong ClientId;
            public string Username;
            public ulong[] BlacklistedModIds;
            public bool HasLocalMods;
        }

        private class PendingPlayerCheck
        {
            public ulong ClientId;
            public ulong SteamId;
            public string Username;
            public ulong[] EnabledModIds;
            public HashSet<ulong> AwaitingDetails;
            public float TimeoutTime;
        }

        private const float PENDING_CHECK_TIMEOUT = 10f; // seconds to wait for mod details

        public static void Initialize()
        {
            // Listen for item details events to complete pending checks
            EventManager.Instance.AddEventListener("Event_Client_OnItemDetails", new Action<Dictionary<string, object>>(OnItemDetails));
            
            Log("Blacklist system initialized.");
            if (ConfigManager.Config.BlacklistedModIds.Count > 0)
            {
                Log($"Blacklisted mods: {ConfigManager.Config.BlacklistedModIds.Count}");
            }
        }

        public static void Shutdown()
        {
            try
            {
                EventManager.Instance.RemoveEventListener("Event_Client_OnItemDetails", new Action<Dictionary<string, object>>(OnItemDetails));
            }
            catch { }
            
            _pendingChecks.Clear();
            _recentlyChecked.Clear();
            _checkTimestamps.Clear();
            _blacklistedPlayers.Clear();
        }

        // Check if a mod ID is blacklisted

        public static bool IsModBlacklisted(ulong modId)
        {
            return ConfigManager.Config.BlacklistedModIds.Contains(modId);
        }


        // Called when a player connects with their mod list.
        // Returns true if player should be kicked.

        public static bool CheckPlayerMods(ulong clientId, ulong steamId, string username, ulong[] enabledModIds)
        {
            try
            {
                // Prevent duplicate checks for same player within cooldown period
                if (_checkTimestamps.TryGetValue(clientId, out float lastCheck))
                {
                    if (Time.time - lastCheck < CHECK_COOLDOWN)
                    {
                        Dbg($"Skipping duplicate check for {username} (checked {Time.time - lastCheck:F2}s ago)");
                        return false;
                    }
                }
                _checkTimestamps[clientId] = Time.time;
            
            Log($"CheckPlayerMods called for {username} ({steamId}) with {enabledModIds.Length} mods");
            
            if (!ConfigManager.Config.Enabled)
            {
                Dbg($"Blacklist disabled, allowing {username}");
                return false;
            }

            // Check for local mods if enabled
            bool hasLocalMods = false;
            if (ConfigManager.Config.KickPlayersWithLocalMods)
            {
                var localMods = enabledModIds.Where(id => id <= 100).ToArray();
                hasLocalMods = localMods.Length > 0;
                if (hasLocalMods)
                {
                    Log($"Player {username} has {localMods.Length} local mod(s)");
                }
            }

            // Find blacklisted mods in player's mod list
            var blacklistedMods = enabledModIds.Where(IsModBlacklisted).ToArray();
            
            Log($"Found {blacklistedMods.Length} blacklisted mods for {username}: {string.Join(", ", blacklistedMods)}");

            // If player has neither local mods nor blacklisted mods, allow them
            if (!hasLocalMods && blacklistedMods.Length == 0)
            {
                Dbg($"Player {username} has no blacklisted or local mods");
                // Remove from tracking if they were previously flagged
                _blacklistedPlayers.Remove(clientId);
                return false;
            }

            // Track player as having blacklisted mods - don't kick yet, wait for team join
            _blacklistedPlayers[clientId] = new BlacklistedPlayerData
            {
                ClientId = clientId,
                Username = username,
                BlacklistedModIds = blacklistedMods,
                HasLocalMods = hasLocalMods
            };

            if (hasLocalMods && blacklistedMods.Length > 0)
            {
                Log($"Player {username} has local mod + {blacklistedMods.Length} blacklisted mod(s)");
            }
            else if (hasLocalMods)
            {
                Log($"Player {username} has local mod");
            }
            else
            {
                Log($"Player {username} has {blacklistedMods.Length} blacklisted mod(s)");
            }

            // If KickOnTeamJoin is disabled, kick immediately on connection
            if (!ConfigManager.Config.KickOnTeamJoin)
            {
                KickBlacklistedPlayer(clientId);
                return true;
            }

            // Otherwise, don't kick yet - wait for them to try joining a team
            return false;
            }
            catch (Exception ex)
            {
                LogError($"Error checking player mods for {username}: {ex.Message}");
                return false;
            }
        }


        // Handle item details response

        private static void OnItemDetails(Dictionary<string, object> message)
        {
            try
            {
                ulong modId = (ulong)message["id"];
                string modTitle = (string)message["title"];
                string modDescription = (string)message["description"];
                string modPreviewUrl = (string)message["previewUrl"];

                _modDetailsCache[modId] = new ItemDetails
                {
                    Title = modTitle,
                    Description = modDescription,
                    PreviewUrl = modPreviewUrl
                };

                // Check pending players
                var completedChecks = new List<ulong>();
                foreach (var kvp in _pendingChecks)
                {
                    var pending = kvp.Value;
                    pending.AwaitingDetails.Remove(modId);

                    if (pending.AwaitingDetails.Count == 0 || Time.time >= pending.TimeoutTime)
                    {
                        // Ready to check this player
                        ProcessPendingCheck(pending);
                        completedChecks.Add(kvp.Key);
                    }
                }

                foreach (var clientId in completedChecks)
                {
                    _pendingChecks.Remove(clientId);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing item details: {ex.Message}");
            }
        }


        // Process a pending player check after we have all mod details

        private static void ProcessPendingCheck(PendingPlayerCheck pending)
        {
            try
            {
                var blacklistedMods = pending.EnabledModIds.Where(IsModBlacklisted).ToArray();
                if (blacklistedMods.Length > 0)
                {
                    // Track the player
                    _blacklistedPlayers[pending.ClientId] = new BlacklistedPlayerData
                    {
                        ClientId = pending.ClientId,
                        Username = pending.Username,
                        BlacklistedModIds = blacklistedMods,
                        HasLocalMods = false
                    };
                    Log($"Player {pending.Username} flagged: {blacklistedMods.Length} blacklisted mod(s) (pending check completed)");

                    // If KickOnTeamJoin is disabled, kick immediately
                    if (!ConfigManager.Config.KickOnTeamJoin)
                    {
                        KickBlacklistedPlayer(pending.ClientId);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing pending check for {pending.Username}: {ex.Message}");
            }
        }


        // Check if a player has blacklisted mods (used by team join patch)
        public static bool IsPlayerBlacklisted(ulong clientId)
        {
            return _blacklistedPlayers.ContainsKey(clientId);
        }


        // Kick a blacklisted player when they try to join a team
        public static void KickBlacklistedPlayer(ulong clientId)
        {
            if (!_blacklistedPlayers.TryGetValue(clientId, out var playerData))
            {
                LogWarning($"Attempted to kick non-blacklisted player {clientId}");
                return;
            }

            // Build kick message
            if (ConfigManager.Config.BroadcastKicks)
            {
                try
                {
                    var chat = UIChat.Instance;
                    if (chat != null)
                    {
                        string broadcastMsg;
                        
                        if (playerData.HasLocalMods && playerData.BlacklistedModIds.Length > 0)
                        {
                            var modNames = playerData.BlacklistedModIds.Select(id => GetModName(id)).ToArray();
                            var modList = string.Join(", ", modNames);
                            broadcastMsg = $"<b><color=#FF6666>[Blacklist]</color></b> <b>{playerData.Username}</b> will be kicked for using a <b>local mod</b> and {playerData.BlacklistedModIds.Length} blacklisted mod: <b>{modList}</b>";
                        }
                        else if (playerData.HasLocalMods)
                        {
                            broadcastMsg = $"<b><color=#FF6666>[Blacklist]</color></b> <b>{playerData.Username}</b> will be kicked for using a <b>local mod</b>.";
                        }
                        else if (playerData.BlacklistedModIds.Length == 1)
                        {
                            var modName = GetModName(playerData.BlacklistedModIds[0]);
                            broadcastMsg = $"<b><color=#FF6666>[Blacklist]</color></b> <b>{playerData.Username}</b> will be kicked for using blacklisted mod: <b>{modName}</b>";
                        }
                        else
                        {
                            var modNames = playerData.BlacklistedModIds.Select(id => GetModName(id)).ToArray();
                            var modList = string.Join(", ", modNames);
                            broadcastMsg = $"<b><color=#FF6666>[Blacklist]</color></b> <b>{playerData.Username}</b> will be kicked for using {playerData.BlacklistedModIds.Length} blacklisted mod: <b>{modList}</b>";
                        }
                        
                        chat.Server_SendSystemChatMessage(broadcastMsg);
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Failed to broadcast kick message: {ex.Message}");
                }
            }

            // Schedule delayed kick so players can see the message
            BlacklistUpdater.ScheduleDelayedKick(clientId, playerData.Username, 3f);
            
            // Remove from tracking
            _blacklistedPlayers.Remove(clientId);
        }


        // Get Readable mod name

        public static string GetModName(ulong modId)
        {
            if (_modDetailsCache.TryGetValue(modId, out var details))
            {
                return details.Title;
            }
            return modId.ToString();
        }


        // Update pending checks (call from Update loop to handle timeouts)

        public static void UpdatePendingChecks()
        {
            if (_pendingChecks.Count == 0 && _checkTimestamps.Count == 0) return;

            // Clean up old pending checks
            var timedOut = new List<ulong>();
            foreach (var kvp in _pendingChecks)
            {
                if (Time.time >= kvp.Value.TimeoutTime)
                {
                    timedOut.Add(kvp.Key);
                }
            }

            foreach (var clientId in timedOut)
            {
                var pending = _pendingChecks[clientId];
                Log($"Timeout waiting for mod details for {pending.Username}, processing check anyway");
                ProcessPendingCheck(pending);
                _pendingChecks.Remove(clientId);
            }

            // Clean up old check timestamps (older than 10 seconds)
            var staleTimestamps = new List<ulong>();
            foreach (var kvp in _checkTimestamps)
            {
                if (Time.time - kvp.Value > 10f)
                {
                    staleTimestamps.Add(kvp.Key);
                }
            }
            foreach (var clientId in staleTimestamps)
            {
                _checkTimestamps.Remove(clientId);
            }
        }

        // Logging


        public static void Log(string message)
        {
            Debug.Log($"[{Plugin.MOD_NAME}/Blacklist] {message}");
        }

        public static void LogError(string message)
        {
            Debug.LogError($"[{Plugin.MOD_NAME}/Blacklist] {message}");
        }

        public static void LogWarning(string message)
        {
            Debug.LogWarning($"[{Plugin.MOD_NAME}/Blacklist] {message}");
        }

        public static void Dbg(string message)
        {
            if (ConfigManager.Config.EnableDebugLogs)
            {
                Log(message);
            }
        }
    }

    // MonoBehaviour for Update loop (timeout handling)
  
    public class BlacklistUpdater : MonoBehaviour
    {
        private static BlacklistUpdater _instance;

        public static void Create()
        {
            if (_instance != null) return;
            var go = new GameObject("BlacklistUpdater");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<BlacklistUpdater>();
        }

        public static void Destroy()
        {
            if (_instance != null)
            {
                UnityEngine.Object.Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        public static void ScheduleDelayedKick(ulong clientId, string username, float delaySeconds)
        {
            if (_instance != null)
            {
                _instance.StartCoroutine(_instance.DelayedKickCoroutine(clientId, username, delaySeconds));
            }
        }

        private System.Collections.IEnumerator DelayedKickCoroutine(ulong clientId, string username, float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);

            try
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer)
                {
                    nm.DisconnectClient(clientId);
                    BlacklistManager.Log($"Successfully kicked {username}");
                }
                else
                {
                    BlacklistManager.LogError("Cannot kick player - NetworkManager not available or not server");
                }
            }
            catch (Exception ex)
            {
                BlacklistManager.LogError($"Failed to kick player {username}: {ex.Message}");
            }
        }

        private void Update()
        {
            BlacklistManager.UpdatePendingChecks();
        }
    }
}
