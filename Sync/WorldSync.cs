using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using Vertigo2;
using Vertigo2.Interaction;
using Vertigo2.Dialogarithm;
using Vertigo2.Player;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Syncs world objects between players:
    /// - NPC Dialog (Dialogarithm system)
    /// - Triggers/Events
    /// - Vehicles/Platforms (Elevators, Trains, Gondolas)
    /// - Physics Objects (Throwables, Grabbables)
    /// </summary>
    public class WorldSync : MonoBehaviour
    {
        public static WorldSync Instance { get; private set; }

        // Physics object tracking
        private Dictionary<int, TrackedPhysicsObject> _trackedPhysicsObjects = new();
        private int _nextPhysicsId = 1;
        
        // Vehicle tracking
        private Dictionary<string, TrackedVehicle> _trackedVehicles = new();
        
        // Dialog tracking
        private HashSet<string> _playedDialogLines = new();
        
        // Sync settings
        private float _syncTimer;
        private const float SYNC_RATE = 10f;
        private const float PHYSICS_SYNC_DISTANCE = 50f;
        
        // Track objects held by local player to skip sync
        private HashSet<int> _locallyHeldObjects = new();
        
        // MovingPlatform tracking for remote players
        private Dictionary<MovingPlatform, HashSet<Steamworks.CSteamID>> _platformOccupants = new();
        private Dictionary<MovingPlatform, Vector3> _platformLastPositions = new();

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
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            _trackedPhysicsObjects.Clear();
            _trackedVehicles.Clear();
            _playedDialogLines.Clear();
            _locallyHeldObjects.Clear();
            _platformOccupants.Clear();
            _platformLastPositions.Clear();
            _nextPhysicsId = 1;
            
            StartCoroutine(ScanSceneDelayed());
        }

        private System.Collections.IEnumerator ScanSceneDelayed()
        {
            yield return new WaitForSeconds(1f);
            ScanForVehicles();
            ScanForPhysicsObjects();
            ScanForPlatforms();
        }

        private void Update()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;

            // Only host syncs world state
            if (!NetworkManager.Instance.IsHost)
                return;

            _syncTimer += Time.deltaTime;
            if (_syncTimer >= 1f / SYNC_RATE)
            {
                _syncTimer = 0f;
                SyncVehicles();
                SyncPhysicsObjects();
            }
        }

        #region NPC Dialog Sync

        public void SyncDialogPlay(string characterName, string lineName, float startTime)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            string dialogId = $"{characterName}_{lineName}_{Time.time:F0}";
            
            if (_playedDialogLines.Contains(dialogId))
            {
                _playedDialogLines.Remove(dialogId);
                return;
            }

            Plugin.Log.LogInfo($"[WORLD] Syncing dialog: {characterName} - {lineName}");

            var packet = PacketBuilder.DialogPlay(characterName, lineName, startTime);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        public void HandleDialogPlay(string characterName, string lineName, float startTime)
        {
            Plugin.Log.LogInfo($"[WORLD] Received dialog: {characterName} - {lineName}");

            string dialogId = $"{characterName}_{lineName}_{Time.time:F0}";
            _playedDialogLines.Add(dialogId);

            var players = DialogarithmManager.players;
            Dialogarithm_Player targetPlayer = null;
            
            foreach (var player in players)
            {
                if (player.character != null && player.character.name == characterName)
                {
                    targetPlayer = player;
                    break;
                }
            }
            
            if (targetPlayer != null)
            {
                if (targetPlayer.currentLine != null && targetPlayer.currentLine.name == lineName)
                {
                    Plugin.Log.LogInfo($"[WORLD] Dialog already playing: {lineName}");
                }
                else
                {
                    var line = Resources.Load<Dialogarithm_LineBase>($"Dialog/{lineName}");
                    if (line != null)
                    {
                        targetPlayer.Play(line, startTime);
                        Plugin.Log.LogInfo($"[WORLD] Playing dialog line from resources: {lineName}");
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[WORLD] Could not find dialog line: {lineName}");
                    }
                }
            }
            else
            {
                Plugin.Log.LogWarning($"[WORLD] Could not find dialog player for character: {characterName}");
            }
        }

        #endregion

        #region Trigger/Event Sync

        public void SyncTriggerActivation(string triggerId, bool entered)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            if (!NetworkManager.Instance.IsHost)
                return;

            Plugin.Log.LogInfo($"[WORLD] Syncing trigger: {triggerId} entered={entered}");

            var packet = PacketBuilder.TriggerActivation(triggerId, entered);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        public void HandleTriggerActivation(string triggerId, bool entered)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            Plugin.Log.LogInfo($"[WORLD] Received trigger: {triggerId} entered={entered}");

            var trigger = GameObject.Find(triggerId);
            if (trigger != null)
            {
                if (entered)
                {
                    trigger.SendMessage("OnTriggerEnter", null, SendMessageOptions.DontRequireReceiver);
                }
                else
                {
                    trigger.SendMessage("OnTriggerExit", null, SendMessageOptions.DontRequireReceiver);
                }
            }
        }

        #endregion

        #region Vehicle/Platform Sync

        public void ScanForVehicles()
        {
            _trackedVehicles.Clear();

            foreach (var elevator in FindObjectsOfType<Elevator>())
            {
                string id = GetObjectId(elevator.gameObject);
                _trackedVehicles[id] = new TrackedVehicle
                {
                    Id = id,
                    GameObject = elevator.gameObject,
                    Type = VehicleType.Elevator,
                    LastPosition = elevator.transform.position,
                    LastState = 0f
                };
            }

            foreach (var train in FindObjectsOfType<Train>())
            {
                string id = GetObjectId(train.gameObject);
                _trackedVehicles[id] = new TrackedVehicle
                {
                    Id = id,
                    GameObject = train.gameObject,
                    Type = VehicleType.Train,
                    LastPosition = train.transform.position,
                    LastState = 0f
                };
            }

            foreach (var gondola in FindObjectsOfType<Gondola>())
            {
                string id = GetObjectId(gondola.gameObject);
                _trackedVehicles[id] = new TrackedVehicle
                {
                    Id = id,
                    GameObject = gondola.gameObject,
                    Type = VehicleType.Gondola,
                    LastPosition = gondola.transform.position,
                    LastState = 0f
                };
            }

            foreach (var boat in FindObjectsOfType<Boat>())
            {
                string id = GetObjectId(boat.gameObject);
                _trackedVehicles[id] = new TrackedVehicle
                {
                    Id = id,
                    GameObject = boat.gameObject,
                    Type = VehicleType.Boat,
                    LastPosition = boat.transform.position,
                    LastState = 0f
                };
            }

            Plugin.Log.LogInfo($"[WORLD] Found {_trackedVehicles.Count} vehicles");
        }

        private void SyncVehicles()
        {
            foreach (var kvp in _trackedVehicles)
            {
                var vehicle = kvp.Value;
                if (vehicle.GameObject == null) continue;

                Vector3 currentPos = vehicle.GameObject.transform.position;
                Quaternion currentRot = vehicle.GameObject.transform.rotation;
                float currentState = GetVehicleState(vehicle);

                float posDelta = Vector3.Distance(currentPos, vehicle.LastPosition);
                float stateDelta = Mathf.Abs(currentState - vehicle.LastState);

                if (posDelta > 0.1f || stateDelta > 0.01f)
                {
                    vehicle.LastPosition = currentPos;
                    vehicle.LastState = currentState;

                    var packet = PacketBuilder.VehicleState(vehicle.Id, (byte)vehicle.Type, 
                        currentPos, currentRot, currentState);
                    NetworkManager.Instance.Broadcast(packet, reliable: false);
                }
            }
        }

        private float GetVehicleState(TrackedVehicle vehicle)
        {
            switch (vehicle.Type)
            {
                case VehicleType.Elevator:
                    var elevator = vehicle.GameObject.GetComponent<Elevator>();
                    return elevator?.position ?? 0f;
                case VehicleType.Train:
                    var train = vehicle.GameObject.GetComponent<Train>();
                    return train?.position ?? 0f;
                case VehicleType.Gondola:
                    var gondola = vehicle.GameObject.GetComponent<Gondola>();
                    return gondola?.throttle ?? 0f;
                default:
                    return 0f;
            }
        }

        public void HandleVehicleState(string vehicleId, byte vehicleType, Vector3 position, Quaternion rotation, float state)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            // Special handling for elevators - call the elevator instead of just moving it
            if ((VehicleType)vehicleType == VehicleType.Elevator)
            {
                ApplyElevatorState(vehicleId, state);
                return;
            }

            if (_trackedVehicles.TryGetValue(vehicleId, out var vehicle) && vehicle.GameObject != null)
            {
                vehicle.GameObject.transform.position = Vector3.Lerp(
                    vehicle.GameObject.transform.position, position, Time.deltaTime * 10f);
                vehicle.GameObject.transform.rotation = Quaternion.Slerp(
                    vehicle.GameObject.transform.rotation, rotation, Time.deltaTime * 10f);
            }
            else
            {
                var obj = GameObject.Find(vehicleId);
                if (obj != null)
                {
                    _trackedVehicles[vehicleId] = new TrackedVehicle
                    {
                        Id = vehicleId,
                        GameObject = obj,
                        Type = (VehicleType)vehicleType,
                        LastPosition = position
                    };
                }
            }
        }

        #endregion

        #region Physics Object Sync

        public void ScanForPhysicsObjects()
        {
            _trackedPhysicsObjects.Clear();
            _nextPhysicsId = 1;

            foreach (var interactable in FindObjectsOfType<VertigoInteractable>())
            {
                if (interactable.interactType == VertigoInteractable.InteractTypes.Grabbable ||
                    interactable.interactType == VertigoInteractable.InteractTypes.Custom)
                {
                    var rb = interactable.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic)
                    {
                        int id = _nextPhysicsId++;
                        _trackedPhysicsObjects[id] = new TrackedPhysicsObject
                        {
                            Id = id,
                            GameObject = interactable.gameObject,
                            Rigidbody = rb,
                            Interactable = interactable,
                            LastPosition = rb.position,
                            LastRotation = rb.rotation,
                            LastVelocity = rb.velocity,
                            ObjectPath = GetObjectId(interactable.gameObject),
                            IsHeldLocally = false,
                            IsHeldRemotely = false
                        };
                    }
                }
            }

            Plugin.Log.LogInfo($"[WORLD] Found {_trackedPhysicsObjects.Count} physics objects");
        }

        public int RegisterPhysicsObject(GameObject obj)
        {
            var rb = obj.GetComponent<Rigidbody>();
            if (rb == null) return -1;

            foreach (var kvp in _trackedPhysicsObjects)
            {
                if (kvp.Value.GameObject == obj)
                    return kvp.Key;
            }

            int id = _nextPhysicsId++;
            _trackedPhysicsObjects[id] = new TrackedPhysicsObject
            {
                Id = id,
                GameObject = obj,
                Rigidbody = rb,
                Interactable = obj.GetComponent<VertigoInteractable>(),
                LastPosition = rb.position,
                LastRotation = rb.rotation,
                LastVelocity = rb.velocity,
                ObjectPath = GetObjectId(obj)
            };

            return id;
        }

        public int GetPhysicsObjectId(GameObject obj)
        {
            foreach (var kvp in _trackedPhysicsObjects)
            {
                if (kvp.Value.GameObject == obj)
                    return kvp.Key;
            }
            return -1;
        }

        private void SyncPhysicsObjects()
        {
            Vector3 playerPos = VertigoPlayer.instance?.transform.position ?? Vector3.zero;
            var toRemove = new List<int>();

            foreach (var kvp in _trackedPhysicsObjects)
            {
                var obj = kvp.Value;
                
                if (obj.GameObject == null || obj.Rigidbody == null)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }

                // SKIP objects held by local player - they control it
                if (obj.IsHeldLocally)
                {
                    continue;
                }

                float distance = Vector3.Distance(obj.Rigidbody.position, playerPos);
                if (distance > PHYSICS_SYNC_DISTANCE) continue;

                float posDelta = Vector3.Distance(obj.Rigidbody.position, obj.LastPosition);
                float rotDelta = Quaternion.Angle(obj.Rigidbody.rotation, obj.LastRotation);
                bool isMoving = obj.Rigidbody.velocity.sqrMagnitude > 0.01f;

                if (posDelta > 0.05f || rotDelta > 5f || isMoving)
                {
                    obj.LastPosition = obj.Rigidbody.position;
                    obj.LastRotation = obj.Rigidbody.rotation;
                    obj.LastVelocity = obj.Rigidbody.velocity;

                    var packet = PacketBuilder.PhysicsObjectState(
                        obj.Id, obj.ObjectPath,
                        obj.Rigidbody.position, obj.Rigidbody.rotation,
                        obj.Rigidbody.velocity, obj.Rigidbody.angularVelocity);
                    NetworkManager.Instance.Broadcast(packet, reliable: false);
                }
            }

            foreach (var id in toRemove)
            {
                _trackedPhysicsObjects.Remove(id);
            }
        }

        public void HandlePhysicsObjectState(int objectId, string objectPath, 
            Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            // Clients apply physics state from host
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            if (_trackedPhysicsObjects.TryGetValue(objectId, out var obj) && obj.Rigidbody != null)
            {
                // SKIP if held locally - local player controls it
                if (obj.IsHeldLocally)
                {
                    return;
                }

                if (obj.IsHeldRemotely)
                {
                    // Object is held by remote player - set position directly
                    obj.Rigidbody.MovePosition(position);
                    obj.Rigidbody.MoveRotation(rotation);
                }
                else
                {
                    // Object is free - apply physics with interpolation
                    float posDiff = Vector3.Distance(obj.Rigidbody.position, position);
                    if (posDiff > 1.0f)
                    {
                        // Large difference - snap
                        obj.Rigidbody.position = position;
                        obj.Rigidbody.rotation = rotation;
                    }
                    else if (posDiff > 0.2f)
                    {
                        // Medium difference - interpolate
                        obj.Rigidbody.position = Vector3.Lerp(obj.Rigidbody.position, position, 0.5f);
                        obj.Rigidbody.rotation = Quaternion.Slerp(obj.Rigidbody.rotation, rotation, 0.5f);
                    }
                    // Small difference - let physics handle it
                    
                    obj.Rigidbody.velocity = velocity;
                    obj.Rigidbody.angularVelocity = angularVelocity;
                }
            }
            else
            {
                var gameObj = GameObject.Find(objectPath);
                if (gameObj != null)
                {
                    var rb = gameObj.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        _trackedPhysicsObjects[objectId] = new TrackedPhysicsObject
                        {
                            Id = objectId,
                            GameObject = gameObj,
                            Rigidbody = rb,
                            ObjectPath = objectPath,
                            LastPosition = position
                        };
                        
                        rb.position = position;
                        rb.rotation = rotation;
                        rb.velocity = velocity;
                        rb.angularVelocity = angularVelocity;
                        
                        Plugin.Log.LogInfo($"[WORLD] Registered physics object from network: {objectPath} (ID: {objectId})");
                    }
                }
            }
        }

        public void HandlePhysicsObjectGrab(int objectId, bool grabbed, Vector3 position)
        {
            Plugin.Log.LogInfo($"[WORLD] HandlePhysicsObjectGrab: ID={objectId}, grabbed={grabbed}, pos={position}");
            
            if (_trackedPhysicsObjects.TryGetValue(objectId, out var obj) && obj.Rigidbody != null)
            {
                if (grabbed)
                {
                    // Object is being held by another player
                    obj.IsHeldRemotely = true;
                    obj.Rigidbody.isKinematic = true;
                    obj.Rigidbody.position = position;
                    Plugin.Log.LogInfo($"[WORLD] Object {objectId} grabbed by remote player");
                }
                else
                {
                    // Object was released
                    obj.IsHeldRemotely = false;
                    obj.Rigidbody.isKinematic = false;
                    Plugin.Log.LogInfo($"[WORLD] Object {objectId} released by remote player");
                }
            }
        }

        /// <summary>
        /// Sync when a physics object is grabbed/released (called from patches)
        /// </summary>
        public void SyncPhysicsObjectGrab(int objectId, bool grabbed, Vector3 position)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            var packet = PacketBuilder.PhysicsObjectGrab(objectId, grabbed, position);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// Set whether an object is held locally (by this player)
        /// </summary>
        public void SetObjectHeldLocally(int objectId, bool held)
        {
            if (_trackedPhysicsObjects.TryGetValue(objectId, out var obj))
            {
                obj.IsHeldLocally = held;
                
                // Update the local tracking set
                if (held)
                    _locallyHeldObjects.Add(objectId);
                else
                    _locallyHeldObjects.Remove(objectId);
                    
                Plugin.Log.LogInfo($"[WORLD] Object {objectId} held locally: {held}");
            }
        }

        #endregion

        #region Helpers

        private string GetObjectId(GameObject obj)
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

        #region Train Sync

        /// <summary>
        /// Called from Train_FixedUpdate_Patch when host's train updates
        /// Syncs train position, velocity, and targetPosition to clients
        /// </summary>
        public void OnTrainUpdate(Train train)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            if (!NetworkManager.Instance.IsHost)
                return;

            string trainId = GetObjectId(train.gameObject);
            
            // Check if we have this train tracked
            if (!_trackedVehicles.TryGetValue(trainId, out var vehicle))
            {
                // Register the train
                vehicle = new TrackedVehicle
                {
                    Id = trainId,
                    GameObject = train.gameObject,
                    Type = VehicleType.Train,
                    LastPosition = train.transform.position,
                    LastState = train.position
                };
                _trackedVehicles[trainId] = vehicle;
                Plugin.Log.LogInfo($"[TRAIN] Registered train: {trainId}");
            }

            // Check if train state changed significantly
            float posDelta = Mathf.Abs(train.position - vehicle.LastState);
            float velDelta = Mathf.Abs(train.velocity);
            
            if (posDelta > 0.1f || velDelta > 0.1f)
            {
                vehicle.LastState = train.position;
                vehicle.LastPosition = train.transform.position;

                // Send train state packet with position, velocity, and targetPosition encoded
                var packet = PacketBuilder.TrainState(trainId, train.position, train.velocity, train.targetPosition);
                NetworkManager.Instance.Broadcast(packet, reliable: false);
                
                // Log occasionally
                if (_trainLogCounter++ % 30 == 0)
                {
                    Plugin.Log.LogInfo($"[TRAIN] Syncing {trainId}: pos={train.position:F2}, vel={train.velocity:F2}, target={train.targetPosition:F2}");
                }
            }
        }
        
        private int _trainLogCounter = 0;

        /// <summary>
        /// Apply train state from network (called from PacketHandler)
        /// </summary>
        public void HandleTrainState(string trainId, float position, float velocity, float targetPosition)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;
            
            // Log occasionally
            if (_trainLogCounter++ % 30 == 0)
            {
                Plugin.Log.LogInfo($"[TRAIN] Received {trainId}: pos={position:F2}, vel={velocity:F2}, target={targetPosition:F2}");
            }

            // Find the train
            if (_trackedVehicles.TryGetValue(trainId, out var vehicle) && vehicle.GameObject != null)
            {
                var train = vehicle.GameObject.GetComponent<Train>();
                if (train != null)
                {
                    // Apply the train state - set all three values
                    train.position = position;
                    train.velocity = velocity;
                    train.targetPosition = targetPosition;
                    
                    // The train's FixedUpdate will call UpdateTrainPosition() to move the transform
                }
            }
            else
            {
                // Try to find the train by ID
                var trains = FindObjectsOfType<Train>();
                foreach (var train in trains)
                {
                    if (GetObjectId(train.gameObject) == trainId)
                    {
                        // Register it
                        _trackedVehicles[trainId] = new TrackedVehicle
                        {
                            Id = trainId,
                            GameObject = train.gameObject,
                            Type = VehicleType.Train,
                            LastPosition = train.transform.position,
                            LastState = position
                        };
                        
                        // Apply state
                        train.position = position;
                        train.velocity = velocity;
                        train.targetPosition = targetPosition;
                        
                        Plugin.Log.LogInfo($"[WORLD] Registered train from network: {trainId}");
                        break;
                    }
                }
            }
        }

        #endregion

        #region Elevator Sync

        /// <summary>
        /// Called when local player calls an elevator
        /// </summary>
        public void OnElevatorCalled(Elevator elevator, float targetPosition)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            string elevatorId = GetObjectId(elevator.gameObject);
            Plugin.Log.LogInfo($"[WORLD] Elevator called: {elevatorId} -> {targetPosition}");

            // Send elevator call to other players
            var packet = PacketBuilder.VehicleState(elevatorId, (byte)VehicleType.Elevator,
                elevator.transform.position, elevator.transform.rotation, targetPosition);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// Apply elevator state from network (called from HandleVehicleState for elevators)
        /// </summary>
        public void ApplyElevatorState(string elevatorId, float targetPosition)
        {
            if (_trackedVehicles.TryGetValue(elevatorId, out var vehicle) && vehicle.GameObject != null)
            {
                var elevator = vehicle.GameObject.GetComponent<Elevator>();
                if (elevator != null)
                {
                    // Call the elevator to the target position
                    elevator.CallElevator(targetPosition);
                    Plugin.Log.LogInfo($"[WORLD] Applied elevator state: {elevatorId} -> {targetPosition}");
                }
            }
        }

        #endregion

        #region Health Station Sync

        /// <summary>
        /// Called when local player uses a health station
        /// </summary>
        public void OnHealthStationUsed(HealthStation station)
        {
            if (!NetworkManager.Instance?.IsConnected ?? true)
                return;

            string stationId = GetObjectId(station.gameObject);
            float juiceLevel = station.juice;
            
            Plugin.Log.LogInfo($"[WORLD] Health station used: {stationId}, juice={juiceLevel}");

            // Send health station state to other players
            // We'll use the PhysicsObjectState packet type with a special ID range for health stations
            // Or create a new packet type - for now, use InteractableState
            var packet = PacketBuilder.InteractableState(stationId, 10, juiceLevel); // Type 10 = health station
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// Apply health station state from network
        /// </summary>
        public void ApplyHealthStationState(string stationId, float juiceLevel)
        {
            // Find the health station
            var stations = FindObjectsOfType<HealthStation>();
            foreach (var station in stations)
            {
                if (GetObjectId(station.gameObject) == stationId)
                {
                    station.juice = juiceLevel;
                    
                    // Update the visual
                    var juiceProps = new MaterialPropertyBlock();
                    juiceProps.SetFloat("_Cutoff", juiceLevel);
                    if (station.juiceBar != null)
                    {
                        station.juiceBar.SetPropertyBlock(juiceProps);
                    }
                    
                    Plugin.Log.LogInfo($"[WORLD] Applied health station state: {stationId}, juice={juiceLevel}");
                    break;
                }
            }
        }

        #endregion

        #region MovingPlatform Sync

        /// <summary>
        /// Scan for all MovingPlatforms in the scene
        /// </summary>
        public void ScanForPlatforms()
        {
            _platformOccupants.Clear();
            _platformLastPositions.Clear();
            
            foreach (var platform in FindObjectsOfType<MovingPlatform>())
            {
                _platformOccupants[platform] = new HashSet<Steamworks.CSteamID>();
                _platformLastPositions[platform] = platform.transform.position;
            }
            
            Plugin.Log.LogInfo($"[WORLD] Found {_platformOccupants.Count} moving platforms");
        }

        /// <summary>
        /// Register a remote player as being on a platform
        /// </summary>
        public void RegisterRemotePlayerOnPlatform(Steamworks.CSteamID playerId, MovingPlatform platform)
        {
            if (platform == null) return;
            
            // Remove from any other platform first
            foreach (var kvp in _platformOccupants)
            {
                kvp.Value.Remove(playerId);
            }
            
            // Add to this platform
            if (!_platformOccupants.ContainsKey(platform))
            {
                _platformOccupants[platform] = new HashSet<Steamworks.CSteamID>();
                _platformLastPositions[platform] = platform.transform.position;
            }
            _platformOccupants[platform].Add(playerId);
            
            Plugin.Log.LogInfo($"[WORLD] Remote player {playerId} registered on platform {platform.name}");
        }

        /// <summary>
        /// Unregister a remote player from all platforms
        /// </summary>
        public void UnregisterRemotePlayerFromPlatforms(Steamworks.CSteamID playerId)
        {
            foreach (var kvp in _platformOccupants)
            {
                if (kvp.Value.Remove(playerId))
                {
                    Plugin.Log.LogInfo($"[WORLD] Remote player {playerId} unregistered from platform {kvp.Key.name}");
                }
            }
        }

        /// <summary>
        /// Check if a remote player is on any platform and return the platform delta
        /// Called from PlayerManager to adjust remote player positions
        /// </summary>
        public Vector3 GetPlatformDeltaForPlayer(Steamworks.CSteamID playerId)
        {
            foreach (var kvp in _platformOccupants)
            {
                if (kvp.Value.Contains(playerId) && kvp.Key != null)
                {
                    var platform = kvp.Key;
                    Vector3 currentPos = platform.transform.position;
                    Vector3 lastPos;
                    if (!_platformLastPositions.TryGetValue(platform, out lastPos))
                        lastPos = currentPos;
                    return currentPos - lastPos;
                }
            }
            return Vector3.zero;
        }

        /// <summary>
        /// Update platform last positions (call at end of frame)
        /// </summary>
        public void UpdatePlatformPositions()
        {
            foreach (var kvp in _platformOccupants)
            {
                if (kvp.Key != null)
                {
                    _platformLastPositions[kvp.Key] = kvp.Key.transform.position;
                }
            }
        }

        /// <summary>
        /// Find the platform a position is on (if any)
        /// </summary>
        public MovingPlatform FindPlatformAtPosition(Vector3 position)
        {
            foreach (var platform in _platformOccupants.Keys)
            {
                if (platform == null) continue;
                
                // Check if position is within the platform's walkable area
                if (platform.WalkableAreaContains(position, 0.5f))
                {
                    return platform;
                }
            }
            return null;
        }

        #endregion

        #region Data Classes

        private class TrackedVehicle
        {
            public string Id;
            public GameObject GameObject;
            public VehicleType Type;
            public Vector3 LastPosition;
            public float LastState;
        }

        private class TrackedPhysicsObject
        {
            public int Id;
            public GameObject GameObject;
            public Rigidbody Rigidbody;
            public VertigoInteractable Interactable;
            public Vector3 LastPosition;
            public Quaternion LastRotation;
            public Vector3 LastVelocity;
            public string ObjectPath;
            public bool IsHeldLocally;
            public bool IsHeldRemotely;
        }

        private enum VehicleType : byte
        {
            Elevator = 0,
            Train = 1,
            Gondola = 2,
            Boat = 3,
            Platform = 4
        }

        #endregion
    }
}
