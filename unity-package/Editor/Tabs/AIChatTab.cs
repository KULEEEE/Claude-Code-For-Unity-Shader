using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// AI Chat tab: free-form chat with Claude about the Unity project.
    /// Supports optional context attachment and conversation history.
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
        private ChatMessage _streamingMessage;
        private string _statusText;

        // Context (generic — can be any asset)
        private string _contextAssetPath;
        private string _contextAssetName;
        private string _contextContent;


        // Quick presets
        private bool _showQuickMenu;
        private static readonly string[] QuickPresets =
        {
            "Explain this code",
            "Find potential issues",
            "Suggest optimizations",
            "How does this work?",
            "Check for best practice violations",
            "Suggest improvements"
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
            AddSystemMessage("Hello! Ask me anything about your Unity project.");
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

            if (!string.IsNullOrEmpty(_contextAssetName))
            {
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.CyanStatus;
                EditorGUILayout.LabelField($"Context: {_contextAssetName}", GUILayout.ExpandWidth(true));
                GUI.color = oldColor;

                if (GUILayout.Button("X", EditorStyles.toolbarButton, GUILayout.Width(22)))
                {
                    ClearContext();
                }
            }
            else
            {
                EditorGUILayout.LabelField("No context attached",
                    EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Clear Chat", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                _messages.Clear();
                AddSystemMessage("Chat cleared.");
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Chat History

        private void DrawChatHistory()
        {
            // Cache volatile streaming state to prevent Layout/Repaint mismatch
            bool showThinking = _isWaitingForResponse;

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

                if (msg.isUser)
                    EditorGUILayout.LabelField(msg.content, EditorStyles.wordWrappedLabel);
                else
                    MarkdownRenderer.Render(msg.content);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            if (showThinking)
            {
                string statusDisplay = !string.IsNullOrEmpty(_statusText) ? _statusText : "AI is thinking...";
                EditorGUILayout.BeginVertical(ShaderInspectorStyles.ChatBubbleAI);
                var oldColor2 = GUI.color;
                GUI.color = ShaderInspectorStyles.AIColor;
                EditorGUILayout.LabelField(statusDisplay, EditorStyles.miniLabel);
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
            string context = null;
            if (!string.IsNullOrEmpty(_contextAssetPath))
            {
                context = _contextContent;
                if (string.IsNullOrEmpty(context))
                    context = LoadAssetContext();
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

            _streamingMessage = null;
            _statusText = null;

            AIRequestHandler.SendQuery(fullPrompt, context,
                onChunk: chunk =>
                {
                    if (_streamingMessage == null)
                    {
                        _streamingMessage = new ChatMessage
                        {
                            isUser = false,
                            content = "",
                            timestamp = DateTime.Now.ToString("HH:mm")
                        };
                        _messages.Add(_streamingMessage);
                    }
                    _streamingMessage.content += chunk;
                    _scrollToBottom = true;
                    _window.Repaint();
                },
                onComplete: fullText =>
                {
                    string text = fullText ?? "Sorry, I couldn't get a response. Check AI connection.";
                    if (_streamingMessage != null)
                    {
                        _streamingMessage.content = text;
                    }
                    else
                    {
                        _messages.Add(new ChatMessage
                        {
                            isUser = false,
                            content = text,
                            timestamp = DateTime.Now.ToString("HH:mm")
                        });
                    }
                    _streamingMessage = null;
                    _statusText = null;
                    _isWaitingForResponse = false;
                    _scrollToBottom = true;
                    _window.Repaint();
                },
                onStatus: status =>
                {
                    _statusText = status;
                    _scrollToBottom = true;
                    _window.Repaint();
                },
                language: _window.SelectedLanguage
            );

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

        private string LoadAssetContext()
        {
            try
            {
                if (string.IsNullOrEmpty(_contextAssetPath)) return null;

                var parts = new List<string>();
                parts.Add($"Asset: {_contextAssetName}");
                parts.Add($"Path: {_contextAssetPath}");

                // Try to load shader-specific context
                if (_contextAssetPath.EndsWith(".shader") || _contextAssetPath.EndsWith(".cginc") ||
                    _contextAssetPath.EndsWith(".hlsl") || _contextAssetPath.EndsWith(".compute"))
                {
                    string codeJson = ShaderAnalyzer.GetShaderCode(_contextAssetPath);
                    var codeData = ShaderCodeData.Parse(codeJson);
                    if (!string.IsNullOrEmpty(codeData.code))
                    {
                        string code = codeData.code;
                        if (code.Length > 4000) code = code.Substring(0, 4000) + "\n... (truncated)";
                        parts.Add("\nCode:\n" + code);
                    }
                }

                _contextContent = string.Join("\n", parts);
                return _contextContent;
            }
            catch
            {
                return $"Asset: {_contextAssetName}\nPath: {_contextAssetPath}";
            }
        }

        #endregion

        #region Public API

        public void SetContext(string assetPath, string assetName)
        {
            _contextAssetPath = assetPath;
            _contextAssetName = assetName;
            _contextContent = null; // Will be loaded lazily
        }

        public void ClearContext()
        {
            _contextAssetPath = null;
            _contextAssetName = null;
            _contextContent = null;
        }

        public void AskQuestion(string prompt, string context = null)
        {
            if (!string.IsNullOrEmpty(context))
                _contextContent = context;

            _inputText = prompt;
            SendMessage();
        }

        #endregion
    }
}
