using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Standalone AI Chat window.
    /// Menu: Tools > Unity Agent > AI Chat
    /// </summary>
    public class AIChatWindow : EditorWindow, IChatHost
    {
        private AIChatTab _chatTab;

        // Language setting
        private int _languageIndex;
        private static readonly string[] LanguageLabels = { "Auto", "\ud55c\uad6d\uc5b4", "English", "\u65e5\u672c\u8a9e", "\u4e2d\u6587" };
        private static readonly string[] LanguageCodes = { "", "Korean", "English", "Japanese", "Chinese" };
        public string SelectedLanguage => _languageIndex > 0 ? LanguageCodes[_languageIndex] : null;

        private bool _aiConnected;
        private double _lastAICheckTime;

        [MenuItem("Tools/Unity Agent/AI Chat")]
        public static void ShowWindow()
        {
            var window = GetWindow<AIChatWindow>("AI Chat");
            window.minSize = new Vector2(400, 300);
        }

        private void OnEnable()
        {
            UnityAgentServer.EnsureRunning();
            _chatTab = new AIChatTab(this);
            CheckAIConnection();
        }

        private void OnGUI()
        {
            DrawToolbar();
            _chatTab?.OnGUI();
            DrawStatusBar();
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
                if (wasConnected != _aiConnected)
                    Repaint();
            }
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("AI Chat", ShaderInspectorStyles.HeaderLabel);
            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Lang:", EditorStyles.miniLabel, GUILayout.Width(30));
            _languageIndex = EditorGUILayout.Popup(_languageIndex, LanguageLabels, EditorStyles.toolbarPopup, GUILayout.Width(65));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(ShaderInspectorStyles.StatusBar);

            GUILayout.Label("AI Chat");
            GUILayout.FlexibleSpace();

            var oldColor = GUI.color;
            GUI.color = _aiConnected ? ShaderInspectorStyles.GreenStatus : ShaderInspectorStyles.RedStatus;
            GUILayout.Label(_aiConnected ? "AI: Connected" : "AI: Disconnected");
            GUI.color = oldColor;

            EditorGUILayout.EndHorizontal();
        }

        private void CheckAIConnection()
        {
            _aiConnected = AIRequestHandler.IsAvailable;
        }

        /// <summary>Set context and optionally ask a question.</summary>
        public void SetContextAndAsk(string assetPath, string assetName, string prompt = null)
        {
            _chatTab?.SetContext(assetPath, assetName);
            if (!string.IsNullOrEmpty(prompt))
                _chatTab?.AskQuestion(prompt);
        }
    }
}
