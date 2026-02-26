using System;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Main Shader Inspector EditorWindow.
    /// Provides tabbed interface for shader browsing, material inspection,
    /// pipeline dashboard, logs, and AI chat.
    /// Menu: Tools > Shader MCP > Shader Inspector
    /// </summary>
    public class ShaderInspectorWindow : EditorWindow
    {
        private enum Tab { Shaders, Materials, Pipeline, Logs, AIChat }
        private static readonly string[] TabNames = { "Shaders", "Materials", "Pipeline", "Logs", "AI Chat" };

        private Tab _currentTab = Tab.Shaders;

        // Tab instances
        private ShaderBrowserTab _shaderTab;
        private MaterialBrowserTab _materialTab;
        private PipelineDashboardTab _pipelineTab;
        private ShaderLogsTab _logsTab;
        private AIChatTab _aiChatTab;

        // Shared state across tabs
        private string _selectedShaderPath;
        private string _selectedShaderName;
        private int _totalShaderCount;
        private string _pipelineType = "...";
        private bool _aiConnected;

        [MenuItem("Tools/Shader MCP/Shader Inspector")]
        public static void ShowWindow()
        {
            var window = GetWindow<ShaderInspectorWindow>("Shader Inspector");
            window.minSize = new Vector2(700, 450);
        }

        private void OnEnable()
        {
            _shaderTab = new ShaderBrowserTab(this);
            _materialTab = new MaterialBrowserTab(this);
            _pipelineTab = new PipelineDashboardTab();
            _logsTab = new ShaderLogsTab(this);
            _aiChatTab = new AIChatTab(this);

            RefreshPipelineInfo();
            CheckAIConnection();
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawTabBar();
            DrawTabContent();
            DrawStatusBar();

            // Handle drag and drop anywhere in the window
            HandleDragAndDrop();

            if (Event.current.type == EventType.MouseDown)
                Repaint();
        }

        private void Update()
        {
            // Check for pending AI responses
            if (AIRequestHandler.HasPendingRequests)
                Repaint();
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label("Shader Inspector", ShaderInspectorStyles.HeaderLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Refresh All", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                RefreshAll();
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Tab Bar

        private void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal();

            for (int i = 0; i < TabNames.Length; i++)
            {
                var tab = (Tab)i;
                bool isSelected = _currentTab == tab;
                var style = isSelected ? ShaderInspectorStyles.TabSelected : ShaderInspectorStyles.TabNormal;

                if (GUILayout.Button(TabNames[i], style))
                {
                    _currentTab = tab;
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Tab Content

        private void DrawTabContent()
        {
            switch (_currentTab)
            {
                case Tab.Shaders:
                    _shaderTab?.OnGUI();
                    break;
                case Tab.Materials:
                    _materialTab?.OnGUI();
                    break;
                case Tab.Pipeline:
                    _pipelineTab?.OnGUI();
                    break;
                case Tab.Logs:
                    _logsTab?.OnGUI();
                    break;
                case Tab.AIChat:
                    _aiChatTab?.OnGUI();
                    break;
            }
        }

        #endregion

        #region Status Bar

        private void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(ShaderInspectorStyles.StatusBar);

            GUILayout.Label($"Status: {_totalShaderCount} shaders | {_pipelineType}");
            GUILayout.FlexibleSpace();

            var oldColor = GUI.color;
            GUI.color = _aiConnected ? ShaderInspectorStyles.GreenStatus : ShaderInspectorStyles.RedStatus;
            GUILayout.Label(_aiConnected ? "AI: Connected" : "AI: Disconnected");
            GUI.color = oldColor;

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Drag and Drop

        private void HandleDragAndDrop()
        {
            if (Event.current.type != EventType.DragUpdated && Event.current.type != EventType.DragPerform)
                return;

            if (DragAndDrop.objectReferences == null || DragAndDrop.objectReferences.Length == 0)
                return;

            var obj = DragAndDrop.objectReferences[0];

            if (obj is Shader || obj is Material)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Link;

                if (Event.current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    string path = AssetDatabase.GetAssetPath(obj);

                    if (obj is Shader)
                    {
                        _currentTab = Tab.Shaders;
                        _shaderTab?.SelectShader(path);
                    }
                    else if (obj is Material)
                    {
                        _currentTab = Tab.Materials;
                        _materialTab?.SelectMaterial(path);
                    }
                    Repaint();
                }

                Event.current.Use();
            }
        }

        #endregion

        #region Public API (for cross-tab communication)

        /// <summary>Navigate to a shader in the Shaders tab.</summary>
        public void NavigateToShader(string shaderPath, string shaderName = null)
        {
            _currentTab = Tab.Shaders;
            _selectedShaderPath = shaderPath;
            _selectedShaderName = shaderName;
            _shaderTab?.SelectShader(shaderPath);
            Repaint();
        }

        /// <summary>Set the current shader context for AI chat.</summary>
        public void SetAIContext(string shaderPath, string shaderName)
        {
            _selectedShaderPath = shaderPath;
            _selectedShaderName = shaderName;
            _aiChatTab?.SetContext(shaderPath, shaderName);
        }

        /// <summary>Switch to AI Chat tab with a pre-filled prompt.</summary>
        public void AskAI(string prompt, string shaderContext = null)
        {
            _currentTab = Tab.AIChat;
            _aiChatTab?.AskQuestion(prompt, shaderContext);
            Repaint();
        }

        public string SelectedShaderPath => _selectedShaderPath;
        public string SelectedShaderName => _selectedShaderName;
        public bool IsAIConnected => _aiConnected;

        #endregion

        #region Refresh

        private void RefreshAll()
        {
            _shaderTab?.Refresh();
            _materialTab?.Refresh();
            _pipelineTab?.Refresh();
            _logsTab?.Refresh();
            RefreshPipelineInfo();
            CheckAIConnection();
        }

        private void RefreshPipelineInfo()
        {
            try
            {
                string json = PipelineDetector.GetPipelineInfoJson();
                _pipelineType = JsonHelper.GetString(json, "pipelineType") ?? "Unknown";
            }
            catch
            {
                _pipelineType = "Unknown";
            }
        }

        private void CheckAIConnection()
        {
            _aiConnected = AIRequestHandler.IsAvailable;
        }

        public void UpdateShaderCount(int count)
        {
            _totalShaderCount = count;
        }

        #endregion
    }
}
