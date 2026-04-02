using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Centralized GUIStyle and Color constants for the SVN Tool window.
    /// </summary>
    public static class SVNToolStyles
    {
        // Colors
        public static readonly Color RevisionColor = new Color(0.3f, 0.8f, 0.9f);
        public static readonly Color DiffAddColor = new Color(0.3f, 0.85f, 0.3f);
        public static readonly Color DiffRemoveColor = new Color(0.9f, 0.3f, 0.3f);
        public static readonly Color DiffHeaderColor = new Color(0.3f, 0.75f, 0.9f);
        public static readonly Color DiffContextColor = new Color(0.65f, 0.65f, 0.65f);
        public static readonly Color AIColor = new Color(0.6f, 0.4f, 0.9f);
        public static readonly Color SuccessColor = new Color(0.2f, 0.8f, 0.2f);
        public static readonly Color WarningColor = new Color(0.9f, 0.8f, 0.1f);
        public static readonly Color ErrorColor = new Color(0.9f, 0.2f, 0.2f);
        public static readonly Color DimText = new Color(0.6f, 0.6f, 0.6f);
        public static readonly Color SplitterColor = new Color(0.15f, 0.15f, 0.15f);

        public static readonly Color StatusModified = new Color(0.9f, 0.8f, 0.1f);
        public static readonly Color StatusAdded = new Color(0.3f, 0.85f, 0.3f);
        public static readonly Color StatusDeleted = new Color(0.9f, 0.3f, 0.3f);
        public static readonly Color StatusConflict = new Color(1f, 0.2f, 0.2f);
        public static readonly Color StatusUnversioned = new Color(0.5f, 0.5f, 0.5f);

        public static readonly Color DiffAddBg = new Color(0.15f, 0.25f, 0.15f);
        public static readonly Color DiffRemoveBg = new Color(0.25f, 0.12f, 0.12f);
        public static readonly Color DiffHeaderBg = new Color(0.12f, 0.18f, 0.25f);

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            _revisionItem = null;
            _revisionItemSelected = null;
            _headerLabel = null;
            _sectionHeader = null;
            _statusBar = null;
            _subTabNormal = null;
            _subTabSelected = null;
            _diffContainer = null;
            _diffLineText = null;
            _commitButton = null;
            _operationButton = null;
            _commitMessageArea = null;
            _resultArea = null;
            _filePathLabel = null;
            _revisionDetailText = null;
        }

        private static GUIStyle _revisionItem;
        private static GUIStyle _revisionItemSelected;
        private static GUIStyle _headerLabel;
        private static GUIStyle _sectionHeader;
        private static GUIStyle _statusBar;
        private static GUIStyle _subTabNormal;
        private static GUIStyle _subTabSelected;
        private static GUIStyle _diffContainer;
        private static GUIStyle _diffLineText;
        private static GUIStyle _commitButton;
        private static GUIStyle _operationButton;
        private static GUIStyle _commitMessageArea;
        private static GUIStyle _resultArea;
        private static GUIStyle _filePathLabel;
        private static GUIStyle _revisionDetailText;

        public static GUIStyle RevisionItem
        {
            get
            {
                if (_revisionItem == null)
                {
                    _revisionItem = new GUIStyle(EditorStyles.label)
                    {
                        richText = true,
                        padding = new RectOffset(6, 6, 4, 4),
                        margin = new RectOffset(0, 0, 1, 1),
                        fontSize = 11,
                        fixedHeight = 0
                    };
                }
                return _revisionItem;
            }
        }

        public static GUIStyle RevisionItemSelected
        {
            get
            {
                if (_revisionItemSelected == null)
                {
                    _revisionItemSelected = new GUIStyle(RevisionItem);
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.17f, 0.36f, 0.53f));
                    tex.Apply();
                    _revisionItemSelected.normal.background = tex;
                    _revisionItemSelected.normal.textColor = Color.white;
                }
                return _revisionItemSelected;
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

        public static GUIStyle SubTabNormal
        {
            get
            {
                if (_subTabNormal == null)
                {
                    _subTabNormal = new GUIStyle(EditorStyles.toolbarButton)
                    {
                        fixedHeight = 26,
                        fontSize = 12,
                        fontStyle = FontStyle.Normal
                    };
                }
                return _subTabNormal;
            }
        }

        public static GUIStyle SubTabSelected
        {
            get
            {
                if (_subTabSelected == null)
                {
                    _subTabSelected = new GUIStyle(EditorStyles.toolbarButton)
                    {
                        fixedHeight = 26,
                        fontSize = 12,
                        fontStyle = FontStyle.Bold
                    };
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.24f, 0.37f, 0.58f));
                    tex.Apply();
                    _subTabSelected.normal.background = tex;
                    _subTabSelected.normal.textColor = Color.white;
                }
                return _subTabSelected;
            }
        }

        public static GUIStyle DiffContainer
        {
            get
            {
                if (_diffContainer == null)
                {
                    _diffContainer = new GUIStyle(EditorStyles.helpBox)
                    {
                        padding = new RectOffset(8, 8, 6, 6),
                        margin = new RectOffset(2, 2, 4, 4)
                    };
                    var tex = new Texture2D(1, 1);
                    tex.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.12f));
                    tex.Apply();
                    _diffContainer.normal.background = tex;
                }
                return _diffContainer;
            }
        }

        public static GUIStyle DiffLineText
        {
            get
            {
                if (_diffLineText == null)
                {
                    _diffLineText = new GUIStyle(EditorStyles.label)
                    {
                        richText = true,
                        wordWrap = false,
                        font = Font.CreateDynamicFontFromOSFont("Consolas", 11),
                        fontSize = 11,
                        padding = new RectOffset(4, 4, 0, 0),
                        margin = new RectOffset(0, 0, 0, 0)
                    };
                    _diffLineText.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                }
                return _diffLineText;
            }
        }

        public static GUIStyle CommitButton
        {
            get
            {
                if (_commitButton == null)
                {
                    _commitButton = new GUIStyle(GUI.skin.button)
                    {
                        fontSize = 13,
                        fontStyle = FontStyle.Bold,
                        fixedHeight = 32,
                        padding = new RectOffset(16, 16, 4, 4)
                    };
                }
                return _commitButton;
            }
        }

        public static GUIStyle OperationButton
        {
            get
            {
                if (_operationButton == null)
                {
                    _operationButton = new GUIStyle(GUI.skin.button)
                    {
                        fontSize = 12,
                        fixedHeight = 28,
                        padding = new RectOffset(12, 12, 4, 4)
                    };
                }
                return _operationButton;
            }
        }

        public static GUIStyle CommitMessageArea
        {
            get
            {
                if (_commitMessageArea == null)
                {
                    _commitMessageArea = new GUIStyle(EditorStyles.textArea)
                    {
                        wordWrap = true,
                        fontSize = 12,
                        padding = new RectOffset(6, 6, 4, 4)
                    };
                }
                return _commitMessageArea;
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
                        fontSize = 11,
                        font = Font.CreateDynamicFontFromOSFont("Consolas", 11)
                    };
                    _resultArea.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
                }
                return _resultArea;
            }
        }

        public static GUIStyle FilePathLabel
        {
            get
            {
                if (_filePathLabel == null)
                {
                    _filePathLabel = new GUIStyle(EditorStyles.label)
                    {
                        fontSize = 11,
                        fontStyle = FontStyle.Italic
                    };
                    _filePathLabel.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
                }
                return _filePathLabel;
            }
        }

        public static GUIStyle RevisionDetailText
        {
            get
            {
                if (_revisionDetailText == null)
                {
                    _revisionDetailText = new GUIStyle(EditorStyles.label)
                    {
                        richText = true,
                        wordWrap = true,
                        fontSize = 12,
                        padding = new RectOffset(4, 4, 2, 2)
                    };
                }
                return _revisionDetailText;
            }
        }

        /// <summary>
        /// Get color for SVN status code.
        /// </summary>
        public static Color GetStatusColor(char code)
        {
            switch (code)
            {
                case 'M': return StatusModified;
                case 'A': return StatusAdded;
                case 'D': return StatusDeleted;
                case 'C': return StatusConflict;
                case '?': return StatusUnversioned;
                case '!': return ErrorColor;
                default: return DimText;
            }
        }
    }
}
