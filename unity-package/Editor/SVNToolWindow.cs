using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// SVN Tool window with two tabs:
    /// - History: select a file to view SVN log, diff per revision, AI explanation
    /// - Operations: project-wide changed files list with diff, commit/revert/update
    /// </summary>
    public class SVNToolWindow : EditorWindow
    {
        private enum Tab { History, Operations }

        // SVN state
        private bool _svnAvailable;
        private bool _svnChecked;
        private string _svnCheckError;

        // Tab
        private Tab _currentTab = Tab.History;

        // ── History tab state ──
        private UnityEngine.Object _selectedAsset;
        private string _selectedAssetPath;
        private string _selectedAbsPath;
        private bool _fileUnderSvnControl;

        private List<SvnHelper.SvnLogEntry> _logEntries = new List<SvnHelper.SvnLogEntry>();
        private int _selectedRevisionIndex = -1;
        private string _historyDiff;
        private bool _isLoadingDiff;
        private Vector2 _logScrollPos;
        private Vector2 _historyDetailScrollPos;
        private float _historySplitRatio = 0.30f;
        private bool _isDraggingHistorySplitter;

        // AI explain
        private bool _isAIExplaining;
        private StringBuilder _aiExplanation = new StringBuilder();
        private string _aiExplanationCached = "";
        private string _aiStatusText;
        private int _aiExplainedRevision = -1;

        // ── Operations tab state ──
        private List<SvnHelper.SvnStatusEntry> _statusEntries = new List<SvnHelper.SvnStatusEntry>();
        private HashSet<int> _checkedFileIndices = new HashSet<int>();
        private int _opsSelectedFileIndex = -1;
        private string _opsDiff;
        private string _commitMessage = "";
        private string _operationResult;
        private bool _operationSuccess;
        private Vector2 _opsFileListScrollPos;
        private Vector2 _opsDiffScrollPos;
        private float _opsSplitRatio = 0.35f;
        private bool _isDraggingOpsSplitter;

        [MenuItem("Tools/Unity Agent/SVN Tool")]
        public static void ShowWindow()
        {
            var window = GetWindow<SVNToolWindow>("SVN Tool");
            window.minSize = new Vector2(700, 500);
        }

        private void OnEnable()
        {
            UnityAgentServer.EnsureRunning();
            CheckSvnAvailability();
        }

        private void CheckSvnAvailability()
        {
            _svnAvailable = SvnHelper.IsSvnInstalled();
            _svnChecked = true;
            if (!_svnAvailable)
                _svnCheckError = "SVN is not installed or not in PATH.\nSet custom path in EditorPrefs key: UnityAgent_SvnPath";
        }

        private string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private void OnGUI()
        {
            DrawToolbar();

            if (!_svnChecked) { CheckSvnAvailability(); return; }
            if (!_svnAvailable) { DrawSvnNotAvailable(); return; }

            DrawTabBar();
            EditorGUILayout.Space(2);

            switch (_currentTab)
            {
                case Tab.History:
                    DrawHistoryTab();
                    break;
                case Tab.Operations:
                    DrawOperationsTab();
                    break;
            }

            if (_isAIExplaining || _isLoadingDiff)
                Repaint();
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("SVN Tool", EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                if (_currentTab == Tab.History)
                    RefreshHistory();
                else
                    RefreshOperations();
            }

            var oldColor = GUI.color;
            GUI.color = AIRequestHandler.IsAvailable ? SVNToolStyles.SuccessColor : SVNToolStyles.DimText;
            GUILayout.Label(AIRequestHandler.IsAvailable ? "AI Connected" : "AI Offline",
                EditorStyles.toolbarButton, GUILayout.Width(85));
            GUI.color = oldColor;

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Tab Bar

        private void DrawTabBar()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("History",
                _currentTab == Tab.History ? SVNToolStyles.SubTabSelected : SVNToolStyles.SubTabNormal))
            {
                _currentTab = Tab.History;
            }

            if (GUILayout.Button("Operations",
                _currentTab == Tab.Operations ? SVNToolStyles.SubTabSelected : SVNToolStyles.SubTabNormal))
            {
                if (_currentTab != Tab.Operations)
                {
                    _currentTab = Tab.Operations;
                    RefreshOperations();
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Error States

        private void DrawSvnNotAvailable()
        {
            EditorGUILayout.Space(40);
            var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13, wordWrap = true };
            EditorGUILayout.LabelField("SVN Not Available", style);
            EditorGUILayout.Space(4);
            style.fontSize = 11;
            EditorGUILayout.LabelField(_svnCheckError, style);
            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Retry", GUILayout.Width(100)))
                CheckSvnAvailability();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        #endregion

        // ═══════════════════════════════════════════
        //  HISTORY TAB
        // ═══════════════════════════════════════════

        #region History Tab

        private void DrawHistoryTab()
        {
            // File selector (History only)
            DrawFileSelector();
            EditorGUILayout.Space(2);

            if (string.IsNullOrEmpty(_selectedAssetPath))
            {
                EditorGUILayout.Space(40);
                var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13 };
                EditorGUILayout.LabelField("Select a file or drag & drop from the Project window", style);
                return;
            }

            if (!_fileUnderSvnControl)
            {
                EditorGUILayout.Space(40);
                var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 13, wordWrap = true };
                EditorGUILayout.LabelField("Selected file is not under SVN version control", style);
                return;
            }

            if (_logEntries.Count == 0)
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("No SVN history found for this file",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // Left-right split: revision list | detail
            EditorGUILayout.BeginHorizontal();

            // Left: revision list
            float listWidth = position.width * _historySplitRatio;
            EditorGUILayout.BeginVertical(GUILayout.Width(listWidth));
            EditorGUILayout.LabelField($"Revisions ({_logEntries.Count})", SVNToolStyles.SectionHeader);
            _logScrollPos = EditorGUILayout.BeginScrollView(_logScrollPos);
            DrawRevisionList();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // Splitter
            DrawVerticalSplitter(ref _historySplitRatio, ref _isDraggingHistorySplitter, position.height - 80);

            // Right: detail
            EditorGUILayout.BeginVertical();
            _historyDetailScrollPos = EditorGUILayout.BeginScrollView(_historyDetailScrollPos);
            DrawRevisionDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFileSelector()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("File:", GUILayout.Width(30));

            EditorGUI.BeginChangeCheck();
            _selectedAsset = EditorGUILayout.ObjectField(_selectedAsset, typeof(UnityEngine.Object), false);
            if (EditorGUI.EndChangeCheck())
                OnHistoryFileSelected();

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_selectedAssetPath))
                EditorGUILayout.LabelField(_selectedAbsPath, SVNToolStyles.FilePathLabel);

            // Drag and drop
            var evt = Event.current;
            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (DragAndDrop.objectReferences.Length > 0)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        _selectedAsset = DragAndDrop.objectReferences[0];
                        OnHistoryFileSelected();
                    }
                    evt.Use();
                }
            }
        }

        private void OnHistoryFileSelected()
        {
            _selectedAssetPath = null;
            _selectedAbsPath = null;
            _fileUnderSvnControl = false;
            ClearHistoryState();

            if (_selectedAsset == null) return;

            _selectedAssetPath = AssetDatabase.GetAssetPath(_selectedAsset);
            if (string.IsNullOrEmpty(_selectedAssetPath)) return;

            _selectedAbsPath = SvnHelper.AssetPathToAbsolute(_selectedAssetPath);
            _fileUnderSvnControl = SvnHelper.IsFileUnderSvnControl(_selectedAbsPath);

            if (_fileUnderSvnControl)
                RefreshHistory();
        }

        private void RefreshHistory()
        {
            if (string.IsNullOrEmpty(_selectedAbsPath) || !_fileUnderSvnControl) return;
            _logEntries = SvnHelper.GetLog(_selectedAbsPath);
            _selectedRevisionIndex = -1;
            _historyDiff = null;
            _aiExplanation.Clear();
            _aiExplanationCached = "";
            _aiExplainedRevision = -1;
        }

        private void ClearHistoryState()
        {
            _logEntries.Clear();
            _selectedRevisionIndex = -1;
            _historyDiff = null;
            _isLoadingDiff = false;
            _aiExplanation.Clear();
            _aiExplanationCached = "";
            _aiExplainedRevision = -1;
            _isAIExplaining = false;
            _aiStatusText = "";
        }

        private void DrawRevisionList()
        {
            for (int i = 0; i < _logEntries.Count; i++)
            {
                var entry = _logEntries[i];
                bool isSelected = _selectedRevisionIndex == i;
                var style = isSelected ? SVNToolStyles.RevisionItemSelected : SVNToolStyles.RevisionItem;

                EditorGUILayout.BeginHorizontal(style);

                var oldColor = GUI.color;
                GUI.color = SVNToolStyles.RevisionColor;
                GUILayout.Label($"r{entry.revision}", EditorStyles.miniLabel, GUILayout.Width(50));
                GUI.color = oldColor;

                GUILayout.Label(entry.author, EditorStyles.miniLabel, GUILayout.Width(60));

                GUI.color = SVNToolStyles.DimText;
                GUILayout.Label(entry.date, EditorStyles.miniLabel, GUILayout.Width(100));
                GUI.color = oldColor;

                string msg = entry.message ?? "";
                if (msg.Length > 40) msg = msg.Substring(0, 40) + "...";
                GUILayout.Label(msg, EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();

                if (Event.current.type == EventType.MouseDown)
                {
                    var lastRect = GUILayoutUtility.GetLastRect();
                    if (lastRect.Contains(Event.current.mousePosition))
                    {
                        if (_selectedRevisionIndex != i)
                        {
                            _selectedRevisionIndex = i;
                            _historyDiff = null;
                            _aiExplanation.Clear();
                            _aiExplanationCached = "";
                            _aiExplainedRevision = -1;
                            LoadRevisionDiff();
                        }
                        Event.current.Use();
                        Repaint();
                    }
                }
            }
        }

        private void LoadRevisionDiff()
        {
            if (_selectedRevisionIndex < 0 || _selectedRevisionIndex >= _logEntries.Count) return;
            _isLoadingDiff = true;
            _historyDiff = SvnHelper.GetDiff(_selectedAbsPath, _logEntries[_selectedRevisionIndex].revision);
            _isLoadingDiff = false;
            Repaint();
        }

        private void DrawRevisionDetail()
        {
            if (_selectedRevisionIndex < 0 || _selectedRevisionIndex >= _logEntries.Count)
            {
                EditorGUILayout.Space(20);
                EditorGUILayout.LabelField("Select a revision to view details",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var entry = _logEntries[_selectedRevisionIndex];

            // Header + AI button
            EditorGUILayout.BeginHorizontal();
            var oldColor = GUI.color;
            GUI.color = SVNToolStyles.RevisionColor;
            EditorGUILayout.LabelField($"Revision {entry.revision}", SVNToolStyles.HeaderLabel);
            GUI.color = oldColor;
            GUILayout.FlexibleSpace();

            EditorGUI.BeginDisabledGroup(_isAIExplaining || !AIRequestHandler.IsAvailable);
            var oldBg = GUI.backgroundColor;
            GUI.backgroundColor = _isAIExplaining ? Color.gray :
                (_aiExplainedRevision == entry.revision ? SVNToolStyles.SuccessColor : SVNToolStyles.AIColor);
            string aiLabel = _isAIExplaining ? "Analyzing..." :
                (_aiExplainedRevision == entry.revision ? "Ask AI Again" : "Ask AI");
            if (GUILayout.Button(aiLabel, SVNToolStyles.CommitButton, GUILayout.Width(130)))
                AskAIToExplainRevision(entry);
            GUI.backgroundColor = oldBg;
            EditorGUI.EndDisabledGroup();

            if (!AIRequestHandler.IsAvailable)
                EditorGUILayout.LabelField("(offline)", EditorStyles.miniLabel, GUILayout.Width(45));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // Meta
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Author:", EditorStyles.boldLabel, GUILayout.Width(50));
            EditorGUILayout.LabelField(entry.author);
            EditorGUILayout.LabelField("Date:", EditorStyles.boldLabel, GUILayout.Width(35));
            EditorGUILayout.LabelField(entry.date);
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(entry.message))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Commit Message:", EditorStyles.boldLabel);
                var content = new GUIContent(entry.message);
                float msgH = SVNToolStyles.RevisionDetailText.CalcHeight(content,
                    EditorGUIUtility.currentViewWidth - 60);
                EditorGUILayout.SelectableLabel(entry.message, SVNToolStyles.RevisionDetailText,
                    GUILayout.Height(Mathf.Max(msgH + 4, 20)));
            }

            // AI status / explanation
            if (_isAIExplaining && !string.IsNullOrEmpty(_aiStatusText))
            {
                EditorGUILayout.Space(2);
                oldColor = GUI.color;
                GUI.color = SVNToolStyles.AIColor;
                EditorGUILayout.LabelField(_aiStatusText, SVNToolStyles.StatusBar);
                GUI.color = oldColor;
            }

            if (_aiExplanation.Length > 0)
            {
                EditorGUILayout.Space(8);
                oldColor = GUI.color;
                GUI.color = SVNToolStyles.AIColor;
                EditorGUILayout.LabelField("AI Analysis", SVNToolStyles.HeaderLabel);
                GUI.color = oldColor;
                EditorGUILayout.Space(2);
                MarkdownRenderer.Render(_aiExplanationCached);
            }

            // Diff
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Changes", SVNToolStyles.SectionHeader);

            if (_isLoadingDiff)
                EditorGUILayout.LabelField("Loading diff...", EditorStyles.centeredGreyMiniLabel);
            else if (!string.IsNullOrEmpty(_historyDiff))
                DiffRenderer.Render(_historyDiff);
            else
                EditorGUILayout.LabelField("No changes in this revision for this file",
                    EditorStyles.centeredGreyMiniLabel);
        }

        #endregion

        #region AI Explain

        private void AskAIToExplainRevision(SvnHelper.SvnLogEntry entry)
        {
            if (_isAIExplaining) return;

            _isAIExplaining = true;
            _aiExplanation.Clear();
            _aiExplanationCached = "";
            _aiStatusText = "Sending to AI...";
            _aiExplainedRevision = entry.revision;

            var ctx = new StringBuilder();
            ctx.AppendLine($"SVN Revision: {entry.revision}");
            ctx.AppendLine($"Author: {entry.author}");
            ctx.AppendLine($"Date: {entry.date}");
            ctx.AppendLine($"Commit Message: {entry.message}");
            ctx.AppendLine($"File: {_selectedAssetPath}");
            ctx.AppendLine();

            if (!string.IsNullOrEmpty(_historyDiff))
            {
                ctx.AppendLine("Diff:");
                string d = _historyDiff.Length > 8000
                    ? _historyDiff.Substring(0, 8000) + "\n... (diff truncated)"
                    : _historyDiff;
                ctx.AppendLine(d);
            }

            AIRequestHandler.SendQuery(
                "Analyze this SVN revision and explain what changes were made. " +
                "Focus on: what was the purpose of this change, what was modified/added/removed, " +
                "and any potential impact. Be concise but thorough. " +
                "Answer in the same language as the commit message if possible.",
                ctx.ToString(),
                onChunk: (chunk) =>
                {
                    _aiExplanation.Append(chunk);
                    _aiExplanationCached = _aiExplanation.ToString();
                    Repaint();
                },
                onComplete: (full) =>
                {
                    _isAIExplaining = false;
                    _aiStatusText = "";
                    if (_aiExplanation.Length == 0)
                    {
                        _aiExplanation.Append(full);
                        _aiExplanationCached = full;
                    }
                    Repaint();
                },
                onStatus: (s) => { _aiStatusText = s; Repaint(); }
            );
        }

        #endregion

        // ═══════════════════════════════════════════
        //  OPERATIONS TAB
        // ═══════════════════════════════════════════

        #region Operations Tab

        private void RefreshOperations()
        {
            string projectRoot = GetProjectRoot();
            _statusEntries = SvnHelper.GetStatus(projectRoot);
            _checkedFileIndices.Clear();
            _opsSelectedFileIndex = -1;
            _opsDiff = null;
            _operationResult = null;
        }

        private void DrawOperationsTab()
        {
            if (_statusEntries.Count == 0 && _operationResult == null)
            {
                // Might not have loaded yet
                if (_statusEntries.Count == 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Load SVN Status", GUILayout.Width(140), GUILayout.Height(28)))
                        RefreshOperations();
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.Space(20);
                    EditorGUILayout.LabelField("Click to scan for changed files in the project",
                        EditorStyles.centeredGreyMiniLabel);
                    return;
                }
            }

            // Left-right split: file list (with checkboxes + actions) | diff view
            EditorGUILayout.BeginHorizontal();

            // Left panel: file list + action buttons
            float listWidth = position.width * _opsSplitRatio;
            EditorGUILayout.BeginVertical(GUILayout.Width(listWidth));
            DrawOpsFileList();
            EditorGUILayout.EndVertical();

            // Splitter
            DrawVerticalSplitter(ref _opsSplitRatio, ref _isDraggingOpsSplitter, position.height - 60);

            // Right panel: diff for selected file
            EditorGUILayout.BeginVertical();
            _opsDiffScrollPos = EditorGUILayout.BeginScrollView(_opsDiffScrollPos);
            DrawOpsDiffPanel();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawOpsFileList()
        {
            // Header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Changed Files ({_statusEntries.Count})", SVNToolStyles.SectionHeader);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", EditorStyles.miniButton, GUILayout.Width(55)))
                RefreshOperations();
            EditorGUILayout.EndHorizontal();

            // Select All / None
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(4);
            if (GUILayout.Button("All", EditorStyles.miniButton, GUILayout.Width(35)))
            {
                for (int i = 0; i < _statusEntries.Count; i++)
                    _checkedFileIndices.Add(i);
            }
            if (GUILayout.Button("None", EditorStyles.miniButton, GUILayout.Width(40)))
                _checkedFileIndices.Clear();

            GUILayout.FlexibleSpace();
            var oldColor = GUI.color;
            GUI.color = SVNToolStyles.DimText;
            GUILayout.Label($"{_checkedFileIndices.Count} checked", EditorStyles.miniLabel);
            GUI.color = oldColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // File list with checkboxes
            _opsFileListScrollPos = EditorGUILayout.BeginScrollView(_opsFileListScrollPos);

            if (_statusEntries.Count == 0)
            {
                oldColor = GUI.color;
                GUI.color = SVNToolStyles.SuccessColor;
                EditorGUILayout.LabelField("  No local modifications", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }
            else
            {
                for (int i = 0; i < _statusEntries.Count; i++)
                {
                    var entry = _statusEntries[i];
                    bool isChecked = _checkedFileIndices.Contains(i);
                    bool isSelected = _opsSelectedFileIndex == i;
                    var rowStyle = isSelected ? SVNToolStyles.RevisionItemSelected : SVNToolStyles.RevisionItem;

                    EditorGUILayout.BeginHorizontal(rowStyle);

                    // Checkbox
                    bool newChecked = EditorGUILayout.Toggle(isChecked, GUILayout.Width(18));
                    if (newChecked != isChecked)
                    {
                        if (newChecked) _checkedFileIndices.Add(i);
                        else _checkedFileIndices.Remove(i);
                    }

                    // Status code
                    var oldClr = GUI.color;
                    GUI.color = SVNToolStyles.GetStatusColor(entry.statusCode);
                    GUILayout.Label($"{entry.statusCode}", EditorStyles.boldLabel, GUILayout.Width(14));
                    GUI.color = oldClr;

                    // File name (clickable to select and show diff)
                    string fileName = Path.GetFileName(entry.filePath);
                    GUILayout.Label(fileName, EditorStyles.miniLabel);

                    EditorGUILayout.EndHorizontal();

                    // Click to select (show diff)
                    if (Event.current.type == EventType.MouseDown)
                    {
                        var rect = GUILayoutUtility.GetLastRect();
                        if (rect.Contains(Event.current.mousePosition))
                        {
                            if (_opsSelectedFileIndex != i)
                            {
                                _opsSelectedFileIndex = i;
                                LoadOpsFileDiff(entry);
                            }
                            Event.current.Use();
                            Repaint();
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);

            // Action buttons
            bool hasChecked = _checkedFileIndices.Count > 0;
            string countLabel = hasChecked ? $" ({_checkedFileIndices.Count})" : "";

            EditorGUILayout.BeginHorizontal();
            var oldBg = GUI.backgroundColor;

            GUI.backgroundColor = SVNToolStyles.SuccessColor;
            EditorGUI.BeginDisabledGroup(!hasChecked);
            if (GUILayout.Button($"Update{countLabel}", SVNToolStyles.OperationButton))
                ExecuteUpdateChecked();
            EditorGUI.EndDisabledGroup();

            GUI.backgroundColor = SVNToolStyles.ErrorColor;
            EditorGUI.BeginDisabledGroup(!hasChecked);
            if (GUILayout.Button($"Revert{countLabel}", SVNToolStyles.OperationButton))
                ExecuteRevertChecked();
            EditorGUI.EndDisabledGroup();

            GUI.backgroundColor = oldBg;
            EditorGUILayout.EndHorizontal();

            // Commit
            EditorGUILayout.Space(4);
            _commitMessage = EditorGUILayout.TextArea(_commitMessage, SVNToolStyles.CommitMessageArea,
                GUILayout.Height(42));
            EditorGUILayout.Space(2);

            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(_commitMessage) || !hasChecked);
            oldBg = GUI.backgroundColor;
            GUI.backgroundColor = SVNToolStyles.AIColor;
            if (GUILayout.Button($"Commit{countLabel}", SVNToolStyles.CommitButton))
                ExecuteCommitChecked();
            GUI.backgroundColor = oldBg;
            EditorGUI.EndDisabledGroup();

            // Result
            if (!string.IsNullOrEmpty(_operationResult))
            {
                EditorGUILayout.Space(4);
                oldColor = GUI.color;
                GUI.color = _operationSuccess ? SVNToolStyles.SuccessColor : SVNToolStyles.ErrorColor;
                EditorGUILayout.LabelField(_operationResult, SVNToolStyles.ResultArea,
                    GUILayout.Height(36));
                GUI.color = oldColor;
            }
        }

        private void LoadOpsFileDiff(SvnHelper.SvnStatusEntry entry)
        {
            string absPath;
            if (Path.IsPathRooted(entry.filePath))
                absPath = entry.filePath;
            else
                absPath = Path.GetFullPath(Path.Combine(GetProjectRoot(), entry.filePath));

            _opsDiff = SvnHelper.GetWorkingDiff(absPath);
            _opsDiffScrollPos = Vector2.zero;
        }

        private void DrawOpsDiffPanel()
        {
            if (_opsSelectedFileIndex < 0 || _opsSelectedFileIndex >= _statusEntries.Count)
            {
                EditorGUILayout.Space(40);
                EditorGUILayout.LabelField("Select a file from the list to view diff",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var entry = _statusEntries[_opsSelectedFileIndex];

            // File header
            EditorGUILayout.BeginHorizontal();
            var oldColor = GUI.color;
            GUI.color = SVNToolStyles.GetStatusColor(entry.statusCode);
            EditorGUILayout.LabelField($"[{entry.statusText}]", EditorStyles.boldLabel, GUILayout.Width(80));
            GUI.color = oldColor;
            EditorGUILayout.LabelField(entry.filePath, SVNToolStyles.HeaderLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (!string.IsNullOrEmpty(_opsDiff))
            {
                DiffRenderer.Render(_opsDiff);
            }
            else
            {
                EditorGUILayout.LabelField("No diff available (binary file or no text changes)",
                    EditorStyles.centeredGreyMiniLabel);
            }
        }

        private List<string> GetCheckedAbsPaths()
        {
            var paths = new List<string>();
            string root = GetProjectRoot();
            foreach (int idx in _checkedFileIndices)
            {
                if (idx < 0 || idx >= _statusEntries.Count) continue;
                string fp = _statusEntries[idx].filePath;
                paths.Add(Path.IsPathRooted(fp) ? fp : Path.GetFullPath(Path.Combine(root, fp)));
            }
            return paths;
        }

        private string GetCheckedFileNames()
        {
            var names = new List<string>();
            foreach (int idx in _checkedFileIndices)
            {
                if (idx >= 0 && idx < _statusEntries.Count)
                    names.Add(Path.GetFileName(_statusEntries[idx].filePath));
            }
            return string.Join(", ", names);
        }

        private void ExecuteUpdateChecked()
        {
            var paths = GetCheckedAbsPaths();
            if (paths.Count == 0) return;

            var (success, output) = SvnHelper.Update(paths);
            _operationSuccess = success;
            _operationResult = output;
            if (success)
            {
                AssetDatabase.Refresh();
                RefreshOperations();
            }
        }

        private void ExecuteCommitChecked()
        {
            if (string.IsNullOrWhiteSpace(_commitMessage)) return;
            var paths = GetCheckedAbsPaths();
            if (paths.Count == 0) return;

            if (!EditorUtility.DisplayDialog("SVN Commit",
                $"Commit {paths.Count} file(s):\n{GetCheckedFileNames()}\n\nMessage:\n{_commitMessage}",
                "Commit", "Cancel"))
                return;

            var (success, output) = SvnHelper.Commit(paths, _commitMessage);
            _operationSuccess = success;
            _operationResult = output;
            if (success)
            {
                _commitMessage = "";
                RefreshOperations();
            }
        }

        private void ExecuteRevertChecked()
        {
            var paths = GetCheckedAbsPaths();
            if (paths.Count == 0) return;

            if (!EditorUtility.DisplayDialog("SVN Revert",
                $"Revert {paths.Count} file(s):\n{GetCheckedFileNames()}\n\nThis cannot be undone.",
                "Revert", "Cancel"))
                return;

            var (success, output) = SvnHelper.Revert(paths);
            _operationSuccess = success;
            _operationResult = output;
            if (success)
            {
                AssetDatabase.Refresh();
                RefreshOperations();
            }
        }

        #endregion

        // ═══════════════════════════════════════════
        //  SHARED UTILITIES
        // ═══════════════════════════════════════════

        #region Shared

        private void DrawVerticalSplitter(ref float ratio, ref bool isDragging, float height)
        {
            var rect = EditorGUILayout.GetControlRect(false, GUILayout.Width(4));
            rect.height = height;
            EditorGUI.DrawRect(rect, SVNToolStyles.SplitterColor);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                isDragging = true;
                Event.current.Use();
            }
            if (isDragging)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    ratio = Mathf.Clamp(Event.current.mousePosition.x / position.width, 0.15f, 0.60f);
                    Event.current.Use();
                    Repaint();
                }
                if (Event.current.type == EventType.MouseUp)
                {
                    isDragging = false;
                    Event.current.Use();
                }
            }
        }

        #endregion
    }
}
