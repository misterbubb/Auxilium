using System.Collections.Generic;
using System.Reflection;
using Steamworks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Vertigo2.UI;
using Vertigo2.Player;

namespace Vertigo2Multiplayer.UI
{
    /// <summary>
    /// Multiplayer menu with friends list, quick join, and invite features
    /// </summary>
    public class MultiplayerMenu : MonoBehaviour
    {
        public static MultiplayerMenu Instance { get; private set; }
        
        // Reference to our injected section in MainMenu
        private GameObject _multiplayerSection;
        private MainMenu _mainMenu;
        
        // UI elements
        private TMP_Text _statusText;
        private TMP_Text _steamIdText;
        private TMP_Text _playerListText;
        private TMP_InputField _hostSteamIdInput;
        private Button _hostButton;
        private Button _joinButton;
        private Button _disconnectButton;
        private Button _backButton;
        private Button _inviteButton;
        private Button _refreshFriendsButton;
        
        // Friends list
        private GameObject _friendsListContainer;
        private TMP_Text _friendsHeaderText;
        private List<FriendEntry> _friendEntries = new();
        
        // Panels for different states
        private GameObject _connectPanel;
        private GameObject _lobbyPanel;
        
        private bool _isVisible = false;
        private float _friendsRefreshTimer = 0f;
        private const float FRIENDS_REFRESH_INTERVAL = 5f;

        // Cached friend data
        private struct FriendData
        {
            public CSteamID SteamId;
            public string Name;
            public bool IsPlayingVertigo;
            public bool IsInMultiplayer;
            public CSteamID LobbyId;
        }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            if (_isVisible && _multiplayerSection != null && _multiplayerSection.activeSelf)
            {
                UpdateStatusDisplay();
                
                // Refresh friends list periodically
                _friendsRefreshTimer += Time.unscaledDeltaTime;
                if (_friendsRefreshTimer >= FRIENDS_REFRESH_INTERVAL)
                {
                    _friendsRefreshTimer = 0f;
                    RefreshFriendsList();
                }
                
                // Continuously ensure other sections stay hidden
                if (_mainMenu != null)
                {
                    EnsureOtherSectionsHidden();
                }
            }
        }
        
        private void EnsureOtherSectionsHidden()
        {
            var fields = typeof(MainMenu).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            foreach (var field in fields)
            {
                if (field.Name.StartsWith("section_") && field.FieldType == typeof(GameObject))
                {
                    var section = field.GetValue(_mainMenu) as GameObject;
                    if (section != null && section != _multiplayerSection && section.activeSelf)
                    {
                        section.SetActive(false);
                    }
                }
            }
        }

        public void Show()
        {
            _mainMenu = FindObjectOfType<MainMenu>();
            if (_mainMenu == null) return;
            
            if (_multiplayerSection == null)
            {
                CreateMultiplayerSection();
            }
            
            if (_multiplayerSection == null) return;
            
            HideAllSections();
            _multiplayerSection.SetActive(true);
            _isVisible = true;
            _friendsRefreshTimer = FRIENDS_REFRESH_INTERVAL; // Trigger immediate refresh
            
            StartCoroutine(EnsureSectionsHidden());
            UpdateStatusDisplay();
        }

        private System.Collections.IEnumerator EnsureSectionsHidden()
        {
            yield return null;
            yield return null;
            
            if (_isVisible && _mainMenu != null)
            {
                HideAllSections();
                if (_multiplayerSection != null)
                {
                    _multiplayerSection.SetActive(true);
                }
            }
        }

        public void Hide()
        {
            _isVisible = false;
            if (_multiplayerSection != null)
            {
                _multiplayerSection.SetActive(false);
            }
            
            if (_mainMenu != null)
            {
                _mainMenu.ChangeActiveSection(MainMenu.MenuSections.Main);
            }
        }

        private void HideAllSections()
        {
            if (_mainMenu == null) return;
            
            var fields = typeof(MainMenu).GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            foreach (var field in fields)
            {
                if (field.Name.StartsWith("section_") && field.FieldType == typeof(GameObject))
                {
                    var section = field.GetValue(_mainMenu) as GameObject;
                    if (section != null)
                    {
                        section.SetActive(false);
                    }
                }
            }
        }

