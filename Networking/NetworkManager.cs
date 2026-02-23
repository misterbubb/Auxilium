using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
using Vertigo2.Steamworks;
using Vertigo2Multiplayer.UI;

namespace Vertigo2Multiplayer
{
    /// <summary>
    /// Manages Steam P2P networking for multiplayer sessions.
    /// Handles lobby creation/joining, connection management, and message routing.
    /// </summary>
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

        #region Constants

        private const int MAX_MESSAGES_PER_FRAME = 32;
        private const int SEND_FLAG_UNRELIABLE = 0;
        private const int SEND_FLAG_RELIABLE = 8;
        private const float CONNECTION_TIMEOUT_SECONDS = 15f;
        private const float LOBBY_SEARCH_RETRY_SECONDS = 3f;
        private const float RECONNECT_DELAY_SECONDS = 2f;
        private const int MAX_RECONNECT_ATTEMPTS = 3;
        
        // Steam ID validation range
        private const ulong STEAM_ID_MIN = 76561197960265728UL;
        private const ulong STEAM_ID_MAX = 76561220000000000UL;

        #endregion

        #region Steam Networking

        private HSteamListenSocket _listenSocket;
        private HSteamNetConnection _connectionToHost;
        private readonly Dictionary<CSteamID, HSteamNetConnection> _clientConnections = new();
        private readonly IntPtr[] _messageBuffer = new IntPtr[MAX_MESSAGES_PER_FRAME];

        #endregion

        #region Callbacks

        private Callback<SteamNetConnectionStatusChangedCallback_t> _connectionStatusChanged;
        private Callback<LobbyCreated_t> _lobbyCreated;
        private Callback<LobbyEnter_t> _lobbyEntered;
        private Callback<GameLobbyJoinRequested_t> _lobbyJoinRequested;
        private Callback<LobbyMatchList_t> _lobbyListReceived;

        #endregion

        #region State

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsInitialized { get; private set; }
        public bool IsConnecting { get; private set; }
        public CSteamID CurrentLobby { get; private set; }
        public CSteamID LocalSteamID => SteamworksManager.Initialized ? SteamUser.GetSteamID() : CSteamID.Nil;
        public List<CSteamID> ConnectedPlayers { get; } = new();

        #endregion

        #region Events

        public event Action OnConnectedToServer;
        public event Action OnDisconnectedFromServer;
        public event Action<CSteamID> OnPlayerJoined;
        public event Action<CSteamID> OnPlayerLeft;
        public event Action<string> OnConnectionFailed;

        #endregion

        #region Connection State

        private string _targetHostSteamId;
        private float _connectionTimer;
        private int _reconnectAttempts;
        private CSteamID _lastHostId;
        private bool _wasConnected;

        #endregion

        #region Debug Counters

        // Reserved for future debug logging
        #pragma warning disable CS0169
        private int _receiveLogCounter;
        private int _senderExtractLogCounter;
        private int _processLogCounter;
        #pragma warning restore CS0169

        #endregion

