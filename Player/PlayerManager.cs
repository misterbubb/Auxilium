using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vertigo2;
using Vertigo2.Player;
using Vertigo2Multiplayer.UI;

namespace Vertigo2Multiplayer
{
    public class PlayerManager : MonoBehaviour
    {
        public static PlayerManager Instance { get; private set; }

        #region Constants

        // Sync rates
        private const float DEFAULT_SYNC_RATE = 30f; // Updates per second (30Hz for smooth VR)
        private const float HEARTBEAT_INTERVAL = 5f;
        
        // Timing
        private const float SCENE_READY_DELAY = 2f;
        private const float TELEPORT_DELAY = 2f;
        private const float TELEPORT_RETRY_DELAY = 1f;
        private const float SCENE_SYNC_TIMEOUT = 30f;
        private const float HOST_POSITION_SEND_DELAY = 1f;
        private const float HOST_POSITION_REQUEST_DELAY = 1.5f;
        
        // Spawn offset
        private const float SPAWN_OFFSET_RANGE = 1f;
        
        // Logging intervals
        private const int SYNC_LOG_INTERVAL = 300;
        private const int POSITION_LOG_INTERVAL = 150;
        private const int UPDATE_LOG_INTERVAL = 60;
        private const int UPDATE_WARNING_INTERVAL = 30;
        
        // Hand pose/weapon send intervals (in sync ticks)
        private const int HAND_POSE_SEND_INTERVAL = 1;
        private const int WEAPON_SEND_INTERVAL = 10;

        #endregion

        // Remote player representations
        private Dictionary<CSteamID, RemotePlayer> _remotePlayers = new();
        
        // Local player sync settings
        [SerializeField] private float _syncRate = DEFAULT_SYNC_RATE;
        private float _syncTimer;
        
        // Heartbeat
        private float _heartbeatTimer;
        
        // Current scene tracking
        public string CurrentScene { get; private set; }

        // References to local VR components (captured via Harmony patches)
        public Transform LocalHead { get; set; }
        public Transform LocalLeftHand { get; set; }
        public Transform LocalRightHand { get; set; }
        public Transform LocalPlayerRoot { get; set; }
        public VertigoPlayer LocalPlayer { get; set; }
        public VertigoCharacterController LocalCharacterController { get; set; }
        
        // Current weapon tracking
        public string CurrentWeaponId { get; set; }

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
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.OnPlayerJoined += OnPlayerJoined;
                NetworkManager.Instance.OnPlayerLeft += OnPlayerLeft;
            }
            
            // Track scene changes
            SceneManager.sceneLoaded += OnSceneLoaded;
            CurrentScene = SceneManager.GetActiveScene().name;
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            string oldScene = CurrentScene;
            CurrentScene = scene.name;
            
            Plugin.Log.LogInfo($"[SCENE] Scene loaded: {oldScene} -> {CurrentScene}");
            
            // Clear local player references - they'll be re-captured when new VertigoPlayer spawns
            LocalHead = null;
            LocalLeftHand = null;
            LocalRightHand = null;
            LocalPlayerRoot = null;
            LocalPlayer = null;
            LocalCharacterController = null;
            
            // Check if we have a pending teleport for this scene
            if (!string.IsNullOrEmpty(_pendingTeleportScene) && _pendingTeleportScene == CurrentScene)
            {
                Plugin.Log.LogInfo($"[JOIN] Scene loaded, executing pending teleport to {_pendingTeleportPosition}");
                StartCoroutine(TeleportToPositionDelayed(_pendingTeleportPosition, _pendingTeleportRotation));
                _pendingTeleportScene = null;
            }
            
            // Notify other players we changed scenes
            if (NetworkManager.Instance?.IsConnected ?? false)
            {
                var packet = PacketBuilder.SceneChange(scene.name);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
                Plugin.Log.LogInfo($"[SCENE] Sent SceneChange packet for {scene.name}");
                
                // Send scene ready after a short delay
                StartCoroutine(SendSceneReadyDelayed(scene.name));
                
                // If we're the host, wait for all players to be ready
                if (NetworkManager.Instance.IsHost && _remotePlayers.Count > 0)
                {
                    StartCoroutine(WaitForPlayersCoroutine());
                }
            }
            
