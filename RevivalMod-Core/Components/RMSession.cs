using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using RevivalMod.Fika;
using UnityEngine;

namespace RevivalMod.Components
{
    internal class RMSession : MonoBehaviour
    {
        private RMSession() { }
        private static RMSession _instance = null;

        public Player Player { get; private set; }
        public GameWorld GameWorld { get; private set; }
        public GamePlayerOwner GamePlayerOwner { get; private set; }

        // Dictionary to track players with revival items
        public Dictionary<string, Vector3> CriticalPlayers = [];

        public static RMSession Instance
        {
            get
            {
                if (_instance is not null) 
                    return _instance;
                
                if (!Singleton<GameWorld>.Instantiated)
                {
                    Plugin.LogSource.LogError("Can't get ModSession Instance when GameWorld is not instantiated!");

                    // Create a temporary instance for error resistance
                    GameObject go = new("RMSessionTemp");
                    _instance = go.AddComponent<RMSession>();

                    return _instance;
                }

                try
                {
                    _instance = Singleton<GameWorld>.Instance.MainPlayer.gameObject.GetOrAddComponent<RMSession>();
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError($"Error creating RMSession: {ex.Message}");
                    
                    GameObject go = new("RMSessionError");
                    
                    _instance = go.AddComponent<RMSession>();
                }
                
                return _instance;
            }
        }

        private void Awake()
        {
            try
            {
                if (!Singleton<GameWorld>.Instantiated) 
                    return;
                
                GameWorld = Singleton<GameWorld>.Instance;
                Player = GameWorld.MainPlayer;
                
                if (Player is not null)
                {
                    GamePlayerOwner = Player.gameObject.GetComponent<GamePlayerOwner>();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error in RMSession.Awake: {ex.Message}");
            }
        }

        public static void AddToCriticalPlayers(string playerId, Vector3 position)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                Plugin.LogSource.LogError("Tried to add player with null or empty ID");
                return;
            }

            if (Singleton<GameWorld>.Instance.MainPlayer.ProfileId == playerId) 
                return;

            // Allow overwrites for updating item status
            Instance.CriticalPlayers[playerId] = position;
            
            Plugin.LogSource.LogDebug($"Player {playerId} added to critical players.");
        }

        public static void RemovePlayerFromCriticalPlayers(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) ||
                Singleton<GameWorld>.Instance.MainPlayer.ProfileId == playerId) 
                return;

            Instance.CriticalPlayers.Remove(playerId);
            
            Plugin.LogSource.LogDebug($"Player {playerId} removed from critical players.");
        }

        public static Vector3 GetPosition(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || 
                Singleton<GameWorld>.Instance.MainPlayer.ProfileId == playerId)
                return Vector3.zero;

            return Instance.CriticalPlayers[playerId];
        }

        public static Dictionary<string, Vector3> GetCriticalPlayers()
        {
            return Instance.CriticalPlayers;
        }

        // Not sure yet
        public static int GetTotalPlayers()
        {
            return Singleton<GameWorld>.Instance.allObservedPlayersByID.Count;
        }

        public static List<Player> GetAllAlivePlayerList()
        {
            return Singleton<GameWorld>.Instance.AllAlivePlayersList;
        }

        private void OnDestroy()
        {
            try
            {
                // Clean up session state when raid ends
                Plugin.LogSource.LogInfo("[RMSession] OnDestroy - Cleaning up session state...");
                
                CriticalPlayers?.Clear();
                _instance = null;
                
                // Clear ghost mode state via Fika bridge (if available)
                FikaBridge.ClearGhostModeState();
                
                Plugin.LogSource.LogInfo("[RMSession] Session cleanup complete.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[RMSession] Error in OnDestroy: {ex.Message}");
            }
        }
    }
}