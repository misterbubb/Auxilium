using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using Vertigo2;
using Vertigo2.Persistence;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Syncs story progression between players:
    /// - Checkpoints/Saves (host-authoritative)
    /// - Progress strings (story flags)
    /// - Cutscenes/Timelines
    /// </summary>
    public class StorySync : MonoBehaviour
    {
        public static StorySync Instance { get; private set; }

        // Track active cutscenes
        private Dictionary<string, PlayableDirector> _activeTimelines = new();
        private HashSet<string> _syncedProgressStrings = new();
        
        // Checkpoint tracking
        private string _lastCheckpointScene = "";
        private Vector3 _lastCheckpointPosition;

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // Subscribe to save events
            SaveGameManager.OnGameSaved += OnGameSaved;
            SaveGameManager.OnGameLoaded += OnGameLoaded;
            
            // Subscribe to progress events
            ProgressManager.OnWillSaveProgress += OnWillSaveProgress;
            
            // Subscribe to scene changes
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SaveGameManager.OnGameSaved -= OnGameSaved;
            SaveGameManager.OnGameLoaded -= OnGameLoaded;
            ProgressManager.OnWillSaveProgress -= OnWillSaveProgress;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            // Clear timeline tracking on scene change
            _activeTimelines.Clear();
            
            // Scan for PlayableDirectors in the scene
            StartCoroutine(ScanForTimelinesDelayed());
        }

        private System.Collections.IEnumerator ScanForTimelinesDelayed()
        {
            yield return new WaitForSeconds(1f);
            ScanForTimelines();
        }

        private void Update()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;

            // Only host monitors timeline state
            if (NetworkManager.Instance.IsHost)
            {
                MonitorTimelines();
            }
        }

        #region Checkpoint/Save Sync

        /// <summary>
        /// Called when the game saves (host broadcasts checkpoint to clients)
        /// </summary>
        private void OnGameSaved(SaveGame game)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            // Only host broadcasts checkpoints
            if (!NetworkManager.Instance.IsHost)
                return;

            Plugin.Log.LogInfo($"[STORY] Game saved: {game.scene} - broadcasting checkpoint");

            // Get player position for checkpoint
            Vector3 playerPos = Vector3.zero;
            if (Vertigo2.Player.VertigoPlayer.instance != null)
            {
                playerPos = Vertigo2.Player.VertigoPlayer.instance.transform.position;
            }

            var packet = PacketBuilder.Checkpoint(game.scene, playerPos, game.autosave);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// Called when a save is loaded
        /// </summary>
        private void OnGameLoaded(SaveGame game)
        {
            Plugin.Log.LogInfo($"[STORY] Game loaded: {game.scene}");
        }

        /// <summary>
        /// Handle checkpoint packet from host (client only)
        /// </summary>
        public void HandleCheckpoint(string sceneName, Vector3 position, bool isAutosave)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            Plugin.Log.LogInfo($"[STORY] Received checkpoint from host: {sceneName} at {position}");

            // Store checkpoint info - we'll use this if we die
            _lastCheckpointScene = sceneName;
            _lastCheckpointPosition = position;

            // If we're in the same scene, just update our position reference
            // If we're in a different scene, the scene sync will handle loading
        }

        /// <summary>
        /// Called when client dies - respawn at host's last checkpoint
        /// </summary>
        public void RespawnAtLastCheckpoint()
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            if (string.IsNullOrEmpty(_lastCheckpointScene))
            {
                Plugin.Log.LogWarning("[STORY] No checkpoint to respawn at!");
                return;
            }

            Plugin.Log.LogInfo($"[STORY] Respawning at checkpoint: {_lastCheckpointScene}");

            // Load the checkpoint scene
            string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (currentScene != _lastCheckpointScene)
            {
                GameManager.LoadLevel(_lastCheckpointScene, true, true);
            }

            // Teleport to checkpoint position after scene loads
            StartCoroutine(TeleportToCheckpointDelayed());
        }

        private System.Collections.IEnumerator TeleportToCheckpointDelayed()
        {
            // Wait for scene to load
            yield return new WaitForSeconds(2f);

            if (Vertigo2.Player.VertigoPlayer.instance != null && _lastCheckpointPosition != Vector3.zero)
            {
                var cc = Vertigo2.Player.VertigoPlayer.instance.characterController;
                if (cc != null)
                {
                    cc.SetStandingPosition(_lastCheckpointPosition);
                    Plugin.Log.LogInfo($"[STORY] Teleported to checkpoint position: {_lastCheckpointPosition}");
                }
            }
        }

        #endregion

        #region Progress String Sync

        /// <summary>
        /// Called before progress is saved - sync any new progress strings
        /// </summary>
        private void OnWillSaveProgress()
        {
            // This is called on the local player before saving
            // We use this to detect new progress strings
        }

        /// <summary>
        /// Sync a progress string to all players
        /// Call this when setting important story flags
        /// </summary>
        public void SyncProgressString(string progressString)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            // Don't re-sync strings we already synced
            if (_syncedProgressStrings.Contains(progressString))
                return;

            _syncedProgressStrings.Add(progressString);

            Plugin.Log.LogInfo($"[STORY] Syncing progress string: {progressString}");

            var packet = PacketBuilder.ProgressString(progressString, true);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// Handle progress string from network
        /// </summary>
        public void HandleProgressString(string progressString, bool isSet)
        {
            Plugin.Log.LogInfo($"[STORY] Received progress string: {progressString} (set={isSet})");

            _syncedProgressStrings.Add(progressString);

            if (isSet)
            {
                if (!ProgressManager.HasString(progressString))
                {
                    ProgressManager.SetString(progressString);
                    Plugin.Log.LogInfo($"[STORY] Applied progress string: {progressString}");
                }
            }
            else
            {
                if (ProgressManager.HasString(progressString))
                {
                    ProgressManager.ClearString(progressString);
                    Plugin.Log.LogInfo($"[STORY] Cleared progress string: {progressString}");
                }
            }
        }

        /// <summary>
        /// Sync all current progress strings to a newly joined player
        /// </summary>
        public void SendAllProgressStrings()
        {
            // This would require access to the progress strings list
            // which is private in ProgressManager
            // For now, we sync strings as they're set during gameplay
        }

        #endregion

        #region Cutscene/Timeline Sync

        /// <summary>
        /// Scan scene for PlayableDirectors to monitor
        /// </summary>
        public void ScanForTimelines()
        {
            _activeTimelines.Clear();

            var directors = FindObjectsOfType<PlayableDirector>();
            foreach (var director in directors)
            {
                string id = GetTimelineId(director);
                _activeTimelines[id] = director;
            }

            Plugin.Log.LogInfo($"[STORY] Found {_activeTimelines.Count} timelines in scene");
        }

        /// <summary>
        /// Monitor timelines for play/stop events (host only)
        /// </summary>
        private void MonitorTimelines()
        {
            foreach (var kvp in _activeTimelines)
            {
                var director = kvp.Value;
                if (director == null) continue;

                // Check if timeline just started playing
                if (director.state == PlayState.Playing)
                {
                    // Track and sync
                    SyncTimelineState(kvp.Key, director, true);
                }
            }
        }

        /// <summary>
        /// Sync timeline play state to all players
        /// </summary>
        private Dictionary<string, bool> _lastTimelineStates = new();
        
        private void SyncTimelineState(string timelineId, PlayableDirector director, bool isPlaying)
        {
            // Only sync state changes
            bool lastState = _lastTimelineStates.TryGetValue(timelineId, out var state) && state;
            if (lastState == isPlaying) return;
            
            _lastTimelineStates[timelineId] = isPlaying;

            double time = director.time;
            double duration = director.duration;

            Plugin.Log.LogInfo($"[STORY] Timeline {timelineId} state changed: playing={isPlaying}, time={time:F2}/{duration:F2}");

            var packet = PacketBuilder.TimelineState(timelineId, isPlaying, (float)time, (float)duration);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// Handle timeline state from host (client only)
        /// </summary>
        public void HandleTimelineState(string timelineId, bool isPlaying, float time, float duration)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            Plugin.Log.LogInfo($"[STORY] Received timeline state: {timelineId} playing={isPlaying} time={time}");

            // Find the timeline
            if (_activeTimelines.TryGetValue(timelineId, out var director) && director != null)
            {
                if (isPlaying && director.state != PlayState.Playing)
                {
                    // Start playing and sync time
                    director.time = time;
                    director.Play();
                    Plugin.Log.LogInfo($"[STORY] Started timeline: {timelineId}");
                }
                else if (!isPlaying && director.state == PlayState.Playing)
                {
                    director.Stop();
                    Plugin.Log.LogInfo($"[STORY] Stopped timeline: {timelineId}");
                }
                else if (isPlaying)
                {
                    // Sync time if significantly different
                    if (Mathf.Abs((float)director.time - time) > 0.5f)
                    {
                        director.time = time;
                    }
                }
            }
            else
            {
                // Timeline not found - try to find it by searching
                TryFindAndPlayTimeline(timelineId, isPlaying, time);
            }
        }

        /// <summary>
        /// Try to find a timeline by ID and play it
        /// </summary>
        private void TryFindAndPlayTimeline(string timelineId, bool isPlaying, float time)
        {
            // Re-scan for timelines
            var directors = FindObjectsOfType<PlayableDirector>();
            foreach (var director in directors)
            {
                string id = GetTimelineId(director);
                if (id == timelineId)
                {
                    _activeTimelines[id] = director;
                    
                    if (isPlaying)
                    {
                        director.time = time;
                        director.Play();
                        Plugin.Log.LogInfo($"[STORY] Found and started timeline: {timelineId}");
                    }
                    return;
                }
            }

            Plugin.Log.LogWarning($"[STORY] Could not find timeline: {timelineId}");
        }

        /// <summary>
        /// Generate unique ID for a timeline
        /// </summary>
        private string GetTimelineId(PlayableDirector director)
        {
            // Use hierarchy path + playable asset name for uniqueness
            string path = GetGameObjectPath(director.gameObject);
            string assetName = director.playableAsset?.name ?? "unknown";
            return $"{path}_{assetName}";
        }

        private string GetGameObjectPath(GameObject obj)
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

        #endregion

        /// <summary>
        /// Clear all synced data (on disconnect)
        /// </summary>
        public void ClearSyncData()
        {
            _syncedProgressStrings.Clear();
            _activeTimelines.Clear();
            _lastTimelineStates.Clear();
            _lastCheckpointScene = "";
            _lastCheckpointPosition = Vector3.zero;
        }
    }
}
