using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using Vertigo2;
using Vertigo2.Weapons;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Replicates player projectiles and ammunition state. The host simulates
    /// remote-player damage; other clients render non-damaging projectile copies.
    /// </summary>
    public class WeaponSync : MonoBehaviour
    {
        public static WeaponSync Instance { get; private set; }

        private static readonly HashSet<Bullet> VisualOnlyBullets = new();
        private readonly Dictionary<Gun, int> _lastLocalRounds = new();

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        public static string GetWeaponId(Equippable equippable)
        {
            if (equippable == null) return "unknown";

            var manager = PlayerManager.Instance?.LocalPlayer?.equippablesManager;
            if (manager != null)
            {
                var hand = manager.GetHand(equippable.inputSource);
                var current = hand?.currentEquippable;
                if (current?.eqip == equippable && current.profile != null)
                    return current.profile.name;
            }

            return equippable.GetType().Name;
        }

        public static byte GetShooterIndex(Equippable equippable, Shooter shooter)
        {
            if (equippable == null || shooter == null)
                return 0;

            var manager = PlayerManager.Instance?.LocalPlayer?.equippablesManager;
            var hand = manager != null ? manager.GetHand(equippable.inputSource) : null;
            var current = hand?.currentEquippable;
            if (current?.eqip != equippable || current.gameObject == null)
                return 0;

            var shooters = current.gameObject.GetComponentsInChildren<Shooter>(true);
            for (int i = 0; i < shooters.Length; i++)
            {
                if (shooters[i] == shooter)
                    return (byte)i;
            }

            Plugin.Log.LogWarning($"[WEAPON] Shooter '{shooter.name}' is outside its equippable hierarchy; using projectile variant 0");
            return 0;
        }

        public static void MarkVisualOnly(Bullet bullet)
        {
            if (bullet != null)
                VisualOnlyBullets.Add(bullet);
        }

        public static bool IsVisualOnly(Bullet bullet)
        {
            return bullet != null && VisualOnlyBullets.Contains(bullet);
        }

        public static void ForgetBullet(Bullet bullet)
        {
            if (bullet != null)
                VisualOnlyBullets.Remove(bullet);
        }


        public void HandleProjectileSpawn(CSteamID sender, string weaponId, byte shooterIndex,
            Vector3 position, Vector3 direction, float powerMultiplier,
            float speedMultiplier, float rangeMultiplier, float bulletSpeed)
        {
            var network = NetworkManager.Instance;
            if (network == null || !network.IsConnected || sender == network.LocalSteamID)
                return;

            bool authoritative = network.IsHost;
            if (authoritative && (PlayerManager.Instance == null ||
                !PlayerManager.Instance.IsRemoteWeaponEquipped(sender, weaponId)))
            {
                Plugin.Log.LogWarning($"[WEAPON] Ignored projectile from {sender}: {weaponId} is not equipped");
                return;
            }

            if (!IsFinite(position) || !IsFinite(direction) || direction.sqrMagnitude < 0.0001f ||
                !IsFinite(powerMultiplier) || !IsFinite(speedMultiplier) ||
                !IsFinite(rangeMultiplier) || !IsFinite(bulletSpeed))
            {
                Plugin.Log.LogWarning($"[WEAPON] Ignored invalid projectile packet from {sender}");
                return;
            }

            SpawnProjectile(sender, weaponId, shooterIndex, position, direction.normalized,
                powerMultiplier, speedMultiplier, rangeMultiplier, bulletSpeed, authoritative);
        }

        private void SpawnProjectile(CSteamID sender, string weaponId, byte shooterIndex,
            Vector3 position,
            Vector3 direction, float powerMultiplier, float speedMultiplier,
            float rangeMultiplier, float bulletSpeed, bool authoritative)
        {
            var equippables = EquippablesManager.instance;
            var profile = equippables != null ? equippables.GetEquippableByName(weaponId) : null;
            var shooters = profile?.prefab != null
                ? profile.prefab.GetComponentsInChildren<Shooter>(true)
                : System.Array.Empty<Shooter>();
            var shooter = shooterIndex < shooters.Length ? shooters[shooterIndex] : null;

            if (shooter == null || shooter.shot == null)
            {
                Plugin.Log.LogWarning($"[WEAPON] Cannot replicate projectile variant {shooterIndex} for weapon '{weaponId}' (found {shooters.Length} shooter(s))");
                return;
            }

            VertigoEntity source = authoritative
                ? EnemySync.Instance?.GetRemotePlayerEntity(sender)
                : null;
            var rotation = Quaternion.LookRotation(direction);

            try
            {
                GameObject projectileObject = DamageSourceUtility.InstantiateDamageSource(
                    shooter.shot, source, shooter.usePooling, position, rotation);
                var bullet = projectileObject != null ? projectileObject.GetComponent<Bullet>() : null;
                if (bullet == null)
                {
                    if (projectileObject != null) Destroy(projectileObject);
                    Plugin.Log.LogWarning($"[WEAPON] Projectile prefab for '{weaponId}' has no Bullet component");
                    return;
                }

                bullet.modifier = new Bullet.BulletModifier
                {
                    powerMultiplier = powerMultiplier,
                    speedMultiplier = speedMultiplier,
                    rangeMultiplier = rangeMultiplier
                };
                bullet.speed = bulletSpeed;
                bullet.playerBullet = true;

                if (!authoritative)
                    MarkVisualOnly(bullet);

                bullet.Initialize();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[WEAPON] Failed to create projectile for '{weaponId}': {ex}");
            }
        }

        public void SyncLocalAmmoState()
        {
            var network = NetworkManager.Instance;
            var manager = PlayerManager.Instance?.LocalPlayer?.equippablesManager;
            if (network == null || !network.IsConnected || manager == null)
                return;

            SyncHandAmmo(manager.leftEquippable?.currentEquippable, null, force: false);
            SyncHandAmmo(manager.rightEquippable?.currentEquippable, null, force: false);
        }

        public void SendCurrentAmmoState(CSteamID? targetPlayer = null)
        {
            var network = NetworkManager.Instance;
            var manager = PlayerManager.Instance?.LocalPlayer?.equippablesManager;
            if (network == null || !network.IsConnected || manager == null)
                return;

            SyncHandAmmo(manager.leftEquippable?.currentEquippable, targetPlayer, force: true);
            SyncHandAmmo(manager.rightEquippable?.currentEquippable, targetPlayer, force: true);
        }

        private void SyncHandAmmo(EquippablesManager.EquippableInstance instance,
            CSteamID? targetPlayer, bool force)
        {
            var gun = instance?.eqip as Gun;
            if (gun == null || instance.profile == null)
                return;

            bool hasPrevious = _lastLocalRounds.TryGetValue(gun, out int previousRounds);
            if (!force && hasPrevious && previousRounds == gun.rounds)
                return;

            _lastLocalRounds[gun] = gun.rounds;
            bool reloaded = !force && hasPrevious && gun.rounds > previousRounds;
            byte[] packet = reloaded
                ? PacketBuilder.WeaponReload(instance.profile.name, gun.rounds, gun.capacity)
                : PacketBuilder.WeaponAmmo(instance.profile.name, gun.rounds, gun.capacity);
            if (targetPlayer.HasValue && NetworkManager.Instance.IsHost)
                NetworkManager.Instance.SendTo(targetPlayer.Value, packet, reliable: true);
            else
                NetworkManager.Instance.Broadcast(packet, reliable: true);
        }

        public void HandleRemoteAmmo(CSteamID sender, string weaponId, int rounds, int capacity, bool reloaded)
        {
            if (string.IsNullOrEmpty(weaponId) || capacity < 0 || rounds < 0 || rounds > capacity)
            {
                Plugin.Log.LogWarning($"[WEAPON] Ignored invalid ammo state from {sender}");
                return;
            }

            PlayerManager.Instance?.UpdateRemoteWeaponAmmo(sender, weaponId, rounds, capacity, reloaded);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

    }
}
