using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// AI Chat tab: free-form chat with Claude about shaders.
    /// Supports context attachment, quick presets, and conversation history.
    /// </summary>
    public class AIChatTab
    {
        private readonly ShaderInspectorWindow _window;

        // Chat state
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private string _inputText = "";
        private Vector2 _chatScrollPos;
        private bool _isWaitingForResponse;
        private bool _scrollToBottom;

        // Context
        private string _contextShaderPath;
        private string _contextShaderName;
        private string _contextShaderCode;

        // Quick presets
        private bool _showQuickMenu;
        private static readonly string[] QuickPresets =
        {
            "Optimize this shader",
            "Explain errors and how to fix them",
            "Explain this shader for non-programmers",
            "How to reduce variant count",
            "Check for best practice violations",
            "Suggest performance improvements"
        };

        private class ChatMessage
        {
            public bool isUser;
            public string content;
            public string timestamp;
        }

        public AIChatTab(ShaderInspectorWindow window)
        {
            _window = window;
            AddSystemMessage("Hello! Ask me anything about shaders. Select a shader in the Shaders tab to automatically attach it as context.");
        }

        public void OnGUI()
        {
            // Context bar
            DrawContextBar();

            // Chat history
            DrawChatHistory();

            // Input area
            DrawInputArea();
        }

        #region Context Bar

        private void DrawContextBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (!string.IsNullOrEmpty(_contextShaderName))
            {
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.CyanStatus;
                EditorGUILayout.LabelField($"Context: {_contextShaderName}", GUILayout.ExpandWidth(true));
                GUI.color = oldColor;

                if (GUILayout.Button("X", EditorStyles.toolbarButton, GUILayout.Width(22)))
                {
                    ClearContext();
                }
            }
            else
            {
                EditorGUILayout.LabelField("No shader context (select a shader in Shaders tab)",
                    EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Clear Chat", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                _messages.Clear();
                AddSystemMessage("Chat cleared. Ask me anything about shaders.");
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Chat History

        private void DrawChatHistory()
        {
            _chatScrollPos = EditorGUILayout.BeginScrollView(_chatScrollPos, GUILayout.ExpandHeight(true));

            foreach (var msg in _messages)
            {
                var style = msg.isUser ? ShaderInspectorStyles.ChatBubbleUser : ShaderInspectorStyles.ChatBubbleAI;
                string prefix = msg.isUser ? "You" : "AI";

                EditorGUILayout.BeginVertical(style);

                EditorGUILayout.BeginHorizontal();
                var oldColor = GUI.color;
                GUI.color = msg.isUser ? Color.white : ShaderInspectorStyles.AIColor;
                EditorGUILayout.LabelField(prefix, EditorStyles.miniBoldLabel, GUILayout.Width(30));
                GUI.color = ShaderInspectorStyles.DimText;
                EditorGUILayout.LabelField(msg.timestamp, EditorStyles.miniLabel, GUILayout.Width(55));
                GUI.color = oldColor;
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    EditorGUIUtility.systemCopyBuffer = msg.content;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField(msg.content, EditorStyles.wordWrappedLabel);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            if (_isWaitingForResponse)
            {
                EditorGUILayout.BeginVertical(ShaderInspectorStyles.ChatBubbleAI);
                var oldColor2 = GUI.color;
                GUI.color = ShaderInspectorStyles.AIColor;
                EditorGUILayout.LabelField("AI is thinking...", EditorStyles.miniLabel);
                GUI.color = oldColor2;
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();

            if (_scrollToBottom)
            {
                _chatScrollPos.y = float.MaxValue;
                _scrollToBottom = false;
            }
        }

        #endregion

        #region Input Area

        private void DrawInputArea()
        {
            EditorGUILayout.Space(2);

            // Quick presets
            if (_showQuickMenu)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Quick Presets:", EditorStyles.miniBoldLabel);
                foreach (var preset in QuickPresets)
                {
                    if (GUILayout.Button(preset, EditorStyles.miniButton))
                    {
                        _inputText = preset;
                        _showQuickMenu = false;
                        SendMessage();
                    }
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.BeginHorizontal();

            // Multi-line text input
            _inputText = EditorGUILayout.TextField(_inputText, GUILayout.Height(24), GUILayout.ExpandWidth(true));

            // Send on Enter (without shift)
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && !Event.current.shift && !string.IsNullOrEmpty(_inputText) && !_isWaitingForResponse)
            {
                SendMessage();
                Event.current.Use();
            }

            EditorGUI.BeginDisabledGroup(_isWaitingForResponse || string.IsNullOrEmpty(_inputText));
            if (GUILayout.Button("Send", GUILayout.Width(50), GUILayout.Height(24)))
            {
                SendMessage();
            }
            EditorGUI.EndDisabledGroup();

            // Quick menu toggle
            string quickLabel = _showQuickMenu ? "Quick ^" : "Quick v";
            if (GUILayout.Button(quickLabel, GUILayout.Width(60), GUILayout.Height(24)))
            {
                _showQuickMenu = !_showQuickMenu;
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Message Handling

        private void SendMessage()
        {
            if (string.IsNullOrWhiteSpace(_inputText) || _isWaitingForResponse) return;

            string userMessage = _inputText.Trim();
            _inputText = "";
            _showQuickMenu = false;

            _messages.Add(new ChatMessage
            {
                isUser = true,
                content = userMessage,
                timestamp = DateTime.Now.ToString("HH:mm")
            });

            _isWaitingForResponse = true;
            _scrollToBottom = true;

            // Build context
            string shaderContext = null;
            if (!string.IsNullOrEmpty(_contextShaderPath))
            {
                shaderContext = _contextShaderCode;
                if (string.IsNullOrEmpty(shaderContext))
                    shaderContext = LoadShaderContext();
            }

            // Build conversation history for multi-turn
            var history = new List<string>();
            int startIdx = Math.Max(0, _messages.Count - 10); // Last 10 messages for context
            for (int i = startIdx; i < _messages.Count - 1; i++) // Exclude last (just added)
            {
                var msg = _messages[i];
                history.Add((msg.isUser ? "User: " : "Assistant: ") + msg.content);
            }

            string conversationContext = history.Count > 0 ? "\n\nPrevious conversation:\n" + string.Join("\n", history) : "";
            string fullPrompt = userMessage + conversationContext;

            AIRequestHandler.SendQuery(fullPrompt, shaderContext, response =>
            {
                _messages.Add(new ChatMessage
                {
                    isUser = false,
                    content = response ?? "Sorry, I couldn't get a response. Check AI connection.",
                    timestamp = DateTime.Now.ToString("HH:mm")
                });
                _isWaitingForResponse = false;
                _scrollToBottom = true;
                _window.Repaint();
            });

            _window.Repaint();
        }

        private void AddSystemMessage(string content)
        {
            _messages.Add(new ChatMessage
            {
                isUser = false,
                content = content,
                timestamp = DateTime.Now.ToString("HH:mm")
            });
        }

        private string LoadShaderContext()
        {
            try
            {
                if (string.IsNullOrEmpty(_contextShaderPath)) return null;

                var parts = new List<string>();
                parts.Add($"Shader: {_contextShaderName}");
                parts.Add($"Path: {_contextShaderPath}");

                string codeJson = ShaderAnalyzer.GetShaderCode(_contextShaderPath);
                var codeData = ShaderCodeData.Parse(codeJson);
                if (!string.IsNullOrEmpty(codeData.code))
                {
                    string code = codeData.code;
                    if (code.Length > 4000) code = code.Substring(0, 4000) + "\n... (truncated)";
                    parts.Add("\nShader Code:\n" + code);
                }

                string variantJson = ShaderAnalyzer.GetVariantInfo(_contextShaderPath);
                var variant = VariantInfo.Parse(variantJson);
                parts.Add($"\nVariants: {variant.totalVariantCount}, Passes: {variant.passCount}");
                if (variant.globalKeywords.Count > 0)
                    parts.Add("Global Keywords: " + string.Join(", ", variant.globalKeywords));

                _contextShaderCode = string.Join("\n", parts);
                return _contextShaderCode;
            }
            catch
            {
                return $"Shader: {_contextShaderName}\nPath: {_contextShaderPath}";
            }
        }

        #endregion

        #region Public API

        public void SetContext(string shaderPath, string shaderName)
        {
            _contextShaderPath = shaderPath;
            _contextShaderName = shaderName;
            _contextShaderCode = null; // Will be loaded lazily
        }

        public void ClearContext()
        {
            _contextShaderPath = null;
            _contextShaderName = null;
            _contextShaderCode = null;
        }

        public void AskQuestion(string prompt, string shaderContext = null)
        {
            if (!string.IsNullOrEmpty(shaderContext))
                _contextShaderCode = shaderContext;

            _inputText = prompt;
            SendMessage();
        }

        #endregion
    }
}
