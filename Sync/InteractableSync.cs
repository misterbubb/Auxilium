using System.Collections.Generic;
using UnityEngine;
using Vertigo2;
using Vertigo2.Interaction;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Syncs interactable objects (doors, levers, buttons) between players.
    /// BIDIRECTIONAL: Either player can interact and the other will see it.
    /// </summary>
    public class InteractableSync : MonoBehaviour
    {
        public static InteractableSync Instance { get; private set; }

        // Track interactables by their unique ID
        private Dictionary<string, InteractableData> _trackedInteractables = new();
        
        // Sync rate
        private float _syncTimer;
        private const float SYNC_RATE = 10f; // 10 times per second
        
        // Track which states we received from network to avoid echo
        private HashSet<string> _receivedStates = new();

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
            _trackedInteractables.Clear();
            _receivedStates.Clear();
        }

        private void Update()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;

            // BIDIRECTIONAL: Both host and client send interactable state updates
            _syncTimer += Time.deltaTime;
            if (_syncTimer >= 1f / SYNC_RATE)
            {
                _syncTimer = 0f;
                SyncInteractables();
            }
        }

        /// <summary>
        /// Register an interactable for syncing
        /// </summary>
        public void RegisterInteractable(string id, GameObject obj, InteractableType type)
        {
            if (!_trackedInteractables.ContainsKey(id))
            {
                _trackedInteractables[id] = new InteractableData
                {
                    Id = id,
                    GameObject = obj,
                    Type = type,
                    LastState = GetInteractableStateValue(obj, type),
                    LastSyncTime = Time.time
                };
            }
        }

        /// <summary>
        /// Sync all tracked interactables - BIDIRECTIONAL
        /// </summary>
        private void SyncInteractables()
        {
            foreach (var kvp in _trackedInteractables)
            {
                var data = kvp.Value;
                if (data.GameObject == null) continue;

                float currentState = GetInteractableStateValue(data.GameObject, data.Type);
                
                // Only send if state changed AND we didn't just receive this state from network
                if (Mathf.Abs(currentState - data.LastState) > 0.01f)
                {
                    // Check if this was a state we received from network (avoid echo)
                    string stateKey = $"{data.Id}_{currentState:F2}";
                    if (_receivedStates.Contains(stateKey))
                    {
                        _receivedStates.Remove(stateKey);
                        data.LastState = currentState;
                        continue;
                    }
                    
                    data.LastState = currentState;
                    data.LastSyncTime = Time.time;
                    
                    var packet = PacketBuilder.InteractableState(data.Id, (byte)data.Type, currentState);
                    NetworkManager.Instance.Broadcast(packet, reliable: true);
                    
                    Plugin.Log.LogInfo($"[INTERACTABLE] Sending state: {data.Id}, type={data.Type}, value={currentState}");
                }
            }
        }

        /// <summary>
        /// Get the current state of an interactable
        /// </summary>
        private float GetInteractableStateValue(GameObject obj, InteractableType type)
        {
            switch (type)
            {
                case InteractableType.Door:
                    return GetDoorState(obj);
                case InteractableType.Lever:
                    return GetLeverState(obj);
                case InteractableType.Button:
                    return GetButtonState(obj);
                default:
                    return 0f;
            }
        }

        private float GetDoorState(GameObject obj)
        {
            // Try base Door class first (covers all door types)
            var door = obj.GetComponent<Door>();
            if (door != null)
            {
                // Use doorTimer > 0 to determine if door is open
                // Also check the 'open' property
                return door.open ? 1f : 0f;
            }
            
            // Fallback: check rotation
            return obj.transform.localEulerAngles.y / 360f;
        }

        private float GetLeverState(GameObject obj)
        {
            var lever = obj.GetComponent<Lever>();
            if (lever != null)
            {
                return lever.lever?.position ?? 0f;
            }
            return 0f;
        }

        private float GetButtonState(GameObject obj)
        {
            var button = obj.GetComponent<PhysicsButton>();
            if (button != null)
            {
                return button.depressed ? 1f : 0f;
            }
            return 0f;
        }

        /// <summary>
        /// Handle incoming interactable state from network - BIDIRECTIONAL
        /// </summary>
        public void HandleInteractableState(string id, byte stateType, float stateValue)
        {
            Plugin.Log.LogInfo($"[INTERACTABLE] Received state: {id}, type={stateType}, value={stateValue}");

            // Special handling for health stations (type 10)
            if (stateType == 10)
            {
                WorldSync.Instance?.ApplyHealthStationState(id, stateValue);
                return;
            }

            // Mark this state as received from network to avoid echo
            string stateKey = $"{id}_{stateValue:F2}";
            _receivedStates.Add(stateKey);

            if (_trackedInteractables.TryGetValue(id, out var data))
            {
                // Don't apply if our state is the same (avoid jitter)
                float currentState = GetInteractableStateValue(data.GameObject, data.Type);
                if (Mathf.Abs(currentState - stateValue) < 0.01f)
                {
                    return;
                }
                
                ApplyInteractableState(data, (InteractableType)stateType, stateValue);
                Plugin.Log.LogInfo($"[INTERACTABLE] Applied state to tracked: {id}");
            }
            else
            {
                Plugin.Log.LogInfo($"[INTERACTABLE] Not tracked, searching for: {id}");
                TryFindAndRegisterInteractable(id, (InteractableType)stateType, stateValue);
            }
        }

        /// <summary>
        /// Apply state to an interactable
        /// </summary>
        private void ApplyInteractableState(InteractableData data, InteractableType type, float state)
        {
            if (data.GameObject == null) return;

            switch (type)
            {
                case InteractableType.Door:
                    ApplyDoorState(data.GameObject, state);
                    break;
                case InteractableType.Lever:
                    ApplyLeverState(data.GameObject, state);
                    break;
                case InteractableType.Button:
                    ApplyButtonState(data.GameObject, state);
                    break;
            }
            
            data.LastState = state;
        }

        private void ApplyDoorState(GameObject obj, float state)
        {
            // Try base Door class first
            var door = obj.GetComponent<Door>();
            if (door != null)
            {
                bool shouldBeOpen = state > 0.5f;
                if (door.open != shouldBeOpen)
                {
                    if (shouldBeOpen)
                    {
                        door.Open(); // Sets doorTimer to infinity
                        door.SetFullyOpen();
                    }
                    else
                    {
                        door.Close(); // Sets doorTimer to -1
                        door.SetFullyClosed();
                    }
                    Plugin.Log.LogInfo($"[INTERACTABLE] Door {obj.name} set to {(shouldBeOpen ? "OPEN" : "CLOSED")}");
                }
                return;
            }
            
            // Fallback for AnimatedDoor specifically
            var animDoor = obj.GetComponent<AnimatedDoor>();
            if (animDoor != null)
            {
                bool shouldBeOpen = state > 0.5f;
                if (animDoor.DoorsFullyOpen != shouldBeOpen)
                {
                    if (shouldBeOpen)
                        animDoor.SetFullyOpen();
                    else
                        animDoor.SetFullyClosed();
                }
            }
        }

        private void ApplyLeverState(GameObject obj, float state)
        {
            var lever = obj.GetComponent<Lever>();
            if (lever != null && lever.lever != null)
            {
                lever.lever.position = state;
            }
        }

        private void ApplyButtonState(GameObject obj, float state)
        {
            var button = obj.GetComponent<PhysicsButton>();
            if (button != null)
            {
                bool shouldBePressed = state > 0.5f;
                if (shouldBePressed && !button.depressed)
                {
                    button.Press();
                }
            }
        }

        /// <summary>
        /// Try to find an interactable by ID and register it
        /// </summary>
        private void TryFindAndRegisterInteractable(string id, InteractableType type, float state)
        {
            GameObject found = GameObject.Find(id);
            
            if (found != null)
            {
                RegisterInteractable(id, found, type);
                
                if (_trackedInteractables.TryGetValue(id, out var data))
                {
                    ApplyInteractableState(data, type, state);
                }
            }
            else
            {
                Plugin.Log.LogWarning($"[INTERACTABLE] Could not find object: {id}");
            }
        }

        /// <summary>
        /// Scan scene for interactables and register them
        /// </summary>
        public void ScanSceneForInteractables()
        {
            _trackedInteractables.Clear();
            _receivedStates.Clear();

            // Find ALL doors using base Door class (includes AnimatedDoor, SlidingDoor, etc.)
            foreach (var door in FindObjectsOfType<Door>())
            {
                string id = GetInteractableId(door.gameObject);
                RegisterInteractable(id, door.gameObject, InteractableType.Door);
                Plugin.Log.LogInfo($"[INTERACTABLE] Found door: {id} (type: {door.GetType().Name})");
            }

            // Find all levers
            foreach (var lever in FindObjectsOfType<Lever>())
            {
                string id = GetInteractableId(lever.gameObject);
                RegisterInteractable(id, lever.gameObject, InteractableType.Lever);
            }

            // Find all buttons
            foreach (var button in FindObjectsOfType<PhysicsButton>())
            {
                string id = GetInteractableId(button.gameObject);
                RegisterInteractable(id, button.gameObject, InteractableType.Button);
            }

            Plugin.Log.LogInfo($"[INTERACTABLE] Scanned scene, found {_trackedInteractables.Count} interactables");
        }

        /// <summary>
        /// Generate a unique ID for an interactable
        /// </summary>
        private string GetInteractableId(GameObject obj)
        {
            return GetGameObjectPath(obj);
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

        /// <summary>
        /// Manually trigger a door sync (called from patch when player interacts)
        /// </summary>
        public void OnDoorInteracted(Door door)
        {
            if (door == null) return;
            
            string id = GetInteractableId(door.gameObject);
            
            // Register if not already tracked
            if (!_trackedInteractables.ContainsKey(id))
            {
                RegisterInteractable(id, door.gameObject, InteractableType.Door);
            }
            
            // Force immediate sync
            if (_trackedInteractables.TryGetValue(id, out var data))
            {
                float currentState = door.open ? 1f : 0f;
                data.LastState = currentState;
                
                var packet = PacketBuilder.InteractableState(id, (byte)InteractableType.Door, currentState);
                NetworkManager.Instance?.Broadcast(packet, reliable: true);
                
                Plugin.Log.LogInfo($"[INTERACTABLE] Door interacted: {id}, state={currentState}");
            }
        }

        private class InteractableData
        {
            public string Id;
            public GameObject GameObject;
            public InteractableType Type;
            public float LastState;
            public float LastSyncTime;
        }
    }

    public enum InteractableType : byte
    {
        Door = 0,
        Lever = 1,
        Button = 2,
        Switch = 3,
        Valve = 4
    }
}
