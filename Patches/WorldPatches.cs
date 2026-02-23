using HarmonyLib;
using UnityEngine;
using Vertigo2;
using Vertigo2.Dialogarithm;
using Vertigo2.Interaction;
using Vertigo2.Player;

namespace Vertigo2Multiplayer.Patches
{
    /// <summary>
    /// Harmony patches for world sync (NPC dialog, triggers, physics objects)
    /// </summary>
    public static class WorldPatches
    {
        #region NPC Dialog Sync

        /// <summary>
        /// Hook into Dialogarithm_Player.Play to sync NPC dialog
        /// </summary>
        [HarmonyPatch(typeof(Dialogarithm_Player), "Play", typeof(Dialogarithm_LineBase), typeof(float))]
        public static class DialogarithmPlayer_Play_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Dialogarithm_Player __instance, Dialogarithm_LineBase lineToPlay, float nTime)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (lineToPlay == null) return;
                
                // Get character name and line name
                string characterName = __instance.character?.name ?? "Unknown";
                string lineName = lineToPlay.name ?? "Unknown";
                
                Plugin.Log.LogInfo($"[WORLD PATCH] Dialog playing: {characterName} - {lineName}");
                
                // Sync to other players
                WorldSync.Instance?.SyncDialogPlay(characterName, lineName, nTime);
            }
        }

        #endregion

        #region Trigger Sync

        /// <summary>
        /// Hook into MessageOnTriggerEnter to sync story triggers
        /// </summary>
        [HarmonyPatch(typeof(MessageOnTriggerEnter), "OnTriggerEnter")]
        public static class MessageOnTriggerEnter_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(MessageOnTriggerEnter __instance, Collider other)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return;
                
                // Only sync if player triggered it
                if (other.GetComponentInParent<VertigoPlayer>() == null) return;
                
                string triggerId = GetObjectPath(__instance.gameObject);
                Plugin.Log.LogInfo($"[WORLD PATCH] Trigger entered: {triggerId}");
                
                WorldSync.Instance?.SyncTriggerActivation(triggerId, true);
            }
            
            private static string GetObjectPath(GameObject obj)
            {
                string path = obj.name;
                Transform parent = obj.transform.parent;
                while (parent != null)
                {
                    path = parent.name + "/" + path;
                    parent = parent.parent;
                }
                return path;
            }
        }

        #endregion

        #region Vehicle Sync

        /// <summary>
        /// Hook into Elevator.CallElevator to sync elevator movement
        /// </summary>
        [HarmonyPatch(typeof(Elevator), "CallElevator")]
        public static class Elevator_CallElevator_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Elevator __instance, float pos)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return;
                
                string elevatorId = GetObjectPath(__instance.gameObject);
                Plugin.Log.LogInfo($"[WORLD PATCH] Elevator called to position {pos}: {elevatorId}");
                
                // Force immediate sync
                var packet = PacketBuilder.VehicleState(
                    elevatorId, 
                    0, // Elevator type
                    __instance.transform.position,
                    __instance.transform.rotation,
                    pos);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }
            
            private static string GetObjectPath(GameObject obj)
            {
                string path = obj.name;
                Transform parent = obj.transform.parent;
                while (parent != null)
                {
                    path = parent.name + "/" + path;
                    parent = parent.parent;
                }
                return path;
            }
        }

        #endregion

        #region Physics Object Sync

        /// <summary>
        /// Hook into VertigoInteractable.Grab to sync physics object grabs
        /// </summary>
        [HarmonyPatch(typeof(VertigoInteractable), "Grab")]
        public static class VertigoInteractable_Grab_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(VertigoInteractable __instance, VertigoHand grabber)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                Plugin.Log.LogInfo($"[WORLD PATCH] VertigoInteractable.Grab called: {__instance.name}, type={__instance.interactType}");
                
                // Only sync grabbable physics objects
                if (__instance.interactType != VertigoInteractable.InteractTypes.Grabbable &&
                    __instance.interactType != VertigoInteractable.InteractTypes.Custom)
                    return;
                
                var rb = __instance.GetComponent<Rigidbody>();
                if (rb == null)
                {
                    Plugin.Log.LogInfo($"[WORLD PATCH] No rigidbody on {__instance.name}");
                    return;
                }
                
                // Register and sync the grab
                int objectId = WorldSync.Instance?.RegisterPhysicsObject(__instance.gameObject) ?? -1;
                if (objectId >= 0)
                {
                    Plugin.Log.LogInfo($"[WORLD PATCH] Physics object grabbed: {__instance.name} (ID: {objectId})");
                    WorldSync.Instance?.SyncPhysicsObjectGrab(objectId, true, rb.position);
                    
                    // Mark as held locally in WorldSync
                    WorldSync.Instance?.SetObjectHeldLocally(objectId, true);
                }
            }
        }

        /// <summary>
        /// Hook into VertigoInteractable.Drop to sync physics object releases
        /// </summary>
        [HarmonyPatch(typeof(VertigoInteractable), "Drop")]
        public static class VertigoInteractable_Drop_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(VertigoInteractable __instance, VertigoHand dropper)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                Plugin.Log.LogInfo($"[WORLD PATCH] VertigoInteractable.Drop called: {__instance.name}");
                
                var rb = __instance.GetComponent<Rigidbody>();
                if (rb == null) return;
                
                // Find the object ID and sync release
                int objectId = WorldSync.Instance?.GetPhysicsObjectId(__instance.gameObject) ?? -1;
                if (objectId >= 0)
                {
                    Plugin.Log.LogInfo($"[WORLD PATCH] Physics object released: {__instance.name} (ID: {objectId})");
                    WorldSync.Instance?.SyncPhysicsObjectGrab(objectId, false, rb.position);
                    
                    // Mark as not held locally
                    WorldSync.Instance?.SetObjectHeldLocally(objectId, false);
                }
            }
        }

        #endregion
    }
}
