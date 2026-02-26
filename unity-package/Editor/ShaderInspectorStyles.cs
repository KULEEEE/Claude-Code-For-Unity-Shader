using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Centralized GUIStyle and Color constants for the Shader Inspector window.
    /// Lazy-initialized to avoid issues with EditorStyles not being ready.
    /// </summary>
    public static class ShaderInspectorStyles
    {
        // Variant count thresholds
        public const int VariantWarningThreshold = 128;
        public const int VariantDangerThreshold = 1024;

        // Colors
        public static readonly Color GreenStatus = new Color(0.2f, 0.8f, 0.2f);
        public static readonly Color YellowStatus = new Color(0.9f, 0.8f, 0.1f);
        public static readonly Color RedStatus = new Color(0.9f, 0.2f, 0.2f);
        public static readonly Color CyanStatus = new Color(0.3f, 0.8f, 0.9f);
        public static readonly Color AIColor = new Color(0.6f, 0.4f, 0.9f);
        public static readonly Color DimText = new Color(0.6f, 0.6f, 0.6f);
        public static readonly Color SplitterColor = new Color(0.15f, 0.15f, 0.15f);

        private static GUIStyle _tabNormal;
        private static GUIStyle _tabSelected;
        private static GUIStyle _headerLabel;
        private static GUIStyle _statusBar;
        private static GUIStyle _resultArea;
        private static GUIStyle _listItem;
        private static GUIStyle _listItemSelected;
        private static GUIStyle _sectionHeader;
        private static GUIStyle _aiResponseArea;
        private static GUIStyle _chatBubbleUser;
        private static GUIStyle _chatBubbleAI;
        private static GUIStyle _codeArea;

        public static GUIStyle TabNormal
        {
            get
            {
                if (_tabNormal == null)
                {
                    _tabNormal = new GUIStyle(EditorStyles.toolbarButton)
                    {
                        fixedHeight = 28,
                        fontSize = 12,
                        fontStyle = FontStyle.Normal
                    };
                }
                return _tabNormal;
            }
        }

        public static GUIStyle TabSelected
        {
            get
            {
                if (_tabSelected == null)
                {
                    _tabSelected = new GUIStyle(EditorStyles.toolbarButton)
                    {
                        fixedHeight = 28,
                        fontSize = 12,
                        fontStyle = FontStyle.Bold
                    };
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.24f, 0.37f, 0.58f));
                    tex.Apply();
                    _tabSelected.normal.background = tex;
                    _tabSelected.normal.textColor = Color.white;
                }
                return _tabSelected;
            }
        }

        public static GUIStyle HeaderLabel
        {
            get
            {
                if (_headerLabel == null)
                {
                    _headerLabel = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 14
                    };
                }
                return _headerLabel;
            }
        }

        public static GUIStyle StatusBar
        {
            get
            {
                if (_statusBar == null)
                {
                    _statusBar = new GUIStyle(EditorStyles.helpBox)
                    {
                        fontSize = 11,
                        alignment = TextAnchor.MiddleLeft,
                        fixedHeight = 22,
                        padding = new RectOffset(8, 8, 2, 2)
                    };
                }
                return _statusBar;
            }
        }

        public static GUIStyle ResultArea
        {
            get
            {
                if (_resultArea == null)
                {
                    _resultArea = new GUIStyle(EditorStyles.helpBox)
                    {
                        richText = true,
                        wordWrap = true,
                        padding = new RectOffset(8, 8, 6, 6),
                        fontSize = 12
                    };
                }
                return _resultArea;
            }
        }

        public static GUIStyle ListItem
        {
            get
            {
                if (_listItem == null)
                {
                    _listItem = new GUIStyle(EditorStyles.label)
                    {
                        padding = new RectOffset(6, 6, 4, 4),
                        margin = new RectOffset(0, 0, 0, 0),
                        fixedHeight = 24
                    };
                }
                return _listItem;
            }
        }

        public static GUIStyle ListItemSelected
        {
            get
            {
                if (_listItemSelected == null)
                {
                    _listItemSelected = new GUIStyle(ListItem);
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.17f, 0.36f, 0.53f));
                    tex.Apply();
                    _listItemSelected.normal.background = tex;
                    _listItemSelected.normal.textColor = Color.white;
                }
                return _listItemSelected;
            }
        }

        public static GUIStyle SectionHeader
        {
            get
            {
                if (_sectionHeader == null)
                {
                    _sectionHeader = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 11,
                        padding = new RectOffset(4, 4, 4, 2)
                    };
                }
                return _sectionHeader;
            }
        }

        public static GUIStyle AIResponseArea
        {
            get
            {
                if (_aiResponseArea == null)
                {
                    _aiResponseArea = new GUIStyle(EditorStyles.helpBox)
                    {
                        richText = true,
                        wordWrap = true,
                        padding = new RectOffset(10, 10, 8, 8),
                        fontSize = 12
                    };
                }
                return _aiResponseArea;
            }
        }

        public static GUIStyle ChatBubbleUser
        {
            get
            {
                if (_chatBubbleUser == null)
                {
                    _chatBubbleUser = new GUIStyle(EditorStyles.helpBox)
                    {
                        richText = true,
                        wordWrap = true,
                        padding = new RectOffset(10, 10, 6, 6),
                        margin = new RectOffset(40, 4, 2, 2),
                        fontSize = 12
                    };
                }
                return _chatBubbleUser;
            }
        }

        public static GUIStyle ChatBubbleAI
        {
            get
            {
                if (_chatBubbleAI == null)
                {
                    _chatBubbleAI = new GUIStyle(EditorStyles.helpBox)
                    {
                        richText = true,
                        wordWrap = true,
                        padding = new RectOffset(10, 10, 6, 6),
                        margin = new RectOffset(4, 40, 2, 2),
                        fontSize = 12
                    };
                }
                return _chatBubbleAI;
            }
        }

        public static GUIStyle CodeArea
        {
            get
            {
                if (_codeArea == null)
                {
                    _codeArea = new GUIStyle(EditorStyles.textArea)
                    {
                        wordWrap = false,
                        richText = false,
                        font = Font.CreateDynamicFontFromOSFont("Consolas", 11),
                        fontSize = 11
                    };
                }
                return _codeArea;
            }
        }

        /// <summary>
        /// Get color for variant count based on thresholds.
        /// </summary>
        public static Color GetVariantColor(int variantCount)
        {
            if (variantCount < 0) return DimText;
            if (variantCount <= VariantWarningThreshold) return GreenStatus;
            if (variantCount <= VariantDangerThreshold) return YellowStatus;
            return RedStatus;
        }

        /// <summary>
        /// Get severity color for log entries.
        /// </summary>
        public static Color GetSeverityColor(string severity)
        {
            switch (severity)
            {
                case "error": return RedStatus;
                case "warning": return YellowStatus;
                default: return Color.white;
            }
        }
    }
}
