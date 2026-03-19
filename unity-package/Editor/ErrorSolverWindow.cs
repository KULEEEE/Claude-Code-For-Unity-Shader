using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Unity Editor window that displays collected errors and provides
    /// an AI-powered "Solve" button to automatically fix them.
    /// </summary>
    public class ErrorSolverWindow : EditorWindow
    {
        // UI state
        private Vector2 _errorListScroll;
        private Vector2 _detailScroll;
        private Vector2 _aiResponseScroll;
        private int _selectedErrorIndex = -1;
        private bool _showWarnings;
        private bool _autoRefresh = true;

        // AI solve state
        private bool _isSolving;
        private string _solveStatus = "";
        private StringBuilder _aiResponse = new StringBuilder();
        private string _aiResponseCached = "";
        private bool _solvingComplete;
        private string _currentSolvingErrorId;

        // Cached error list
        private List<ErrorCollector.ErrorEntry> _cachedErrors = new List<ErrorCollector.ErrorEntry>();
        private List<ErrorCollector.ErrorEntry> _cachedWarnings = new List<ErrorCollector.ErrorEntry>();

        // Split panel
        private float _splitRatio = 0.5f;
        private bool _isDraggingSplitter;

        [MenuItem("Tools/Unity Agent/Error Solver")]
        public static void ShowWindow()
        {
            var window = GetWindow<ErrorSolverWindow>("Error Solver");
            window.minSize = new Vector2(600, 400);
        }

        private void OnEnable()
        {
            UnityAgentServer.EnsureRunning();
            ErrorCollector.OnErrorsChanged += OnErrorsChanged;
            RefreshErrors();
        }

        private void OnDisable()
        {
            ErrorCollector.OnErrorsChanged -= OnErrorsChanged;
        }

        private void OnErrorsChanged()
        {
            if (_autoRefresh)
            {
                RefreshErrors();
                Repaint();
            }
        }

        private void RefreshErrors()
        {
            _cachedErrors = ErrorCollector.GetErrors();
            _cachedWarnings = ErrorCollector.GetWarnings();
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawStatusBar();

            EditorGUILayout.Space(2);

            // Main content: split between error list and detail/AI panel
            var mainRect = EditorGUILayout.BeginVertical(GUILayout.ExpandHeight(true));

            if (mainRect.width > 1)
            {
                float totalHeight = mainRect.height;
                float topHeight = totalHeight * _splitRatio;
                float splitterHeight = 4;
                float bottomHeight = totalHeight - topHeight - splitterHeight;

                // Top: Error list
                GUILayout.BeginArea(new Rect(mainRect.x, mainRect.y, mainRect.width, topHeight));
                DrawErrorList(mainRect.width, topHeight);
                GUILayout.EndArea();

                // Splitter
                var splitterRect = new Rect(mainRect.x, mainRect.y + topHeight, mainRect.width, splitterHeight);
                EditorGUI.DrawRect(splitterRect, ErrorSolverStyles.SplitterColor);
                EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeVertical);

                if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
                {
                    _isDraggingSplitter = true;
                    Event.current.Use();
                }

                if (_isDraggingSplitter)
                {
                    if (Event.current.type == EventType.MouseDrag)
                    {
                        _splitRatio = Mathf.Clamp((Event.current.mousePosition.y - mainRect.y) / totalHeight, 0.15f, 0.85f);
                        Event.current.Use();
                        Repaint();
                    }
                    if (Event.current.type == EventType.MouseUp)
                    {
                        _isDraggingSplitter = false;
                        Event.current.Use();
                    }
                }

                // Bottom: Detail + AI Response
                GUILayout.BeginArea(new Rect(mainRect.x, mainRect.y + topHeight + splitterHeight,
                    mainRect.width, bottomHeight));
                DrawDetailPanel(mainRect.width, bottomHeight);
                GUILayout.EndArea();
            }

            EditorGUILayout.EndVertical();

            // Repaint while solving
            if (_isSolving)
                Repaint();
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                RefreshErrors();
            }

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                ErrorCollector.Clear();
                _selectedErrorIndex = -1;
                _aiResponse.Clear();
                _aiResponseCached = "";
                RefreshErrors();
            }

            EditorGUILayout.Space(10);

            _showWarnings = GUILayout.Toggle(_showWarnings, "Warnings", EditorStyles.toolbarButton, GUILayout.Width(70));
            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto", EditorStyles.toolbarButton, GUILayout.Width(50));

            GUILayout.FlexibleSpace();

            // Error/Warning counts
            var oldColor = GUI.color;
            if (_cachedErrors.Count > 0)
            {
                GUI.color = ErrorSolverStyles.ErrorColor;
                GUILayout.Label($"Errors: {_cachedErrors.Count}", EditorStyles.toolbarButton, GUILayout.Width(80));
            }
            if (_showWarnings && _cachedWarnings.Count > 0)
            {
                GUI.color = ErrorSolverStyles.WarningColor;
                GUILayout.Label($"Warns: {_cachedWarnings.Count}", EditorStyles.toolbarButton, GUILayout.Width(80));
            }
            GUI.color = oldColor;

            // Connection status
            GUI.color = UnityAgentServer.IsClientConnected ? ErrorSolverStyles.SuccessColor : ErrorSolverStyles.DimText;
            GUILayout.Label(UnityAgentServer.IsClientConnected ? "● AI Connected" : "● AI Offline",
                EditorStyles.toolbarButton, GUILayout.Width(90));
            GUI.color = oldColor;

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Status Bar

        private void DrawStatusBar()
        {
            if (_isSolving && !string.IsNullOrEmpty(_solveStatus))
            {
                var oldColor = GUI.color;
                GUI.color = ErrorSolverStyles.AIColor;
                EditorGUILayout.LabelField(_solveStatus, ErrorSolverStyles.StatusBar);
                GUI.color = oldColor;
            }
        }

        #endregion

        #region Error List

        private void DrawErrorList(float width, float height)
        {
            _errorListScroll = GUILayout.BeginScrollView(_errorListScroll);

            if (_cachedErrors.Count == 0 && (!_showWarnings || _cachedWarnings.Count == 0))
            {
                EditorGUILayout.Space(20);
                var centeredStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    fontSize = 13
                };
                EditorGUILayout.LabelField("No errors detected", centeredStyle);
                EditorGUILayout.LabelField("Errors will appear here as they occur", centeredStyle);
            }
            else
            {
                // Draw errors
                for (int i = 0; i < _cachedErrors.Count; i++)
                {
                    DrawErrorEntry(_cachedErrors[i], i, false);
                }

                // Draw warnings if enabled
                if (_showWarnings)
                {
                    for (int i = 0; i < _cachedWarnings.Count; i++)
                    {
                        DrawErrorEntry(_cachedWarnings[i], _cachedErrors.Count + i, true);
                    }
                }
            }

            GUILayout.EndScrollView();
        }

        private void DrawErrorEntry(ErrorCollector.ErrorEntry entry, int index, bool isWarning)
        {
            bool isSelected = _selectedErrorIndex == index;
            var style = isSelected ? ErrorSolverStyles.ErrorItemSelected : ErrorSolverStyles.ErrorItem;

            EditorGUILayout.BeginHorizontal(style);

            // Icon
            var oldColor = GUI.color;
            GUI.color = isWarning ? ErrorSolverStyles.WarningColor : ErrorSolverStyles.ErrorColor;
            string icon = isWarning ? "⚠" : "✖";
            string typeLabel = entry.isCompileError ? "[Compile]" : (entry.logType == "Exception" ? "[Exception]" : "");
            GUILayout.Label(icon, GUILayout.Width(18));
            GUI.color = oldColor;

            // Timestamp
            GUILayout.Label(entry.timestamp, EditorStyles.miniLabel, GUILayout.Width(55));

            // Type badge
            if (!string.IsNullOrEmpty(typeLabel))
            {
                var badgeColor = entry.isCompileError ? ErrorSolverStyles.ErrorColor : ErrorSolverStyles.WarningColor;
                GUI.color = badgeColor;
                GUILayout.Label(typeLabel, EditorStyles.miniLabel, GUILayout.Width(65));
                GUI.color = oldColor;
            }

            // Message (truncated)
            string displayMsg = entry.message;
            if (displayMsg.Length > 120)
                displayMsg = displayMsg.Substring(0, 120) + "...";
            GUILayout.Label(displayMsg, style);

            // Source file shortcut
            if (!string.IsNullOrEmpty(entry.sourceFile))
            {
                GUI.color = ErrorSolverStyles.InfoColor;
                string fileLabel = System.IO.Path.GetFileName(entry.sourceFile);
                if (entry.sourceLine > 0) fileLabel += $":{entry.sourceLine}";
                if (GUILayout.Button(fileLabel, EditorStyles.miniButton, GUILayout.Width(100)))
                {
                    // Open file in code editor
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.sourceFile);
                    if (asset != null)
                        AssetDatabase.OpenAsset(asset, entry.sourceLine);
                }
                GUI.color = oldColor;
            }

            EditorGUILayout.EndHorizontal();

            // Handle click
            if (Event.current.type == EventType.MouseDown)
            {
                var lastRect = GUILayoutUtility.GetLastRect();
                if (lastRect.Contains(Event.current.mousePosition))
                {
                    _selectedErrorIndex = index;
                    _aiResponse.Clear();
                    _aiResponseCached = "";
                    _solvingComplete = false;
                    Event.current.Use();
                    Repaint();
                }
            }
        }

        #endregion

        #region Detail Panel

        private void DrawDetailPanel(float width, float height)
        {
            _detailScroll = GUILayout.BeginScrollView(_detailScroll);

            ErrorCollector.ErrorEntry selected = GetSelectedError();

            if (selected == null)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Select an error above to see details",
                    EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                // Error detail header
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Error Detail", ErrorSolverStyles.HeaderLabel);
                GUILayout.FlexibleSpace();

                // Solve button
                EditorGUI.BeginDisabledGroup(_isSolving || !AIRequestHandler.IsAvailable);
                var oldBgColor = GUI.backgroundColor;
                GUI.backgroundColor = _isSolving ? Color.gray :
                    (_solvingComplete ? ErrorSolverStyles.SuccessColor : ErrorSolverStyles.AIColor);

                string solveLabel = _isSolving ? "Solving..." :
                    (_solvingComplete ? "Solve Again" : "Solve");

                if (GUILayout.Button(solveLabel, ErrorSolverStyles.SolveButton, GUILayout.Width(120)))
                {
                    SolveError(selected);
                }
                GUI.backgroundColor = oldBgColor;
                EditorGUI.EndDisabledGroup();

                if (!AIRequestHandler.IsAvailable)
                {
                    EditorGUILayout.LabelField("(AI offline)", EditorStyles.miniLabel, GUILayout.Width(60));
                }

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(4);

                // Error message
                EditorGUILayout.LabelField("Message:", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(selected.message, EditorStyles.wordWrappedLabel,
                    GUILayout.Height(EditorStyles.wordWrappedLabel.CalcHeight(
                        new GUIContent(selected.message),
                        width - 20) + 4));

                // Source file
                if (!string.IsNullOrEmpty(selected.sourceFile))
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Source:", EditorStyles.boldLabel, GUILayout.Width(50));
                    var oldClr = GUI.color;
                    GUI.color = ErrorSolverStyles.InfoColor;
                    string srcLabel = selected.sourceFile;
                    if (selected.sourceLine > 0) srcLabel += $" (line {selected.sourceLine})";
                    if (GUILayout.Button(srcLabel, EditorStyles.linkLabel))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(selected.sourceFile);
                        if (asset != null) AssetDatabase.OpenAsset(asset, selected.sourceLine);
                    }
                    GUI.color = oldClr;
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }

                // Stack trace
                if (!string.IsNullOrEmpty(selected.stackTrace))
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Stack Trace:", EditorStyles.boldLabel);
                    string st = selected.stackTrace;
                    if (st.Length > 2000) st = st.Substring(0, 2000) + "\n... (truncated)";
                    float stHeight = ErrorSolverStyles.StackTraceArea.CalcHeight(
                        new GUIContent(st), width - 20);
                    stHeight = Mathf.Min(stHeight, 150);
                    EditorGUILayout.SelectableLabel(st, ErrorSolverStyles.StackTraceArea,
                        GUILayout.Height(stHeight + 4));
                }

                // AI Response
                if (_aiResponse.Length > 0 || _isSolving)
                {
                    EditorGUILayout.Space(8);

                    var oldColor = GUI.color;
                    GUI.color = ErrorSolverStyles.AIColor;
                    EditorGUILayout.LabelField("AI Solution", ErrorSolverStyles.HeaderLabel);
                    GUI.color = oldColor;

                    EditorGUILayout.Space(2);

                    if (_aiResponse.Length > 0)
                    {
                        MarkdownRenderer.Render(_aiResponseCached);
                    }
                    else if (_isSolving)
                    {
                        EditorGUILayout.LabelField("Analyzing error and generating solution...",
                            EditorStyles.centeredGreyMiniLabel);
                    }
                }
            }

            GUILayout.EndScrollView();
        }

        private ErrorCollector.ErrorEntry GetSelectedError()
        {
            if (_selectedErrorIndex < 0) return null;

            if (_selectedErrorIndex < _cachedErrors.Count)
                return _cachedErrors[_selectedErrorIndex];

            int warningIdx = _selectedErrorIndex - _cachedErrors.Count;
            if (_showWarnings && warningIdx >= 0 && warningIdx < _cachedWarnings.Count)
                return _cachedWarnings[warningIdx];

            return null;
        }

        #endregion

        #region AI Solve

        private void SolveError(ErrorCollector.ErrorEntry error)
        {
            if (_isSolving) return;

            _isSolving = true;
            _solvingComplete = false;
            _aiResponse.Clear();
            _aiResponseCached = "";
            _solveStatus = "Sending error to AI...";
            _currentSolvingErrorId = error.id;

            // Build context with error details
            var contextBuilder = new StringBuilder();
            contextBuilder.AppendLine($"Error Type: {error.logType}");
            contextBuilder.AppendLine($"Is Compile Error: {error.isCompileError}");
            contextBuilder.AppendLine($"Error Message: {error.message}");

            if (!string.IsNullOrEmpty(error.sourceFile))
                contextBuilder.AppendLine($"Source File: {error.sourceFile} (line {error.sourceLine})");

            if (!string.IsNullOrEmpty(error.stackTrace))
            {
                contextBuilder.AppendLine($"Stack Trace:");
                contextBuilder.AppendLine(error.stackTrace);
            }

            // Build prompt
            string prompt =
                "The following Unity error is preventing the project from running. " +
                "Please analyze the error, read the relevant source files if needed, " +
                "identify the root cause, and fix it. " +
                "If you modify any files, explain what you changed and why.\n\n" +
                $"Error: {error.message}";

            AIRequestHandler.SendQuery(
                prompt,
                contextBuilder.ToString(),
                onChunk: (chunk) =>
                {
                    _aiResponse.Append(chunk);
                    _aiResponseCached = _aiResponse.ToString();
                    Repaint();
                },
                onComplete: (fullResponse) =>
                {
                    _isSolving = false;
                    _solvingComplete = true;
                    _solveStatus = "";

                    if (_aiResponse.Length == 0)
                    {
                        _aiResponse.Append(fullResponse);
                        _aiResponseCached = fullResponse;
                    }

                    Repaint();
                },
                onStatus: (status) =>
                {
                    _solveStatus = status;
                    Repaint();
                }
            );
        }

        #endregion
    }
}
