using System.Collections.Concurrent;
using System.IO;
using UnityEngine;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Helper class for building network packets.
    /// Uses MemoryStream pooling to reduce GC allocations.
    /// </summary>
    public static class PacketBuilder
    {
        #region MemoryStream Pool

        private const int POOL_SIZE = 16;
        private const int INITIAL_STREAM_CAPACITY = 128;
        
        private static readonly ConcurrentBag<MemoryStream> _streamPool = new();
        
        private static MemoryStream GetStream()
        {
            if (_streamPool.TryTake(out var stream))
            {
                stream.SetLength(0);
                stream.Position = 0;
                return stream;
            }
            return new MemoryStream(INITIAL_STREAM_CAPACITY);
        }
        
        private static void ReturnStream(MemoryStream stream)
        {
            if (_streamPool.Count < POOL_SIZE)
            {
                _streamPool.Add(stream);
            }
            else
            {
                stream.Dispose();
            }
        }
        
        private static byte[] ToArrayAndReturn(MemoryStream stream)
        {
            byte[] result = stream.ToArray();
            ReturnStream(stream);
            return result;
        }

        #endregion

        public static byte[] Handshake(string playerName)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.Handshake);
            writer.Write(playerName);
            writer.Write(PluginInfo.PLUGIN_VERSION);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] Heartbeat()
        {
            return new[] { (byte)PacketType.Heartbeat };
        }

        public static byte[] PlayerPosition(Vector3 position)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.PlayerPosition);
            WriteVector3(writer, position);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Combined VR transform packet - sends root, head, and both hands in one packet.
        /// More efficient than 4 separate packets.
        /// </summary>
        public static byte[] FullVRTransform(Vector3 rootPos, Vector3 headPos, Quaternion headRot, 
            Vector3 leftHandPos, Quaternion leftHandRot, Vector3 rightHandPos, Quaternion rightHandRot)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.FullVRTransform);
            WriteVector3(writer, rootPos);
            WriteVector3(writer, headPos);
            WriteQuaternion(writer, headRot);
            WriteVector3(writer, leftHandPos);
            WriteQuaternion(writer, leftHandRot);
            WriteVector3(writer, rightHandPos);
            WriteQuaternion(writer, rightHandRot);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] HeadTransform(Vector3 position, Quaternion rotation)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.HeadTransform);
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] LeftHandTransform(Vector3 position, Quaternion rotation)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.LeftHandTransform);
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] RightHandTransform(Vector3 position, Quaternion rotation)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.RightHandTransform);
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] PlayerDamage(float damage, Vector3 hitPoint, float currentHealth, float maxHealth)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.PlayerDamage);
            writer.Write(damage);
            WriteVector3(writer, hitPoint);
            writer.Write(currentHealth);
            writer.Write(maxHealth);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] PlayerDeath()
        {
            return new[] { (byte)PacketType.PlayerDeath };
        }

        public static byte[] PlayerRespawn(Vector3 spawnPosition)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.PlayerRespawn);
            WriteVector3(writer, spawnPosition);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] PlayerState(float health, float maxHealth, bool isDead)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.PlayerState);
            writer.Write(health);
            writer.Write(maxHealth);
            writer.Write(isDead);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] WeaponSwitch(string weaponId)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.WeaponSwitch);
            writer.Write(weaponId ?? "none");
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] WeaponFire(string weaponId, Vector3 direction)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.WeaponFire);
            writer.Write(weaponId ?? "unknown");
            WriteVector3(writer, direction);
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] WeaponReload(string weaponId)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.WeaponReload);
            writer.Write(weaponId ?? "unknown");
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] SceneChange(string sceneName)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.SceneChange);
            writer.Write(sceneName ?? "unknown");
            
            return ToArrayAndReturn(ms);
        }

        public static byte[] CharacterSelect(string characterId)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.CharacterSelect);
            writer.Write(characterId ?? "default");
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Hand grip/finger curl packet - sends curl values for all 10 fingers.
        /// </summary>
        public static byte[] HandGrip(float lThumb, float lIndex, float lMiddle, float lRing, float lPinky,
            float rThumb, float rIndex, float rMiddle, float rRing, float rPinky)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.HandGrip);
            writer.Write(lThumb);
            writer.Write(lIndex);
            writer.Write(lMiddle);
            writer.Write(lRing);
            writer.Write(lPinky);
            writer.Write(rThumb);
            writer.Write(rIndex);
            writer.Write(rMiddle);
            writer.Write(rRing);
            writer.Write(rPinky);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Weapon equip packet - which weapons are in which hands.
        /// </summary>
        public static byte[] WeaponEquip(string leftWeaponId, string rightWeaponId)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.WeaponEquip);
            writer.Write(leftWeaponId ?? "none");
            writer.Write(rightWeaponId ?? "none");
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Scene ready packet - player finished loading a scene.
        /// </summary>
        public static byte[] SceneReady(string sceneName)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.SceneReady);
            writer.Write(sceneName ?? "unknown");
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Interactable state packet - sync doors, levers, buttons.
        /// </summary>
        public static byte[] InteractableState(string interactableId, byte stateType, float stateValue)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.InteractableState);
            writer.Write(interactableId);
            writer.Write(stateType);
            writer.Write(stateValue);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Weapon pickup packet - sync weapon pickups to all players.
        /// </summary>
        public static byte[] WeaponPickup(string weaponName, bool unlockWeapon)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.WeaponPickup);
            writer.Write(weaponName ?? "unknown");
            writer.Write(unlockWeapon);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Enemy spawn packet - host tells clients about new enemy.
        /// </summary>
        public static byte[] EnemySpawn(int enemyId, string enemyType, Vector3 position, Quaternion rotation)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.EnemySpawn);
            writer.Write(enemyId);
            writer.Write(enemyType ?? "Unknown");
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Enemy state packet - position/rotation/health update.
        /// </summary>
        public static byte[] EnemyState(int enemyId, Vector3 position, Quaternion rotation, float health)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.EnemyState);
            writer.Write(enemyId);
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            writer.Write(health);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Enemy death packet.
        /// </summary>
        public static byte[] EnemyDeath(int enemyId)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.EnemyDeath);
            writer.Write(enemyId);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Remote player damage packet - host tells client they took damage from enemy.
        /// </summary>
        public static byte[] RemotePlayerDamage(Steamworks.CSteamID targetPlayer, float damage, Vector3 hitPoint)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.RemotePlayerDamage);
            writer.Write(targetPlayer.m_SteamID);
            writer.Write(damage);
            WriteVector3(writer, hitPoint);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Checkpoint packet - host tells clients about save/checkpoint.
        /// </summary>
        public static byte[] Checkpoint(string sceneName, Vector3 position, bool isAutosave)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.Checkpoint);
            writer.Write(sceneName ?? "unknown");
            WriteVector3(writer, position);
            writer.Write(isAutosave);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Progress string packet - sync story flags between players.
        /// </summary>
        public static byte[] ProgressString(string progressString, bool isSet)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.ProgressString);
            writer.Write(progressString ?? "");
            writer.Write(isSet);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Timeline state packet - sync cutscene playback.
        /// </summary>
        public static byte[] TimelineState(string timelineId, bool isPlaying, float time, float duration)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.TimelineState);
            writer.Write(timelineId ?? "unknown");
            writer.Write(isPlaying);
            writer.Write(time);
            writer.Write(duration);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Dialog play packet - sync NPC dialog lines.
        /// </summary>
        public static byte[] DialogPlay(string characterName, string lineName, float startTime)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.DialogPlay);
            writer.Write(characterName ?? "unknown");
            writer.Write(lineName ?? "unknown");
            writer.Write(startTime);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Trigger activation packet - sync story triggers.
        /// </summary>
        public static byte[] TriggerActivation(string triggerId, bool entered)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.TriggerActivation);
            writer.Write(triggerId ?? "unknown");
            writer.Write(entered);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Vehicle state packet - sync elevators, trains, gondolas, boats.
        /// </summary>
        public static byte[] VehicleState(string vehicleId, byte vehicleType, Vector3 position, Quaternion rotation, float state)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.VehicleState);
            writer.Write(vehicleId ?? "unknown");
            writer.Write(vehicleType);
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            writer.Write(state);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Physics object state packet - sync throwable/grabbable objects.
        /// </summary>
        public static byte[] PhysicsObjectState(int objectId, string objectPath, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.PhysicsObjectState);
            writer.Write(objectId);
            writer.Write(objectPath ?? "unknown");
            WriteVector3(writer, position);
            WriteQuaternion(writer, rotation);
            WriteVector3(writer, velocity);
            WriteVector3(writer, angularVelocity);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Physics object grab packet - sync when objects are grabbed/released.
        /// </summary>
        public static byte[] PhysicsObjectGrab(int objectId, bool grabbed, Vector3 position)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.PhysicsObjectGrab);
            writer.Write(objectId);
            writer.Write(grabbed);
            WriteVector3(writer, position);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Train state packet - sync train position, velocity, and targetPosition.
        /// </summary>
        public static byte[] TrainState(string trainId, float position, float velocity, float targetPosition)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.TrainState);
            writer.Write(trainId ?? "unknown");
            writer.Write(position);
            writer.Write(velocity);
            writer.Write(targetPosition);
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Request host position packet - client asks host for their position on join.
        /// </summary>
        public static byte[] RequestHostPosition(string currentScene)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.RequestHostPosition);
            writer.Write(currentScene ?? "unknown");
            
            return ToArrayAndReturn(ms);
        }

        /// <summary>
        /// Host position response packet - host sends position to newly joined client.
        /// </summary>
        public static byte[] HostPositionResponse(string sceneName, Vector3 position, float yRotation)
        {
            var ms = GetStream();
            using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            writer.Write((byte)PacketType.HostPositionResponse);
            writer.Write(sceneName ?? "unknown");
            WriteVector3(writer, position);
            writer.Write(yRotation);
            
            return ToArrayAndReturn(ms);
        }

        #region Helpers

        private static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
        }

        private static void WriteQuaternion(BinaryWriter writer, Quaternion q)
        {
            writer.Write(q.x);
            writer.Write(q.y);
            writer.Write(q.z);
            writer.Write(q.w);
        }

        #endregion
    }
}
