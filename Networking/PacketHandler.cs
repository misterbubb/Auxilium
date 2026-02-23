using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Packet type identifiers for network communication.
    /// </summary>
    public enum PacketType : byte
    {
        // Core
        Handshake = 0,
        Heartbeat = 1,
        
        // Player sync
        PlayerPosition = 10,
        PlayerRotation = 11,
        PlayerState = 12,
        PlayerAnimation = 13,
        FullVRTransform = 14, // Combined head + hands in one packet
        
        // VR specific
        HeadTransform = 20,
        LeftHandTransform = 21,
        RightHandTransform = 22,
        HandGrip = 23, // Finger curl values for both hands
        
        // Game events
        PlayerDamage = 30,
        PlayerDeath = 31,
        PlayerRespawn = 32,
        
        // Weapon sync
        WeaponSwitch = 50,
        WeaponFire = 51,
        WeaponReload = 52,
        WeaponEquip = 53, // Which weapon is equipped in which hand
        WeaponPickup = 54, // Weapon picked up - sync to all players
        
        // Scene sync
        SceneChange = 60,
        SceneReady = 61, // Player finished loading scene
        SceneWait = 62, // Host telling clients to wait
        
        // Character sync
        CharacterSelect = 65,
        
        // Voice
        VoiceData = 70,
        
        // World sync
        ObjectSync = 40,
        EnemySync = 41,
        InteractionSync = 42, // Doors, levers, buttons
        InteractableState = 43, // State of an interactable object
        EnemySpawn = 44, // Enemy spawned
        EnemyState = 45, // Enemy position/health update
        EnemyDeath = 46, // Enemy died
        RemotePlayerDamage = 47, // Damage to remote player from enemy
        
        // Story sync
        Checkpoint = 80, // Save/checkpoint sync
        ProgressString = 81, // Story progress flag
        TimelineState = 82, // Cutscene/timeline sync
        
        // Join sync
        RequestHostPosition = 85, // Client requests host position on join
        HostPositionResponse = 86, // Host sends position to newly joined client
        
        // World sync (NPC dialog, triggers, vehicles, physics)
        DialogPlay = 90, // NPC dialog line playing
        TriggerActivation = 91, // Story trigger activated
        VehicleState = 92, // Vehicle/platform position sync
        PhysicsObjectState = 93, // Physics object position/velocity
        PhysicsObjectGrab = 94, // Physics object grabbed/released
        TrainState = 95, // Train position/velocity/targetPosition sync
        
        // Custom
        Custom = 255
    }

    /// <summary>
    /// Handles incoming network packets and routes them to appropriate handlers.
    /// Includes packet validation to prevent crashes from malformed data.
    /// </summary>
    public static class PacketHandler
    {
        #region Constants
        
        // Minimum packet sizes for validation (packet type byte + required data)
        private const int MIN_PACKET_SIZE = 1;
        private const int VECTOR3_SIZE = 12; // 3 floats
        private const int QUATERNION_SIZE = 16; // 4 floats
        private const int FULL_VR_TRANSFORM_SIZE = 1 + (VECTOR3_SIZE * 4) + (QUATERNION_SIZE * 3); // 93 bytes
        private const int HAND_GRIP_SIZE = 1 + (10 * 4); // 41 bytes (10 floats)
        private const int TRANSFORM_SIZE = 1 + VECTOR3_SIZE + QUATERNION_SIZE; // 29 bytes
        
        // Logging
        private const int VR_TRANSFORM_LOG_INTERVAL = 60;
        
        #endregion

        private static Dictionary<PacketType, Action<BinaryReader, CSteamID>> _handlers = new();
        private static int _vrTransformLogCounter = 0;

        static PacketHandler()
        {
            RegisterDefaultHandlers();
        }

        private static void RegisterDefaultHandlers()
        {
            Register(PacketType.Handshake, HandleHandshake);
            Register(PacketType.Heartbeat, HandleHeartbeat);
            Register(PacketType.PlayerPosition, HandlePlayerPosition);
            Register(PacketType.PlayerState, HandlePlayerState);
            Register(PacketType.FullVRTransform, HandleFullVRTransform);
            Register(PacketType.HeadTransform, HandleHeadTransform);
            Register(PacketType.LeftHandTransform, HandleLeftHandTransform);
            Register(PacketType.RightHandTransform, HandleRightHandTransform);
            Register(PacketType.HandGrip, HandleHandGrip);
            Register(PacketType.PlayerDamage, HandlePlayerDamage);
            Register(PacketType.PlayerDeath, HandlePlayerDeath);
            Register(PacketType.PlayerRespawn, HandlePlayerRespawn);
            Register(PacketType.WeaponSwitch, HandleWeaponSwitch);
            Register(PacketType.WeaponFire, HandleWeaponFire);
            Register(PacketType.WeaponEquip, HandleWeaponEquip);
            Register(PacketType.SceneChange, HandleSceneChange);
            Register(PacketType.SceneReady, HandleSceneReady);
            Register(PacketType.CharacterSelect, HandleCharacterSelect);
            Register(PacketType.InteractableState, HandleInteractableState);
            Register(PacketType.WeaponPickup, HandleWeaponPickup);
            Register(PacketType.EnemySpawn, HandleEnemySpawn);
            Register(PacketType.EnemyState, HandleEnemyState);
            Register(PacketType.EnemyDeath, HandleEnemyDeath);
            Register(PacketType.RemotePlayerDamage, HandleRemotePlayerDamage);
            Register(PacketType.Checkpoint, HandleCheckpoint);
            Register(PacketType.ProgressString, HandleProgressString);
            Register(PacketType.TimelineState, HandleTimelineState);
            Register(PacketType.RequestHostPosition, HandleRequestHostPosition);
            Register(PacketType.HostPositionResponse, HandleHostPositionResponse);
            Register(PacketType.DialogPlay, HandleDialogPlay);
            Register(PacketType.TriggerActivation, HandleTriggerActivation);
            Register(PacketType.VehicleState, HandleVehicleState);
            Register(PacketType.PhysicsObjectState, HandlePhysicsObjectState);
            Register(PacketType.PhysicsObjectGrab, HandlePhysicsObjectGrab);
            Register(PacketType.TrainState, HandleTrainState);
        }

        public static void Register(PacketType type, Action<BinaryReader, CSteamID> handler)
        {
            _handlers[type] = handler;
        }

        public static void HandlePacket(byte[] data, CSteamID sender)
        {
            if (data == null || data.Length < MIN_PACKET_SIZE) return;

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            var packetType = (PacketType)reader.ReadByte();
            
            // Validate packet size before processing
            if (!ValidatePacketSize(packetType, data.Length))
            {
                Plugin.Log.LogWarning($"Invalid packet size for {packetType}: {data.Length} bytes");
                return;
            }

            if (_handlers.TryGetValue(packetType, out var handler))
            {
                try
                {
                    handler(reader, sender);
                }
                catch (EndOfStreamException)
                {
                    Plugin.Log.LogWarning($"Packet {packetType} truncated - not enough data");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Error handling packet {packetType}: {ex}");
                }
            }
            else
            {
                Plugin.Log.LogWarning($"Unhandled packet type: {packetType}");
            }
        }
        
        /// <summary>
        /// Validates that a packet has the minimum required size for its type.
        /// </summary>
        private static bool ValidatePacketSize(PacketType type, int size)
        {
            int minSize = type switch
            {
                PacketType.Heartbeat => 1,
                PacketType.PlayerDeath => 1,
                PacketType.FullVRTransform => FULL_VR_TRANSFORM_SIZE,
                PacketType.HeadTransform => TRANSFORM_SIZE,
                PacketType.LeftHandTransform => TRANSFORM_SIZE,
                PacketType.RightHandTransform => TRANSFORM_SIZE,
                PacketType.HandGrip => HAND_GRIP_SIZE,
                PacketType.PlayerPosition => 1 + VECTOR3_SIZE,
                PacketType.PlayerState => 1 + 4 + 4 + 1, // health, maxHealth, isDead
                PacketType.PlayerDamage => 1 + 4 + VECTOR3_SIZE + 4 + 4, // damage, hitPoint, health, maxHealth
                PacketType.PlayerRespawn => 1 + VECTOR3_SIZE,
                PacketType.EnemyState => 1 + 4 + VECTOR3_SIZE + QUATERNION_SIZE + 4, // id, pos, rot, health
                PacketType.EnemyDeath => 1 + 4, // id
                _ => MIN_PACKET_SIZE // For string-based packets, just require the type byte
            };
            
            return size >= minSize;
        }

        #region Default Handlers

        private static void HandleHandshake(BinaryReader reader, CSteamID sender)
        {
            string playerName = reader.ReadString();
            string modVersion = reader.ReadString();
            
            // Verify version compatibility
            if (modVersion != PluginInfo.PLUGIN_VERSION)
            {
                Plugin.Log.LogWarning($"Version mismatch with {playerName}! Local: {PluginInfo.PLUGIN_VERSION}, Remote: {modVersion}");
            }
        }

        private static void HandleHeartbeat(BinaryReader reader, CSteamID sender)
        {
            // Keep connection alive, update last seen time
        }

        private static void HandlePlayerPosition(BinaryReader reader, CSteamID sender)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            
            PlayerManager.Instance?.UpdateRemotePlayerPosition(sender, x, y, z);
        }

        private static void HandlePlayerState(BinaryReader reader, CSteamID sender)
        {
            float health = reader.ReadSingle();
            float maxHealth = reader.ReadSingle();
            bool isDead = reader.ReadBoolean();
            
            PlayerManager.Instance?.UpdateRemotePlayerHealth(sender, health, maxHealth);
            
            if (isDead)
            {
                PlayerManager.Instance?.HandleRemotePlayerDeath(sender);
            }
        }

        private static void HandleFullVRTransform(BinaryReader reader, CSteamID sender)
        {
            // Validate sender
            if (!sender.IsValid() || sender == CSteamID.Nil)
            {
                if (_vrTransformLogCounter++ % VR_TRANSFORM_LOG_INTERVAL == 0)
                {
                    Plugin.Log.LogWarning($"[RECV] FullVRTransform with invalid sender: {sender}");
                }
                return;
            }
            
            // Root position
            float rootX = reader.ReadSingle();
            float rootY = reader.ReadSingle();
            float rootZ = reader.ReadSingle();
            
            // Head position + rotation
            float headPX = reader.ReadSingle();
            float headPY = reader.ReadSingle();
            float headPZ = reader.ReadSingle();
            float headRX = reader.ReadSingle();
            float headRY = reader.ReadSingle();
            float headRZ = reader.ReadSingle();
            float headRW = reader.ReadSingle();
            
            // Left hand position + rotation
            float lhPX = reader.ReadSingle();
            float lhPY = reader.ReadSingle();
            float lhPZ = reader.ReadSingle();
            float lhRX = reader.ReadSingle();
            float lhRY = reader.ReadSingle();
            float lhRZ = reader.ReadSingle();
            float lhRW = reader.ReadSingle();
            
            // Right hand position + rotation
            float rhPX = reader.ReadSingle();
            float rhPY = reader.ReadSingle();
            float rhPZ = reader.ReadSingle();
            float rhRX = reader.ReadSingle();
            float rhRY = reader.ReadSingle();
            float rhRZ = reader.ReadSingle();
            float rhRW = reader.ReadSingle();
            
            // Debug: Log received transforms periodically
            if (_vrTransformLogCounter++ % VR_TRANSFORM_LOG_INTERVAL == 0)
            {
                string senderName = SteamFriends.GetFriendPersonaName(sender);
                Plugin.Log.LogInfo($"[RECV] FullVRTransform from {senderName} ({sender}): head=({headPX:F2},{headPY:F2},{headPZ:F2})");
            }
            
            var pm = PlayerManager.Instance;
            if (pm == null) 
            {
                Plugin.Log.LogWarning("[RECV] PlayerManager.Instance is null!");
                return;
            }
            
            pm.UpdateRemotePlayerPosition(sender, rootX, rootY, rootZ);
            pm.UpdateRemotePlayerHead(sender, headPX, headPY, headPZ, headRX, headRY, headRZ, headRW);
            pm.UpdateRemotePlayerLeftHand(sender, lhPX, lhPY, lhPZ, lhRX, lhRY, lhRZ, lhRW);
            pm.UpdateRemotePlayerRightHand(sender, rhPX, rhPY, rhPZ, rhRX, rhRY, rhRZ, rhRW);
        }

        private static void HandleHeadTransform(BinaryReader reader, CSteamID sender)
        {
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            
            PlayerManager.Instance?.UpdateRemotePlayerHead(sender, px, py, pz, rx, ry, rz, rw);
        }

        private static void HandleLeftHandTransform(BinaryReader reader, CSteamID sender)
        {
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            
            PlayerManager.Instance?.UpdateRemotePlayerLeftHand(sender, px, py, pz, rx, ry, rz, rw);
        }

        private static void HandleRightHandTransform(BinaryReader reader, CSteamID sender)
        {
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            
            PlayerManager.Instance?.UpdateRemotePlayerRightHand(sender, px, py, pz, rx, ry, rz, rw);
        }

        private static void HandlePlayerDamage(BinaryReader reader, CSteamID sender)
        {
            float damage = reader.ReadSingle();
            float hitX = reader.ReadSingle();
            float hitY = reader.ReadSingle();
            float hitZ = reader.ReadSingle();
            float currentHealth = reader.ReadSingle();
            float maxHealth = reader.ReadSingle();
            
            PlayerManager.Instance?.UpdateRemotePlayerHealth(sender, currentHealth, maxHealth);
        }

        private static void HandlePlayerDeath(BinaryReader reader, CSteamID sender)
        {
            PlayerManager.Instance?.HandleRemotePlayerDeath(sender);
        }

        private static void HandlePlayerRespawn(BinaryReader reader, CSteamID sender)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            
            PlayerManager.Instance?.HandleRemotePlayerRespawn(sender, new Vector3(x, y, z));
        }

        private static void HandleWeaponSwitch(BinaryReader reader, CSteamID sender)
        {
            string weaponId = reader.ReadString();
            Plugin.Log.LogInfo($"[WEAPON RECV] WeaponSwitch from {sender}: {weaponId}");
            PlayerManager.Instance?.UpdateRemotePlayerWeapon(sender, weaponId);
        }

        private static void HandleWeaponFire(BinaryReader reader, CSteamID sender)
        {
            string weaponId = reader.ReadString();
            float dirX = reader.ReadSingle();
            float dirY = reader.ReadSingle();
            float dirZ = reader.ReadSingle();
            Vector3 direction = new Vector3(dirX, dirY, dirZ);
            
            Plugin.Log.LogInfo($"[WEAPON RECV] WeaponFire from {sender}: {weaponId} dir={direction}");
            PlayerManager.Instance?.HandleRemotePlayerFire(sender, weaponId, direction);
        }

        private static void HandleSceneChange(BinaryReader reader, CSteamID sender)
        {
            string sceneName = reader.ReadString();
            PlayerManager.Instance?.UpdateRemotePlayerScene(sender, sceneName);
        }

        private static void HandleCharacterSelect(BinaryReader reader, CSteamID sender)
        {
            string characterId = reader.ReadString();
            Plugin.Log.LogInfo($"[CHARACTER RECV] CharacterSelect from {sender}: {characterId}");
            PlayerManager.Instance?.UpdateRemotePlayerCharacter(sender, characterId);
        }

        private static void HandleHandGrip(BinaryReader reader, CSteamID sender)
        {
            // Left hand finger curls
            float lThumb = reader.ReadSingle();
            float lIndex = reader.ReadSingle();
            float lMiddle = reader.ReadSingle();
            float lRing = reader.ReadSingle();
            float lPinky = reader.ReadSingle();
            
            // Right hand finger curls
            float rThumb = reader.ReadSingle();
            float rIndex = reader.ReadSingle();
            float rMiddle = reader.ReadSingle();
            float rRing = reader.ReadSingle();
            float rPinky = reader.ReadSingle();
            
            PlayerManager.Instance?.UpdateRemotePlayerGrip(sender, 
                lThumb, lIndex, lMiddle, lRing, lPinky,
                rThumb, rIndex, rMiddle, rRing, rPinky);
        }

        private static void HandleWeaponEquip(BinaryReader reader, CSteamID sender)
        {
            string leftWeaponId = reader.ReadString();
            string rightWeaponId = reader.ReadString();
            
            Plugin.Log.LogInfo($"[WEAPON RECV] WeaponEquip from {sender}: L={leftWeaponId}, R={rightWeaponId}");
            PlayerManager.Instance?.UpdateRemotePlayerWeapons(sender, leftWeaponId, rightWeaponId);
        }

        private static void HandleSceneReady(BinaryReader reader, CSteamID sender)
        {
            string sceneName = reader.ReadString();
            PlayerManager.Instance?.HandleRemotePlayerSceneReady(sender, sceneName);
        }

        private static void HandleInteractableState(BinaryReader reader, CSteamID sender)
        {
            string interactableId = reader.ReadString();
            byte stateType = reader.ReadByte();
            float stateValue = reader.ReadSingle();
            
            InteractableSync.Instance?.HandleInteractableState(interactableId, stateType, stateValue);
        }

        private static void HandleWeaponPickup(BinaryReader reader, CSteamID sender)
        {
            string weaponName = reader.ReadString();
            bool unlockWeapon = reader.ReadBoolean();
            
            InventorySync.Instance?.OnRemoteWeaponPickup(weaponName, unlockWeapon);
        }

        private static void HandleEnemySpawn(BinaryReader reader, CSteamID sender)
        {
            int enemyId = reader.ReadInt32();
            string enemyType = reader.ReadString();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            
            EnemySync.Instance?.HandleEnemySpawn(enemyId, enemyType, 
                new Vector3(px, py, pz), new Quaternion(rx, ry, rz, rw));
        }

        private static void HandleEnemyState(BinaryReader reader, CSteamID sender)
        {
            int enemyId = reader.ReadInt32();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            float health = reader.ReadSingle();
            
            EnemySync.Instance?.HandleEnemyState(enemyId, 
                new Vector3(px, py, pz), new Quaternion(rx, ry, rz, rw), health);
        }

        private static void HandleEnemyDeath(BinaryReader reader, CSteamID sender)
        {
            int enemyId = reader.ReadInt32();
            EnemySync.Instance?.HandleEnemyDeath(enemyId);
        }

        private static void HandleRemotePlayerDamage(BinaryReader reader, CSteamID sender)
        {
            ulong targetSteamId = reader.ReadUInt64();
            float damage = reader.ReadSingle();
            float hpx = reader.ReadSingle();
            float hpy = reader.ReadSingle();
            float hpz = reader.ReadSingle();
            
            EnemySync.Instance?.HandleRemotePlayerDamage(
                new CSteamID(targetSteamId), damage, new Vector3(hpx, hpy, hpz));
        }

        private static void HandleCheckpoint(BinaryReader reader, CSteamID sender)
        {
            string sceneName = reader.ReadString();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            bool isAutosave = reader.ReadBoolean();
            
            StorySync.Instance?.HandleCheckpoint(sceneName, new Vector3(px, py, pz), isAutosave);
        }

        private static void HandleProgressString(BinaryReader reader, CSteamID sender)
        {
            string progressString = reader.ReadString();
            bool isSet = reader.ReadBoolean();
            
            StorySync.Instance?.HandleProgressString(progressString, isSet);
        }

        private static void HandleTimelineState(BinaryReader reader, CSteamID sender)
        {
            string timelineId = reader.ReadString();
            bool isPlaying = reader.ReadBoolean();
            float time = reader.ReadSingle();
            float duration = reader.ReadSingle();
            
            StorySync.Instance?.HandleTimelineState(timelineId, isPlaying, time, duration);
        }

        private static void HandleDialogPlay(BinaryReader reader, CSteamID sender)
        {
            string characterName = reader.ReadString();
            string lineName = reader.ReadString();
            float startTime = reader.ReadSingle();
            
            WorldSync.Instance?.HandleDialogPlay(characterName, lineName, startTime);
        }

        private static void HandleTriggerActivation(BinaryReader reader, CSteamID sender)
        {
            string triggerId = reader.ReadString();
            bool entered = reader.ReadBoolean();
            
            WorldSync.Instance?.HandleTriggerActivation(triggerId, entered);
        }

        private static void HandleVehicleState(BinaryReader reader, CSteamID sender)
        {
            string vehicleId = reader.ReadString();
            byte vehicleType = reader.ReadByte();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            float state = reader.ReadSingle();
            
            WorldSync.Instance?.HandleVehicleState(vehicleId, vehicleType, 
                new Vector3(px, py, pz), new Quaternion(rx, ry, rz, rw), state);
        }

        private static void HandlePhysicsObjectState(BinaryReader reader, CSteamID sender)
        {
            int objectId = reader.ReadInt32();
            string objectPath = reader.ReadString();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float rx = reader.ReadSingle();
            float ry = reader.ReadSingle();
            float rz = reader.ReadSingle();
            float rw = reader.ReadSingle();
            float vx = reader.ReadSingle();
            float vy = reader.ReadSingle();
            float vz = reader.ReadSingle();
            float avx = reader.ReadSingle();
            float avy = reader.ReadSingle();
            float avz = reader.ReadSingle();
            
            WorldSync.Instance?.HandlePhysicsObjectState(objectId, objectPath,
                new Vector3(px, py, pz), new Quaternion(rx, ry, rz, rw),
                new Vector3(vx, vy, vz), new Vector3(avx, avy, avz));
        }

        private static void HandlePhysicsObjectGrab(BinaryReader reader, CSteamID sender)
        {
            int objectId = reader.ReadInt32();
            bool grabbed = reader.ReadBoolean();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            
            WorldSync.Instance?.HandlePhysicsObjectGrab(objectId, grabbed, new Vector3(px, py, pz));
        }

        private static void HandleRequestHostPosition(BinaryReader reader, CSteamID sender)
        {
            string sceneName = reader.ReadString();
            
            // Only host responds to this
            if (!NetworkManager.Instance?.IsHost ?? true) return;
            
            Plugin.Log.LogInfo($"[JOIN] Received position request from {SteamFriends.GetFriendPersonaName(sender)} for scene {sceneName}");
            
            // Send our position to the requesting player
            PlayerManager.Instance?.SendHostPositionToPlayer(sender, sceneName);
        }

        private static void HandleHostPositionResponse(BinaryReader reader, CSteamID sender)
        {
            string sceneName = reader.ReadString();
            float px = reader.ReadSingle();
            float py = reader.ReadSingle();
            float pz = reader.ReadSingle();
            float ry = reader.ReadSingle(); // Y rotation for facing direction
            
            Plugin.Log.LogInfo($"[JOIN] Received host position: scene={sceneName}, pos=({px},{py},{pz}), rotY={ry}");
            
            // Teleport to host position
            PlayerManager.Instance?.TeleportToHostPosition(sceneName, new Vector3(px, py, pz), ry);
        }

        private static void HandleTrainState(BinaryReader reader, CSteamID sender)
        {
            string trainId = reader.ReadString();
            float position = reader.ReadSingle();
            float velocity = reader.ReadSingle();
            float targetPosition = reader.ReadSingle();
            
            WorldSync.Instance?.HandleTrainState(trainId, position, velocity, targetPosition);
        }

        #endregion
    }
}
