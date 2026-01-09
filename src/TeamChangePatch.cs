// TeamChangePatch.cs
// Patches team join to kick players with blacklisted mods when they try to join Red or Blue team

using System;
using HarmonyLib;
using Unity.Netcode;

namespace ToasterHeresMyMods;

[HarmonyPatch]
public static class TeamChangePatch
{
    // Patch Client_SetPlayerTeamRpc which is called when a player requests to change teams
    [HarmonyPatch(typeof(Player), "Client_SetPlayerTeamRpc")]
    [HarmonyPrefix]
    public static bool Client_SetPlayerTeamRpcPrefix(Player __instance, PlayerTeam team)
    {
        try
        {
            // Only run on server
            if (!__instance.IsServer)
            {
                return true; // Allow original method to run
            }

            // Check if team-based kicking is enabled
            if (!ConfigManager.Config.KickOnTeamJoin)
            {
                return true; // Allow team change, kicks happen on connection instead
            }

            ulong clientId = __instance.OwnerClientId;
            
            // PlayerTeam enum: None=0, Spectator=1, Blue=2, Red=3
            // Only check blacklist if player is joining Blue or Red team
            if (team == PlayerTeam.Blue || team == PlayerTeam.Red)
            {
                // Check if this player has blacklisted mods
                bool hasBlacklistedMods = BlacklistManager.IsPlayerBlacklisted(clientId);
                
                if (hasBlacklistedMods)
                {
                    string teamName = team == PlayerTeam.Blue ? "Blue" : "Red";
                    BlacklistManager.LogWarning($"Preventing player {clientId} from joining {teamName} team due to blacklisted mods");
                    
                    // Kick the player
                    BlacklistManager.KickBlacklistedPlayer(clientId);
                    
                    // Prevent the team change
                    return false;
                }
            }
            
            // Allow the team change if no blacklisted mods or joining spectator
            return true;
        }
        catch (Exception ex)
        {
            BlacklistManager.LogError($"Error in team change patch: {ex.Message}");
            return true; // Allow team change on error to prevent game breaking
        }
    }
}
