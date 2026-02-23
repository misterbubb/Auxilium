using System.Collections.Generic;
using UnityEngine;
using Valve.VR;
using Vertigo2.Weapons;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Syncs weapon pickups and inventory between all players.
    /// When one player picks up a weapon, ALL players get it.
    /// </summary>
    public class InventorySync : MonoBehaviour
    {
        public static InventorySync Instance { get; private set; }

        // Track which weapons have been synced to avoid duplicates
        private HashSet<string> _syncedWeapons = new();

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
            // Subscribe to scene changes for cleanup
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            // Clear synced weapons tracking on scene change
            ClearSyncedWeapons();
        }

        /// <summary>
        /// Called when local player picks up a weapon - broadcasts to all other players
        /// </summary>
        public void OnLocalWeaponPickup(string weaponName, bool unlockWeapon)
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
                return;

            // Don't re-broadcast weapons we received from network
            if (_syncedWeapons.Contains(weaponName))
            {
                _syncedWeapons.Remove(weaponName);
                return;
            }

            Plugin.Log.LogInfo($"[INVENTORY] Local pickup: {weaponName}, unlock={unlockWeapon}");

            // Broadcast to all players
            var packet = PacketBuilder.WeaponPickup(weaponName, unlockWeapon);
            NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        /// <summary>
        /// Called when receiving a weapon pickup from another player
        /// </summary>
        public void OnRemoteWeaponPickup(string weaponName, bool unlockWeapon)
        {
            Plugin.Log.LogInfo($"[INVENTORY] Remote pickup received: {weaponName}, unlock={unlockWeapon}");
            
            if (EquippablesManager.instance == null)
            {
                Plugin.Log.LogWarning($"[INVENTORY] Cannot apply remote pickup - EquippablesManager not ready");
                return;
            }

            // Mark as synced so we don't re-broadcast when we pick it up
            _syncedWeapons.Add(weaponName);

            // Give the weapon to local player
            try
            {
                if (unlockWeapon)
                {
                    // Check if already unlocked
                    var existing = EquippablesManager.instance.GetEquippableByName(weaponName, true);
                    if (existing != null)
                    {
                        Plugin.Log.LogInfo($"[INVENTORY] Weapon already unlocked: {weaponName}");
                        return;
                    }
                    
                    // Unlock the weapon (adds to inventory permanently)
                    EquippablesManager.instance.UnlockEquippable(weaponName);
                    Plugin.Log.LogInfo($"[INVENTORY] Unlocked weapon: {weaponName}");
                }
                else
                {
                    // Just switch to it temporarily (for combat challenges etc)
                    var profile = EquippablesManager.instance.GetEquippableByName(weaponName, false);
                    if (profile != null)
                    {
                        // Don't auto-equip, just unlock
                        EquippablesManager.instance.UnlockEquippable(weaponName);
                        Plugin.Log.LogInfo($"[INVENTORY] Unlocked weapon (non-permanent): {weaponName}");
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[INVENTORY] Could not find weapon profile: {weaponName}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[INVENTORY] Failed to unlock weapon {weaponName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sync full inventory state (used when joining a game in progress)
        /// </summary>
        public void SendFullInventory()
        {
            if (EquippablesManager.instance == null || NetworkManager.Instance == null)
                return;

            var acquired = EquippablesManager.instance.acquiredEquippables;
            foreach (var weapon in acquired)
            {
                var packet = PacketBuilder.WeaponPickup(weapon.name, true);
                NetworkManager.Instance.Broadcast(packet, reliable: true);
            }

            Plugin.Log.LogInfo($"[INVENTORY] Sent full inventory: {acquired.Count} weapons");
        }

        /// <summary>
        /// Clear synced weapons tracking (on scene change)
        /// </summary>
        public void ClearSyncedWeapons()
        {
            _syncedWeapons.Clear();
        }
    }
}
