using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Standalone AI Chat window with Nano Banana (Gemini Image) integration.
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

        // Nano Banana settings
        private string _geminiApiKey = "";
        private int _modelIndex;
        private Texture2D _referenceImage;
        private bool _showSettings;

        private static readonly string[] ModelLabels =
        {
            "[FREE] Nano Banana",
            "[PAID] Nano Banana 2",
            "[PAID] Nano Banana Pro"
        };
        private static readonly string[] ModelIds =
        {
            "gemini-2.5-flash-preview-image-generation",
            "gemini-2.0-flash-exp-image-generation",
            "gemini-2.0-flash-exp-image-generation"
        };

        // EditorPrefs keys
        private const string PrefKeyApiKey = "UnityAgent_GeminiApiKey";
        private const string PrefKeyModel = "UnityAgent_GeminiModel";

        // IChatHost - Nano Banana properties
        public string GeminiApiKey => _geminiApiKey;
        public string GeminiModel => ModelIds[_modelIndex];
        public Texture2D ReferenceImage => _referenceImage;

        // Connection state
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

            // Load saved settings
            _geminiApiKey = EditorPrefs.GetString(PrefKeyApiKey, "");
            _modelIndex = EditorPrefs.GetInt(PrefKeyModel, 0);
        }

        private void OnGUI()
        {
            DrawToolbar();

            if (_showSettings)
                DrawSettingsPanel();

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

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("AI Chat", ShaderInspectorStyles.HeaderLabel);
            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Lang:", EditorStyles.miniLabel, GUILayout.Width(30));
            _languageIndex = EditorGUILayout.Popup(_languageIndex, LanguageLabels, EditorStyles.toolbarPopup, GUILayout.Width(65));

            // Settings toggle
            var settingsIcon = _showSettings ? "Settings (Hide)" : "Settings";
            if (GUILayout.Button(settingsIcon, EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                _showSettings = !_showSettings;
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Settings Panel

        private void DrawSettingsPanel()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header
            EditorGUILayout.LabelField("Nano Banana (Gemini Image)", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            // API Key
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("API Key:", GUILayout.Width(55));
            string newKey = EditorGUILayout.PasswordField(_geminiApiKey);
            if (newKey != _geminiApiKey)
            {
                _geminiApiKey = newKey;
                EditorPrefs.SetString(PrefKeyApiKey, _geminiApiKey);
            }
            EditorGUILayout.EndHorizontal();

            // API Key status
            if (string.IsNullOrEmpty(_geminiApiKey))
            {
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.YellowStatus;
                EditorGUILayout.LabelField("Get your key at: aistudio.google.com", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }

            // Model selection
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Model:", GUILayout.Width(55));
            int newModel = EditorGUILayout.Popup(_modelIndex, ModelLabels);
            if (newModel != _modelIndex)
            {
                _modelIndex = newModel;
                EditorPrefs.SetInt(PrefKeyModel, _modelIndex);
                EditorPrefs.SetString("UnityAgent_GeminiModel", ModelIds[_modelIndex]);
            }
            EditorGUILayout.EndHorizontal();

            // Reference image
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Ref Image:", GUILayout.Width(70));
            _referenceImage = (Texture2D)EditorGUILayout.ObjectField(
                _referenceImage, typeof(Texture2D), false, GUILayout.Height(18));
            if (_referenceImage != null)
            {
                if (GUILayout.Button("X", GUILayout.Width(20), GUILayout.Height(18)))
                {
                    _referenceImage = null;
                }
            }
            EditorGUILayout.EndHorizontal();

            // Reference image preview
            if (_referenceImage != null)
            {
                var previewRect = GUILayoutUtility.GetRect(100, 80, GUILayout.ExpandWidth(false));
                previewRect.x += 75;
                previewRect.width = 80;
                EditorGUI.DrawPreviewTexture(previewRect, _referenceImage, null, ScaleMode.ScaleToFit);
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Status Bar

        private void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(ShaderInspectorStyles.StatusBar);

            // Nano Banana status
            bool hasKey = !string.IsNullOrEmpty(_geminiApiKey);
            var oldColor = GUI.color;
            GUI.color = hasKey ? ShaderInspectorStyles.GreenStatus : ShaderInspectorStyles.DimText;
            GUILayout.Label(hasKey ? "IMG: Ready" : "IMG: No Key");
            GUI.color = oldColor;

            GUILayout.Label("|");

            GUILayout.Label(ModelLabels[_modelIndex], EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            // AI connection status
            oldColor = GUI.color;
            GUI.color = _aiConnected ? ShaderInspectorStyles.GreenStatus : ShaderInspectorStyles.RedStatus;
            GUILayout.Label(_aiConnected ? "AI: Connected" : "AI: Disconnected");
            GUI.color = oldColor;

            EditorGUILayout.EndHorizontal();
        }

        #endregion

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

        /// <summary>Display a generated image in the chat.</summary>
        public void DisplayGeneratedImage(string base64Data, string description)
        {
            _chatTab?.AddGeneratedImage(base64Data, description);
        }
    }
}
