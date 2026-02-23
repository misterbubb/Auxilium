using UnityEngine;
using Steamworks;
using Vertigo2;
using Vertigo2.AI;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// A fake VertigoEntity that represents a remote player.
    /// This allows enemies to target and attack remote players, not just the host.
    /// Only used on the HOST side.
    /// </summary>
    public class RemotePlayerEntity : VertigoEntity
    {
        public CSteamID SteamId { get; private set; }
        public RemotePlayer RemotePlayer { get; private set; }
        
        // Cached references
        private Transform _headTransform;
        private Transform _attackTargetTransform;

        public void Initialize(CSteamID steamId, RemotePlayer remotePlayer)
        {
            SteamId = steamId;
            RemotePlayer = remotePlayer;
            
            // Set up required VertigoEntity properties
            this.health = 100f;
            this.maxHealth = 100f;
            this.dead = false;
            // Note: homingTargetable is read-only, enemies will still target based on allegiance
            
            // Create head transform for enemy targeting
            var headObj = new GameObject("Head");
            headObj.transform.SetParent(transform);
            headObj.transform.localPosition = new Vector3(0, 1.6f, 0);
            _headTransform = headObj.transform;
            this.head = _headTransform;
            
            // Create attack target transform
            var targetObj = new GameObject("AttackTarget");
            targetObj.transform.SetParent(transform);
            targetObj.transform.localPosition = new Vector3(0, 1f, 0);
            _attackTargetTransform = targetObj.transform;
            this.attackTarget = _attackTargetTransform;
            
            // Set allegiance to match player (enemies will attack)
            if (AIManager.world != null && AIManager.world.playerAllegience != null)
            {
                this.allegiance = AIManager.world.playerAllegience;
            }
            
            Plugin.Log.LogInfo($"[REMOTE ENTITY] Initialized for {SteamFriends.GetFriendPersonaName(steamId)}");
        }

        /// <summary>
        /// Update position from RemotePlayer's current position
        /// </summary>
        public void UpdatePosition()
        {
            if (RemotePlayer == null) return;
            
            // Get position from remote player's model
            var modelRoot = RemotePlayer.GetModelRoot();
            if (modelRoot != null)
            {
                transform.position = modelRoot.position;
                transform.rotation = modelRoot.rotation;
            }
            
            // Update head position
            var headTarget = RemotePlayer.GetHeadTarget();
            if (headTarget != null && _headTransform != null)
            {
                _headTransform.position = headTarget.position;
                _headTransform.rotation = headTarget.rotation;
            }
        }

        /// <summary>
        /// Called when this entity takes damage from an enemy
        /// </summary>
        public override void Hit(HitInfo hit, bool crit = false)
        {
            // Don't process if we're dead
            if (dead) return;
            
            Plugin.Log.LogInfo($"[REMOTE ENTITY] {SteamFriends.GetFriendPersonaName(SteamId)} hit for {hit.hitDamage} damage");
            
            // Send damage to the actual remote player
            if (NetworkManager.Instance != null)
            {
                var packet = PacketBuilder.RemotePlayerDamage(SteamId, hit.hitDamage, hit.hitPoint);
                NetworkManager.Instance.SendTo(SteamId, packet, reliable: true);
            }
            
            // Update our fake health (for enemy AI decision making)
            health -= hit.hitDamage;
            if (health <= 0)
            {
                health = 0;
                // Don't set dead = true, let the actual client handle death
            }
        }

        /// <summary>
        /// Reset health when remote player respawns
        /// </summary>
        public void OnRemotePlayerRespawn()
        {
            health = maxHealth;
            dead = false;
        }

        /// <summary>
        /// Mark as dead when remote player dies
        /// </summary>
        public void OnRemotePlayerDeath()
        {
            dead = true;
            health = 0;
        }
    }
}
