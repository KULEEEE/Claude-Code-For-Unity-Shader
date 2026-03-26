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

        // Backend selection
        private int _backendIndex;
        private string _comfyuiUrl = "http://127.0.0.1:8188";

        private static readonly string[] BackendLabels = { "Nano Banana (Gemini)", "ComfyUI (Local)" };
        private static readonly string[] BackendIds = { "gemini", "comfyui" };

        private static readonly string[] ModelLabels =
        {
            "[FREE] Nano Banana",
            "[PAID] Nano Banana 2",
            "[PAID] Nano Banana Pro"
        };
        private static readonly string[] ModelIds =
        {
            "gemini-2.5-flash-image",
            "gemini-3.1-flash-image-preview",
            "gemini-3-pro-image-preview"
        };

        // EditorPrefs keys
        private const string PrefKeyApiKey = "UnityAgent_GeminiApiKey";
        private const string PrefKeyModel = "UnityAgent_GeminiModel";
        private const string PrefKeyBackend = "UnityAgent_ImageBackend";
        private const string PrefKeyComfyUrl = "UnityAgent_ComfyUIUrl";

        // IChatHost properties
        public string ImageBackend => BackendIds[_backendIndex];
        public string GeminiApiKey => _geminiApiKey;
        public string GeminiModel => ModelIds[_modelIndex];
        public string ComfyUIUrl => _comfyuiUrl;
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
            _backendIndex = EditorPrefs.GetInt(PrefKeyBackend, 0);
            _comfyuiUrl = EditorPrefs.GetString(PrefKeyComfyUrl, "http://127.0.0.1:8188");
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

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("AI Chat", ShaderInspectorStyles.HeaderLabel);
            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField("Lang:", EditorStyles.miniLabel, GUILayout.Width(30));
            _languageIndex = EditorGUILayout.Popup(_languageIndex, LanguageLabels, EditorStyles.toolbarPopup, GUILayout.Width(65));

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Settings Panel

        /// <summary>Draw image gen settings inline (called by AIChatTab in Image Gen mode).</summary>
        public new void DrawImageGenSettings()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Row 1: Backend selection
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Backend:", GUILayout.Width(55));
            int newBackend = EditorGUILayout.Popup(_backendIndex, BackendLabels);
            if (newBackend != _backendIndex)
            {
                _backendIndex = newBackend;
                EditorPrefs.SetInt(PrefKeyBackend, _backendIndex);
                EditorPrefs.SetString("UnityAgent_ImageBackend", BackendIds[_backendIndex]);
            }
            EditorGUILayout.EndHorizontal();

            if (_backendIndex == 0) // Gemini
            {
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

                if (string.IsNullOrEmpty(_geminiApiKey))
                {
                    var oldColor = GUI.color;
                    GUI.color = ShaderInspectorStyles.YellowStatus;
                    EditorGUILayout.LabelField("Get your key at: aistudio.google.com", EditorStyles.miniLabel);
                    GUI.color = oldColor;
                }
            }
            else // ComfyUI
            {
                // Server URL
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Server:", GUILayout.Width(55));
                string newUrl = EditorGUILayout.TextField(_comfyuiUrl);
                if (newUrl != _comfyuiUrl)
                {
                    _comfyuiUrl = newUrl;
                    EditorPrefs.SetString(PrefKeyComfyUrl, _comfyuiUrl);
                }
                EditorGUILayout.EndHorizontal();
            }

            // Reference image (common)
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Ref Image:", GUILayout.Width(65));
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

            // Small preview
            if (_referenceImage != null)
            {
                var previewRect = GUILayoutUtility.GetRect(60, 60, GUILayout.ExpandWidth(false));
                previewRect.x += 70;
                previewRect.width = 60;
                EditorGUI.DrawPreviewTexture(previewRect, _referenceImage, null, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Status Bar

        private void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(ShaderInspectorStyles.StatusBar);

            // Backend status
            var oldColor = GUI.color;
            if (_backendIndex == 0) // Gemini
            {
                bool hasKey = !string.IsNullOrEmpty(_geminiApiKey);
                GUI.color = hasKey ? ShaderInspectorStyles.GreenStatus : ShaderInspectorStyles.DimText;
                GUILayout.Label(hasKey ? "IMG: Ready" : "IMG: No Key");
                GUI.color = oldColor;
                GUILayout.Label("|");
                GUILayout.Label(ModelLabels[_modelIndex], EditorStyles.miniLabel);
            }
            else // ComfyUI
            {
                GUI.color = ShaderInspectorStyles.CyanStatus;
                GUILayout.Label("IMG: ComfyUI");
                GUI.color = oldColor;
                GUILayout.Label("|");
                GUILayout.Label(_comfyuiUrl, EditorStyles.miniLabel);
            }

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
