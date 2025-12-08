using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.UI;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using HarmonyLib;
using RevivalMod.Components;
using RevivalMod.Features;
using RevivalMod.FikaModule.Packets;
using RevivalMod.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using TMPro;

namespace RevivalMod.FikaModule.Common
{
    internal class FikaMethods
    {
        // Track players currently in ghost mode - accessible for patches
        // Using ConcurrentDictionary for thread safety (SAIN 3.1+ uses multithreading)
        // We use a dictionary with dummy values since ConcurrentHashSet doesn't exist
        // Lazy initialization to avoid potential issues during early assembly loading
        private static readonly Lazy<ConcurrentDictionary<string, byte>> _playersInGhostMode = 
            new Lazy<ConcurrentDictionary<string, byte>>(() => new ConcurrentDictionary<string, byte>());
        
        /// <summary>
        /// Thread-safe check if a player is in ghost mode
        /// </summary>
        public static bool IsPlayerInGhostMode(string profileId)
        {
            return _playersInGhostMode.IsValueCreated && _playersInGhostMode.Value.ContainsKey(profileId);
        }
        
        /// <summary>
        /// Thread-safe add player to ghost mode
        /// </summary>
        public static bool AddPlayerToGhostMode(string profileId) => _playersInGhostMode.Value.TryAdd(profileId, 0);
        
        /// <summary>
        /// Thread-safe remove player from ghost mode
        /// </summary>
        public static bool RemovePlayerFromGhostMode(string profileId)
        {
            return _playersInGhostMode.IsValueCreated && _playersInGhostMode.Value.TryRemove(profileId, out _);
        }
        
        /// <summary>
        /// Get count of players in ghost mode (for logging)
        /// </summary>
        public static int GhostModePlayerCount => _playersInGhostMode.IsValueCreated ? _playersInGhostMode.Value.Count : 0;
        
        /// <summary>
        /// Clear all ghost mode state (call on raid end)
        /// </summary>
        public static void ClearGhostModeState()
        {
            if (_playersInGhostMode.IsValueCreated)
            {
                _playersInGhostMode.Value.Clear();
            }
        }
        
        // Harmony instance for patching SAIN
        private static Harmony _harmonyInstance;
        
        /// <summary>
        /// Initialize Harmony patches for ghost mode integration (vanilla AI + SAIN)
        /// </summary>
        public static void InitSAINPatches()
        {
            _harmonyInstance = new Harmony("com.revivalmod.ghostmode");
            
            // Patch vanilla EFT enemy-adding methods to prevent ghost mode players from being added
            InitVanillaAIPatches();
            
            // Patch SAIN's IsEnemyValid for additional protection
            InitSAINEnemyValidPatch();
        }
        