            // After scene load, make sure all remote players in the same scene are visible
            foreach (var kvp in _remotePlayers)
            {
                var player = kvp.Value;
                bool sameScene = player.CurrentScene == CurrentScene || string.IsNullOrEmpty(player.CurrentScene);
                player.SetVisible(sameScene);
                Plugin.Log.LogInfo($"[SCENE] Remote player {player.PlayerName} in scene {player.CurrentScene}, visible={sameScene}");
            }
        }
        
        /// <summary>
        /// Teleport to a position after a delay (to let scene initialize)
        /// </summary>
        private System.Collections.IEnumerator TeleportToPositionDelayed(Vector3 position, float yRotation)
        {
            // Wait for scene to fully initialize and player to spawn
            yield return new WaitForSeconds(TELEPORT_DELAY);
            
            // Try to find player if we don't have reference
            if (LocalCharacterController == null)
            {
                TryCaptureLocalPlayer();
            }
            
            // Wait a bit more if still no player
            if (LocalCharacterController == null)
            {
                yield return new WaitForSeconds(TELEPORT_RETRY_DELAY);
                TryCaptureLocalPlayer();
            }
            
            if (LocalCharacterController != null)
            {
                // Offset position slightly so players don't overlap
                Vector3 offset = new Vector3(
                    UnityEngine.Random.Range(-SPAWN_OFFSET_RANGE, SPAWN_OFFSET_RANGE), 
                    0, 
                    UnityEngine.Random.Range(-SPAWN_OFFSET_RANGE, SPAWN_OFFSET_RANGE));
                Vector3 teleportPos = position + offset;
                
                LocalCharacterController.SetStandingPosition(teleportPos);
                Plugin.Log.LogInfo($"[JOIN] Teleported to position: {teleportPos} (host was at {position})");
                
                // Show notification
                NotificationUI.Instance?.ShowNotification("Teleported to host location", 3f);
            }
            else
            {
                Plugin.Log.LogWarning("[JOIN] Could not teleport - no character controller found");
            }
        }
        
        private System.Collections.IEnumerator SendSceneReadyDelayed(string sceneName)
        {
            // Wait for scene to fully initialize
            yield return new WaitForSeconds(SCENE_READY_DELAY);
            
            if (NetworkManager.Instance?.IsConnected ?? false)
            {
                var packet = PacketBuilder.SceneReady(sceneName);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
                Plugin.Log.LogInfo($"[SCENE] Sent SceneReady for {sceneName}");
            }
        }
        
        private System.Collections.IEnumerator WaitForPlayersCoroutine()
        {
            Plugin.Log.LogInfo("[SCENE SYNC] Host waiting for all players to load scene...");
            
            // Pause the game while waiting
            float originalTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            
            float elapsed = 0f;
            
            while (elapsed < SCENE_SYNC_TIMEOUT)
            {
                // Check if all players are in the same scene
                bool allReady = true;
                foreach (var kvp in _remotePlayers)
                {
                    if (kvp.Value.CurrentScene != CurrentScene || !kvp.Value.IsSceneReady)
                    {
                        allReady = false;
                        break;
                    }
                }
                
                if (allReady)
                {
                    Plugin.Log.LogInfo("[SCENE SYNC] All players ready!");
                    break;
                }
                
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
            
            if (elapsed >= SCENE_SYNC_TIMEOUT)
            {
                Plugin.Log.LogWarning("[SCENE SYNC] Timeout waiting for players, continuing anyway");
            }
            
            // Resume the game
            Time.timeScale = originalTimeScale;
            Plugin.Log.LogInfo("[SCENE SYNC] Game resumed");
        }

        private void Update()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;

            // Try to capture local player transforms if we don't have them
            if (LocalHead == null || LocalPlayerRoot == null)
            {
                TryCaptureLocalPlayer();
            }

            _syncTimer += Time.deltaTime;
            if (_syncTimer >= 1f / _syncRate)
            {
                _syncTimer = 0f;
                SendLocalPlayerState();
            }
            
            // Send heartbeat periodically
            _heartbeatTimer += Time.deltaTime;
            if (_heartbeatTimer >= HEARTBEAT_INTERVAL)
            {
                _heartbeatTimer = 0f;
                var heartbeat = PacketBuilder.Heartbeat();
                NetworkManager.Instance.Broadcast(heartbeat, reliable: false);
            }
        }
        
        private void LateUpdate()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            
            // Apply platform deltas to remote players (so they move with platforms)
            foreach (var kvp in _remotePlayers)
            {
                kvp.Value.ApplyPlatformDelta();
            }
            
            // Update platform last positions for next frame's delta calculation
            WorldSync.Instance?.UpdatePlatformPositions();
        }
        
        /// <summary>
        /// Try to find and capture the local player's VR transforms
        /// </summary>
        private void TryCaptureLocalPlayer()
        {
            // Try to find VertigoPlayer.instance
            if (VertigoPlayer.instance != null)
            {
                var player = VertigoPlayer.instance;
                LocalPlayer = player;
                LocalPlayerRoot = player.transform;
                LocalHead = player.headCamera?.transform;
                LocalLeftHand = player.LController?.transform;
                LocalRightHand = player.RController?.transform;
                
                if (LocalHead != null && _captureLogCounter == 0)
                {
                    Plugin.Log.LogInfo($"[CAPTURE] ========== LOCAL PLAYER CAPTURED ==========");
                    Plugin.Log.LogInfo($"[CAPTURE] LocalPlayer: {LocalPlayer.name}");
                    Plugin.Log.LogInfo($"[CAPTURE] LocalPlayerRoot: {LocalPlayerRoot?.name ?? "NULL"} at {LocalPlayerRoot?.position}");
                    Plugin.Log.LogInfo($"[CAPTURE] LocalHead: {LocalHead?.name ?? "NULL"} at {LocalHead?.position}");
                    Plugin.Log.LogInfo($"[CAPTURE] LocalLeftHand: {LocalLeftHand?.name ?? "NULL"} at {LocalLeftHand?.position}");
                    Plugin.Log.LogInfo($"[CAPTURE] LocalRightHand: {LocalRightHand?.name ?? "NULL"} at {LocalRightHand?.position}");
                    Plugin.Log.LogInfo($"[CAPTURE] ============================================");
                    _captureLogCounter++;
                }
            }
            
            // Also try to find character controller
            if (LocalCharacterController == null)
            {
                var cc = Object.FindObjectOfType<VertigoCharacterController>();
                if (cc != null)
                {
                    LocalCharacterController = cc;
                    Plugin.Log.LogInfo($"[CAPTURE] Found VertigoCharacterController: {cc.name}");
                }
            }
        }
        
        private int _captureLogCounter = 0;

        private void SendLocalPlayerState()
        {
            // Skip if no transforms captured (normal in menu)
            if (LocalPlayerRoot == null && LocalHead == null) 
            {
                // Log occasionally to debug
                if (_syncLogCounter++ % SYNC_LOG_INTERVAL == 0)
                {
                    Plugin.Log.LogWarning("[SYNC] SendLocalPlayerState: No local transforms captured yet");
                }
                return;
            }

            // Use combined packet for efficiency (1 packet instead of 4)
            Vector3 rootPos = LocalPlayerRoot?.position ?? Vector3.zero;
            Vector3 headPos = LocalHead?.position ?? rootPos;
            Quaternion headRot = LocalHead?.rotation ?? Quaternion.identity;
            var bodyAnimator = LocalPlayer?.GetComponentInChildren<BodyAnimator>(true);
            Transform leftHandSource = bodyAnimator?.handLeft?.handTarget ?? LocalLeftHand;
            Transform rightHandSource = bodyAnimator?.handRight?.handTarget ?? LocalRightHand;
            Vector3 leftHandPos = leftHandSource?.position ?? headPos;
            Quaternion leftHandRot = leftHandSource?.rotation ?? Quaternion.identity;
            Vector3 rightHandPos = rightHandSource?.position ?? headPos;
            Quaternion rightHandRot = rightHandSource?.rotation ?? Quaternion.identity;
            
            // Debug: Log position updates periodically (every 5 seconds at 30Hz = every 150 frames)
            if (_syncLogCounter % POSITION_LOG_INTERVAL == 0)
            {
                Plugin.Log.LogInfo($"[SYNC] ===== SENDING LOCAL TRANSFORMS (frame {_syncLogCounter}) =====");
                Plugin.Log.LogInfo($"[SYNC] Root: {rootPos}");
                Plugin.Log.LogInfo($"[SYNC] Head: pos={headPos}, rot={headRot.eulerAngles}");
                Plugin.Log.LogInfo($"[SYNC] LHand target: pos={leftHandPos}, rot={leftHandRot.eulerAngles}");
                Plugin.Log.LogInfo($"[SYNC] RHand target: pos={rightHandPos}, rot={rightHandRot.eulerAngles}");
                if (LocalLeftHand != null && leftHandSource != null)
                    Plugin.Log.LogInfo($"[SYNC] Left controller-to-rig rotation offset: {Quaternion.Angle(LocalLeftHand.rotation, leftHandSource.rotation):F1} deg");
                if (LocalRightHand != null && rightHandSource != null)
                    Plugin.Log.LogInfo($"[SYNC] Right controller-to-rig rotation offset: {Quaternion.Angle(LocalRightHand.rotation, rightHandSource.rotation):F1} deg");
                Plugin.Log.LogInfo($"[SYNC] LocalLeftHand null? {LocalLeftHand == null}, LocalRightHand null? {LocalRightHand == null}");
                Plugin.Log.LogInfo($"[SYNC] Connected players: {NetworkManager.Instance?.ConnectedPlayers?.Count ?? 0}");
            }
            
            var packet = PacketBuilder.FullVRTransform(
                rootPos, headPos, headRot, 
                leftHandPos, leftHandRot, 
                rightHandPos, rightHandRot);
            NetworkManager.Instance.Broadcast(packet, reliable: false);
            
            // Send final finger bones at the same 30 Hz rate as the hand rig.
            if (_syncLogCounter % HAND_POSE_SEND_INTERVAL == 0)
            {
                SendHandPose();
            }
            
            // Send weapon state (less frequently)
            if (_syncLogCounter % WEAPON_SEND_INTERVAL == 0)
            {
                SendWeaponState();
                WeaponSync.Instance?.SyncLocalAmmoState();
            }
            
            _syncLogCounter++;
        }
        
        /// <summary>Send the game's final finger bones after SteamVR and weapon posing.</summary>
        private void SendHandPose()
        {
            if (LocalPlayer == null) return;

            try
            {
                var bodyAnimator = LocalPlayer.GetComponentInChildren<BodyAnimator>(true);
                var leftBones = bodyAnimator?.handLeft?.animator?.bones;
                var rightBones = bodyAnimator?.handRight?.animator?.bones;
                int boneCount = Vertigo2.Interaction.HandPose.numBones;
                if (leftBones == null || rightBones == null ||
                    leftBones.Length < boneCount || rightBones.Length < boneCount) return;

                if (_gripLogCounter++ % 60 == 0)
                    Plugin.Log.LogInfo("[HAND POSE SEND] Sending final local finger transforms for both hands");

                NetworkManager.Instance.Broadcast(PacketBuilder.HandPose(leftBones, rightBones), reliable: false);
            }
            catch (System.Exception ex)
            {
                if (_gripLogCounter++ % 300 == 0)
                {
                    Plugin.Log.LogWarning($"[HAND POSE SEND] Failed to read the local hand rig: {ex.Message}");
                }
            }
        }
        
        private int _gripLogCounter = 0;
        
        /// <summary>
        /// Send current equipped weapon state
        /// </summary>
        private void SendWeaponState(bool force = false, CSteamID? targetPlayer = null)
        {
            if (LocalPlayer == null) return;
            
            string leftWeapon = "none";
            string rightWeapon = "none";
            
            try
            {
                var equippables = LocalPlayer.equippablesManager;
                if (equippables != null)
                {
                    var leftEquip = equippables.GetHand(Valve.VR.SteamVR_Input_Sources.LeftHand)?.currentEquippable;
                    var rightEquip = equippables.GetHand(Valve.VR.SteamVR_Input_Sources.RightHand)?.currentEquippable;
                    
                    if (leftEquip?.profile != null)
                        leftWeapon = leftEquip.profile.name;
                    if (rightEquip?.profile != null)
                        rightWeapon = rightEquip.profile.name;
                }
            }
            catch
            {
                // Equippables not available
            }
            
            // Only send if changed
            if (force || leftWeapon != _lastLeftWeapon || rightWeapon != _lastRightWeapon)
            {
                _lastLeftWeapon = leftWeapon;
                _lastRightWeapon = rightWeapon;
                
                var packet = PacketBuilder.WeaponEquip(leftWeapon, rightWeapon);
                if (targetPlayer.HasValue)
                    NetworkManager.Instance.SendTo(targetPlayer.Value, packet, reliable: true);
                else
                    NetworkManager.Instance.Broadcast(packet, reliable: true);
                
                string destination = targetPlayer.HasValue ? $" to {targetPlayer.Value}" : " to lobby";
                Plugin.Log.LogInfo($"[WEAPON] Sending weapon state{destination}: L={leftWeapon}, R={rightWeapon}");
                if (!targetPlayer.HasValue)
                    WeaponSync.Instance?.SendCurrentAmmoState();
            }
        }
        
        public void SendCurrentWeaponState()
        {
            SendWeaponState(force: true);
        }

        private string _lastLeftWeapon = "none";
        private string _lastRightWeapon = "none";
        
        private int _syncLogCounter = 0;

        #region Remote Player Management

        private void OnPlayerJoined(CSteamID steamId)
        {
            Plugin.Log.LogInfo($"[JOIN] ========== PLAYER JOINED ==========");
            Plugin.Log.LogInfo($"[JOIN] Steam ID: {steamId}");
            Plugin.Log.LogInfo($"[JOIN] Name: {SteamFriends.GetFriendPersonaName(steamId)}");
            Plugin.Log.LogInfo($"[JOIN] Already exists? {_remotePlayers.ContainsKey(steamId)}");
            
            if (_remotePlayers.ContainsKey(steamId)) 
            {
                Plugin.Log.LogInfo($"[JOIN] Remote player already exists for {steamId}, skipping creation");
                return;
            }

            var playerObj = new GameObject($"RemotePlayer_{steamId}");
            DontDestroyOnLoad(playerObj);
            var remotePlayer = playerObj.AddComponent<RemotePlayer>();
            remotePlayer.Initialize(steamId);
            
            _remotePlayers[steamId] = remotePlayer;
            
            // Check if this is the host
            bool isHost = false;
            if (NetworkManager.Instance != null && NetworkManager.Instance.CurrentLobby.IsValid())
            {
                CSteamID hostId = SteamMatchmaking.GetLobbyOwner(NetworkManager.Instance.CurrentLobby);
                isHost = (steamId == hostId);
            }
            
            Plugin.Log.LogInfo($"[JOIN] Created RemotePlayer GameObject: {playerObj.name}");
            Plugin.Log.LogInfo($"[JOIN] Is host? {isHost}");
            Plugin.Log.LogInfo($"[JOIN] Total remote players: {_remotePlayers.Count}");
            Plugin.Log.LogInfo($"[JOIN] =====================================");
            
            // Register with EnemySync so enemies can target this player (host only)
            if (NetworkManager.Instance.IsHost)
            {
                EnemySync.Instance?.RegisterRemotePlayer(steamId, remotePlayer);
                
                // HOST: Send our position to the newly joined player so they can teleport to us
                StartCoroutine(SendHostPositionDelayed(steamId));
            }
            else
            {
                // CLIENT: Request host position so we can teleport to them
                StartCoroutine(RequestHostPositionDelayed());
            }
            
            // Send handshake with our info
            var handshake = PacketBuilder.Handshake(SteamFriends.GetPersonaName());
            NetworkManager.Instance.Broadcast(handshake, reliable: true);
            
            // Send our character selection
            BroadcastCharacterSelection();
            
            // Send our current scene
            var scenePacket = PacketBuilder.SceneChange(CurrentScene);
            NetworkManager.Instance.Broadcast(scenePacket, reliable: true);
            
            // Send current position immediately so they see us right away
            SendLocalPlayerState();

            // Send the current loadout for this peer. The periodic change detector
            // won't resend an unchanged loadout to someone who just joined.
            StartCoroutine(SendWeaponStateAfterJoin(steamId));
        }

        private System.Collections.IEnumerator SendWeaponStateAfterJoin(CSteamID joiningPlayer)
        {
            const int maxAttempts = 20;
            const float retryDelay = 0.25f;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (NetworkManager.Instance?.IsConnected == true && LocalPlayer?.equippablesManager != null)
                {
                    // Hosts send the snapshot directly to the new peer. Clients
                    // send theirs to the host, which relays it to the lobby.
                    if (NetworkManager.Instance.IsHost)
                    {
                        SendWeaponState(force: true, targetPlayer: joiningPlayer);
                        WeaponSync.Instance?.SendCurrentAmmoState(joiningPlayer);
                        NetworkManager.Instance.Broadcast(PacketBuilder.RequestWeaponState(), reliable: true);
                    }
                    else
                    {
                        SendWeaponState(force: true);
                    }
                    yield break;
                }

                yield return new WaitForSecondsRealtime(retryDelay);
            }

            Plugin.Log.LogWarning("[WEAPON] Skipped join snapshot: local equippables were not ready");
        }
        
        /// <summary>
        /// HOST: Send position to newly joined player after a short delay
        /// </summary>
        private System.Collections.IEnumerator SendHostPositionDelayed(CSteamID targetPlayer)
        {
            // Wait for connection to stabilize
            yield return new WaitForSeconds(HOST_POSITION_SEND_DELAY);
            
            SendHostPositionToPlayer(targetPlayer, CurrentScene);
        }
        
        /// <summary>
        /// CLIENT: Request host position after joining
        /// </summary>
        private System.Collections.IEnumerator RequestHostPositionDelayed()
        {
            // Wait for connection to stabilize
            yield return new WaitForSeconds(HOST_POSITION_REQUEST_DELAY);
            
            if (NetworkManager.Instance?.IsConnected ?? false)
            {
                Plugin.Log.LogInfo($"[JOIN] Requesting host position for scene {CurrentScene}");
                var packet = PacketBuilder.RequestHostPosition(CurrentScene);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }
        }
        
        /// <summary>
        /// HOST: Send our position to a specific player
        /// </summary>
        public void SendHostPositionToPlayer(CSteamID targetPlayer, string requestedScene)
        {
            if (!NetworkManager.Instance?.IsHost ?? true) return;
            
            Vector3 hostPos = Vector3.zero;
            float hostRotY = 0f;
            
            if (VertigoPlayer.instance != null)
            {
                hostPos = VertigoPlayer.instance.transform.position;
                hostRotY = VertigoPlayer.instance.transform.eulerAngles.y;
            }
            else if (LocalPlayerRoot != null)
            {
                hostPos = LocalPlayerRoot.position;
                hostRotY = LocalPlayerRoot.eulerAngles.y;
            }
            
            Plugin.Log.LogInfo($"[JOIN] Sending host position to {SteamFriends.GetFriendPersonaName(targetPlayer)}: scene={CurrentScene}, pos={hostPos}");
            
            var packet = PacketBuilder.HostPositionResponse(CurrentScene, hostPos, hostRotY);
            NetworkManager.Instance.SendTo(targetPlayer, packet, reliable: true);
        }
        
        /// <summary>
        /// CLIENT: Teleport to host position after receiving it
        /// </summary>
        public void TeleportToHostPosition(string sceneName, Vector3 position, float yRotation)
        {
            if (NetworkManager.Instance?.IsHost ?? true) return;
            
            Plugin.Log.LogInfo($"[JOIN] Teleporting to host position: scene={sceneName}, pos={position}");
            
            // If we're in a different scene, load it first
            if (sceneName != CurrentScene)
            {
                Plugin.Log.LogInfo($"[JOIN] Need to load scene {sceneName} first (current: {CurrentScene})");
                _pendingTeleportPosition = position;
                _pendingTeleportRotation = yRotation;
                _pendingTeleportScene = sceneName;
                FollowHostToScene(sceneName);
                return;
            }
            
            // Same scene - teleport immediately
            StartCoroutine(TeleportToPositionDelayed(position, yRotation));
        }
        
        /// <summary>
        /// CLIENT: Request teleport to host (used after respawn)
        /// </summary>
        public void RequestTeleportToHost()
        {
            if (NetworkManager.Instance?.IsHost ?? true) return;
            
            Plugin.Log.LogInfo("[RESPAWN] Requesting teleport to host after respawn");
            StartCoroutine(RequestHostPositionDelayed());
        }
        
        // Pending teleport data for after scene load
        private Vector3 _pendingTeleportPosition;
        private float _pendingTeleportRotation;
        private string _pendingTeleportScene;

        private void OnPlayerLeft(CSteamID steamId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                // Unregister from EnemySync
                EnemySync.Instance?.UnregisterRemotePlayer(steamId);
                
                Destroy(player.gameObject);
                _remotePlayers.Remove(steamId);
                Plugin.Log.LogInfo($"Removed remote player {steamId}");
            }
        }

        #endregion

        #region Remote Player Updates (called from PacketHandler)

        public void UpdateRemotePlayerPosition(CSteamID steamId, float x, float y, float z)
        {
            // Validate Steam ID
            if (!steamId.IsValid() || steamId == CSteamID.Nil)
            {
                if (_updateLogCounter++ % UPDATE_LOG_INTERVAL == 0)
                {
                    Plugin.Log.LogWarning($"[UPDATE] UpdateRemotePlayerPosition called with invalid Steam ID: {steamId}");
                }
                return;
            }
            
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.SetTargetPosition(new Vector3(x, y, z));
                
                // Debug: Log position updates periodically
                if (_updateLogCounter++ % UPDATE_LOG_INTERVAL == 0)
                {
                    Plugin.Log.LogInfo($"[UPDATE] Set position for {player.PlayerName} ({steamId}) to ({x:F2},{y:F2},{z:F2})");
                }
            }
            else
            {
                // Log more frequently to debug - this is the likely issue
                if (_updateLogCounter++ % UPDATE_WARNING_INTERVAL == 0)
                {
                    string playerName = SteamFriends.GetFriendPersonaName(steamId);
                    var knownPlayers = new System.Collections.Generic.List<string>();
                    foreach (var kvp in _remotePlayers)
                    {
                        knownPlayers.Add($"{kvp.Value.PlayerName}({kvp.Key})");
                    }
                    Plugin.Log.LogWarning($"[UPDATE] No remote player found for {playerName} ({steamId}). Known players: [{string.Join(", ", knownPlayers)}]");
                    
                    // Try to create the remote player if they're in our connected players list
                    if (NetworkManager.Instance?.ConnectedPlayers.Contains(steamId) ?? false)
                    {
                        Plugin.Log.LogInfo($"[UPDATE] Player {playerName} is connected but not in _remotePlayers - creating now");
                        OnPlayerJoined(steamId);
                        
                        // Try again after creating
                        if (_remotePlayers.TryGetValue(steamId, out player))
                        {
                            player.SetTargetPosition(new Vector3(x, y, z));
                        }
                    }
                }
            }
        }
        
        private int _updateLogCounter = 0;

        public void UpdateRemotePlayerHead(CSteamID steamId, float px, float py, float pz, float rx, float ry, float rz, float rw)
        {
            // Validate Steam ID
            if (!steamId.IsValid() || steamId == CSteamID.Nil)
            {
                return;
            }
            
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.SetTargetHeadTransform(
                    new Vector3(px, py, pz),
                    new Quaternion(rx, ry, rz, rw));
            }
            else if (_updateLogCounter % UPDATE_LOG_INTERVAL == 0)
            {
                Plugin.Log.LogWarning($"[UPDATE] UpdateRemotePlayerHead: No player for {SteamFriends.GetFriendPersonaName(steamId)} ({steamId})");
            }
        }

        public void UpdateRemotePlayerLeftHand(CSteamID steamId, float px, float py, float pz, float rx, float ry, float rz, float rw)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.SetTargetLeftHandTransform(
                    new Vector3(px, py, pz),
                    new Quaternion(rx, ry, rz, rw));
            }
        }

        public void UpdateRemotePlayerRightHand(CSteamID steamId, float px, float py, float pz, float rx, float ry, float rz, float rw)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.SetTargetRightHandTransform(
                    new Vector3(px, py, pz),
                    new Quaternion(rx, ry, rz, rw));
            }
        }

        public void UpdateRemotePlayerHealth(CSteamID steamId, float currentHealth, float maxHealth)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.SetHealth(currentHealth, maxHealth);
            }
        }

        public void HandleRemotePlayerDeath(CSteamID steamId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.OnDeath();
                NotificationUI.Instance?.ShowCustom("PLAYER DIED", player.PlayerName, new Color(0.9f, 0.3f, 0.3f));
                Plugin.Log.LogInfo($"[DEATH] Remote player {player.PlayerName} died");
            }
        }

        public void HandleRemotePlayerRespawn(CSteamID steamId, Vector3 position)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.OnRespawn(position);
                NotificationUI.Instance?.ShowCustom("PLAYER RESPAWNED", player.PlayerName, new Color(0.3f, 0.9f, 0.3f));
                Plugin.Log.LogInfo($"[RESPAWN] Remote player {player.PlayerName} respawned at {position}");
            }
        }

        public void UpdateRemotePlayerWeapon(CSteamID steamId, string weaponId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                Plugin.Log.LogInfo($"[WEAPON] UpdateRemotePlayerWeapon: {player.PlayerName} switched to {weaponId}");
                player.SetWeapon(weaponId);
            }
            else
            {
                Plugin.Log.LogWarning($"[WEAPON] UpdateRemotePlayerWeapon: No remote player found for {steamId}");
            }
        }

        public bool IsRemoteWeaponEquipped(CSteamID steamId, string weaponId)
        {
            return _remotePlayers.TryGetValue(steamId, out var player) &&
                   player.HasEquippedWeapon(weaponId);
        }

        public void UpdateRemoteWeaponAmmo(CSteamID steamId, string weaponId, int rounds, int capacity, bool reloaded)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.UpdateWeaponAmmo(weaponId, rounds, capacity);
                Plugin.Log.LogInfo($"[AMMO] {player.PlayerName} {weaponId}: {rounds}/{capacity}");
                if (reloaded)
                    Plugin.Log.LogInfo($"[RELOAD] {player.PlayerName} reloaded {weaponId}: {rounds}/{capacity}");
            }
        }

        public void HandleRemotePlayerFire(CSteamID steamId, string weaponId, Vector3 position, Vector3 direction)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                Plugin.Log.LogInfo($"[WEAPON] HandleRemotePlayerFire: {player.PlayerName} fired {weaponId}");
                player.OnWeaponFire(weaponId, position, direction);
            }
            else
            {
                Plugin.Log.LogWarning($"[WEAPON] HandleRemotePlayerFire: No remote player found for {steamId}");
            }
        }

        public void UpdateRemotePlayerScene(CSteamID steamId, string sceneName)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                string oldScene = player.CurrentScene;
                player.SetScene(sceneName);
                
                // Show/hide player based on if they're in the same scene
                bool sameScene = sceneName == CurrentScene;
                player.SetVisible(sameScene);
                
                Plugin.Log.LogInfo($"[SCENE] Player {player.PlayerName} scene: {oldScene} -> {sceneName}. Same as us ({CurrentScene})? {sameScene}");
            }
            else
            {
                Plugin.Log.LogWarning($"[SCENE] UpdateRemotePlayerScene: No remote player found for {steamId}");
            }
            
            // HOST SCENE SYNC: If this scene change is from the host, follow them!
            if (NetworkManager.Instance != null && !NetworkManager.Instance.IsHost)
            {
                // Check if the sender is the host
                CSteamID hostId = SteamMatchmaking.GetLobbyOwner(NetworkManager.Instance.CurrentLobby);
                if (steamId == hostId && sceneName != CurrentScene)
                {
                    Plugin.Log.LogInfo($"[SCENE SYNC] Host changed to scene: {sceneName}. Following...");
                    FollowHostToScene(sceneName);
                }
            }
        }
        
        /// <summary>
        /// Follow the host to a new scene
        /// </summary>
        private void FollowHostToScene(string sceneName)
        {
            Plugin.Log.LogInfo($"[SCENE SYNC] Loading scene: {sceneName}");
            
            try
            {
                // Use the game's level loading system
                GameManager.LoadLevel(sceneName, true, true);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[SCENE SYNC] Failed to load scene via GameManager: {ex.Message}");
                // Fallback to direct scene load
                try
                {
                    SceneManager.LoadScene(sceneName);
                }
                catch
                {
                    Plugin.Log.LogError($"[SCENE SYNC] Direct scene load also failed");
                }
            }
        }
        
        /// <summary>
        /// Get the host's current scene (for clients to check)
        /// </summary>
        public string GetHostScene()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return null;
                
            CSteamID hostId = SteamMatchmaking.GetLobbyOwner(NetworkManager.Instance.CurrentLobby);
            if (_remotePlayers.TryGetValue(hostId, out var hostPlayer))
            {
                return hostPlayer.CurrentScene;
            }
            return null;
        }
        
        /// <summary>
        /// Check if we're in sync with the host
        /// </summary>
        public bool IsInSyncWithHost()
        {
            string hostScene = GetHostScene();
            return hostScene == null || hostScene == CurrentScene;
        }

        public void UpdateRemotePlayerCharacter(CSteamID steamId, string characterId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.SetCharacter(characterId);
            }
        }
        
        /// <summary>
        /// Send our current character selection to all players
        /// </summary>
        public void BroadcastCharacterSelection()
        {
            if (NetworkManager.Instance?.IsConnected ?? false)
            {
                string characterId = GameManager.CurrentCharacter?.id ?? "default";
                var packet = PacketBuilder.CharacterSelect(characterId);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }
        }

        /// <summary>
        /// Apply the final hand pose received from another player's local hand animators.
        /// </summary>
        public void UpdateRemotePlayerGrip(CSteamID steamId,
            float lThumb, float lIndex, float lMiddle, float lRing, float lPinky,
            float rThumb, float rIndex, float rMiddle, float rRing, float rPinky)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
                player.SetGrip(lThumb, lIndex, lMiddle, lRing, lPinky,
                    rThumb, rIndex, rMiddle, rRing, rPinky);
        }

        public void UpdateRemotePlayerHandPose(CSteamID steamId,
            RemotePlayer.FingerBonePose[] left, RemotePlayer.FingerBonePose[] right)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
                player.SetHandPose(left, right);
        }

        /// <summary>
        /// Update remote player's equipped weapons
        /// </summary>
        public void UpdateRemotePlayerWeapons(CSteamID steamId, string leftWeaponId, string rightWeaponId)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                Plugin.Log.LogInfo($"[WEAPON ROUTE] Routing weapons to {player.PlayerName}: L={leftWeaponId}, R={rightWeaponId}");
                player.SetEquippedWeapons(leftWeaponId, rightWeaponId);
            }
            else
            {
                Plugin.Log.LogWarning($"[WEAPON ROUTE] No remote player found for {SteamFriends.GetFriendPersonaName(steamId)} ({steamId})");
            }
        }

        /// <summary>
        /// Handle when a remote player finishes loading a scene
        /// </summary>
        public void HandleRemotePlayerSceneReady(CSteamID steamId, string sceneName)
        {
            if (_remotePlayers.TryGetValue(steamId, out var player))
            {
                player.SetSceneReady(sceneName);
                Plugin.Log.LogInfo($"[SCENE] Player {player.PlayerName} is ready in scene {sceneName}");
            }
            
            // If we're the host, check if all players are ready
            if (NetworkManager.Instance?.IsHost ?? false)
            {
                CheckAllPlayersReady();
            }
        }

        /// <summary>
        /// Check if all players are in the same scene and ready
        /// </summary>
        private void CheckAllPlayersReady()
        {
            bool allReady = true;
            foreach (var kvp in _remotePlayers)
            {
                if (kvp.Value.CurrentScene != CurrentScene)
                {
                    allReady = false;
                    break;
                }
            }
            
            if (allReady)
            {
                Plugin.Log.LogInfo($"[SCENE] All players ready in scene {CurrentScene}!");
                // Could trigger game unpause or cutscene start here
            }
        }

        #endregion

        public RemotePlayer GetRemotePlayer(CSteamID steamId)
        {
            _remotePlayers.TryGetValue(steamId, out var player);
            return player;
        }

        public IEnumerable<RemotePlayer> GetAllRemotePlayers()
        {
            return _remotePlayers.Values;
        }

        private void OnDestroy()
        {
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.OnPlayerJoined -= OnPlayerJoined;
                NetworkManager.Instance.OnPlayerLeft -= OnPlayerLeft;
            }
            
            SceneManager.sceneLoaded -= OnSceneLoaded;

            foreach (var player in _remotePlayers.Values)
            {
                if (player != null)
                    Destroy(player.gameObject);
            }
            _remotePlayers.Clear();
        }
    }
}
