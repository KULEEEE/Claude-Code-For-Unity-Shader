using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Centralized GUIStyle and Color constants for the Error Solver window.
    /// </summary>
    public static class ErrorSolverStyles
    {
        // Colors
        public static readonly Color ErrorColor = new Color(0.9f, 0.2f, 0.2f);
        public static readonly Color WarningColor = new Color(0.9f, 0.8f, 0.1f);
        public static readonly Color SuccessColor = new Color(0.2f, 0.8f, 0.2f);
        public static readonly Color InfoColor = new Color(0.3f, 0.8f, 0.9f);
        public static readonly Color AIColor = new Color(0.6f, 0.4f, 0.9f);
        public static readonly Color DimText = new Color(0.6f, 0.6f, 0.6f);
        public static readonly Color SplitterColor = new Color(0.15f, 0.15f, 0.15f);

        private static GUIStyle _errorItem;
        private static GUIStyle _errorItemSelected;
        private static GUIStyle _warningItem;
        private static GUIStyle _headerLabel;
        private static GUIStyle _statusBar;
        private static GUIStyle _stackTraceArea;
        private static GUIStyle _solveButton;
        private static GUIStyle _markdownText;
        private static GUIStyle _codeBlockContainer;
        private static GUIStyle _codeBlockText;
        private static GUIStyle _aiResponseArea;
        private static GUIStyle _errorCountBadge;
        private static GUIStyle _sectionHeader;

        public static GUIStyle ErrorItem
        {
            get
            {
                if (_errorItem == null)
                {
                    _errorItem = new GUIStyle(EditorStyles.label)
                    {
                        richText = true,
                        wordWrap = true,
                        padding = new RectOffset(6, 6, 4, 4),
                        margin = new RectOffset(0, 0, 1, 1),
                        fontSize = 11
                    };
                }
                return _errorItem;
            }
        }

        public static GUIStyle ErrorItemSelected
        {
            get
            {
                if (_errorItemSelected == null)
                {
                    _errorItemSelected = new GUIStyle(ErrorItem);
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.17f, 0.36f, 0.53f));
                    tex.Apply();
                    _errorItemSelected.normal.background = tex;
                    _errorItemSelected.normal.textColor = Color.white;
                }
                return _errorItemSelected;
            }
        }

        public static GUIStyle WarningItem
        {
            get
            {
                if (_warningItem == null)
                {
                    _warningItem = new GUIStyle(ErrorItem);
                }
                return _warningItem;
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

        public static GUIStyle StackTraceArea
        {
            get
            {
                if (_stackTraceArea == null)
                {
                    _stackTraceArea = new GUIStyle(EditorStyles.helpBox)
                    {
                        richText = false,
                        wordWrap = true,
                        font = Font.CreateDynamicFontFromOSFont("Consolas", 10),
                        fontSize = 10,
                        padding = new RectOffset(8, 8, 6, 6)
                    };
                    _stackTraceArea.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
                }
                return _stackTraceArea;
            }
        }

        public static GUIStyle SolveButton
        {
            get
            {
                if (_solveButton == null)
                {
                    _solveButton = new GUIStyle(GUI.skin.button)
                    {
                        fontSize = 13,
                        fontStyle = FontStyle.Bold,
                        fixedHeight = 32,
                        padding = new RectOffset(16, 16, 4, 4)
                    };
                }
                return _solveButton;
            }
        }

        public static GUIStyle MarkdownText
        {
            get
            {
                if (_markdownText == null)
                {
                    _markdownText = new GUIStyle(EditorStyles.label)
                    {
                        richText = true,
                        wordWrap = true,
                        fontSize = 12,
                        padding = new RectOffset(0, 0, 0, 0)
                    };
                }
                return _markdownText;
            }
        }

        public static GUIStyle CodeBlockContainer
        {
            get
            {
                if (_codeBlockContainer == null)
                {
                    _codeBlockContainer = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(8, 8, 6, 6),
                        margin = new RectOffset(2, 2, 4, 4)
                    };
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.12f));
                    tex.Apply();
                    _codeBlockContainer.normal.background = tex;
                }
                return _codeBlockContainer;
            }
        }

        public static GUIStyle CodeBlockText
        {
            get
            {
                if (_codeBlockText == null)
                {
                    _codeBlockText = new GUIStyle(EditorStyles.label)
                    {
                        richText = false,
                        wordWrap = true,
                        font = Font.CreateDynamicFontFromOSFont("Consolas", 11),
                        fontSize = 11,
                        padding = new RectOffset(4, 4, 2, 2)
                    };
                    _codeBlockText.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                }
                return _codeBlockText;
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

        public static GUIStyle ErrorCountBadge
        {
            get
            {
                if (_errorCountBadge == null)
                {
                    _errorCountBadge = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 10,
                        fontStyle = FontStyle.Bold,
                        fixedWidth = 24,
                        fixedHeight = 18
                    };
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.8f, 0.15f, 0.15f));
                    tex.Apply();
                    _errorCountBadge.normal.background = tex;
                    _errorCountBadge.normal.textColor = Color.white;
                }
                return _errorCountBadge;
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
    }
}