        #region Unity Lifecycle

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
            if (SteamworksManager.Initialized)
            {
                InitializeSteamNetworking();
            }
            else
            {
                Plugin.Log.LogInfo("Waiting for Steam to initialize...");
                SteamworksManager.OnInitialize += OnSteamInitialized;
            }
        }

        private void Update()
        {
            if (!SteamworksManager.Initialized) return;

            // Handle connection timeout
            if (IsConnecting)
            {
                _connectionTimer += Time.unscaledDeltaTime;
                if (_connectionTimer >= CONNECTION_TIMEOUT_SECONDS)
                {
                    HandleConnectionTimeout();
                }
            }

            ReceiveMessages();
            
            // Periodic connection status log (every ~10 seconds)
            if (_connectionStatusLogCounter++ % 300 == 0 && IsConnected)
            {
                Plugin.Log.LogInfo($"[NET STATUS] ========== CONNECTION STATUS ==========");
                Plugin.Log.LogInfo($"[NET STATUS] IsHost: {IsHost}");
                Plugin.Log.LogInfo($"[NET STATUS] IsConnected: {IsConnected}");
                Plugin.Log.LogInfo($"[NET STATUS] LocalSteamID: {LocalSteamID}");
                Plugin.Log.LogInfo($"[NET STATUS] CurrentLobby: {CurrentLobby}");
                Plugin.Log.LogInfo($"[NET STATUS] ConnectedPlayers: {ConnectedPlayers.Count}");
                foreach (var player in ConnectedPlayers)
                {
                    Plugin.Log.LogInfo($"[NET STATUS]   - {SteamFriends.GetFriendPersonaName(player)} ({player})");
                }
                if (IsHost)
                {
                    Plugin.Log.LogInfo($"[NET STATUS] Client connections: {_clientConnections.Count}");
                }
                Plugin.Log.LogInfo($"[NET STATUS] ==========================================");
            }
        }
        
        private int _connectionStatusLogCounter = 0;

        private void OnDestroy()
        {
            Shutdown();
        }

        #endregion

        #region Initialization

        private void OnSteamInitialized()
        {
            SteamworksManager.OnInitialize -= OnSteamInitialized;
            InitializeSteamNetworking();
        }

        private void InitializeSteamNetworking()
        {
            if (!SteamworksManager.Initialized)
            {
                Plugin.Log.LogError("Steam not initialized! Multiplayer disabled.");
                enabled = false;
                return;
            }

            _connectionStatusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _lobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnLobbyJoinRequested);
            _lobbyListReceived = Callback<LobbyMatchList_t>.Create(OnLobbyListReceived);

            IsInitialized = true;
            Plugin.Log.LogInfo($"NetworkManager initialized. Steam ID: {LocalSteamID}");
        }

        #endregion

        #region Lobby Management

        public void CreateLobby(int maxPlayers = 4)
        {
            if (IsConnected || IsConnecting)
            {
                Plugin.Log.LogWarning("Already connected or connecting");
                return;
            }

            maxPlayers = Mathf.Clamp(maxPlayers, 2, 8);
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
        }

        public void JoinLobby(CSteamID lobbyId)
        {
            if (IsConnected || IsConnecting)
            {
                Plugin.Log.LogWarning("Already connected or connecting");
                return;
            }

            IsConnecting = true;
            _connectionTimer = 0f;
            SteamMatchmaking.JoinLobby(lobbyId);
        }

        public void JoinLobbyBySteamId(string steamIdString)
        {
            if (IsConnected || IsConnecting)
            {
                Plugin.Log.LogWarning("Already connected or connecting");
                return;
            }

            _targetHostSteamId = steamIdString;
            IsConnecting = true;
            _connectionTimer = 0f;

            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListStringFilter("game", "Vertigo2", ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.RequestLobbyList();
        }

        public void LeaveLobby()
        {
            if (!CurrentLobby.IsValid()) return;

            _wasConnected = false;
            _reconnectAttempts = 0;

            SteamMatchmaking.LeaveLobby(CurrentLobby);
            CurrentLobby = CSteamID.Nil;
            IsHost = false;
            IsConnected = false;
            IsConnecting = false;
            ConnectedPlayers.Clear();
            CloseConnections();

            SteamFriends.ClearRichPresence();
            Plugin.Log.LogInfo("Left lobby");
        }

        #endregion

        #region Steam Callbacks

        private void OnLobbyCreated(LobbyCreated_t callback)
        {
            IsConnecting = false;

            if (callback.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogError($"Failed to create lobby: {callback.m_eResult}");
                OnConnectionFailed?.Invoke($"Failed to create lobby: {callback.m_eResult}");
                return;
            }

            CurrentLobby = new CSteamID(callback.m_ulSteamIDLobby);
            IsHost = true;
            IsConnected = true;
            _wasConnected = true;

            SteamMatchmaking.SetLobbyData(CurrentLobby, "game", "Vertigo2");
            SteamMatchmaking.SetLobbyData(CurrentLobby, "version", PluginInfo.PLUGIN_VERSION);
            SteamMatchmaking.SetLobbyJoinable(CurrentLobby, true);

            SteamFriends.SetRichPresence("connect", $"+connect_lobby {CurrentLobby.m_SteamID}");
            SteamFriends.SetRichPresence("status", "Hosting Multiplayer");
            SteamFriends.SetRichPresence("steam_player_group", CurrentLobby.m_SteamID.ToString());
            SteamFriends.SetRichPresence("steam_player_group_size", "1");

            StartHosting();
            Plugin.Log.LogInfo($"Lobby created: {CurrentLobby}");
        }

        private void OnLobbyEntered(LobbyEnter_t callback)
        {
            CurrentLobby = new CSteamID(callback.m_ulSteamIDLobby);

            // Check version compatibility
            string hostVersion = SteamMatchmaking.GetLobbyData(CurrentLobby, "version");
            if (!string.IsNullOrEmpty(hostVersion) && hostVersion != PluginInfo.PLUGIN_VERSION)
            {
                Plugin.Log.LogError($"Version mismatch! Host: {hostVersion}, Local: {PluginInfo.PLUGIN_VERSION}");
                NotificationUI.Instance?.ShowCustom("VERSION MISMATCH", 
                    $"Host: {hostVersion}, You: {PluginInfo.PLUGIN_VERSION}", 
                    new Color(0.9f, 0.3f, 0.3f));
                LeaveLobby();
                OnConnectionFailed?.Invoke($"Version mismatch: Host has {hostVersion}, you have {PluginInfo.PLUGIN_VERSION}");
                return;
            }

            IsConnected = true;
            IsConnecting = false;
            _wasConnected = true;

            SteamFriends.SetRichPresence("status", IsHost ? "Hosting Multiplayer" : "In Multiplayer Game");
            SteamFriends.SetRichPresence("steam_player_group", CurrentLobby.m_SteamID.ToString());

            int numPlayers = SteamMatchmaking.GetNumLobbyMembers(CurrentLobby);
            SteamFriends.SetRichPresence("steam_player_group_size", numPlayers.ToString());

            if (!IsHost)
            {
                CSteamID hostId = SteamMatchmaking.GetLobbyOwner(CurrentLobby);
                _lastHostId = hostId;
                Plugin.Log.LogInfo($"[CLIENT] Connecting to host: {hostId} ({SteamFriends.GetFriendPersonaName(hostId)})");
                ConnectToHost(hostId);

                if (hostId != LocalSteamID && hostId.IsValid())
                {
                    OnPlayerJoined?.Invoke(hostId);
                }
            }

            Plugin.Log.LogInfo($"Entered lobby: {CurrentLobby} ({numPlayers} players)");
        }

        private void OnLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            Plugin.Log.LogInfo($"Friend join request from {callback.m_steamIDFriend}");

            if (CurrentLobby.IsValid())
            {
                LeaveLobby();
            }

            JoinLobby(callback.m_steamIDLobby);
        }

        private void OnLobbyListReceived(LobbyMatchList_t callback)
        {
            if (string.IsNullOrEmpty(_targetHostSteamId)) return;

            for (int i = 0; i < callback.m_nLobbiesMatching; i++)
            {
                CSteamID lobbyId = SteamMatchmaking.GetLobbyByIndex(i);
                CSteamID ownerId = SteamMatchmaking.GetLobbyOwner(lobbyId);

                if (ownerId.m_SteamID.ToString() == _targetHostSteamId)
                {
                    Plugin.Log.LogInfo("Found target lobby, joining...");
                    _targetHostSteamId = null;
                    SteamMatchmaking.JoinLobby(lobbyId);
                    return;
                }
            }

            StartCoroutine(RetryLobbySearch());
        }

        private System.Collections.IEnumerator RetryLobbySearch()
        {
            yield return new WaitForSecondsRealtime(LOBBY_SEARCH_RETRY_SECONDS);

            if (!string.IsNullOrEmpty(_targetHostSteamId) && !IsConnected)
            {
                if (_connectionTimer < CONNECTION_TIMEOUT_SECONDS)
                {
                    SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                    SteamMatchmaking.AddRequestLobbyListStringFilter("game", "Vertigo2", ELobbyComparison.k_ELobbyComparisonEqual);
                    SteamMatchmaking.RequestLobbyList();
                }
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            var info = callback.m_info;

            switch (info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (IsHost)
                    {
                        SteamNetworkingSockets.AcceptConnection(callback.m_hConn);
                    }
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    HandlePlayerConnected(callback.m_hConn, info.m_identityRemote.GetSteamID());
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    HandlePlayerDisconnected(callback.m_hConn, info.m_identityRemote.GetSteamID());
                    break;
            }
        }

        #endregion

        #region Connection Management

        private void StartHosting()
        {
            var options = new SteamNetworkingConfigValue_t[] { };
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
        }

        private void ConnectToHost(CSteamID hostId)
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(hostId);

            var options = new SteamNetworkingConfigValue_t[] { };
            _connectionToHost = SteamNetworkingSockets.ConnectP2P(ref identity, 0, options.Length, options);
        }

        private void HandlePlayerConnected(HSteamNetConnection connection, CSteamID steamId)
        {
            if (steamId == LocalSteamID || !steamId.IsValid() || steamId == CSteamID.Nil)
            {
                return;
            }

            string playerName = SteamFriends.GetFriendPersonaName(steamId);
            Plugin.Log.LogInfo($"Player connected: {playerName} ({steamId})");

            if (IsHost)
            {
                _clientConnections[steamId] = connection;
            }

            if (!ConnectedPlayers.Contains(steamId))
            {
                ConnectedPlayers.Add(steamId);
            }

            IsConnected = true;
            IsConnecting = false;
            _reconnectAttempts = 0;

            OnPlayerJoined?.Invoke(steamId);
            OnConnectedToServer?.Invoke();
        }

        private void HandlePlayerDisconnected(HSteamNetConnection connection, CSteamID steamId)
        {
            Plugin.Log.LogInfo($"Player disconnected: {steamId}");

            SteamNetworkingSockets.CloseConnection(connection, 0, "Disconnected", false);

            if (IsHost)
            {
                _clientConnections.Remove(steamId);
            }
            else
            {
                IsConnected = false;
                _connectionToHost = HSteamNetConnection.Invalid;

                // Attempt reconnection if we were previously connected
                if (_wasConnected && _reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                {
                    StartCoroutine(AttemptReconnect());
                }
            }

            ConnectedPlayers.Remove(steamId);
            OnPlayerLeft?.Invoke(steamId);
            OnDisconnectedFromServer?.Invoke();
        }

        private void HandleConnectionTimeout()
        {
            Plugin.Log.LogWarning("Connection timed out");
            IsConnecting = false;
            _targetHostSteamId = null;

            NotificationUI.Instance?.ShowCustom("CONNECTION FAILED", "Timed out", new Color(0.9f, 0.3f, 0.3f));
            OnConnectionFailed?.Invoke("Connection timed out");

            if (CurrentLobby.IsValid())
            {
                LeaveLobby();
            }
        }

        private System.Collections.IEnumerator AttemptReconnect()
        {
            _reconnectAttempts++;
            Plugin.Log.LogInfo($"Attempting reconnect ({_reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS})...");
            NotificationUI.Instance?.ShowCustom("RECONNECTING", $"Attempt {_reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS}", new Color(0.9f, 0.6f, 0.2f));

            yield return new WaitForSecondsRealtime(RECONNECT_DELAY_SECONDS);

            if (!IsConnected && _lastHostId.IsValid())
            {
                ConnectToHost(_lastHostId);

                yield return new WaitForSecondsRealtime(CONNECTION_TIMEOUT_SECONDS);

                if (!IsConnected && _reconnectAttempts < MAX_RECONNECT_ATTEMPTS)
                {
                    StartCoroutine(AttemptReconnect());
                }
                else if (!IsConnected)
                {
                    Plugin.Log.LogWarning("Reconnection failed after max attempts");
                    NotificationUI.Instance?.ShowCustom("DISCONNECTED", "Could not reconnect", new Color(0.9f, 0.3f, 0.3f));
                    LeaveLobby();
                }
            }
        }

        private void CloseConnections()
        {
            if (_listenSocket != HSteamListenSocket.Invalid)
            {
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                _listenSocket = HSteamListenSocket.Invalid;
            }

            if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                SteamNetworkingSockets.CloseConnection(_connectionToHost, 0, "Leaving", false);
                _connectionToHost = HSteamNetConnection.Invalid;
            }

            foreach (var conn in _clientConnections.Values)
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "Host closing", false);
            }
            _clientConnections.Clear();
        }

        #endregion

        #region Message Sending

        public void SendToServer(byte[] data, bool reliable = true)
        {
            if (!IsConnected || IsHost) return;
            SendMessage(_connectionToHost, data, reliable);
        }

        public void SendToClient(CSteamID clientId, byte[] data, bool reliable = true)
        {
            if (!IsHost || !_clientConnections.TryGetValue(clientId, out var conn)) return;
            SendMessage(conn, data, reliable);
        }

        public void SendToAllClients(byte[] data, bool reliable = true)
        {
            if (!IsHost || _clientConnections.Count == 0) return;

            // Prepend sender ID for clients
            byte[] dataWithSender = new byte[data.Length + 8];
            BitConverter.GetBytes(LocalSteamID.m_SteamID).CopyTo(dataWithSender, 0);
            Buffer.BlockCopy(data, 0, dataWithSender, 8, data.Length);

            foreach (var conn in _clientConnections.Values)
            {
                SendMessage(conn, dataWithSender, reliable);
            }
        }

        public void Broadcast(byte[] data, bool reliable = true)
        {
            if (IsHost)
            {
                SendToAllClients(data, reliable);
            }
            else
            {
                SendToServer(data, reliable);
            }
        }

        public void SendTo(CSteamID targetPlayer, byte[] data, bool reliable = true)
        {
            if (IsHost)
            {
                if (_clientConnections.TryGetValue(targetPlayer, out var connection))
                {
                    SendMessage(connection, data, reliable);
                }
            }
            else
            {
                SendToServer(data, reliable);
            }
        }

        private void SendMessage(HSteamNetConnection connection, byte[] data, bool reliable)
        {
            int flags = reliable ? SEND_FLAG_RELIABLE : SEND_FLAG_UNRELIABLE;

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    SteamNetworkingSockets.SendMessageToConnection(
                        connection,
                        (IntPtr)ptr,
                        (uint)data.Length,
                        flags,
                        out _);
                }
            }
        }

        #endregion

        #region Message Receiving

        private void ReceiveMessages()
        {
            if (IsHost)
            {
                foreach (var kvp in _clientConnections)
                {
                    int numMessages = SteamNetworkingSockets.ReceiveMessagesOnConnection(kvp.Value, _messageBuffer, MAX_MESSAGES_PER_FRAME);
                    ProcessMessages(_messageBuffer, numMessages, kvp.Key);
                }
            }
            else if (_connectionToHost != HSteamNetConnection.Invalid)
            {
                int numMessages = SteamNetworkingSockets.ReceiveMessagesOnConnection(_connectionToHost, _messageBuffer, MAX_MESSAGES_PER_FRAME);
                ProcessMessages(_messageBuffer, numMessages, CSteamID.Nil);
            }
        }

        private void ProcessMessages(IntPtr[] messages, int count, CSteamID sender)
        {
            for (int i = 0; i < count; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(messages[i]);

                byte[] data = new byte[msg.m_cbSize];
                System.Runtime.InteropServices.Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);

                CSteamID actualSender = ExtractSender(ref data, sender);

                if (!actualSender.IsValid() || actualSender == CSteamID.Nil)
                {
                    SteamNetworkingMessage_t.Release(messages[i]);
                    continue;
                }

                if (data.Length > 0)
                {
                    PacketHandler.HandlePacket(data, actualSender);

                    // Host relays to other clients
                    if (IsHost && sender.IsValid() && sender != CSteamID.Nil)
                    {
                        RelayToOtherClients(data, sender);
                    }
                }

                SteamNetworkingMessage_t.Release(messages[i]);
            }
        }

        private CSteamID ExtractSender(ref byte[] data, CSteamID defaultSender)
        {
            if (IsHost) return defaultSender;
            if (data.Length <= 8) return defaultSender;

            ulong senderId = BitConverter.ToUInt64(data, 0);
            byte potentialPacketType = data[8];

            bool validPacket = potentialPacketType < 100;
            bool validSteamId = senderId >= STEAM_ID_MIN && senderId <= STEAM_ID_MAX;

            if (validPacket && validSteamId && senderId != LocalSteamID.m_SteamID)
            {
                // Remove sender prefix
                byte[] actualData = new byte[data.Length - 8];
                Buffer.BlockCopy(data, 8, actualData, 0, actualData.Length);
                data = actualData;

                return new CSteamID(senderId);
            }

            if (validPacket && !validSteamId && CurrentLobby.IsValid())
            {
                return SteamMatchmaking.GetLobbyOwner(CurrentLobby);
            }

            return defaultSender;
        }

        private void RelayToOtherClients(byte[] data, CSteamID originalSender)
        {
            byte[] relayData = new byte[data.Length + 8];
            BitConverter.GetBytes(originalSender.m_SteamID).CopyTo(relayData, 0);
            Buffer.BlockCopy(data, 0, relayData, 8, data.Length);

            var packetType = (PacketType)data[0];
            bool reliable = IsReliablePacketType(packetType);

            foreach (var kvp in _clientConnections)
            {
                if (kvp.Key != originalSender)
                {
                    SendMessage(kvp.Value, relayData, reliable);
                }
            }
        }

        private static bool IsReliablePacketType(PacketType type)
        {
            return type == PacketType.PlayerDamage ||
                   type == PacketType.PlayerDeath ||
                   type == PacketType.PlayerRespawn ||
                   type == PacketType.Handshake ||
                   type == PacketType.CharacterSelect ||
                   type == PacketType.SceneChange ||
                   type == PacketType.WeaponSwitch ||
                   type == PacketType.WeaponPickup ||
                   type == PacketType.InteractableState;
        }

        #endregion

        #region Shutdown

        public void Shutdown()
        {
            LeaveLobby();
            CloseConnections();
        }

        #endregion
    }
}
