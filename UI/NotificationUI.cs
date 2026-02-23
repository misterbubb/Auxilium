using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Steamworks;

namespace Vertigo2Multiplayer.UI
{
    /// <summary>
    /// Shows in-game notifications for multiplayer events
    /// </summary>
    public class NotificationUI : MonoBehaviour
    {
        public static NotificationUI Instance { get; private set; }
        
        private Canvas _canvas;
        private GameObject _notificationContainer;
        private Queue<NotificationData> _pendingNotifications = new();
        private GameObject _currentNotification;
        private float _notificationTimer;
        private const float NOTIFICATION_DURATION = 4f;
        private const float FADE_DURATION = 0.5f;

        private struct NotificationData
        {
            public string Title;
            public string Message;
            public Color Color;
        }

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            CreateCanvas();
        }

        private void Start()
        {
            // Subscribe to network events
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.OnPlayerJoined += OnPlayerJoined;
                NetworkManager.Instance.OnPlayerLeft += OnPlayerLeft;
                NetworkManager.Instance.OnConnectedToServer += OnConnected;
                NetworkManager.Instance.OnDisconnectedFromServer += OnDisconnected;
            }
        }

        private void OnDestroy()
        {
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.OnPlayerJoined -= OnPlayerJoined;
                NetworkManager.Instance.OnPlayerLeft -= OnPlayerLeft;
                NetworkManager.Instance.OnConnectedToServer -= OnConnected;
                NetworkManager.Instance.OnDisconnectedFromServer -= OnDisconnected;
            }
        }

        private void CreateCanvas()
        {
            // Create a world-space canvas that follows the camera
            var canvasObj = new GameObject("NotificationCanvas");
            canvasObj.transform.SetParent(transform);
            
            _canvas = canvasObj.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 1000;
            
            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            
            canvasObj.AddComponent<GraphicRaycaster>();
            
            // Container for notifications (top-right corner)
            _notificationContainer = new GameObject("NotificationContainer");
            _notificationContainer.transform.SetParent(_canvas.transform, false);
            
            var containerRect = _notificationContainer.AddComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(1, 1);
            containerRect.anchorMax = new Vector2(1, 1);
            containerRect.pivot = new Vector2(1, 1);
            containerRect.anchoredPosition = new Vector2(-20, -20);
            containerRect.sizeDelta = new Vector2(400, 100);
        }

        private void Update()
        {
            // Handle notification display timing
            if (_currentNotification != null)
            {
                _notificationTimer += Time.unscaledDeltaTime;
                
                // Fade out
                if (_notificationTimer > NOTIFICATION_DURATION - FADE_DURATION)
                {
                    float fadeProgress = (_notificationTimer - (NOTIFICATION_DURATION - FADE_DURATION)) / FADE_DURATION;
                    var group = _currentNotification.GetComponent<CanvasGroup>();
                    if (group != null)
                        group.alpha = 1f - fadeProgress;
                }
                
                // Remove when done
                if (_notificationTimer >= NOTIFICATION_DURATION)
                {
                    Destroy(_currentNotification);
                    _currentNotification = null;
                }
            }
            
            // Show next notification if queue has items
            if (_currentNotification == null && _pendingNotifications.Count > 0)
            {
                ShowNotification(_pendingNotifications.Dequeue());
            }
        }

        private void ShowNotification(NotificationData data)
        {
            _notificationTimer = 0f;
            
            var notifObj = new GameObject("Notification");
            notifObj.transform.SetParent(_notificationContainer.transform, false);
            
            var rect = notifObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(0, 80);
            
            // Background
            var bg = notifObj.AddComponent<Image>();
            bg.color = new Color(0.1f, 0.1f, 0.15f, 0.9f);
            
            // Add canvas group for fading
            var group = notifObj.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            
            // Layout
            var layout = notifObj.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.padding = new RectOffset(15, 15, 10, 10);
            layout.spacing = 5;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            
            // Accent bar
            var accentObj = new GameObject("Accent");
            accentObj.transform.SetParent(notifObj.transform, false);
            var accentRect = accentObj.AddComponent<RectTransform>();
            accentRect.sizeDelta = new Vector2(0, 4);
            var accentImage = accentObj.AddComponent<Image>();
            accentImage.color = data.Color;
            
            // Title
            var titleObj = new GameObject("Title");
            titleObj.transform.SetParent(notifObj.transform, false);
            var titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(0, 25);
            var titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = data.Title;
            titleText.fontSize = 20;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.color = data.Color;
            
            // Message
            var msgObj = new GameObject("Message");
            msgObj.transform.SetParent(notifObj.transform, false);
            var msgRect = msgObj.AddComponent<RectTransform>();
            msgRect.sizeDelta = new Vector2(0, 22);
            var msgText = msgObj.AddComponent<TextMeshProUGUI>();
            msgText.text = data.Message;
            msgText.fontSize = 16;
            msgText.alignment = TextAlignmentOptions.Center;
            msgText.color = Color.white;
            
            _currentNotification = notifObj;
            
            // Play sound (if available)
            // AudioSource.PlayClipAtPoint(notificationSound, Camera.main.transform.position);
        }

        #region Public API

        public void ShowPlayerJoined(string playerName)
        {
            _pendingNotifications.Enqueue(new NotificationData
            {
                Title = "PLAYER JOINED",
                Message = playerName,
                Color = new Color(0.3f, 0.9f, 0.3f) // Green
            });
        }

        public void ShowPlayerLeft(string playerName)
        {
            _pendingNotifications.Enqueue(new NotificationData
            {
                Title = "PLAYER LEFT",
                Message = playerName,
                Color = new Color(0.9f, 0.6f, 0.2f) // Orange
            });
        }

        public void ShowConnected(bool asHost)
        {
            _pendingNotifications.Enqueue(new NotificationData
            {
                Title = asHost ? "LOBBY CREATED" : "CONNECTED",
                Message = asHost ? "Friends can now join!" : "Joined multiplayer session",
                Color = new Color(0.3f, 0.7f, 1f) // Blue
            });
        }

        public void ShowDisconnected()
        {
            _pendingNotifications.Enqueue(new NotificationData
            {
                Title = "DISCONNECTED",
                Message = "Left multiplayer session",
                Color = new Color(0.9f, 0.3f, 0.3f) // Red
            });
        }

        public void ShowCustom(string title, string message, Color color)
        {
            _pendingNotifications.Enqueue(new NotificationData
            {
                Title = title,
                Message = message,
                Color = color
            });
        }

        /// <summary>
        /// Show a simple notification with default styling
        /// </summary>
        public void ShowNotification(string message, float duration = 4f)
        {
            _pendingNotifications.Enqueue(new NotificationData
            {
                Title = "MULTIPLAYER",
                Message = message,
                Color = new Color(0.3f, 0.7f, 1f) // Blue
            });
        }

        #endregion

        #region Event Handlers

        private void OnPlayerJoined(CSteamID steamId)
        {
            string name = SteamFriends.GetFriendPersonaName(steamId);
            ShowPlayerJoined(name);
        }

        private void OnPlayerLeft(CSteamID steamId)
        {
            string name = SteamFriends.GetFriendPersonaName(steamId);
            ShowPlayerLeft(name);
        }

        private void OnConnected()
        {
            bool isHost = NetworkManager.Instance?.IsHost ?? false;
            ShowConnected(isHost);
        }

        private void OnDisconnected()
        {
            ShowDisconnected();
        }

        #endregion
    }
}
