using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// AI Chat tab: free-form chat with Claude about the Unity project.
    /// Supports optional context attachment, conversation history, and inline image display.
    /// </summary>
    public class AIChatTab
    {
        private readonly IChatHost _host;

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

        // Mode toggle
        private bool _imageGenMode;

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

            // Image data (for generated images)
            public Texture2D image;
            public string imageBase64;
            public bool imageSaved;
            public string imageSavePath;
        }

        public AIChatTab(IChatHost host)
        {
            _host = host;
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

                // Text content
                if (!string.IsNullOrEmpty(msg.content))
                {
                    if (msg.isUser)
                        EditorGUILayout.LabelField(msg.content, EditorStyles.wordWrappedLabel);
                    else
                        MarkdownRenderer.Render(msg.content);
                }

                // Inline image display
                if (msg.image != null)
                {
                    DrawInlineImage(msg);
                }

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

        private void DrawInlineImage(ChatMessage msg)
        {
            EditorGUILayout.Space(4);

            // Image preview (max 256px wide, maintain aspect ratio)
            float maxWidth = Mathf.Min(256f, EditorGUIUtility.currentViewWidth - 80f);
            float aspect = (float)msg.image.height / msg.image.width;
            float displayWidth = maxWidth;
            float displayHeight = displayWidth * aspect;

            var imageRect = GUILayoutUtility.GetRect(displayWidth, displayHeight);
            EditorGUI.DrawPreviewTexture(imageRect, msg.image, null, ScaleMode.ScaleToFit);

            EditorGUILayout.Space(2);

            // Save button
            EditorGUILayout.BeginHorizontal();
            if (msg.imageSaved)
            {
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.GreenStatus;
                EditorGUILayout.LabelField($"Saved: {msg.imageSavePath}", EditorStyles.miniLabel);
                GUI.color = oldColor;

                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(msg.imageSavePath);
                    if (asset != null) EditorGUIUtility.PingObject(asset);
                }
            }
            else
            {
                if (GUILayout.Button("Save to Assets", EditorStyles.miniButton, GUILayout.Width(100)))
                {
                    SaveImageToAssets(msg);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void SaveImageToAssets(ChatMessage msg)
        {
            // Default folder
            string folder = "Assets/GeneratedImages";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "GeneratedImages");
            }

            string filename = $"NanoBanana_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            string path = EditorUtility.SaveFilePanel("Save Generated Image", folder, filename, "png");

            if (string.IsNullOrEmpty(path)) return;

            // Convert to project-relative path
            if (path.StartsWith(Application.dataPath))
            {
                string relativePath = "Assets" + path.Substring(Application.dataPath.Length);

                byte[] pngData = msg.image.EncodeToPNG();
                File.WriteAllBytes(path, pngData);
                AssetDatabase.Refresh();

                msg.imageSaved = true;
                msg.imageSavePath = relativePath;
                _host.Repaint();
            }
            else
            {
                // Save outside project (just write file, no asset import)
                byte[] pngData = msg.image.EncodeToPNG();
                File.WriteAllBytes(path, pngData);
                msg.imageSaved = true;
                msg.imageSavePath = path;
                _host.Repaint();
            }
        }

        #endregion

        #region Input Area

        private void DrawInputArea()
        {
            EditorGUILayout.Space(2);

            // Mode toggle bar
            EditorGUILayout.BeginHorizontal();
            var chatStyle = _imageGenMode ? EditorStyles.miniButton : EditorStyles.miniBoldLabel;
            var imgStyle = _imageGenMode ? EditorStyles.miniBoldLabel : EditorStyles.miniButton;
            if (GUILayout.Button("Chat", chatStyle, GUILayout.Width(50)))
                _imageGenMode = false;
            if (GUILayout.Button("Image Gen", imgStyle, GUILayout.Width(70)))
                _imageGenMode = true;
            GUILayout.FlexibleSpace();

            if (_imageGenMode)
            {
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.CyanStatus;
                EditorGUILayout.LabelField("Claude enhances prompt + Gemini generates", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }

            EditorGUILayout.EndHorizontal();

            // Quick presets (Chat mode only)
            if (!_imageGenMode && _showQuickMenu)
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

            // Text input
            string placeholder = _imageGenMode ? "Describe the image you want..." : "";
            _inputText = EditorGUILayout.TextField(_inputText, GUILayout.Height(24), GUILayout.ExpandWidth(true));

            // Send on Enter (without shift)
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return
                && !Event.current.shift && !string.IsNullOrEmpty(_inputText) && !_isWaitingForResponse)
            {
                if (_imageGenMode)
                    SendImageGenMessage();
                else
                    SendMessage();
                Event.current.Use();
            }

            EditorGUI.BeginDisabledGroup(_isWaitingForResponse || string.IsNullOrEmpty(_inputText));
            string btnLabel = _imageGenMode ? "Generate" : "Send";
            float btnWidth = _imageGenMode ? 65 : 50;
            if (GUILayout.Button(btnLabel, GUILayout.Width(btnWidth), GUILayout.Height(24)))
            {
                if (_imageGenMode)
                    SendImageGenMessage();
                else
                    SendMessage();
            }
            EditorGUI.EndDisabledGroup();

            // Quick menu toggle (Chat mode only)
            if (!_imageGenMode)
            {
                string quickLabel = _showQuickMenu ? "Quick ^" : "Quick v";
                if (GUILayout.Button(quickLabel, GUILayout.Width(60), GUILayout.Height(24)))
                {
                    _showQuickMenu = !_showQuickMenu;
                }
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
                    _host.Repaint();
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
                    _host.Repaint();
                },
                onStatus: status =>
                {
                    _statusText = status;
                    _scrollToBottom = true;
                    _host.Repaint();
                },
                language: _host.SelectedLanguage
            );

            _host.Repaint();
        }

        /// <summary>
        /// Image Gen mode: sends prompt to Claude for enhancement, then generates image.
        /// Uses image/enhance message type instead of ai/query.
        /// </summary>
        private void SendImageGenMessage()
        {
            if (string.IsNullOrWhiteSpace(_inputText) || _isWaitingForResponse) return;

            string userMessage = _inputText.Trim();
            _inputText = "";

            _messages.Add(new ChatMessage
            {
                isUser = true,
                content = $"[Image Gen] {userMessage}",
                timestamp = DateTime.Now.ToString("HH:mm")
            });

            _isWaitingForResponse = true;
            _scrollToBottom = true;
            _statusText = "Enhancing prompt with Claude...";

            AIRequestHandler.SendImageEnhance(userMessage,
                onStatus: status =>
                {
                    _statusText = status;
                    _scrollToBottom = true;
                    _host.Repaint();
                },
                onComplete: result =>
                {
                    if (!string.IsNullOrEmpty(result))
                    {
                        _messages.Add(new ChatMessage
                        {
                            isUser = false,
                            content = result,
                            timestamp = DateTime.Now.ToString("HH:mm")
                        });
                    }
                    _statusText = null;
                    _isWaitingForResponse = false;
                    _scrollToBottom = true;
                    _host.Repaint();
                },
                language: _host.SelectedLanguage
            );

            _host.Repaint();
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

        /// <summary>Add a generated image to the chat as an AI message.</summary>
        public void AddGeneratedImage(string base64Data, string description = null)
        {
            try
            {
                byte[] imageBytes = Convert.FromBase64String(base64Data);
                var texture = new Texture2D(2, 2);
                if (texture.LoadImage(imageBytes))
                {
                    _messages.Add(new ChatMessage
                    {
                        isUser = false,
                        content = description ?? "Generated image:",
                        timestamp = DateTime.Now.ToString("HH:mm"),
                        image = texture,
                        imageBase64 = base64Data
                    });
                    _scrollToBottom = true;
                    _host.Repaint();
                }
            }
            catch (Exception e)
            {
                AddSystemMessage($"Failed to load image: {e.Message}");
            }
        }

        #endregion
    }
}
