using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using Vertigo2;
using Vertigo2Multiplayer.Patches;
using Vertigo2Multiplayer.UI;

namespace Vertigo2Multiplayer
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }
        
        private Harmony _harmony;
        private bool _initialized = false;
        
        // Config
        private ConfigEntry<string> _mode;
        private ConfigEntry<string> _hostSteamId;
        private ConfigEntry<int> _maxPlayers;
        public static ConfigEntry<float> ButtonPosX;
        public static ConfigEntry<float> ButtonPosY;
        public static ConfigEntry<float> ButtonWidth;
        public static ConfigEntry<float> ButtonHeight;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            
            Log.LogInfo($"Vertigo 2 Multiplayer v{PluginInfo.PLUGIN_VERSION} loading...");
            
            InitConfig();
            InitHarmony();
            InitGameObjects();
            
            SceneManager.sceneLoaded += OnSceneLoaded;
            
            Log.LogInfo("Vertigo 2 Multiplayer loaded!");
        }

        private void InitConfig()
        {
            _mode = Config.Bind("Multiplayer", "Mode", "none", 
                "Set to 'host' to auto-host, 'join' to auto-join, or 'none' to use in-game menu");
            _hostSteamId = Config.Bind("Multiplayer", "HostSteamID", "", 
                "Steam ID of host when Mode=join");
            _maxPlayers = Config.Bind("Multiplayer", "MaxPlayers", 4, 
                "Maximum players when hosting (2-8)");
            
            ButtonPosX = Config.Bind("UI", "ButtonPosX", 0f, "X position of MULTIPLAYER button");
            ButtonPosY = Config.Bind("UI", "ButtonPosY", -25f, "Y position of MULTIPLAYER button");
            ButtonWidth = Config.Bind("UI", "ButtonWidth", 300f, "Width of MULTIPLAYER button");
            ButtonHeight = Config.Bind("UI", "ButtonHeight", 60f, "Height of MULTIPLAYER button");
        }

        private void InitHarmony()
        {
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();
        }

        private void InitGameObjects()
        {
            var networkObj = new GameObject("V2MP_Network");
            DontDestroyOnLoad(networkObj);
            networkObj.AddComponent<NetworkManager>();
            networkObj.AddComponent<PlayerManager>();
            networkObj.AddComponent<InteractableSync>();
            networkObj.AddComponent<InventorySync>();
            networkObj.AddComponent<WeaponSync>();
            networkObj.AddComponent<EnemySync>();
            networkObj.AddComponent<StorySync>();
            networkObj.AddComponent<WorldSync>();
            
            var menuObj = new GameObject("V2MP_MenuInjector");
            DontDestroyOnLoad(menuObj);
            menuObj.AddComponent<MenuButtonInjector>();
            
            var notifObj = new GameObject("V2MP_Notifications");
            DontDestroyOnLoad(notifObj);
            notifObj.AddComponent<NotificationUI>();
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureComponentsExist();
            
            if (InteractableSync.Instance != null && NetworkManager.Instance?.IsConnected == true)
            {
                NetworkManager.Instance.StartCoroutine(ScanInteractablesDelayed());
            }
            
            if (scene.name == "MainMenu" && !_initialized)
            {
                _initialized = true;
                string modeValue = _mode.Value.ToLower().Trim();
                
                if (modeValue == "host" || modeValue == "join")
                    NetworkManager.Instance.StartCoroutine(AutoConnect());
            }
        }

        private void EnsureComponentsExist()
        {
            if (NetworkManager.Instance == null)
                InitGameObjects();
            
            if (MenuButtonInjector.Instance == null)
            {
                var menuObj = new GameObject("V2MP_MenuInjector");
                DontDestroyOnLoad(menuObj);
                menuObj.AddComponent<MenuButtonInjector>();
            }
            
            if (NotificationUI.Instance == null)
            {
                var notifObj = new GameObject("V2MP_Notifications");
                DontDestroyOnLoad(notifObj);
                notifObj.AddComponent<NotificationUI>();
            }
        }
        
        private IEnumerator ScanInteractablesDelayed()
        {
            yield return new WaitForSeconds(1f);
            InteractableSync.Instance?.ScanSceneForInteractables();
        }
        
        private IEnumerator AutoConnect()
        {
            while (NetworkManager.Instance == null || !NetworkManager.Instance.IsInitialized)
                yield return new WaitForSecondsRealtime(0.5f);
            
            yield return new WaitForSecondsRealtime(1f);
            
            string modeValue = _mode.Value.ToLower().Trim();
            
            if (modeValue == "host")
            {
                Log.LogInfo("Auto-hosting lobby from config...");
                NetworkManager.Instance.CreateLobby(Mathf.Clamp(_maxPlayers.Value, 2, 8));
            }
            else if (modeValue == "join" && !string.IsNullOrEmpty(_hostSteamId.Value))
            {
                Log.LogInfo($"Auto-joining host: {_hostSteamId.Value}");
                NetworkManager.Instance.JoinLobbyBySteamId(_hostSteamId.Value.Trim());
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            NetworkManager.Instance?.Shutdown();
        }
    }
}
