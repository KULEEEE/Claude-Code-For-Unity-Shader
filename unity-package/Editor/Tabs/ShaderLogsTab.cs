using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Logs tab: shader-related log viewer with severity filter and AI error analysis.
    /// </summary>
    public class ShaderLogsTab
    {
        private readonly ShaderInspectorWindow _window;

        private LogsData _logsData;
        private int _severityFilter; // 0=All, 1=Errors, 2=Warnings, 3=Info
        private static readonly string[] SeverityOptions = { "All", "Errors", "Warnings", "Info" };
        private static readonly string[] SeverityValues = { "all", "error", "warning", "info" };

        private Vector2 _scrollPos;
        private int _selectedLogIndex = -1;
        private bool _autoRefresh = true;
        private double _lastRefreshTime;

        public ShaderLogsTab(ShaderInspectorWindow window)
        {
            _window = window;
            Refresh();
        }

        public void OnGUI()
        {
            // Auto-refresh every 3 seconds
            if (_autoRefresh && EditorApplication.timeSinceStartup - _lastRefreshTime > 3.0)
            {
                Refresh();
                _lastRefreshTime = EditorApplication.timeSinceStartup;
            }

            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField("Severity:", GUILayout.Width(55));
            int newFilter = EditorGUILayout.Popup(_severityFilter, SeverityOptions, EditorStyles.toolbarPopup,
                GUILayout.Width(80));
            if (newFilter != _severityFilter)
            {
                _severityFilter = newFilter;
                Refresh();
            }

            _autoRefresh = EditorGUILayout.ToggleLeft("Auto-refresh", _autoRefresh, GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
                Refresh();

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                ShaderCompileWatcher.ClearLogs();
                Refresh();
            }

            EditorGUILayout.EndHorizontal();

            // Log count
            int count = _logsData?.logs?.Count ?? 0;
            EditorGUILayout.LabelField($"{count} shader-related log entries", EditorStyles.miniLabel);

            // Log list
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            if (_logsData == null || _logsData.logs.Count == 0)
            {
                EditorGUILayout.LabelField("No shader-related logs.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                for (int i = _logsData.logs.Count - 1; i >= 0; i--) // Newest first
                {
                    var log = _logsData.logs[i];
                    bool isSelected = i == _selectedLogIndex;

                    EditorGUILayout.BeginHorizontal(isSelected ?
                        ShaderInspectorStyles.ListItemSelected : ShaderInspectorStyles.ListItem);

                    // Severity indicator
                    var oldColor = GUI.color;
                    GUI.color = ShaderInspectorStyles.GetSeverityColor(log.severity);
                    GUILayout.Label(GetSeverityIcon(log.severity), GUILayout.Width(20));
                    GUI.color = oldColor;

                    // Timestamp
                    GUILayout.Label(log.timestamp, EditorStyles.miniLabel, GUILayout.Width(130));

                    // Message (truncated)
                    string displayMsg = log.message;
                    if (displayMsg.Length > 100) displayMsg = displayMsg.Substring(0, 100) + "...";
                    if (GUILayout.Button(displayMsg, EditorStyles.label, GUILayout.ExpandWidth(true)))
                    {
                        _selectedLogIndex = i;
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();

            // Selected log detail
            if (_selectedLogIndex >= 0 && _selectedLogIndex < (_logsData?.logs?.Count ?? 0))
            {
                EditorGUILayout.Space(4);
                var selectedLog = _logsData.logs[_selectedLogIndex];

                EditorGUILayout.LabelField("Log Detail", ShaderInspectorStyles.SectionHeader);
                EditorGUILayout.TextArea(selectedLog.message, ShaderInspectorStyles.ResultArea,
                    GUILayout.MaxHeight(80));

                if (!string.IsNullOrEmpty(selectedLog.stackTrace))
                {
                    EditorGUILayout.TextArea(selectedLog.stackTrace, EditorStyles.helpBox,
                        GUILayout.MaxHeight(60));
                }

                EditorGUI.BeginDisabledGroup(!_window.IsAIConnected);
                if (GUILayout.Button("AI: Analyze this error", GUILayout.Height(24)))
                {
                    _window.AskAI(
                        $"Analyze this Unity shader log entry and suggest how to fix it:\n\n" +
                        $"Severity: {selectedLog.severity}\n" +
                        $"Message: {selectedLog.message}\n" +
                        $"Stack Trace: {selectedLog.stackTrace}",
                        selectedLog.message
                    );
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private string GetSeverityIcon(string severity)
        {
            switch (severity)
            {
                case "error": return "X";
                case "warning": return "!";
                default: return "i";
            }
        }

        public void Refresh()
        {
            try
            {
                string json = ShaderCompileWatcher.GetLogsJson(SeverityValues[_severityFilter]);
                _logsData = LogsData.Parse(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderInspector] Failed to refresh logs: {ex.Message}");
            }
        }
    }
}
