using UnityEditor;
using UnityEngine;

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

            if (_isCaptured)
                GUILayout.Label($"Captured: {_eventCount} events");
            else if (!string.IsNullOrEmpty(_lastError))
            {
                var old = GUI.color;
                GUI.color = ShaderInspectorStyles.RedStatus;
                GUILayout.Label($"Error: {_lastError}");
                GUI.color = old;
            }
            else
                GUILayout.Label("Not captured — press 'Capture Frame' while Play Mode is active");

            GUILayout.FlexibleSpace();

            var oldColor = GUI.color;
            GUI.color = _aiConnected ? ShaderInspectorStyles.GreenStatus : ShaderInspectorStyles.RedStatus;
            GUILayout.Label(_aiConnected ? "AI: Connected" : "AI: Disconnected");
            GUI.color = oldColor;

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Capture API (shared with tabs)

        /// <summary>Capture current frame + pull a summary. Enters FD if not already on.</summary>
        public void CaptureFrame()
        {
            if (!EditorApplication.isPlaying)
            {
                _lastError = "Enter Play Mode first — FrameDebugger only captures during play";
                _isCaptured = false;
                Repaint();
                return;
            }

            // Capture with a modest cap — summary() pulls full deep-sweep data anyway.
            string capJson = FrameDebugBridge.Capture(maxEvents: 256, includeShaders: false);
            string err = JsonHelper.GetString(capJson, "error");
            if (!string.IsNullOrEmpty(err))
            {
                _lastError = err + " : " + (JsonHelper.GetString(capJson, "detail") ?? "");
                _isCaptured = false;
                Repaint();
                return;
            }

            _lastError = null;
            _isCaptured = true;
            RefreshSummary();
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
            }
            Repaint();
        }

        public string LastSummaryJson => _lastSummaryJson;
        public int EventCount => _eventCount;
        public bool IsCaptured => _isCaptured;

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
        public void AskAIAboutFrame(string prompt, string contextBlock, string contextLabel)
        {
            _currentTab = Tab.AIChat;
            _aiChatTab?.SetContext(contextLabel, contextLabel);
            if (!string.IsNullOrEmpty(prompt))
                _aiChatTab?.AskQuestion(prompt, contextBlock);
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
