using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAgent.Editor
{
    /// <summary>
    /// AI-collaborative Frame Debugger window.
    /// Captures a frame via FrameDebugBridge, shows aggregate views (summary/events/compare),
    /// and hands frame context to an embedded AI chat so the user can ask Claude about it.
    ///
    /// Menu: Tools > Unity Agent > Frame Debugger AI
    /// </summary>
    public class FrameDebuggerAIWindow : EditorWindow, IChatHost
    {
        private enum Tab { Overview, Events, Compare, AIChat }
        private static readonly string[] TabNames = { "Overview", "Events", "Compare", "AI Chat" };

        private Tab _currentTab = Tab.Overview;

        private FrameOverviewTab _overviewTab;
        private FrameEventsTab _eventsTab;
        private FrameCompareTab _compareTab;
        private AIChatTab _aiChatTab;

        // Capture state (shared across tabs)
        private string _lastSummaryJson;
        private int _eventCount;
        private bool _isCaptured;
        private string _lastError;

        // Language
        private int _languageIndex;
        private static readonly string[] LanguageLabels = { "Auto", "\ud55c\uad6d\uc5b4", "English", "\u65e5\u672c\u8a9e", "\u4e2d\u6587" };
        private static readonly string[] LanguageCodes = { "", "Korean", "English", "Japanese", "Chinese" };
        public string SelectedLanguage => _languageIndex > 0 ? LanguageCodes[_languageIndex] : null;

        // Connection
        private bool _aiConnected;
        private double _lastAICheckTime;

        [MenuItem("Tools/Unity Agent/Frame Debugger AI")]
        public static void ShowWindow()
        {
            var window = GetWindow<FrameDebuggerAIWindow>("Frame Debugger AI");
            window.minSize = new Vector2(780, 500);
        }

        private void OnEnable()
        {
            UnityAgentServer.EnsureRunning();

            _overviewTab = new FrameOverviewTab(this);
            _eventsTab = new FrameEventsTab(this);
            _compareTab = new FrameCompareTab(this);
            _aiChatTab = new AIChatTab(this);

            CheckAIConnection();
        }

        private void Update()
        {
            if (AIRequestHandler.HasPendingRequests)
                Repaint();

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastAICheckTime > 2.0)
            {
                _lastAICheckTime = now;
                bool wasConnected = _aiConnected;
                CheckAIConnection();
                if (wasConnected != _aiConnected) Repaint();
            }
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawTabBar();
            DrawTabContent();
            DrawStatusBar();
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Frame Debugger AI", ShaderInspectorStyles.HeaderLabel);

            GUILayout.Space(8);

            if (GUILayout.Button("Capture Frame", EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                CaptureFrame();
            }
            if (GUILayout.Button("Refresh Summary", EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                RefreshSummary();
            }
            if (GUILayout.Button("Disable FD", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                FrameDebugBridge.Disable();
                _isCaptured = false;
                _eventCount = 0;
                _lastSummaryJson = null;
                Repaint();
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Lang:", EditorStyles.miniLabel, GUILayout.Width(30));
            _languageIndex = EditorGUILayout.Popup(_languageIndex, LanguageLabels, EditorStyles.toolbarPopup, GUILayout.Width(65));

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Tabs

        private void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < TabNames.Length; i++)
            {
                var tab = (Tab)i;
                bool sel = _currentTab == tab;
                var style = sel ? ShaderInspectorStyles.TabSelected : ShaderInspectorStyles.TabNormal;
                if (GUILayout.Button(TabNames[i], style))
                    _currentTab = tab;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTabContent()
        {
            switch (_currentTab)
            {
                case Tab.Overview: _overviewTab?.OnGUI(); break;
                case Tab.Events:   _eventsTab?.OnGUI();   break;
                case Tab.Compare:  _compareTab?.OnGUI();  break;
                case Tab.AIChat:   _aiChatTab?.OnGUI();   break;
            }
        }

        #endregion

        #region Status Bar

        private void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(ShaderInspectorStyles.StatusBar);

            if (IsCacheBuilding)
            {
                var old2 = GUI.color;
                GUI.color = ShaderInspectorStyles.CyanStatus;
                GUILayout.Label($"Caching: {_cacheCurrent}/{_cacheTotal}");
                GUI.color = old2;
            }
            else if (_isCaptured)
                GUILayout.Label($"Captured: {_eventCount} events");
            else if (!string.IsNullOrEmpty(_lastError))
            {
                var old = GUI.color;
                GUI.color = ShaderInspectorStyles.RedStatus;
                GUILayout.Label($"Error: {_lastError}");
                GUI.color = old;
            }
            else
                GUILayout.Label("Play Mode에서 Capture Frame 클릭");

            GUILayout.FlexibleSpace();

            var oldColor = GUI.color;
            GUI.color = _aiConnected ? ShaderInspectorStyles.GreenStatus : ShaderInspectorStyles.RedStatus;
            GUILayout.Label(_aiConnected ? "AI: Connected" : "AI: Disconnected");
            GUI.color = oldColor;

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Capture API (shared with tabs)

        /// <summary>
        /// Read current frame data from the FD capture system.
        /// If FD is not active, try to enable it. If that fails, guide the user.
        /// </summary>
        public void CaptureFrame()
        {
            _lastError = null;

            // FD already active with events? Just refresh + cache.
            int count = JsonHelper.GetInt(FrameDebugBridge.Status(), "eventCount", 0);
            if (count > 0)
            {
                _isCaptured = true;
                RefreshSummary();
                Repaint();
                return;
            }

            // FD not active — open FD window, then try to auto-click Enable
            EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");
            _autoEnableTick = 0;
            EditorApplication.update -= AutoEnableAndCapture;
            EditorApplication.update += AutoEnableAndCapture;
            Repaint();
        }

        private int _autoEnableTick;

        private void AutoEnableAndCapture()
        {
            _autoEnableTick++;

            // Check if FD got enabled (by us or by user)
            int count = JsonHelper.GetInt(FrameDebugBridge.Status(), "eventCount", 0);
            if (count > 0)
            {
                EditorApplication.update -= AutoEnableAndCapture;
                _lastError = null;
                _isCaptured = true;
                RefreshSummary();
                Repaint();
                return;
            }

            // Try to auto-click Enable via UITK at tick 5, 15, 30
            if (_autoEnableTick == 5 || _autoEnableTick == 15 || _autoEnableTick == 30)
            {
                try
                {
                    var asm = typeof(UnityEditor.Editor).Assembly;
                    var fdWindowType = asm.GetType("UnityEditor.FrameDebuggerWindow");
                    if (fdWindowType != null)
                    {
                        var fdWindow = Resources.FindObjectsOfTypeAll(fdWindowType);
                        if (fdWindow.Length > 0)
                        {
                            var window = fdWindow[0] as EditorWindow;
                            var root = window?.rootVisualElement;
                            if (root != null)
                            {
                                // Find ALL toggles and try each
                                var toggles = root.Query<Toggle>().ToList();
                                foreach (var t in toggles)
                                {
                                    if (!t.value) { t.value = true; break; }
                                }

                                // Also try ToolbarToggle
                                var tbToggles = root.Query<ToolbarToggle>().ToList();
                                foreach (var t in tbToggles)
                                {
                                    if (!t.value) { t.value = true; break; }
                                }
                            }
                        }
                    }
                }
                catch { }

                // Also try SetEnabled as fallback
                FrameDebugBridge.EnableCapture();
            }
        }

        /// <summary>Re-pull summary without resetting FD.</summary>
        public void RefreshSummary()
        {
            string json = FrameDebugBridge.Summary(topHotspots: 12, includeShaders: true);
            string err = JsonHelper.GetString(json, "error");
            if (!string.IsNullOrEmpty(err))
            {
                _lastError = err;
                _lastSummaryJson = null;
            }
            else
            {
                _lastError = null;
                _lastSummaryJson = json;
                _eventCount = JsonHelper.GetInt(json, "eventCount", 0);
                _isCaptured = _eventCount > 0 || _isCaptured;
                _overviewTab?.OnSummaryChanged(json);

                // Start background cache build (1 event per render frame)
                StartCacheBuilder(_eventCount);
            }
            Repaint();
        }

        public string LastSummaryJson => _lastSummaryJson;
        public int EventCount => _eventCount;
        public bool IsCaptured => _isCaptured;
        public bool IsCacheBuilding => _cacheTotal > 0 && _cacheCurrent < _cacheTotal;
        public int CacheCurrent => _cacheCurrent;
        public int CacheTotal => _cacheTotal;

        #endregion

        #region Async Cache Builder

        private int _cacheCurrent;
        private int _cacheTotal;
        private bool _cachePhaseRead; // false=SetLimit, true=Read

        private void StartCacheBuilder(int totalEvents)
        {
            FrameDebugBridge.ClearCache();
            _cacheCurrent = 0;
            _cacheTotal = totalEvents;
            _cachePhaseRead = false;
            EditorUtility.DisplayProgressBar("Frame Debugger AI", "Loading event data...", 0);
            EditorApplication.update -= CacheBuilderTick;
            EditorApplication.update += CacheBuilderTick;
        }

        private void CacheBuilderTick()
        {
            if (_cacheCurrent >= _cacheTotal)
            {
                EditorApplication.update -= CacheBuilderTick;
                EditorUtility.ClearProgressBar();
                _cacheTotal = 0;
                Repaint();
                return;
            }

            if (!_cachePhaseRead)
            {
                // Phase 1: SetLimit + force render
                FrameDebugBridge.SetLimitPublic(_cacheCurrent + 1);
                _cachePhaseRead = true;
            }
            else
            {
                // Phase 2: Read data
                string detail = FrameDebugBridge.GetEventDetail(_cacheCurrent);
                if (!string.IsNullOrEmpty(detail))
                    FrameDebugBridge.CacheEventDetail(_cacheCurrent, detail);

                _cacheCurrent++;
                _cachePhaseRead = false;

                float pct = (float)_cacheCurrent / _cacheTotal;
                EditorUtility.DisplayProgressBar("Frame Debugger AI",
                    $"Loading event data... {_cacheCurrent}/{_cacheTotal}", pct);
            }
        }

        #endregion

        #region Cross-tab navigation

        /// <summary>Jump to Events tab and focus a specific event index.</summary>
        public void GoToEvent(int eventIndex)
        {
            _currentTab = Tab.Events;
            _eventsTab?.SelectEvent(eventIndex);
            Repaint();
        }

        /// <summary>Push event A/B into Compare tab and switch.</summary>
        public void CompareEvents(int indexA, int indexB)
        {
            _currentTab = Tab.Compare;
            _compareTab?.SetPair(indexA, indexB);
            Repaint();
        }

        /// <summary>Set Compare slot A or B individually.</summary>
        public void SetCompareSlot(int eventIndex, bool isSlotA)
        {
            _compareTab?.SetSlot(eventIndex, isSlotA);
            Repaint();
        }

        /// <summary>
        /// Jump to AI Chat, pre-load a frame-context block, and optionally auto-send a prompt.
        /// </summary>
        public void AskAIAboutFrame(string prompt, string contextBlock, string contextLabel, string imagePath = null)
        {
            _currentTab = Tab.AIChat;
            _aiChatTab?.SetContext(contextLabel, contextLabel);

            if (!string.IsNullOrEmpty(imagePath))
            {
                // Include image in context for AI to analyze
                string imageContext = contextBlock + $"\n\n[RT Screenshot: {imagePath}]";
                _aiChatTab?.AskQuestion(prompt, imageContext);

                // Also show the image inline in chat
                try
                {
                    byte[] pngData = System.IO.File.ReadAllBytes(imagePath);
                    string base64 = System.Convert.ToBase64String(pngData);
                    _aiChatTab?.AddGeneratedImage(base64, $"Event render target");
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(prompt))
            {
                _aiChatTab?.AskQuestion(prompt, contextBlock);
            }
            Repaint();
        }

        #endregion

        #region IChatHost

        // Image-gen features not wired here — chat tab stays in text-only mode by default.

        #endregion

        private void CheckAIConnection()
        {
            _aiConnected = AIRequestHandler.IsAvailable;
        }
    }
}