        private void CreateMultiplayerSection()
        {
            var sectionMainField = typeof(MainMenu).GetField("section_Main", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            var sectionMain = sectionMainField?.GetValue(_mainMenu) as GameObject;
            
            if (sectionMain == null) return;
            
            _multiplayerSection = new GameObject("section_Multiplayer");
            _multiplayerSection.transform.SetParent(sectionMain.transform.parent, false);
            
            var sourceRect = sectionMain.GetComponent<RectTransform>();
            var rect = _multiplayerSection.AddComponent<RectTransform>();
            if (sourceRect != null)
            {
                rect.anchorMin = sourceRect.anchorMin;
                rect.anchorMax = sourceRect.anchorMax;
                rect.anchoredPosition = sourceRect.anchoredPosition;
                rect.sizeDelta = new Vector2(sourceRect.sizeDelta.x, sourceRect.sizeDelta.y + 200); // Taller for friends list
                rect.pivot = sourceRect.pivot;
            }

            // Add scroll view for content
            var scrollView = _multiplayerSection.AddComponent<ScrollRect>();
            
            // Content container
            var content = new GameObject("Content");
            content.transform.SetParent(_multiplayerSection.transform, false);
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.5f, 1f);
            contentRect.anchorMax = new Vector2(0.5f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(800, 0); // Height will be set by layout
            
            var layout = content.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.spacing = 12;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.padding = new RectOffset(20, 20, 20, 20);
            
            var fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            
            scrollView.content = contentRect;
            scrollView.vertical = true;
            scrollView.horizontal = false;
            
            // Find button template
            Button templateButton = null;
            foreach (Transform child in sectionMain.transform)
            {
                var btn = child.GetComponent<Button>();
                if (btn != null)
                {
                    templateButton = btn;
                    break;
                }
            }
            
            // Create UI elements (parented to content)
            var originalParent = _multiplayerSection;
            _multiplayerSection = content; // Temporarily redirect
            
            CreateTitle("MULTIPLAYER");
            CreateStatusSection();
            CreateConnectPanel(templateButton);
            CreateFriendsSection(templateButton);
            CreateLobbyPanel(templateButton);
            _backButton = CreateButton("BACK", templateButton, OnBackClicked);
            
            _multiplayerSection = originalParent; // Restore
            
            // Initial state
            _connectPanel.SetActive(true);
            _lobbyPanel.SetActive(false);
            _multiplayerSection.SetActive(false);
        }

        private void CreateTitle(string text)
        {
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(_multiplayerSection.transform, false);
            
            var rect = titleObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(800, 70);
            
            var tmp = titleObj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 42;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
        }

        private void CreateStatusSection()
        {
            var statusObj = new GameObject("StatusSection");
            statusObj.transform.SetParent(_multiplayerSection.transform, false);
            
            var rect = statusObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(800, 55);
            
            var layout = statusObj.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 3;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            
            // Status text
            var statusTextObj = new GameObject("StatusText");
            statusTextObj.transform.SetParent(statusObj.transform, false);
            var statusRect = statusTextObj.AddComponent<RectTransform>();
            statusRect.sizeDelta = new Vector2(800, 28);
            _statusText = statusTextObj.AddComponent<TextMeshProUGUI>();
            _statusText.text = "Status: Checking...";
            _statusText.fontSize = 22;
            _statusText.alignment = TextAlignmentOptions.Center;
            _statusText.color = Color.white;
            
            // Steam ID text
            var steamIdObj = new GameObject("SteamIdText");
            steamIdObj.transform.SetParent(statusObj.transform, false);
            var steamRect = steamIdObj.AddComponent<RectTransform>();
            steamRect.sizeDelta = new Vector2(800, 22);
            _steamIdText = steamIdObj.AddComponent<TextMeshProUGUI>();
            _steamIdText.text = "Your Steam ID: Loading...";
            _steamIdText.fontSize = 16;
            _steamIdText.alignment = TextAlignmentOptions.Center;
            _steamIdText.color = new Color(0.7f, 0.7f, 0.7f);
        }

        private void CreateConnectPanel(Button templateButton)
        {
            _connectPanel = new GameObject("ConnectPanel");
            _connectPanel.transform.SetParent(_multiplayerSection.transform, false);
            
            var rect = _connectPanel.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(800, 130);
            
            var layout = _connectPanel.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 10;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            
            // Host button
            _hostButton = CreateButton("HOST GAME", templateButton, OnHostClicked);
            _hostButton.transform.SetParent(_connectPanel.transform, false);
            
            // Join section with input field
            var joinSection = new GameObject("JoinSection");
            joinSection.transform.SetParent(_connectPanel.transform, false);
            var joinRect = joinSection.AddComponent<RectTransform>();
            joinRect.sizeDelta = new Vector2(800, 45);
            
            var joinLayout = joinSection.AddComponent<HorizontalLayoutGroup>();
            joinLayout.childAlignment = TextAnchor.MiddleCenter;
            joinLayout.spacing = 8;
            joinLayout.childControlWidth = false;
            joinLayout.childControlHeight = false;
            
            // Label
            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(joinSection.transform, false);
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(180, 40);
            var labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.text = "Host Steam ID:";
            labelText.fontSize = 18;
            labelText.alignment = TextAlignmentOptions.MidlineRight;
            labelText.color = Color.white;
            
            // Input field
            _hostSteamIdInput = CreateInputField(joinSection.transform);
            
            // Join button
            _joinButton = CreateButton("JOIN", templateButton, OnJoinClicked);
            _joinButton.transform.SetParent(joinSection.transform, false);
            var joinBtnRect = _joinButton.GetComponent<RectTransform>();
            if (joinBtnRect != null) joinBtnRect.sizeDelta = new Vector2(120, 45);
        }

        private void CreateFriendsSection(Button templateButton)
        {
            var friendsSection = new GameObject("FriendsSection");
            friendsSection.transform.SetParent(_multiplayerSection.transform, false);
            
            var rect = friendsSection.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(800, 220);
            
            var layout = friendsSection.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.spacing = 8;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.padding = new RectOffset(0, 0, 5, 5);
            
            // Header with refresh button
            var headerRow = new GameObject("HeaderRow");
            headerRow.transform.SetParent(friendsSection.transform, false);
            var headerRect = headerRow.AddComponent<RectTransform>();
            headerRect.sizeDelta = new Vector2(750, 30);
            
            var headerLayout = headerRow.AddComponent<HorizontalLayoutGroup>();
            headerLayout.childAlignment = TextAnchor.MiddleCenter;
            headerLayout.spacing = 10;
            headerLayout.childControlWidth = false;
            headerLayout.childControlHeight = false;
            
            // Friends header text
            var headerObj = new GameObject("FriendsHeader");
            headerObj.transform.SetParent(headerRow.transform, false);
            var headerTextRect = headerObj.AddComponent<RectTransform>();
            headerTextRect.sizeDelta = new Vector2(600, 28);
            _friendsHeaderText = headerObj.AddComponent<TextMeshProUGUI>();
            _friendsHeaderText.text = "FRIENDS PLAYING";
            _friendsHeaderText.fontSize = 20;
            _friendsHeaderText.alignment = TextAlignmentOptions.Center;
            _friendsHeaderText.color = new Color(0.4f, 0.8f, 0.4f);
            
            // Refresh button
            _refreshFriendsButton = CreateSmallButton("↻", templateButton, RefreshFriendsList);
            _refreshFriendsButton.transform.SetParent(headerRow.transform, false);
            var refreshRect = _refreshFriendsButton.GetComponent<RectTransform>();
            if (refreshRect != null) refreshRect.sizeDelta = new Vector2(40, 28);
            
            // Friends list container with background
            var listBg = new GameObject("FriendsListBg");
            listBg.transform.SetParent(friendsSection.transform, false);
            var bgRect = listBg.AddComponent<RectTransform>();
            bgRect.sizeDelta = new Vector2(750, 170);
            var bgImage = listBg.AddComponent<Image>();
            bgImage.color = new Color(0.1f, 0.1f, 0.15f, 0.8f);
            
            _friendsListContainer = new GameObject("FriendsList");
            _friendsListContainer.transform.SetParent(listBg.transform, false);
            var listRect = _friendsListContainer.AddComponent<RectTransform>();
            listRect.anchorMin = Vector2.zero;
            listRect.anchorMax = Vector2.one;
            listRect.offsetMin = new Vector2(10, 10);
            listRect.offsetMax = new Vector2(-10, -10);
            
            var listLayout = _friendsListContainer.AddComponent<VerticalLayoutGroup>();
            listLayout.childAlignment = TextAnchor.UpperCenter;
            listLayout.spacing = 5;
            listLayout.childControlWidth = false;
            listLayout.childControlHeight = false;
        }

        private void CreateLobbyPanel(Button templateButton)
        {
            _lobbyPanel = new GameObject("LobbyPanel");
            _lobbyPanel.transform.SetParent(_multiplayerSection.transform, false);
            
            var rect = _lobbyPanel.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(800, 180);
            
            var layout = _lobbyPanel.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 8;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            
            // Connected players label
            var labelObj = new GameObject("Label");
            labelObj.transform.SetParent(_lobbyPanel.transform, false);
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(800, 28);
            var labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.text = "CONNECTED PLAYERS";
            labelText.fontSize = 22;
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.color = new Color(0.3f, 0.7f, 1f);
            
            // Player list
            var listObj = new GameObject("PlayerList");
            listObj.transform.SetParent(_lobbyPanel.transform, false);
            var listRect = listObj.AddComponent<RectTransform>();
            listRect.sizeDelta = new Vector2(800, 70);
            _playerListText = listObj.AddComponent<TextMeshProUGUI>();
            _playerListText.text = "Loading...";
            _playerListText.fontSize = 18;
            _playerListText.alignment = TextAlignmentOptions.Center;
            _playerListText.color = Color.white;
            
            // Button row
            var buttonRow = new GameObject("ButtonRow");
            buttonRow.transform.SetParent(_lobbyPanel.transform, false);
            var buttonRect = buttonRow.AddComponent<RectTransform>();
            buttonRect.sizeDelta = new Vector2(800, 55);
            
            var buttonLayout = buttonRow.AddComponent<HorizontalLayoutGroup>();
            buttonLayout.childAlignment = TextAnchor.MiddleCenter;
            buttonLayout.spacing = 15;
            buttonLayout.childControlWidth = false;
            buttonLayout.childControlHeight = false;
            
            // Invite button
            _inviteButton = CreateButton("INVITE FRIEND", templateButton, OnInviteClicked);
            _inviteButton.transform.SetParent(buttonRow.transform, false);
            var inviteRect = _inviteButton.GetComponent<RectTransform>();
            if (inviteRect != null) inviteRect.sizeDelta = new Vector2(200, 50);
            
            // Disconnect button
            _disconnectButton = CreateButton("DISCONNECT", templateButton, OnDisconnectClicked);
            _disconnectButton.transform.SetParent(buttonRow.transform, false);
            var disconnectRect = _disconnectButton.GetComponent<RectTransform>();
            if (disconnectRect != null) disconnectRect.sizeDelta = new Vector2(200, 50);
        }

        private Button CreateButton(string label, Button template, UnityEngine.Events.UnityAction onClick)
        {
            GameObject buttonObj;
            
            if (template != null)
            {
                buttonObj = Instantiate(template.gameObject, _multiplayerSection.transform);
                buttonObj.name = "Button_" + label;
                
                // Disable localization
                var localizers = buttonObj.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (var loc in localizers)
                {
                    if (loc.GetType().Name.Contains("Localize"))
                        loc.enabled = false;
                }
                
                // Update text
                var tmpTexts = buttonObj.GetComponentsInChildren<TMP_Text>(true);
                foreach (var txt in tmpTexts)
                {
                    txt.text = label;
                    txt.SetText(label);
                    txt.ForceMeshUpdate();
                }
                
                var legacyTexts = buttonObj.GetComponentsInChildren<Text>(true);
                foreach (var txt in legacyTexts)
                    txt.text = label;
                
                var btnRect = buttonObj.GetComponent<RectTransform>();
                if (btnRect != null)
                    btnRect.sizeDelta = new Vector2(350, 55);
                
                var btn = buttonObj.GetComponent<Button>();
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(onClick);
                
                return btn;
            }
            else
            {
                buttonObj = new GameObject("Button_" + label);
                buttonObj.transform.SetParent(_multiplayerSection.transform, false);
                
                var rect = buttonObj.AddComponent<RectTransform>();
                rect.sizeDelta = new Vector2(350, 55);
                
                var image = buttonObj.AddComponent<Image>();
                image.color = new Color(0.2f, 0.4f, 0.6f, 1f);
                
                var btn = buttonObj.AddComponent<Button>();
                btn.targetGraphic = image;
                btn.onClick.AddListener(onClick);
                
                var textObj = new GameObject("Text");
                textObj.transform.SetParent(buttonObj.transform, false);
                var textRect = textObj.AddComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.sizeDelta = Vector2.zero;
                
                var text = textObj.AddComponent<TextMeshProUGUI>();
                text.text = label;
                text.fontSize = 22;
                text.alignment = TextAlignmentOptions.Center;
                text.color = Color.white;
                
                return btn;
            }
        }

        private Button CreateSmallButton(string label, Button template, UnityEngine.Events.UnityAction onClick)
        {
            var buttonObj = new GameObject("SmallButton_" + label);
            buttonObj.transform.SetParent(_multiplayerSection.transform, false);
            
            var rect = buttonObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(40, 28);
            
            var image = buttonObj.AddComponent<Image>();
            image.color = new Color(0.25f, 0.45f, 0.65f, 1f);
            
            var btn = buttonObj.AddComponent<Button>();
            btn.targetGraphic = image;
            btn.onClick.AddListener(onClick);
            
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.35f, 0.55f, 0.75f, 1f);
            colors.pressedColor = new Color(0.2f, 0.4f, 0.6f, 1f);
            btn.colors = colors;
            
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(buttonObj.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            
            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 18;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            
            return btn;
        }

        private TMP_InputField CreateInputField(Transform parent)
        {
            var inputObj = new GameObject("InputField");
            inputObj.transform.SetParent(parent, false);
            
            var rect = inputObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(220, 38);
            
            var image = inputObj.AddComponent<Image>();
            image.color = new Color(0.12f, 0.12f, 0.18f, 1f);
            
            var textArea = new GameObject("TextArea");
            textArea.transform.SetParent(inputObj.transform, false);
            var textAreaRect = textArea.AddComponent<RectTransform>();
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            textAreaRect.offsetMin = new Vector2(8, 4);
            textAreaRect.offsetMax = new Vector2(-8, -4);
            textArea.AddComponent<RectMask2D>();
            
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(textArea.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            
            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.fontSize = 16;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Left;
            
            var placeholderObj = new GameObject("Placeholder");
            placeholderObj.transform.SetParent(textArea.transform, false);
            var placeholderRect = placeholderObj.AddComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.sizeDelta = Vector2.zero;
            
            var placeholder = placeholderObj.AddComponent<TextMeshProUGUI>();
            placeholder.fontSize = 16;
            placeholder.color = new Color(0.5f, 0.5f, 0.5f, 0.6f);
            placeholder.alignment = TextAlignmentOptions.Left;
            placeholder.text = "Steam ID...";
            
            var inputField = inputObj.AddComponent<TMP_InputField>();
            inputField.textViewport = textAreaRect;
            inputField.textComponent = text;
            inputField.placeholder = placeholder;
            inputField.contentType = TMP_InputField.ContentType.IntegerNumber;
            
            return inputField;
        }

        #region Friends List

        private void RefreshFriendsList()
        {
            if (_friendsListContainer == null) return;
            if (!Vertigo2.Steamworks.SteamworksManager.Initialized) return;
            
            // Clear existing entries
            foreach (var entry in _friendEntries)
            {
                if (entry.GameObject != null)
                    Destroy(entry.GameObject);
            }
            _friendEntries.Clear();
            
            // Get friends list
            var friends = GetFriendsPlayingVertigo();
            
            // Update header
            if (_friendsHeaderText != null)
            {
                if (friends.Count > 0)
                    _friendsHeaderText.text = $"FRIENDS PLAYING ({friends.Count})";
                else
                    _friendsHeaderText.text = "FRIENDS PLAYING";
            }
            
            if (friends.Count == 0)
            {
                // Show "no friends" message
                CreateFriendEntry("No friends playing Vertigo 2", CSteamID.Nil, false, CSteamID.Nil);
                return;
            }
            
            // Create entries for each friend
            foreach (var friend in friends)
            {
                CreateFriendEntry(friend.Name, friend.SteamId, friend.IsInMultiplayer, friend.LobbyId);
            }
        }

        private List<FriendData> GetFriendsPlayingVertigo()
        {
            var result = new List<FriendData>();
            
            int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            
            for (int i = 0; i < friendCount; i++)
            {
                CSteamID friendId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                
                // Check if friend is online
                var state = SteamFriends.GetFriendPersonaState(friendId);
                if (state == EPersonaState.k_EPersonaStateOffline)
                    continue;
                
                // Check if playing Vertigo 2 (App ID 843390)
                FriendGameInfo_t gameInfo;
                if (SteamFriends.GetFriendGamePlayed(friendId, out gameInfo))
                {
                    if (gameInfo.m_gameID.AppID().m_AppId == 843390)
                    {
                        var friendData = new FriendData
                        {
                            SteamId = friendId,
                            Name = SteamFriends.GetFriendPersonaName(friendId),
                            IsPlayingVertigo = true,
                            IsInMultiplayer = gameInfo.m_steamIDLobby.IsValid(),
                            LobbyId = gameInfo.m_steamIDLobby
                        };
                        result.Add(friendData);
                    }
                }
            }
            
            // Sort: friends in multiplayer first, then alphabetically
            result.Sort((a, b) => {
                if (a.IsInMultiplayer != b.IsInMultiplayer)
                    return b.IsInMultiplayer.CompareTo(a.IsInMultiplayer);
                return a.Name.CompareTo(b.Name);
            });
            
            return result;
        }

        private void CreateFriendEntry(string name, CSteamID steamId, bool canJoin, CSteamID lobbyId)
        {
            var entryObj = new GameObject("FriendEntry_" + name);
            entryObj.transform.SetParent(_friendsListContainer.transform, false);
            
            var rect = entryObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(720, 32);
            
            var layout = entryObj.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.spacing = 10;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.padding = new RectOffset(5, 5, 0, 0);
            
            // Status indicator
            var statusObj = new GameObject("Status");
            statusObj.transform.SetParent(entryObj.transform, false);
            var statusRect = statusObj.AddComponent<RectTransform>();
            statusRect.sizeDelta = new Vector2(12, 12);
            var statusImage = statusObj.AddComponent<Image>();
            
            if (!steamId.IsValid())
            {
                statusImage.color = new Color(0.4f, 0.4f, 0.4f); // Gray for "no friends"
            }
            else if (canJoin)
            {
                statusImage.color = new Color(0.3f, 0.9f, 0.3f); // Green for joinable
            }
            else
            {
                statusImage.color = new Color(0.9f, 0.7f, 0.2f); // Yellow for playing but not hosting
            }
            
            // Name text
            var nameObj = new GameObject("Name");
            nameObj.transform.SetParent(entryObj.transform, false);
            var nameRect = nameObj.AddComponent<RectTransform>();
            nameRect.sizeDelta = new Vector2(canJoin ? 500 : 680, 28);
            var nameText = nameObj.AddComponent<TextMeshProUGUI>();
            nameText.text = name;
            nameText.fontSize = 18;
            nameText.alignment = TextAlignmentOptions.MidlineLeft;
            nameText.color = steamId.IsValid() ? Color.white : new Color(0.5f, 0.5f, 0.5f);
            
            // Join button (only if they're hosting)
            if (canJoin && lobbyId.IsValid())
            {
                var joinBtnObj = new GameObject("JoinBtn");
                joinBtnObj.transform.SetParent(entryObj.transform, false);
                var joinRect = joinBtnObj.AddComponent<RectTransform>();
                joinRect.sizeDelta = new Vector2(80, 28);
                
                var joinImage = joinBtnObj.AddComponent<Image>();
                joinImage.color = new Color(0.2f, 0.5f, 0.3f, 1f);
                
                var joinBtn = joinBtnObj.AddComponent<Button>();
                joinBtn.targetGraphic = joinImage;
                
                var colors = joinBtn.colors;
                colors.highlightedColor = new Color(0.3f, 0.6f, 0.4f, 1f);
                colors.pressedColor = new Color(0.15f, 0.4f, 0.25f, 1f);
                joinBtn.colors = colors;
                
                // Capture lobbyId for closure
                CSteamID capturedLobby = lobbyId;
                joinBtn.onClick.AddListener(() => OnJoinFriendClicked(capturedLobby));
                
                var joinTextObj = new GameObject("Text");
                joinTextObj.transform.SetParent(joinBtnObj.transform, false);
                var joinTextRect = joinTextObj.AddComponent<RectTransform>();
                joinTextRect.anchorMin = Vector2.zero;
                joinTextRect.anchorMax = Vector2.one;
                joinTextRect.sizeDelta = Vector2.zero;
                
                var joinText = joinTextObj.AddComponent<TextMeshProUGUI>();
                joinText.text = "JOIN";
                joinText.fontSize = 16;
                joinText.alignment = TextAlignmentOptions.Center;
                joinText.color = Color.white;
            }
            
            _friendEntries.Add(new FriendEntry { GameObject = entryObj, SteamId = steamId, LobbyId = lobbyId });
        }

        private class FriendEntry
        {
            public GameObject GameObject;
            public CSteamID SteamId;
            public CSteamID LobbyId;
        }

        #endregion

        #region Button Handlers

        private void OnHostClicked()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsInitialized) return;
            if (NetworkManager.Instance.IsConnected) return;
            
            Plugin.Log.LogInfo("Hosting lobby...");
            NetworkManager.Instance.CreateLobby(4);
        }