        /// <summary>
        /// Patches vanilla EFT methods to prevent ghost mode players from being added as enemies
        /// </summary>
        private static void InitVanillaAIPatches()
        {
            try
            {
                // Patch BotsGroup.AddEnemy
                var botsGroupAddEnemy = AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.AddEnemy));
                if (botsGroupAddEnemy != null)
                {
                    var prefix = typeof(FikaMethods).GetMethod(nameof(AddEnemyPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmonyInstance.Patch(botsGroupAddEnemy, prefix: new HarmonyMethod(prefix));
                    Plugin.LogSource.LogInfo("[GhostMode] Patched BotsGroup.AddEnemy");
                }
                
                // Patch BotMemoryClass.AddEnemy
                var botMemoryAddEnemy = AccessTools.Method(typeof(BotMemoryClass), nameof(BotMemoryClass.AddEnemy));
                if (botMemoryAddEnemy != null)
                {
                    var prefix = typeof(FikaMethods).GetMethod(nameof(AddEnemyPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmonyInstance.Patch(botMemoryAddEnemy, prefix: new HarmonyMethod(prefix));
                    Plugin.LogSource.LogInfo("[GhostMode] Patched BotMemoryClass.AddEnemy");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[GhostMode] Failed to patch vanilla AI: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Prefix patch for AddEnemy methods - blocks ghost mode players from being added
        /// Uses __0 (positional param) since BotsGroup uses "person" but BotMemoryClass uses "enemy"
        /// </summary>
        private static bool AddEnemyPrefix(IPlayer __0)
        {
            if (__0 == null)
                return true;
                
            // Block ghost mode players from being added as enemies
            if (IsPlayerInGhostMode(__0.ProfileId))
            {
                return false; // Skip the original method
            }
            
            return true;
        }
        
        /// <summary>
        /// Prefix patch for SAIN's tryAddEnemy - blocks ghost mode players from being added to SAIN's enemy list
        /// </summary>
        private static bool SAINTryAddEnemyPrefix(IPlayer enemyPlayer, ref object __result)
        {
            if (enemyPlayer == null)
                return true;
                
            // Block ghost mode players from being added as enemies
            if (IsPlayerInGhostMode(enemyPlayer.ProfileId))
            {
                __result = null; // Return null (no enemy added)
                return false; // Skip the original method
            }
            
            return true;
        }
        
        /// <summary>
        /// Patches SAIN's enemy tracking methods
        /// </summary>
        private static void InitSAINEnemyValidPatch()
        {
            try
            {
                // Try to find SAIN's EnemyListController.tryAddEnemy method (the actual internal entry point)
                var enemyListControllerType = Type.GetType("SAIN.SAINComponent.Classes.EnemyClasses.EnemyListController, SAIN");
                if (enemyListControllerType != null)
                {
                    // Patch tryAddEnemy - the internal method ALL enemy additions go through
                    var tryAddEnemyMethod = enemyListControllerType.GetMethod("tryAddEnemy", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    if (tryAddEnemyMethod != null)
                    {
                        var prefix = typeof(FikaMethods).GetMethod(nameof(SAINTryAddEnemyPrefix), 
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _harmonyInstance.Patch(tryAddEnemyMethod, prefix: new HarmonyMethod(prefix));
                        Plugin.LogSource.LogInfo("[GhostMode] Patched SAIN EnemyListController.tryAddEnemy");
                    }
                    else
                    {
                        Plugin.LogSource.LogWarning("[GhostMode] Could not find SAIN tryAddEnemy method");
                    }
                }
                else
                {
                    Plugin.LogSource.LogInfo("[GhostMode] SAIN EnemyListController not found, skipping");
                }
                
                // Also patch IsEnemyValid as a backup
                var sainEnemyType = Type.GetType("SAIN.SAINComponent.Classes.EnemyClasses.Enemy, SAIN");
                if (sainEnemyType != null)
                {
                    var isEnemyValidMethod = sainEnemyType.GetMethod("IsEnemyValid", 
                        BindingFlags.Public | BindingFlags.Static);
                    
                    if (isEnemyValidMethod != null)
                    {
                        var postfixMethod = typeof(FikaMethods).GetMethod(nameof(IsEnemyValidPostfix), 
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _harmonyInstance.Patch(isEnemyValidMethod, postfix: new HarmonyMethod(postfixMethod));
                        Plugin.LogSource.LogInfo("[GhostMode] Patched SAIN Enemy.IsEnemyValid");
                    }
                }
                else
                {
                    Plugin.LogSource.LogInfo("[GhostMode] SAIN not found, skipping SAIN-specific patches");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[GhostMode] Failed to patch SAIN: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Harmony postfix for SAIN's Enemy.IsEnemyValid - returns false for ghost mode players
        /// </summary>
        private static void IsEnemyValidPostfix(string botProfileId, object enemyPlayerComp, ref bool __result)
        {
            // If already invalid, don't need to check
            if (!__result) return;
            
            // Quick exit if no one is in ghost mode (avoid reflection overhead)
            if (GhostModePlayerCount == 0) return;
            
            try
            {
                // Get the Player from the PlayerComponent
                var playerProp = enemyPlayerComp?.GetType().GetProperty("Player");
                if (playerProp == null) return;
                
                var player = playerProp.GetValue(enemyPlayerComp) as Player;
                if (player == null) return;
                
                // Check if this player is in ghost mode (thread-safe)
                if (IsPlayerInGhostMode(player.ProfileId))
                {
                    __result = false;
                }
            }
            catch
            {
                // Ignore reflection errors
            }
        }

        public static void SendPlayerPositionPacket(string playerId, DateTime timeOfDeath, Vector3 position)
        {
            PlayerPositionPacket packet = new()
            {
                playerId = playerId,
                timeOfDeath = timeOfDeath,
                position = position
            };

            if (FikaBackendUtils.IsServer)
            {
                try
                {
                    Singleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError(ex);
                }
            }
            else if (FikaBackendUtils.IsClient)
            {
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }

        }
                
        public static void SendRemovePlayerFromCriticalPlayersListPacket(string playerId)
        {
            RemovePlayerFromCriticalPlayersListPacket packet = new()
            {
                playerId = playerId
            };

            if (FikaBackendUtils.IsServer)
            {
                try
                {
                    Singleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError(ex);
                }
            }
            else if (FikaBackendUtils.IsClient)
            {
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
        }

        public static void SendReviveMePacket(string reviveeId, string reviverId)
        {
            ReviveMePacket packet = new()
            {
                reviverId = reviverId,
                reviveeId = reviveeId
            };

            if (FikaBackendUtils.IsServer)
            {               
                try
                {
                    Singleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError(ex);
                }
            }
            else if (FikaBackendUtils.IsClient)
            {              
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
        }

        public static void SendReviveSucceedPacket(string reviverId, NetPeer peer)
        {
            RevivedPacket packet = new()
            {
                reviverId = reviverId
            };

            if (FikaBackendUtils.IsServer)
            {
                try
                {
                    Singleton<FikaServer>.Instance.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError(ex);
                }
            }
            else if (FikaBackendUtils.IsClient)
            {
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }

        }

        public static void SendReviveStartedPacket(string reviveeId, string reviverId)
        {
            ReviveStartedPacket packet = new()
            {
                reviverId = reviverId,
                reviveeId = reviveeId
            };

            if (FikaBackendUtils.IsServer)
            {
                try
                {
                    Singleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError(ex);
                }
            }
            else if (FikaBackendUtils.IsClient)
            {
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
        }
        
        public static void SendReviveCanceledPacket(string reviveeId, string reviverId)
        {
            ReviveCanceledPacket packet = new()
            {
                reviverId = reviverId,
                reviveeId = reviveeId
            };

            if (FikaBackendUtils.IsServer)
            {               
                try
                {
                    Singleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError(ex);
                }
            }
            else if (FikaBackendUtils.IsClient)
            {
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
        }

        public static void SendPlayerGhostModePacket(string playerId, bool isAlive)
        {
            PlayerGhostModePacket packet = new()
            {
                playerId = playerId,
                isAlive = isAlive
            };

            if (FikaBackendUtils.IsServer)
            {
                try
                {
                    Singleton<FikaServer>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered, true);
                    
                    // Host must also process locally since we don't receive our own packets
                    ProcessGhostModeLocally(playerId, isAlive);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource.LogError(ex);
                }
            }
            else if (FikaBackendUtils.IsClient)
            {
                Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableSequenced);
            }
        }
        
        /// <summary>
        /// Processes ghost mode state locally. Called by host when sending packets (since hosts don't receive their own packets).
        /// </summary>
        private static void ProcessGhostModeLocally(string playerId, bool isAlive)
        {
            Plugin.LogSource.LogInfo($"[GhostMode] Processing locally: playerId={playerId}, isAlive={isAlive}");
            
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
                return;

            Player targetPlayer = gameWorld.GetEverExistedPlayerByID(playerId);
            
            if (!isAlive)
            {
                // Player entering ghost mode
                AddPlayerToGhostMode(playerId);
                Plugin.LogSource.LogInfo($"[GhostMode] Local: Player {playerId} added to ghost mode ({GhostModePlayerCount} total)");
                
                if (targetPlayer != null)
                {
                    ClearVanillaAITargeting(targetPlayer, gameWorld);
                }
            }
            else
            {
                // Player exiting ghost mode
                RemovePlayerFromGhostMode(playerId);
                Plugin.LogSource.LogInfo($"[GhostMode] Local: Player {playerId} removed from ghost mode ({GhostModePlayerCount} remaining)");
            }
        }

        private static void OnPlayerPositionPacketReceived(PlayerPositionPacket packet, NetPeer peer)
        {
            // Server (player host or headless) always forwards to all clients
            if (FikaBackendUtils.IsServer)
            {
                SendPlayerPositionPacket(packet.playerId, packet.timeOfDeath, packet.position);
            }
            
            // Non-headless machines (player hosts and clients) process the packet
            if (!FikaBackendUtils.IsHeadless)
            {
                RMSession.AddToCriticalPlayers(packet.playerId, packet.position);
            }
        }
        
        private static void OnRemovePlayerFromCriticalPlayersListPacketReceived(RemovePlayerFromCriticalPlayersListPacket packet, NetPeer peer)
        {
            // Server (player host or headless) always forwards to all clients
            if (FikaBackendUtils.IsServer)
            {
                SendRemovePlayerFromCriticalPlayersListPacket(packet.playerId);
            }
            
            // Non-headless machines (player hosts and clients) process the packet
            if (!FikaBackendUtils.IsHeadless)
            {
                RMSession.RemovePlayerFromCriticalPlayers(packet.playerId);
            }
        }
        
        /// <summary>
        /// Handles the reception of a <see cref="ReviveMePacket"/> from a network peer.
        /// Depending on the server state and backend configuration, either forwards the revive request
        /// or attempts to perform a revival by a teammate. If the revival is successful, sends a notification
        /// packet to the reviver.
        /// </summary>
        /// <param name="packet">The <see cref="ReviveMePacket"/> containing revivee and reviver IDs.</param>
        /// <param name="peer">The <see cref="NetPeer"/> that sent the packet.</param>
        private static void OnReviveMePacketReceived(ReviveMePacket packet, NetPeer peer)
        {
            // Server (player host or headless) always forwards to all clients
            if (FikaBackendUtils.IsServer)
            {
                SendReviveMePacket(packet.reviveeId, packet.reviverId);
            }
            
            // Non-headless machines (player hosts and clients) process the packet
            if (!FikaBackendUtils.IsHeadless)
            {
                bool revived = RevivalFeatures.TryPerformRevivalByTeammate(packet.reviveeId);
                
                if (!revived) 
                    return;
                
                SendReviveSucceedPacket(packet.reviverId, peer);
                Singleton<GameUI>.Instance.BattleUiPanelExtraction.Close();
            }
        }

        private static void OnReviveSucceedPacketReceived(RevivedPacket packet, NetPeer peer)
        {
            // Server (player host or headless) always forwards to all clients
            if (FikaBackendUtils.IsServer)
            {
                SendReviveSucceedPacket(packet.reviverId, peer);
            }
            
            // Non-headless machines (player hosts and clients) process the packet
            if (!FikaBackendUtils.IsHeadless)
            {
                NotificationManagerClass.DisplayMessageNotification(
                        $"Successfully revived your teammate!",
                        ENotificationDurationType.Long,
                        ENotificationIconType.Friend,
                        Color.green);
            }
        }

        private static void OnReviveStartedPacketReceived(ReviveStartedPacket packet, NetPeer peer)
        {
            // Server (player host or headless) always forwards to all clients
            if (FikaBackendUtils.IsServer)
            {
                SendReviveStartedPacket(packet.reviveeId, packet.reviverId);
            }
            
            // Non-headless machines (player hosts and clients) process the packet
            if (!FikaBackendUtils.IsHeadless)
            {
                if (FikaBackendUtils.Profile.ProfileId != packet.reviveeId)
                    return;

                Plugin.LogSource.LogDebug("ReviveStarted packet received");
                
                RevivalFeatures.criticalStateMainTimer.StopTimer();
                Singleton<GameUI>.Instance.BattleUiPanelExtraction.Display();
                
                TextMeshProUGUI textTimerPanel = MonoBehaviourSingleton<GameUI>.Instance.BattleUiPanelExtraction.GetComponentInChildren<TextMeshProUGUI>();
                
                textTimerPanel.SetText("Being revived...");
            }
        }

        private static void OnReviveCanceledPacketReceived(ReviveCanceledPacket packet, NetPeer peer)
        {
            // Server (player host or headless) always forwards to all clients
            if (FikaBackendUtils.IsServer)
            {
                SendReviveCanceledPacket(packet.reviveeId, packet.reviverId);
            }
            
            // Non-headless machines (player hosts and clients) process the packet
            if (!FikaBackendUtils.IsHeadless)
            {
                if (FikaBackendUtils.Profile.ProfileId != packet.reviveeId)
                    return;
                    
                Plugin.LogSource.LogDebug("ReviveCanceled packet received");

                Singleton<GameUI>.Instance.BattleUiPanelExtraction.Close();

                RevivalFeatures.criticalStateMainTimer.StartCountdown(RevivalFeatures._playerList[packet.reviveeId].CriticalTimer,
                                                                    "Critical State Timer", TimerPosition.MiddleCenter);
            }
        }

        private static void OnPlayerGhostModePacketReceived(PlayerGhostModePacket packet, NetPeer peer)
        {
            Plugin.LogSource.LogInfo($"[GhostMode] Packet received: playerId={packet.playerId}, isAlive={packet.isAlive}, IsServer={FikaBackendUtils.IsServer}, IsHeadless={FikaBackendUtils.IsHeadless}");

            // Server (player host or headless) always forwards to all clients
            if (FikaBackendUtils.IsServer)
            {
                SendPlayerGhostModePacket(packet.playerId, packet.isAlive);
            }
            
            // All machines process ghost mode state (needed for AI targeting)
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                Plugin.LogSource.LogError("[GhostMode] GameWorld is null!");
                return;
            }

            // Find the target player
            Player targetPlayer = gameWorld.GetEverExistedPlayerByID(packet.playerId);
            
            if (targetPlayer == null)
            {
                Plugin.LogSource.LogWarning($"[GhostMode] GetEverExistedPlayerByID returned null for {packet.playerId}");
                return;
            }

            Plugin.LogSource.LogInfo($"[GhostMode] Found player: ProfileId={targetPlayer.ProfileId}, IsAI={targetPlayer.IsAI}, Type={targetPlayer.GetType().Name}");

            // NOTE: We do NOT modify IsAlive - that breaks Fika's death/extraction sync on headless
            // Instead, we ONLY use the PlayersInGhostMode HashSet which SAIN checks via our patch
            
            // Track ghost mode state - this is used by the SAIN patch to mark enemies as invalid
            if (!packet.isAlive)
            {
                // Player is entering ghost mode (thread-safe)
                AddPlayerToGhostMode(packet.playerId);
                Plugin.LogSource.LogInfo($"[GhostMode] Player {packet.playerId} added to ghost mode list ({GhostModePlayerCount} total in ghost mode)");
                
                // The Harmony patch on IsEnemyValid will cause SAIN to automatically
                // detect and remove these enemies on its next update cycle.
                // We clear vanilla AI immediately since it doesn't have the same automatic cleanup.
                ClearVanillaAITargeting(targetPlayer, gameWorld);
            }
            else
            {
                // Player is exiting ghost mode (revived or died) - thread-safe
                bool wasInGhostMode = RemovePlayerFromGhostMode(packet.playerId);
                Plugin.LogSource.LogInfo($"[GhostMode] Player {packet.playerId} removed from ghost mode list (was in list: {wasInGhostMode}). Remaining in ghost mode: {GhostModePlayerCount}");
                
                // Ensure any residual state is cleaned up
                // Note: For extraction to work properly after death, the player must NOT be in ghost mode
                if (wasInGhostMode)
                {
                    Plugin.LogSource.LogInfo($"[GhostMode] Player {packet.playerId} ghost mode fully cleared - extraction should work");
                }
            }
        }

        /// <summary>
        /// Clears vanilla AI targeting for a player. SAIN handles its own cleanup
        /// via the Harmony patch on IsEnemyValid, so we don't need to call RemoveEnemy directly.
        /// </summary>
        private static void ClearVanillaAITargeting(Player targetPlayer, GameWorld gameWorld)
        {
            if (targetPlayer == null || gameWorld == null)
                return;

            int botsCleared = 0;
            
            try
            {
                // Get all registered players and find bots
                foreach (Player player in gameWorld.RegisteredPlayers)
                {
                    if (player == null || !player.IsAI)
                        continue;

                    // Get the bot's AIData which contains the BotOwner
                    if (player.AIData?.BotOwner == null)
                        continue;

                    BotOwner botOwner = player.AIData.BotOwner;

                    try
                    {
                        // Clear from vanilla AI goal enemy if this is the current target
                        if (botOwner.Memory?.GoalEnemy?.Person?.ProfileId == targetPlayer.ProfileId)
                        {
                            botOwner.Memory.GoalEnemy = null;
                            botsCleared++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource.LogWarning($"[GhostMode] Error clearing bot {player.ProfileId}: {ex.Message}");
                    }
                }

                if (botsCleared > 0)
                {
                    Plugin.LogSource.LogInfo($"[GhostMode] Cleared vanilla GoalEnemy for {botsCleared} bots targeting {targetPlayer.ProfileId}");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[GhostMode] Error in ClearVanillaAITargeting: {ex.Message}");
            }
        }

        public static void OnFikaNetManagerCreated(FikaNetworkManagerCreatedEvent managerCreatedEvent)
        {
            Plugin.LogSource.LogInfo("[Fika Module] OnFikaNetManagerCreated - Registering packet handlers...");
            managerCreatedEvent.Manager.RegisterPacket<PlayerPositionPacket, NetPeer>(OnPlayerPositionPacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<RemovePlayerFromCriticalPlayersListPacket, NetPeer>(OnRemovePlayerFromCriticalPlayersListPacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<ReviveMePacket, NetPeer>(OnReviveMePacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<RevivedPacket, NetPeer>(OnReviveSucceedPacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<ReviveStartedPacket, NetPeer>(OnReviveStartedPacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<ReviveCanceledPacket, NetPeer>(OnReviveCanceledPacketReceived);
            managerCreatedEvent.Manager.RegisterPacket<PlayerGhostModePacket, NetPeer>(OnPlayerGhostModePacketReceived);
            Plugin.LogSource.LogInfo("[Fika Module] All packet handlers registered (including GhostMode)!");
        }
        
        public static void InitOnPluginEnabled()
        {
            Plugin.LogSource.LogInfo("[Fika Module] InitOnPluginEnabled - Subscribing to FikaNetworkManagerCreatedEvent");
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnFikaNetManagerCreated);
            
            // Initialize SAIN patches for ghost mode (if SAIN is installed)
            InitSAINPatches();
        }
    }
}