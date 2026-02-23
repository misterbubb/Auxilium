using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vertigo2;
using Vertigo2.Persistence;
using Vertigo2.Player;
using Vertigo2.Interaction;
using Vertigo2.Weapons;
using Vertigo2Multiplayer.UI;

namespace Vertigo2Multiplayer.Patches
{
    /// <summary>
    /// Harmony patches for hooking into Vertigo 2's player system
    /// </summary>
    public static class PlayerPatches
    {
        /// <summary>
        /// Hook into VertigoPlayer.Awake to capture player instance
        /// </summary>
        [HarmonyPatch(typeof(VertigoPlayer), "Awake")]
        public static class VertigoPlayer_Awake_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(VertigoPlayer __instance)
            {
                if (PlayerManager.Instance == null) return;
                
                PlayerManager.Instance.LocalHead = __instance.headCamera?.transform;
                PlayerManager.Instance.LocalLeftHand = __instance.LController?.transform;
                PlayerManager.Instance.LocalRightHand = __instance.RController?.transform;
                PlayerManager.Instance.LocalPlayerRoot = __instance.transform;
                PlayerManager.Instance.LocalPlayer = __instance;
            }
        }

        /// <summary>
        /// Hook into player damage to sync to other players
        /// </summary>
        [HarmonyPatch(typeof(VertigoPlayer), "Hit")]
        public static class VertigoPlayer_Hit_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(VertigoPlayer __instance, HitInfo hit, bool crit)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (__instance != VertigoPlayer.instance) return;
                
