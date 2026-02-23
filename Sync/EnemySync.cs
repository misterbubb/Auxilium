using System.Collections.Generic;
using UnityEngine;
using Steamworks;
using Vertigo2;
using Vertigo2.AI;
using Vertigo2.Player;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Syncs enemies between host and clients.
    /// Host-authoritative: Host runs AI, syncs positions to clients.
    /// Enemies can target ALL players (host and clients).
    /// </summary>
    public class EnemySync : MonoBehaviour
    {
        public static EnemySync Instance { get; private set; }

        // Track enemies by unique ID
        private Dictionary<int, EnemyData> _trackedEnemies = new();
        private int _nextEnemyId = 1;
        
        // Remote player entities (so enemies can target them)
        private Dictionary<CSteamID, RemotePlayerEntity> _remotePlayerEntities = new();
        
        // Sync settings
        private float _syncTimer;
        private const float SYNC_RATE = 10f; // 10 times per second
        private const float POSITION_THRESHOLD = 0.1f; // Only sync if moved more than this

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
            // Subscribe to enemy spawn/death events
            AIManager.OnEnemySpawned += OnEnemySpawned;
            AIManager.OnEnemyKilled += OnEnemyKilled;
            
            // Subscribe to scene changes for cleanup
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }
        
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            // Clear tracked enemies on scene change
            ClearTrackedEnemies();
        }

        private void OnDestroy()
        {
            AIManager.OnEnemySpawned -= OnEnemySpawned;
            AIManager.OnEnemyKilled -= OnEnemyKilled;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            
            // Clean up remote player entities
            foreach (var entity in _remotePlayerEntities.Values)
            {
                if (entity != null)
                    Destroy(entity.gameObject);
            }
            _remotePlayerEntities.Clear();
        }

        private void Update()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;

            // Only host syncs enemy state
            if (!NetworkManager.Instance.IsHost)
                return;

            _syncTimer += Time.deltaTime;
            if (_syncTimer >= 1f / SYNC_RATE)
            {
                _syncTimer = 0f;
                SyncEnemies();
            }
            
            // Update remote player entity positions (so enemies can target them)
            UpdateRemotePlayerEntities();
        }

        #region Remote Player Entities (for enemy targeting)

        /// <summary>
        /// Create a fake VertigoEntity for a remote player so enemies can target them
        /// </summary>
        public void RegisterRemotePlayer(CSteamID steamId, RemotePlayer remotePlayer)
        {
            if (!NetworkManager.Instance?.IsHost ?? true)
                return;
                
            if (_remotePlayerEntities.ContainsKey(steamId))
                return;

            // Create a GameObject with RemotePlayerEntity component
            var entityObj = new GameObject($"RemotePlayerEntity_{steamId}");
            entityObj.transform.SetParent(transform);
            
            var entity = entityObj.AddComponent<RemotePlayerEntity>();
            entity.Initialize(steamId, remotePlayer);
            
            _remotePlayerEntities[steamId] = entity;
            
            // Register with AIManager so enemies can see it
            if (AIManager.world != null)
            {
                AIManager.world.AddEntity(entity);
            }
            
            Plugin.Log.LogInfo($"[ENEMY SYNC] Registered remote player entity for {steamId}");
        }

        /// <summary>
        /// Remove remote player entity when they disconnect
        /// </summary>
        public void UnregisterRemotePlayer(CSteamID steamId)
        {
            if (_remotePlayerEntities.TryGetValue(steamId, out var entity))
            {
                if (AIManager.world != null)
                {
                    AIManager.world.RemoveEntity(entity);
                }
                
                Destroy(entity.gameObject);
                _remotePlayerEntities.Remove(steamId);
                
                Plugin.Log.LogInfo($"[ENEMY SYNC] Unregistered remote player entity for {steamId}");
            }
        }

        /// <summary>
        /// Update remote player entity positions from RemotePlayer transforms
        /// </summary>
        private void UpdateRemotePlayerEntities()
        {
            foreach (var kvp in _remotePlayerEntities)
            {
                var entity = kvp.Value;
                if (entity != null && entity.RemotePlayer != null)
                {
                    entity.UpdatePosition();
                }
            }
        }

        #endregion

        #region Enemy Tracking

        private void OnEnemySpawned(Enemy enemy)
        {
            if (!NetworkManager.Instance?.IsHost ?? true)
                return;

            // Assign unique ID and track
            int enemyId = _nextEnemyId++;
            var data = new EnemyData
            {
                Id = enemyId,
                Enemy = enemy,
                LastSyncedPosition = enemy.transform.position,
                LastSyncedRotation = enemy.transform.rotation,
                LastSyncedHealth = enemy.health
            };
            
            _trackedEnemies[enemyId] = data;
            
            // Broadcast spawn to clients
            string enemyType = enemy.GetType().Name;
            var packet = PacketBuilder.EnemySpawn(enemyId, enemyType, 
                enemy.transform.position, enemy.transform.rotation);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
            
            Plugin.Log.LogInfo($"[ENEMY SYNC] Enemy spawned: {enemyType} (ID: {enemyId})");
        }

        private void OnEnemyKilled(Enemy enemy, HitInfo hit, bool crit)
        {
            if (!NetworkManager.Instance?.IsHost ?? true)
                return;

            // Find and remove from tracking
            int enemyId = -1;
            foreach (var kvp in _trackedEnemies)
            {
                if (kvp.Value.Enemy == enemy)
                {
                    enemyId = kvp.Key;
                    break;
                }
            }
            
            if (enemyId >= 0)
            {
                _trackedEnemies.Remove(enemyId);
                
                // Broadcast death to clients
                var packet = PacketBuilder.EnemyDeath(enemyId);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
                
                Plugin.Log.LogInfo($"[ENEMY SYNC] Enemy killed: ID {enemyId}");
            }
        }

        /// <summary>
        /// Sync enemy positions and health to clients
        /// </summary>
        private void SyncEnemies()
        {
            var toRemove = new List<int>();
            
            foreach (var kvp in _trackedEnemies)
            {
                var data = kvp.Value;
                
                // Clean up destroyed enemies
                if (data.Enemy == null)
                {
                    toRemove.Add(kvp.Key);
                    continue;
                }
                
                // Check if position changed significantly
                float posDelta = Vector3.Distance(data.Enemy.transform.position, data.LastSyncedPosition);
                float rotDelta = Quaternion.Angle(data.Enemy.transform.rotation, data.LastSyncedRotation);
                float healthDelta = Mathf.Abs(data.Enemy.health - data.LastSyncedHealth);
                
                if (posDelta > POSITION_THRESHOLD || rotDelta > 5f || healthDelta > 0.1f)
                {
                    data.LastSyncedPosition = data.Enemy.transform.position;
                    data.LastSyncedRotation = data.Enemy.transform.rotation;
                    data.LastSyncedHealth = data.Enemy.health;
                    
                    var packet = PacketBuilder.EnemyState(data.Id, 
                        data.Enemy.transform.position, 
                        data.Enemy.transform.rotation,
                        data.Enemy.health);
                    NetworkManager.Instance.Broadcast(packet, reliable: false);
                }
            }
            
            // Clean up
            foreach (var id in toRemove)
            {
                _trackedEnemies.Remove(id);
            }
        }

        #endregion

        #region Client-side: Handle incoming enemy data

        /// <summary>
        /// Handle enemy spawn packet (client only)
        /// </summary>
        public void HandleEnemySpawn(int enemyId, string enemyType, Vector3 position, Quaternion rotation)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            // On client, we just track the ID -> existing enemy mapping
            // The enemy should already exist from the game's normal spawning
            // We'll match by position/type
            
            Plugin.Log.LogInfo($"[ENEMY SYNC] Client received enemy spawn: {enemyType} at {position}");
            
            // Check if we already have this enemy ID tracked
            if (_trackedEnemies.ContainsKey(enemyId))
                return;
            
            // Try to find matching enemy
            if (AIManager.world != null)
            {
                foreach (var enemy in AIManager.world.enemies)
                {
                    if (enemy == null) continue;
                    
                    // Check if this enemy is already tracked
                    bool alreadyTracked = false;
                    foreach (var kvp in _trackedEnemies)
                    {
                        if (kvp.Value.Enemy == enemy)
                        {
                            alreadyTracked = true;
                            break;
                        }
                    }
                    
                    if (!alreadyTracked)
                    {
                        float dist = Vector3.Distance(enemy.transform.position, position);
                        if (dist < 5f && enemy.GetType().Name == enemyType)
                        {
                            _trackedEnemies[enemyId] = new EnemyData
                            {
                                Id = enemyId,
                                Enemy = enemy,
                                LastSyncedPosition = position,
                                LastSyncedRotation = rotation
                            };
                            Plugin.Log.LogInfo($"[ENEMY SYNC] Matched enemy {enemyType} to ID {enemyId}");
                            return;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Handle enemy state update (client only)
        /// </summary>
        public void HandleEnemyState(int enemyId, Vector3 position, Quaternion rotation, float health)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            if (_trackedEnemies.TryGetValue(enemyId, out var data) && data.Enemy != null)
            {
                // Smoothly interpolate position
                data.Enemy.transform.position = Vector3.Lerp(
                    data.Enemy.transform.position, position, Time.deltaTime * 10f);
                data.Enemy.transform.rotation = Quaternion.Slerp(
                    data.Enemy.transform.rotation, rotation, Time.deltaTime * 10f);
                
                // Update health directly (could trigger damage effects)
                // data.Enemy.health = health; // Can't set directly, would need reflection
            }
        }

        /// <summary>
        /// Handle enemy death (client only)
        /// </summary>
        public void HandleEnemyDeath(int enemyId)
        {
            if (NetworkManager.Instance?.IsHost ?? true)
                return;

            if (_trackedEnemies.TryGetValue(enemyId, out var data))
            {
                if (data.Enemy != null && !data.Enemy.dead)
                {
                    // Force kill the enemy on client
                    // This ensures death animations/effects play
                    data.Enemy.SendMessage("Die", null, SendMessageOptions.DontRequireReceiver);
                }
                
                _trackedEnemies.Remove(enemyId);
                Plugin.Log.LogInfo($"[ENEMY SYNC] Enemy {enemyId} died");
            }
        }

        /// <summary>
        /// Handle damage to remote player (host sends this to client)
        /// </summary>
        public void HandleRemotePlayerDamage(CSteamID targetPlayer, float damage, Vector3 hitPoint)
        {
            // If we're the target player, apply damage to local player
            if (targetPlayer == NetworkManager.Instance.LocalSteamID)
            {
                if (VertigoPlayer.instance != null)
                {
                    // Create HitInfo - simple constructor: (damage, damageType, isCrit)
                    var hitInfo = new HitInfo(damage, DamageType.Generic, false);
                    VertigoPlayer.instance.Hit(hitInfo, false);
                    Plugin.Log.LogInfo($"[ENEMY SYNC] Received damage from enemy: {damage}");
                }
            }
        }

        #endregion

        /// <summary>
        /// Clear all tracked enemies (on scene change)
        /// </summary>
        public void ClearTrackedEnemies()
        {
            _trackedEnemies.Clear();
            _nextEnemyId = 1;
        }

        private class EnemyData
        {
            public int Id;
            public Enemy Enemy;
            public Vector3 LastSyncedPosition;
            public Quaternion LastSyncedRotation;
            public float LastSyncedHealth;
        }
    }
}
