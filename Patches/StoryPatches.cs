using HarmonyLib;
using UnityEngine;
using UnityEngine.Playables;
using Vertigo2;
using Vertigo2.Persistence;
using Vertigo2.Player;
using Vertigo2.Dialogarithm;
using Vertigo2.AI;

namespace Vertigo2Multiplayer.Patches
{
    /// <summary>
    /// Harmony patches for story/progression syncing
    /// </summary>
    public static class StoryPatches
    {
        /// <summary>
        /// Hook into ProgressManager.SetString to sync progress flags
        /// </summary>
        [HarmonyPatch(typeof(ProgressManager), "SetString")]
        public static class ProgressManager_SetString_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(string s)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                Plugin.Log.LogInfo($"[STORY] ProgressManager.SetString: {s}");
                
                // Sync the progress string to all players
                StorySync.Instance?.SyncProgressString(s);
            }
        }

        /// <summary>
        /// Hook into ProgressManager.ClearString to sync progress flag removal
        /// </summary>
        [HarmonyPatch(typeof(ProgressManager), "ClearString")]
        public static class ProgressManager_ClearString_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(string s)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                Plugin.Log.LogInfo($"[STORY] ProgressManager.ClearString: {s}");
                
                // Sync the progress string removal
                if (NetworkManager.Instance.IsHost)
                {
                    var packet = PacketBuilder.ProgressString(s, false);
                    NetworkManager.Instance.Broadcast(packet, reliable: true);
                }
            }
        }

        /// <summary>
        /// Hook into PlayableDirector.Play to sync cutscene starts
        /// </summary>
        [HarmonyPatch(typeof(PlayableDirector), "Play", new System.Type[] { })]
        public static class PlayableDirector_Play_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(PlayableDirector __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return;
                
                // Generate timeline ID
                string timelineId = GetTimelineId(__instance);
                string assetName = __instance.playableAsset?.name ?? "unknown";
                
                Plugin.Log.LogInfo($"[STORY] Timeline PLAY: {timelineId} (asset={assetName}, duration={__instance.duration:F2}s)");
                
                // Broadcast timeline start
                var packet = PacketBuilder.TimelineState(
                    timelineId, 
                    true, 
                    (float)__instance.time, 
                    (float)__instance.duration);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }
            
            private static string GetTimelineId(PlayableDirector director)
            {
                string path = GetGameObjectPath(director.gameObject);
                string assetName = director.playableAsset?.name ?? "unknown";
                return $"{path}_{assetName}";
            }
            
            private static string GetGameObjectPath(GameObject obj)
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

        /// <summary>
        /// Hook into PlayableDirector.Stop to sync cutscene stops
        /// </summary>
        [HarmonyPatch(typeof(PlayableDirector), "Stop")]
        public static class PlayableDirector_Stop_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(PlayableDirector __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return;
                
                string timelineId = GetTimelineId(__instance);
                
                Plugin.Log.LogInfo($"[STORY] Timeline STOP: {timelineId}");
                
                var packet = PacketBuilder.TimelineState(timelineId, false, 0f, (float)__instance.duration);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }
            
            private static string GetTimelineId(PlayableDirector director)
            {
                string path = GetGameObjectPath(director.gameObject);
                string assetName = director.playableAsset?.name ?? "unknown";
                return $"{path}_{assetName}";
            }
            
            private static string GetGameObjectPath(GameObject obj)
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

        /// <summary>
        /// Hook into Dialogarithm_Player.Play to sync dialogue
        /// </summary>
        [HarmonyPatch(typeof(Dialogarithm_Player), "Play", typeof(Dialogarithm_LineBase), typeof(float))]
        public static class DialogarithmPlayer_Play_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Dialogarithm_Player __instance, Dialogarithm_LineBase lineToPlay, float nTime)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return;
                if (lineToPlay == null) return;
                
                string characterName = __instance.character?.name ?? "Unknown";
                string lineName = lineToPlay.name;
                
                Plugin.Log.LogInfo($"[DIALOG] Playing: {characterName} - {lineName} (time={nTime:F2})");
                
                WorldSync.Instance?.SyncDialogPlay(characterName, lineName, nTime);
            }
        }

        /// <summary>
        /// Hook into Dialogarithm_Player.Stop to sync dialogue stop
        /// </summary>
        [HarmonyPatch(typeof(Dialogarithm_Player), "Stop")]
        public static class DialogarithmPlayer_Stop_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Dialogarithm_Player __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return;
                
                string characterName = __instance.character?.name ?? "Unknown";
                
                Plugin.Log.LogInfo($"[DIALOG] Stopped: {characterName}");
            }
        }

        /// <summary>
        /// Hook into NPC.Refocus to sync NPC attention
        /// </summary>
        [HarmonyPatch(typeof(NPC), "Refocus")]
        public static class NPC_Refocus_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(NPC __instance, VertigoEntity target)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                string npcName = __instance.gameObject.name;
                string targetName = target?.gameObject.name ?? "null";
                
                Plugin.Log.LogInfo($"[NPC] {npcName} refocused on {targetName}");
            }
        }

        /// <summary>
        /// Hook into Enemy.Die to sync enemy deaths
        /// </summary>
        [HarmonyPatch(typeof(Enemy), "Die")]
        public static class Enemy_Die_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(Enemy __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                string enemyName = __instance.gameObject.name;
                string enemyType = __instance.GetType().Name;
                Vector3 position = __instance.transform.position;
                
                Plugin.Log.LogInfo($"[ENEMY DEATH] {enemyType} '{enemyName}' died at {position}");
                
                // TODO: Sync enemy death via EnemySync when implemented
            }
        }

        /// <summary>
        /// Hook into Enemy.Hit to sync enemy damage
        /// </summary>
        [HarmonyPatch(typeof(Enemy), "Hit")]
        public static class Enemy_Hit_Patch
        {
            private static int _hitLogCounter = 0;
            
            [HarmonyPostfix]
            public static void Postfix(Enemy __instance, HitInfo hit)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                // Log occasionally to avoid spam
                if (_hitLogCounter++ % 10 == 0)
                {
                    string enemyName = __instance.gameObject.name;
                    Plugin.Log.LogInfo($"[ENEMY HIT] {enemyName} took {hit.hitDamage:F1} damage, health={__instance.health:F1}");
                }
                
                // TODO: Sync enemy damage via EnemySync when implemented
            }
        }

        /// <summary>
        /// Hook into player death to handle respawn at checkpoint
        /// </summary>
        [HarmonyPatch(typeof(VertigoPlayer), "Die")]
        public static class VertigoPlayer_Die_StoryPatch
        {
            [HarmonyPostfix]
            public static void Postfix(VertigoPlayer __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (__instance != VertigoPlayer.instance) return;
                
                Plugin.Log.LogInfo("[STORY] Local player died");
                
                // If we're a client, we should respawn at the host's checkpoint
                if (!NetworkManager.Instance.IsHost)
                {
                    Plugin.Log.LogInfo("[STORY] Client died - will respawn at host checkpoint");
                    // The game's normal respawn will handle this, but we track it
                }
            }
        }

        /// <summary>
        /// Hook into SaveGameManager.DieAndRespawn to use synced checkpoint for clients
        /// </summary>
        [HarmonyPatch(typeof(SaveGameManager), "DieAndRespawn")]
        public static class SaveGameManager_DieAndRespawn_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(SaveGameManager __instance, ref System.Collections.IEnumerator __result)
            {
                // If we're a client, use the synced checkpoint instead of local save
                if (NetworkManager.Instance?.IsConnected ?? false)
                {
                    if (!NetworkManager.Instance.IsHost)
                    {
                        Plugin.Log.LogInfo("[STORY] Client respawning - using synced checkpoint");
                        __result = ClientRespawnCoroutine(__instance);
                        return false; // Skip original method
                    }
                }
                
                return true; // Run original method for host
            }
            
            private static System.Collections.IEnumerator ClientRespawnCoroutine(SaveGameManager manager)
            {
                // Wait a moment
                yield return new WaitForSecondsRealtime(0.3f);
                
                // Use StorySync to respawn at host's checkpoint
                StorySync.Instance?.RespawnAtLastCheckpoint();
                
                // Wait for scene to load
                while (LoadingScreen.loading)
                {
                    yield return null;
                }
                
                yield return null;
                
                // Show death screen
                if (PauseManager.instance != null)
                {
                    PauseManager.instance.Pause();
                    if (PauseMenu.instance != null)
                    {
                        PauseMenu.instance.ChangeActiveSection(PauseMenu.PauseMenuSections.DeathScreen);
                    }
                }
            }
        }

        /// <summary>
        /// Hook into PlayerTrigger.UpdateTrigger to sync trigger activations
        /// PlayerTrigger uses polling, not Unity's OnTriggerEnter
        /// </summary>
        [HarmonyPatch(typeof(PlayerTrigger), "UpdateTrigger")]
        public static class PlayerTrigger_UpdateTrigger_Patch
        {
            // Track previous state per-instance using instance ID
            private static System.Collections.Generic.Dictionary<int, bool> _previousStates = 
                new System.Collections.Generic.Dictionary<int, bool>();
            
            [HarmonyPostfix]
            public static void Postfix(PlayerTrigger __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                if (!NetworkManager.Instance.IsHost) return;
                
                int instanceId = __instance.GetInstanceID();
                bool isInside = __instance.playerInside;
                
                // Get previous state (default false if not tracked)
                bool wasInside = false;
                _previousStates.TryGetValue(instanceId, out wasInside);
                
                // Detect enter/exit transitions
                if (isInside && !wasInside)
                {
                    string triggerName = __instance.gameObject.name;
                    Plugin.Log.LogInfo($"[TRIGGER] PlayerTrigger entered: {triggerName}");
                }
                else if (!isInside && wasInside)
                {
                    string triggerName = __instance.gameObject.name;
                    Plugin.Log.LogInfo($"[TRIGGER] PlayerTrigger exited: {triggerName}");
                }
                
                // Update tracked state
                _previousStates[instanceId] = isInside;
            }
        }

        /// <summary>
        /// Hook into SaveTrigger to sync checkpoint saves
        /// </summary>
        [HarmonyPatch(typeof(SaveTrigger), "Save")]
        public static class SaveTrigger_Save_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(SaveTrigger __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                string triggerName = __instance.gameObject.name;
                Plugin.Log.LogInfo($"[CHECKPOINT] SaveTrigger activated: {triggerName}");
            }
        }

        /// <summary>
        /// Hook into GameManager.LoadLevel to sync level loads
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "LoadLevel", typeof(string), typeof(bool), typeof(bool))]
        public static class GameManager_LoadLevel_Patch
        {
            [HarmonyPrefix]
            public static void Prefix(string scene, bool showLoadingScreen, bool keepPlayerState)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                Plugin.Log.LogInfo($"[LEVEL] GameManager.LoadLevel: {scene} (loading={showLoadingScreen}, keepState={keepPlayerState})");
            }
        }

        /// <summary>
        /// Hook into StageUnlocker to sync stage unlocks
        /// </summary>
        [HarmonyPatch(typeof(StageUnlocker), "Unlock")]
        public static class StageUnlocker_Unlock_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(StageUnlocker __instance)
            {
                if (!NetworkManager.Instance?.IsConnected ?? true) return;
                
                string stageName = __instance.stage?.sceneName ?? "unknown";
                Plugin.Log.LogInfo($"[STAGE] Stage unlocked: {stageName}");
            }
        }
    }
}