                var packet = PacketBuilder.PlayerDamage(
                    hit.hitDamage, 
                    hit.hitPoint,
                    __instance.health,
                    __instance.startHealth
                );
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }
        }

        /// <summary>
        /// Hook into player death
        /// </summary>
        [HarmonyPatch(typeof(VertigoPlayer), "Die")]
        public static class VertigoPlayer_Die_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(VertigoPlayer __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (__instance != VertigoPlayer.instance) return;
                
                var packet = PacketBuilder.PlayerDeath();
                NetworkManager.Instance.Broadcast(packet, reliable: true);
                
                Plugin.Log.LogInfo("[DEATH] Local player died, notifying other players");
            }
        }

        /// <summary>
        /// Hook into player death event to sync death and handle respawn
        /// Note: VertigoPlayer.Respawn doesn't exist - respawn is handled via SaveGameManager.DieAndRespawn
        /// which shows death screen, then GameManager.LoadLevel loads the checkpoint scene
        /// </summary>
        [HarmonyPatch(typeof(SaveGameManager), "DieAndRespawn", MethodType.Enumerator)]
        public static class SaveGameManager_DieAndRespawn_Patch
        {
            // This is handled in StoryPatches.cs - we just need to sync the death event
            // The respawn position sync happens via HostPositionResponse when scene loads
        }

        /// <summary>
        /// Hook into character controller for position updates
        /// </summary>
        [HarmonyPatch(typeof(VertigoCharacterController), "Awake")]
        public static class VertigoCharacterController_Awake_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(VertigoCharacterController __instance)
            {
                if (PlayerManager.Instance != null)
                {
                    PlayerManager.Instance.LocalCharacterController = __instance;
                }
            }
        }

        /// <summary>
        /// Hook into Gun firing to sync weapon shots - using static event
        /// </summary>
        [HarmonyPatch(typeof(Shooter), "Start")]
        public static class Shooter_Start_Patch
        {
            private static bool _eventRegistered = false;
            
            [HarmonyPostfix]
            public static void Postfix()
            {
                if (!_eventRegistered)
                {
                    Shooter.OnAnyShoot += OnShooterFired;
                    _eventRegistered = true;
                    Plugin.Log.LogInfo("[WEAPON SYNC] Registered Shooter.OnAnyShoot event handler");
                }
            }
            
            private static void OnShooterFired(Shooter shooter)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!shooter.playerControlled)
                {
                    // Log enemy shots occasionally for debugging
                    return;
                }
                
                var equippable = shooter.GetComponentInParent<Equippable>();
                string weaponId = equippable != null ? equippable.GetType().Name : "Unknown";
                Vector3 direction = shooter.transform.forward;
                Vector3 position = shooter.transform.position;
                
                Plugin.Log.LogInfo($"[WEAPON FIRE] Local player fired: {weaponId} dir={direction} pos={position}");
                
                var packet = PacketBuilder.WeaponFire(weaponId, direction);
                NetworkManager.Instance.Broadcast(packet, reliable: false);
            }
        }

        /// <summary>
        /// Hook into Equippable enable to track weapon switches
        /// </summary>
        [HarmonyPatch(typeof(Equippable), "OnEnable")]
        public static class Equippable_OnEnable_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Equippable __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (PlayerManager.Instance == null) return;
                
                string weaponId = __instance.GetType().Name;
                
                PlayerManager.Instance.CurrentWeaponId = weaponId;
                
                Plugin.Log.LogInfo($"[WEAPON SWITCH] Equippable enabled: {weaponId}");
                
                var packet = PacketBuilder.WeaponSwitch(weaponId);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }
        }

        /// <summary>
        /// Hook into WeaponPickup.Pickup to sync weapon pickups to all players
        /// </summary>
        [HarmonyPatch(typeof(WeaponPickup), "Pickup")]
        public static class WeaponPickup_Pickup_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(WeaponPickup __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (InventorySync.Instance == null) return;
                
                string weaponName = __instance.weapon?.name;
                if (string.IsNullOrEmpty(weaponName))
                {
                    Plugin.Log.LogWarning("[INVENTORY] WeaponPickup.Pickup - weapon name is null/empty");
                    return;
                }
                
                Plugin.Log.LogInfo($"[INVENTORY] WeaponPickup.Pickup triggered: {weaponName}, unlock={__instance.unlockWeapon}");
                InventorySync.Instance.OnLocalWeaponPickup(weaponName, __instance.unlockWeapon);
            }
        }

        /// <summary>
        /// Hook into EquippablesManager.UnlockEquippable to catch ALL weapon unlocks
        /// This catches weapons unlocked via console, story events, etc.
        /// </summary>
        [HarmonyPatch(typeof(EquippablesManager), "UnlockEquippable", typeof(string))]
        public static class EquippablesManager_UnlockEquippable_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(string name)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (InventorySync.Instance == null) return;
                if (string.IsNullOrEmpty(name)) return;
                
                Plugin.Log.LogInfo($"[INVENTORY] EquippablesManager.UnlockEquippable triggered: {name}");
                InventorySync.Instance.OnLocalWeaponPickup(name, true);
            }
        }

        /// <summary>
        /// Hook into Door.Open to sync door interactions - BIDIRECTIONAL
        /// </summary>
        [HarmonyPatch(typeof(Door), "Open", new System.Type[0])]
        public static class Door_Open_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Door __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (InteractableSync.Instance == null) return;
                
                InteractableSync.Instance.OnDoorInteracted(__instance);
            }
        }

        /// <summary>
        /// Hook into Door.Close to sync door interactions - BIDIRECTIONAL
        /// </summary>
        [HarmonyPatch(typeof(Door), "Close")]
        public static class Door_Close_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Door __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (InteractableSync.Instance == null) return;
                
                InteractableSync.Instance.OnDoorInteracted(__instance);
            }
        }

        /// <summary>
        /// Hook into Door.Trigger to sync door interactions - BIDIRECTIONAL
        /// </summary>
        [HarmonyPatch(typeof(Door), "Trigger")]
        public static class Door_Trigger_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Door __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (InteractableSync.Instance == null) return;
                
                InteractableSync.Instance.OnDoorInteracted(__instance);
            }
        }

        /// <summary>
        /// DISABLE TIME FREEZE during multiplayer but still allow pause menu access
        /// We patch the Pause method to skip Time.timeScale = 0 AND reset GameManager.paused
        /// </summary>
        [HarmonyPatch(typeof(PauseManager), "Pause")]
        public static class PauseManager_Pause_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(PauseManager __instance)
            {
                // If in multiplayer, restore time scale and paused flag so game keeps running
                if (NetworkManager.Instance != null && NetworkManager.Instance.IsConnected)
                {
                    Time.timeScale = 1f;
                    GameManager.paused = false; // Also reset the static paused flag
                    Plugin.Log.LogInfo("[PAUSE] Restored timeScale to 1 and GameManager.paused to false during multiplayer pause");
                }
            }
        }

        /// <summary>
        /// Hook into Elevator.CallElevator to sync elevator calls
        /// </summary>
        [HarmonyPatch(typeof(Elevator), "CallElevator")]
        public static class Elevator_CallElevator_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Elevator __instance, float pos)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (WorldSync.Instance == null) return;
                
                WorldSync.Instance.OnElevatorCalled(__instance, pos);
            }
        }

        /// <summary>
        /// Hook into Elevator.Move to sync elevator toggle
        /// </summary>
        [HarmonyPatch(typeof(Elevator), "Move")]
        public static class Elevator_Move_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Elevator __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (WorldSync.Instance == null) return;
                
                // Move() calls CallElevator internally, so the CallElevator patch handles it
            }
        }

        /// <summary>
        /// Hook into HealthStation injection to sync juice depletion
        /// </summary>
        [HarmonyPatch(typeof(HealthStation), "Inject")]
        public static class HealthStation_Inject_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(HealthStation __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (WorldSync.Instance == null) return;
                
                WorldSync.Instance.OnHealthStationUsed(__instance);
            }
        }

        /// <summary>
        /// Hook into Train movement to sync train position
        /// </summary>
        [HarmonyPatch(typeof(Train), "FixedUpdate")]
        public static class Train_FixedUpdate_Patch
        {
            private static int _frameCounter = 0;
            
            [HarmonyPostfix]
            public static void Postfix(Train __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return; // Only host syncs trains
                if (WorldSync.Instance == null) return;
                
                // Only sync every 5 frames to reduce bandwidth
                _frameCounter++;
                if (_frameCounter % 5 != 0) return;
                
                WorldSync.Instance.OnTrainUpdate(__instance);
            }
        }

        /// <summary>
        /// Hook into GameManager.LoadStage to sync stage loading
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "LoadStage")]
        public static class GameManager_LoadStage_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(Stage stage)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                // If we're the host, broadcast the stage load to clients
                if (NetworkManager.Instance.IsHost)
                {
                    Plugin.Log.LogInfo($"[STAGE] Host loading stage: {stage.sceneName}");
                    // The scene change will be synced via the existing SceneChange packet
                }
            }
        }
    }
}