        private void OnJoinClicked()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsInitialized) return;
            if (NetworkManager.Instance.IsConnected) return;
            
            string hostId = _hostSteamIdInput?.text?.Trim() ?? "";
            if (string.IsNullOrEmpty(hostId)) return;
            
            Plugin.Log.LogInfo($"Joining lobby hosted by: {hostId}");
            NetworkManager.Instance.JoinLobbyBySteamId(hostId);
        }

        private void OnJoinFriendClicked(CSteamID lobbyId)
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsInitialized) return;
            if (NetworkManager.Instance.IsConnected) return;
            if (!lobbyId.IsValid()) return;
            
            Plugin.Log.LogInfo($"Joining friend's lobby: {lobbyId}");
            NetworkManager.Instance.JoinLobby(lobbyId);
        }

        private void OnInviteClicked()
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected) return;
            
            // Open Steam overlay to invite friends
            if (NetworkManager.Instance.CurrentLobby.IsValid())
            {
                SteamFriends.ActivateGameOverlayInviteDialog(NetworkManager.Instance.CurrentLobby);
                Plugin.Log.LogInfo("Opened Steam invite dialog");
            }
        }

        private void OnDisconnectClicked()
        {
            NetworkManager.Instance?.LeaveLobby();
            Plugin.Log.LogInfo("Disconnected from lobby");
        }

        private void OnBackClicked()
        {
            Hide();
        }

        #endregion

        #region Status Updates

        private void UpdateStatusDisplay()
        {
            if (_statusText == null) return;
            
            if (NetworkManager.Instance == null)
            {
                _statusText.text = "Status: <color=#FF6666>NetworkManager not found</color>";
                return;
            }
            
            if (!NetworkManager.Instance.IsInitialized)
            {
                _statusText.text = "Status: <color=#FFFF66>Initializing Steam...</color>";
                if (_steamIdText != null) _steamIdText.text = "Your Steam ID: Loading...";
                return;
            }
            
            // Update Steam ID
            if (_steamIdText != null)
            {
                _steamIdText.text = $"Your Steam ID: <color=#66CCFF>{NetworkManager.Instance.LocalSteamID}</color>";
            }
            
            // Update connection status and panels
            if (NetworkManager.Instance.IsConnected)
            {
                string role = NetworkManager.Instance.IsHost ? "HOST" : "CLIENT";
                string roleColor = NetworkManager.Instance.IsHost ? "#66FF66" : "#66CCFF";
                _statusText.text = $"Status: <color={roleColor}>Connected ({role})</color>";
                
                // Show lobby panel, hide connect panel
                if (_connectPanel != null) _connectPanel.SetActive(false);
                if (_lobbyPanel != null) _lobbyPanel.SetActive(true);
                
                UpdatePlayerList();
            }
            else
            {
                _statusText.text = "Status: <color=#FFFF66>Not Connected</color>";
                
                // Show connect panel, hide lobby panel
                if (_connectPanel != null) _connectPanel.SetActive(true);
                if (_lobbyPanel != null) _lobbyPanel.SetActive(false);
            }
        }

        private void UpdatePlayerList()
        {
            if (_playerListText == null || NetworkManager.Instance == null) return;
            
            var players = NetworkManager.Instance.ConnectedPlayers;
            string list = $"<color=#66FF66>● {SteamFriends.GetPersonaName()}</color> (You)\n";
            
            foreach (var playerId in players)
            {
                string name = SteamFriends.GetFriendPersonaName(playerId);
                list += $"<color=#66CCFF>●</color> {name}\n";
            }
            
            _playerListText.text = list;
        }

        #endregion
    }
}
