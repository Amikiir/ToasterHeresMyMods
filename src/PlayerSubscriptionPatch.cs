// PlayerSubscriptionPatch.cs

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;

namespace ToasterHeresMyMods;

public static class PlayerSubscriptionPatch
{
    static Dictionary<ulong, ulong[]> playersConnectingWithMods = new Dictionary<ulong, ulong[]>();
    static Dictionary<ulong, ItemDetails> modDetails = new Dictionary<ulong, ItemDetails>();
    private static List<ulong> donePlayers = new List<ulong>();
    
    public static void Setup()
    {
        EventManager.Instance.AddEventListener("Event_Client_OnItemDetails", new Action<Dictionary<string, object>>(OnItemDetails));
    }

    public static void Destroy()
    {
        EventManager.Instance.RemoveEventListener("Event_Client_OnItemDetails", new Action<Dictionary<string, object>>(OnItemDetails));
    }

    public static void OnItemDetails(Dictionary<string, object> message)
    {
        ulong num = (ulong)message["id"];
        string modTitle = (string)message["title"];
        string modDescription = (string)message["description"];
        string modPreviewUrl = (string)message["previewUrl"];

        modDetails[num] = new ItemDetails
        {
            Title = modTitle,
            Description = modDescription,
            PreviewUrl = modPreviewUrl
        };

        foreach (ulong clientId in playersConnectingWithMods.Keys)
        {
            CheckIfHaveAllPlayersModsDetails(clientId);
        }

        // Hack because we are enumerating playersConnectingWithMods above
        for (int i = 0; i < donePlayers.Count; i++)
        {
            playersConnectingWithMods.Remove(donePlayers[i]);
        }
        donePlayers.Clear();
    }

    public static void CheckIfHaveAllPlayersModsDetails(ulong clientId)
    {
        int haveTitleForModCount = 0;
        foreach (ulong modId in playersConnectingWithMods[clientId])
        {
            if (modDetails.ContainsKey(modId) || modId < 2500000000)
            {
                haveTitleForModCount++;
                continue;
            }
            
            return;
        }
        
        if (haveTitleForModCount == playersConnectingWithMods[clientId].Length)
        {
            SayPlayersMods(clientId);
            return;
        }
    }
    
    public static void SayPlayersMods(ulong playerClientId)
    {
        string output = "";
        int localModCount = 0;
        for (int i = 0; i < playersConnectingWithMods[playerClientId].Length; i++)
        {
            ulong modId = playersConnectingWithMods[playerClientId][i];
            if (modId < 2500000000)
            {
                localModCount++;
            }
            else
            {
                if (i < playersConnectingWithMods[playerClientId].Length - 1)
                {
                    output += modDetails[modId].Title + ", ";
                }
                else
                {
                    output += modDetails[modId].Title;
                }
            }
        }

        if (localModCount > 0)
        {
            output += $"{(output != "" ? ", & " : "")}{localModCount} local mod{(localModCount != 1 ? "s" : "")}";
        }
        
        UIChat chat = UIChat.Instance;
        PlayerManager pm = PlayerManager.Instance;
        Player player = pm.GetPlayerByClientId(playerClientId);
        
        if (playersConnectingWithMods[playerClientId].Length == 0)
        {
            chat.Server_SendSystemChatMessage($"<size=14>{chat.WrapPlayerUsername(player)} has no mods.</size>");
            Plugin.Log($"#{player.Number.Value} {player.Username.Value} has no mods.");
        }
        else
        {
            chat.Server_SendSystemChatMessage($"<size=14>{chat.WrapPlayerUsername(player)} has {playersConnectingWithMods[playerClientId].Length} mod{(playersConnectingWithMods[playerClientId].Length != 1 ? "s" : "")}: {output}</size>");
            Plugin.Log($"#{player.Number.Value} {player.Username.Value} has {playersConnectingWithMods[playerClientId].Length} mod{(playersConnectingWithMods[playerClientId].Length != 1 ? "s" : "")}: {output}");
        }
        donePlayers.Add(playerClientId); // Hack because we are currently enumerating playersConnectingWithMods 
    }

    [HarmonyPatch(typeof(Player), "Client_PlayerSubscriptionRpc")]
    public class PlayerClientPlayerSubscriptionRpcPatch
    {
        [HarmonyPostfix]
        public static void Client_PlayerSubscriptionRpcPostfix(Player __instance, FixedString32Bytes username,
            int number, PlayerHandedness handedness, FixedString32Bytes country,
            FixedString32Bytes visorAttackerBlueSkin, FixedString32Bytes visorAttackerRedSkin,
            FixedString32Bytes visorGoalieBlueSkin, FixedString32Bytes visorGoalieRedSkin, FixedString32Bytes mustache,
            FixedString32Bytes beard, FixedString32Bytes jerseyAttackerBlueSkin,
            FixedString32Bytes jerseyAttackerRedSkin, FixedString32Bytes jerseyGoalieBlueSkin,
            FixedString32Bytes jerseyGoalieRedSkin, FixedString32Bytes stickAttackerBlueSkin,
            FixedString32Bytes stickAttackerRedSkin, FixedString32Bytes stickGoalieBlueSkin,
            FixedString32Bytes stickGoalieRedSkin, FixedString32Bytes stickShaftAttackerBlueTapeSkin,
            FixedString32Bytes stickShaftAttackerRedTapeSkin, FixedString32Bytes stickShaftGoalieBlueTapeSkin,
            FixedString32Bytes stickShaftGoalieRedTapeSkin, FixedString32Bytes stickBladeAttackerBlueTapeSkin,
            FixedString32Bytes stickBladeAttackerRedTapeSkin, FixedString32Bytes stickBladeGoalieBlueTapeSkin,
            FixedString32Bytes stickBladeGoalieRedTapeSkin, int patreonLevel, int adminLevel,
            FixedString32Bytes steamId, ulong[] enabledModIds)
        {
            Plugin.Log($"Enabled mods by {username.ToString()}: " +
                       $"{(enabledModIds.Length == 0 ? "None" : $"{steamId.ToString()}: {string.Join(", ", enabledModIds)}")}");

            // Check for blacklisted mods (only on server side)
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                ulong parsedSteamId = 0;
                ulong.TryParse(steamId.ToString(), out parsedSteamId);
                BlacklistManager.CheckPlayerMods(__instance.OwnerClientId, parsedSteamId, username.ToString(), enabledModIds);
            }

            ulong[] enabledModIdsToSearch = enabledModIds.Where((ulong modId) => modId > 2500000000 && !modDetails.ContainsKey(modId)).ToArray();
            playersConnectingWithMods.Add(__instance.OwnerClientId, enabledModIds);

            if (enabledModIdsToSearch.Length > 0)
            {
                SteamWorkshopManager.Instance.GetItemDetails(enabledModIdsToSearch);
            }
            else
            {
                CheckIfHaveAllPlayersModsDetails(__instance.OwnerClientId);
            }
        }
    }
}