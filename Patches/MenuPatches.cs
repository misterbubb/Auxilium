using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.Events;
using TMPro;
using System.Reflection;
using Vertigo2Multiplayer.UI;

namespace Vertigo2Multiplayer.Patches
{
    /// <summary>
    /// Adds multiplayer button to game menus by detecting when they exist
    /// Uses Update loop instead of Harmony patches for reliability
    /// </summary>
    public class MenuButtonInjector : MonoBehaviour
    {
        public static MenuButtonInjector Instance { get; private set; }
        
        private static GameObject _multiplayerMenuObj;
        private bool _mainMenuButtonAdded = false;
        private bool _pauseMenuButtonAdded = false;
        private string _currentScene = "";
        private float _checkTimer = 0f;
        private int _updateCount = 0;
        
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _currentScene = SceneManager.GetActiveScene().name;
        }
        
        private void Start()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            _currentScene = SceneManager.GetActiveScene().name;
        }
        
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Instance == this) Instance = null;
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _currentScene = scene.name;
            _mainMenuButtonAdded = false;
            _pauseMenuButtonAdded = false;
        }
        
        private void Update()
        {
            _updateCount++;
            
            // Check every 0.5 seconds to reduce overhead
            _checkTimer += Time.unscaledDeltaTime;
            if (_checkTimer < 0.5f) return;
            _checkTimer = 0f;
            
            // Check for MainMenu
            if (_currentScene == "MainMenu" && !_mainMenuButtonAdded)
            {
                TryAddMainMenuButton();
            }
            
            // Check for PauseMenu (can appear in any gameplay scene)
            if (!_pauseMenuButtonAdded)
            {
                TryAddPauseMenuButton();
            }
        }
        
        private void TryAddMainMenuButton()
        {
            // Find MainMenu instance
            var mainMenu = FindObjectOfType<MainMenu>();
            if (mainMenu == null) return;
            
            // Get section_Main via reflection (it's private)
            var sectionMainField = typeof(MainMenu).GetField("section_Main", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (sectionMainField == null) return;
            
            var sectionMain = sectionMainField.GetValue(mainMenu) as GameObject;
            if (sectionMain == null) return;
            
            // Check if our button already exists
            var existingButton = sectionMain.transform.Find("Button_Multiplayer");
            if (existingButton != null)
            {
                _mainMenuButtonAdded = true;
                return;
            }
            
            // Find the PLAY button specifically to clone it
            Transform playButton = null;
            foreach (Transform child in sectionMain.transform)
            {
                if (child.name.ToLower().Contains("play") && child.GetComponent<Button>() != null)
                {
                    playButton = child;
                    break;
                }
            }
            
            if (playButton == null) return;
            
            // Clone the button
            var newButtonObj = Instantiate(playButton.gameObject, sectionMain.transform);
            newButtonObj.name = "Button_Multiplayer";
            
            // Disable any localization components that override our text
            var localizers = newButtonObj.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var loc in localizers)
            {
                if (loc.GetType().Name.Contains("Localize"))
                {
                    loc.enabled = false;
                }
            }
            
            // Update ALL text components
            var tmpTexts = newButtonObj.GetComponentsInChildren<TMP_Text>(true);
            foreach (var txt in tmpTexts)
            {
                txt.text = "MULTIPLAYER";
                txt.SetText("MULTIPLAYER");
                txt.ForceMeshUpdate();
            }
            
            // Also check for legacy Text components
            var legacyTexts = newButtonObj.GetComponentsInChildren<UnityEngine.UI.Text>(true);
            foreach (var txt in legacyTexts)
            {
                txt.text = "MULTIPLAYER";
            }
            
            // Clear existing onClick listeners and add our own
            var button = newButtonObj.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(OnMultiplayerButtonClicked);
            
            // Position it right after the PLAY button
            int playIndex = playButton.GetSiblingIndex();
            newButtonObj.transform.SetSiblingIndex(playIndex + 1);
            
            // Make it smaller and position using config values
            var playRect = playButton.GetComponent<RectTransform>();
            var newRect = newButtonObj.GetComponent<RectTransform>();
            if (newRect != null && playRect != null)
            {
                float width = Plugin.ButtonWidth?.Value ?? 300f;
                float height = Plugin.ButtonHeight?.Value ?? 60f;
                float posX = Plugin.ButtonPosX?.Value ?? 0f;
                float posY = Plugin.ButtonPosY?.Value ?? -25f;
                
                newRect.sizeDelta = new Vector2(width, height);
                newRect.anchoredPosition = new Vector2(posX, posY);
            }
            
            // Make the text smaller too
            foreach (var txt in tmpTexts)
            {
                txt.fontSize = 24f;
                txt.ForceMeshUpdate();
            }
            
            newButtonObj.SetActive(true);
            _mainMenuButtonAdded = true;
            Plugin.Log.LogInfo("MULTIPLAYER button added to MainMenu");
        }
        
        private void TryAddPauseMenuButton()
        {
            // Find PauseMenu instance
            if (PauseMenu.instance == null) return;
            
            var pauseMenu = PauseMenu.instance;
            if (pauseMenu.section_Main == null) return;
            
            // Check if already added
            var existingButton = pauseMenu.section_Main.transform.Find("Button_Multiplayer");
            if (existingButton != null)
            {
                _pauseMenuButtonAdded = true;
                return;
            }
            
            // Find a button to clone
            Button templateButton = null;
            foreach (Transform child in pauseMenu.section_Main.transform)
            {
                var btn = child.GetComponent<Button>();
                if (btn != null && child.gameObject.activeSelf)
                {
                    templateButton = btn;
                    break;
                }
            }
            
            if (templateButton == null) return;
            
            // Clone the button
            var newButtonObj = Instantiate(templateButton.gameObject, pauseMenu.section_Main.transform);
            newButtonObj.name = "Button_Multiplayer";
            newButtonObj.SetActive(true);
            
            // Update the text
            var tmpTexts = newButtonObj.GetComponentsInChildren<TMP_Text>(true);
            foreach (var txt in tmpTexts)
            {
                txt.text = "MULTIPLAYER";
            }
            
            // Clear existing onClick listeners and add our own
            var button = newButtonObj.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(OnMultiplayerButtonClicked);
            
            // Position it before the last button (usually "Exit to Menu")
            int siblingIndex = pauseMenu.section_Main.transform.childCount - 2;
            if (siblingIndex < 0) siblingIndex = 0;
            newButtonObj.transform.SetSiblingIndex(siblingIndex);
            
            _pauseMenuButtonAdded = true;
            Plugin.Log.LogInfo("MULTIPLAYER button added to PauseMenu");
        }
        
        private void OnMultiplayerButtonClicked()
        {
            Plugin.Log.LogInfo("MULTIPLAYER button clicked");
            EnsureMultiplayerMenuExists();
            MultiplayerMenu.Instance?.Show();
        }
        
        private void EnsureMultiplayerMenuExists()
        {
            if (_multiplayerMenuObj == null || MultiplayerMenu.Instance == null)
            {
                if (_multiplayerMenuObj != null)
                {
                    Destroy(_multiplayerMenuObj);
                }
                
                _multiplayerMenuObj = new GameObject("V2MP_MultiplayerMenu");
                DontDestroyOnLoad(_multiplayerMenuObj);
                _multiplayerMenuObj.AddComponent<MultiplayerMenu>();
            }
        }
    }
}
