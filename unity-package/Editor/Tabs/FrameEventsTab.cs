using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Events tab — shows all frame events in a tree-like list (similar to Unity's
    /// built-in Frame Debugger). Clicking an event shows structured detail on the right.
    /// Search filter is available as an optional toolbar.
    /// </summary>
    public class FrameEventsTab
    {
        private readonly FrameDebuggerAIWindow _window;

        // Tree data
        private readonly List<TreeNode> _treeNodes = new List<TreeNode>();
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private bool _treeBuilt;
        private string _lastSummaryJson;

        // Selection
        private int _selectedIndex = -1;
        private string _selectedDetailJson;

        // Scroll
        private Vector2 _listScroll;
        private Vector2 _detailScroll;

        // Search filter (optional)
        private string _filterText = "";
        private bool _showRawJson;

        // RT preview
        private Texture2D _rtPreview;
        private int _rtPreviewIndex = -1;

        private class TreeNode
        {
            public int eventIndex;      // -1 for group nodes
            public string name;
            public string type;
            public int depth;
            public int childCount;      // how many events in this group
            public string groupKey;     // unique key for collapse state
            public bool isGroup;
        }

        public FrameEventsTab(FrameDebuggerAIWindow window)
        {
            _window = window;
        }

        public void OnGUI()
        {
            if (!_window.IsCaptured && string.IsNullOrEmpty(_window.LastSummaryJson))
            {
                EditorGUILayout.HelpBox("Capture a frame first (toolbar).", MessageType.Info);
                return;
            }

            // Cache building — block events tab
            if (_window.IsCacheBuilding)
            {
                EditorGUILayout.Space(40);
                EditorGUILayout.LabelField("Event 데이터를 로딩하고 있습니다...",
                    ShaderInspectorStyles.SectionHeader);
                EditorGUILayout.Space(10);
                float pct = _window.CacheTotal > 0 ? (float)_window.CacheCurrent / _window.CacheTotal : 0;
                var rect = GUILayoutUtility.GetRect(0, 28, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, pct, $"{_window.CacheCurrent} / {_window.CacheTotal}");
                return;
            }

            // Auto-build tree when summary changes
            if (_window.LastSummaryJson != _lastSummaryJson)
            {
                _lastSummaryJson = _window.LastSummaryJson;
                BuildTree();
            }

            DrawToolbar();

            EditorGUILayout.BeginHorizontal();
            DrawTreeList();
            DrawDetailPanel();
            EditorGUILayout.EndHorizontal();
        }

        public void SelectEvent(int eventIndex)
        {
            _selectedIndex = eventIndex;

            // Try cache first (instant)
            string cached = FrameDebugBridge.GetCachedEventDetail(eventIndex);
            if (cached != null)
            {
                _selectedDetailJson = cached;
                LoadRTPreview(eventIndex);
                return;
            }

            // Not cached yet — priority load this event
            _selectedDetailJson = null;
            _priorityLoadIndex = eventIndex;
            _priorityTick = 0;
            FrameDebugBridge.SetLimitPublic(eventIndex + 1);
            EditorApplication.update -= PriorityLoadTick;
            EditorApplication.update += PriorityLoadTick;
        }

        private void LoadRTPreview(int eventIndex)
        {
            // Don't reload if already showing this event's RT
            if (_rtPreviewIndex == eventIndex && _rtPreview != null) return;

            // Destroy old texture
            if (_rtPreview != null)
            {
                UnityEngine.Object.DestroyImmediate(_rtPreview);
                _rtPreview = null;
            }
            _rtPreviewIndex = eventIndex;

            // Load RT asynchronously — SetLimit + wait 1 tick + read
            FrameDebugBridge.SetLimitPublic(eventIndex + 1);
            EditorApplication.delayCall += () =>
            {
                if (_selectedIndex != eventIndex) return;
                _rtPreview = FrameDebugBridge.GetRenderTargetTexture(eventIndex, 512);
                _window.Repaint();
            };
        }

        private int _priorityLoadIndex = -1;
        private int _priorityTick;

        private void PriorityLoadTick()
        {
            _priorityTick++;
            if (_priorityTick < 3) return; // skip a couple ticks for SetLimit to process

            EditorApplication.update -= PriorityLoadTick;
            if (_selectedIndex != _priorityLoadIndex) return;

            string detail = FrameDebugBridge.GetEventDetail(_priorityLoadIndex);
            if (!string.IsNullOrEmpty(detail))
            {
                FrameDebugBridge.CacheEventDetail(_priorityLoadIndex, detail);
                _selectedDetailJson = detail;
                LoadRTPreview(_priorityLoadIndex);
            }
            else
            {
                _selectedDetailJson = "{\"error\":\"load-failed\",\"detail\":\"데이터를 가져올 수 없습니다.\"}";
            }
            _window.Repaint();
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Filter:", EditorStyles.miniLabel, GUILayout.Width(38));
            _filterText = EditorGUILayout.TextField(_filterText, EditorStyles.toolbarSearchField,
                GUILayout.Width(200));
            if (GUILayout.Button("X", EditorStyles.toolbarButton, GUILayout.Width(22)))
                _filterText = "";
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{_window.EventCount} events", EditorStyles.miniLabel, GUILayout.Width(80));
            if (GUILayout.Button("Collapse All", EditorStyles.toolbarButton, GUILayout.Width(80)))
                CollapseAll();
            if (GUILayout.Button("Expand All", EditorStyles.toolbarButton, GUILayout.Width(75)))
                _collapsed.Clear();
            EditorGUILayout.EndHorizontal();
        }

        private void CollapseAll()
        {
            foreach (var n in _treeNodes)
                if (n.isGroup) _collapsed.Add(n.groupKey);
        }

        #endregion

        #region Tree Build

        private void BuildTree()
        {
            _treeNodes.Clear();
            _treeBuilt = false;

            var allEvents = FrameDebugBridge.GetAllEventNames();
            if (allEvents == null || allEvents.Count == 0) return;

            // Build hierarchy from consecutive same-prefix groups.
            // Events with '/' in their name create implicit groups.
            // We use a simple heuristic: events whose name matches
            // a previous group name are children of that group.
            var groupStack = new List<string>();
            string prevGroup = "";

            for (int i = 0; i < allEvents.Count; i++)
            {
                var (index, name, type) = allEvents[i];
                if (string.IsNullOrEmpty(name)) name = type ?? $"Event {index}";

                // Determine depth by checking if this name is contained within a parent group
                // Simple approach: count how many '/' segments match the current group context
                int depth = 0;
                string groupName = "";

                // Check if this event name has hierarchy indicators
                int lastSlash = name.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    groupName = name.Substring(0, lastSlash);
                    depth = 1;
                    // Check for deeper nesting
                    int firstSlash = name.IndexOf('/');
                    if (firstSlash != lastSlash) depth = 2;
                }

                _treeNodes.Add(new TreeNode
                {
                    eventIndex = index,
                    name = name,
                    type = type,
                    depth = depth,
                    childCount = 0,
                    groupKey = null,
                    isGroup = false,
                });
            }

            // Second pass: group consecutive events with the same type pattern
            GroupByConsecutiveType();

            _treeBuilt = true;
        }

        /// <summary>
        /// Groups consecutive events of the same type into collapsible sections.
        /// E.g., 15 consecutive "Draw Dynamic" become a group with count.
        /// </summary>
        private void GroupByConsecutiveType()
        {
            if (_treeNodes.Count == 0) return;

            var grouped = new List<TreeNode>();
            int i = 0;
            while (i < _treeNodes.Count)
            {
                var current = _treeNodes[i];

                // Look ahead: how many consecutive events have the same name?
                int runStart = i;
                while (i + 1 < _treeNodes.Count &&
                       _treeNodes[i + 1].name == current.name)
                {
                    i++;
                }
                int runLength = i - runStart + 1;

                if (runLength >= 3)
                {
                    // Create a group node
                    string key = $"group_{runStart}_{current.name}";
                    grouped.Add(new TreeNode
                    {
                        eventIndex = -1,
                        name = current.name,
                        type = current.type,
                        depth = current.depth,
                        childCount = runLength,
                        groupKey = key,
                        isGroup = true,
                    });
                    // Add children
                    for (int j = runStart; j <= i; j++)
                    {
                        var child = _treeNodes[j];
                        child.depth = current.depth + 1;
                        child.groupKey = key;
                        grouped.Add(child);
                    }
                }
                else
                {
                    for (int j = runStart; j <= i; j++)
                        grouped.Add(_treeNodes[j]);
                }
                i++;
            }

            _treeNodes.Clear();
            _treeNodes.AddRange(grouped);
        }

        #endregion

        #region Tree Draw

        private void DrawTreeList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(400), GUILayout.ExpandHeight(true));
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, EditorStyles.helpBox);

            if (!_treeBuilt || _treeNodes.Count == 0)
            {
                EditorGUILayout.LabelField("No events loaded.", EditorStyles.miniLabel);
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
                return;
            }

            bool hasFilter = !string.IsNullOrEmpty(_filterText);
            string filterLower = hasFilter ? _filterText.ToLowerInvariant() : "";

            for (int i = 0; i < _treeNodes.Count; i++)
            {
                var node = _treeNodes[i];

                // Skip children of collapsed groups
                if (!node.isGroup && node.groupKey != null && _collapsed.Contains(node.groupKey))
                    continue;

                // Filter
                if (hasFilter && !node.name.ToLowerInvariant().Contains(filterLower))
                    continue;

                // Indent
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(node.depth * 16);

                if (node.isGroup)
                {
                    DrawGroupRow(node);
                }
                else
                {
                    DrawEventRow(node);
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawGroupRow(TreeNode node)
        {
            bool collapsed = _collapsed.Contains(node.groupKey);
            string arrow = collapsed ? "\u25B6" : "\u25BC";

            var style = EditorStyles.miniBoldLabel;
            if (GUILayout.Button($"{arrow} {node.name}", style))
            {
                if (collapsed) _collapsed.Remove(node.groupKey);
                else _collapsed.Add(node.groupKey);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(node.childCount.ToString(),
                EditorStyles.miniLabel, GUILayout.Width(40));
        }

        private void DrawEventRow(TreeNode node)
        {
            bool selected = node.eventIndex == _selectedIndex;
            var bgStyle = selected ? ShaderInspectorStyles.ListItemSelected : ShaderInspectorStyles.ListItem;

            if (GUILayout.Button($"#{node.eventIndex}  {node.name}", bgStyle,
                GUILayout.Height(20), GUILayout.ExpandWidth(true)))
            {
                SelectEvent(node.eventIndex);
            }
        }

        #endregion

        #region Detail

        private void DrawDetailPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            if (_selectedIndex < 0)
            {
                EditorGUILayout.HelpBox("Select an event on the left to see its detail.", MessageType.None);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField($"Event #{_selectedIndex}", EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                _selectedDetailJson = FrameDebugBridge.GetEventDetail(_selectedIndex);
            if (GUILayout.Button("To Compare A", EditorStyles.toolbarButton, GUILayout.Width(100)))
                _window.SetCompareSlot(_selectedIndex, true);
            if (GUILayout.Button("To Compare B", EditorStyles.toolbarButton, GUILayout.Width(100)))
                _window.SetCompareSlot(_selectedIndex, false);
            if (GUILayout.Button("Ask AI", EditorStyles.toolbarButton, GUILayout.Width(70)))
                HandOffSelected();
            EditorGUILayout.EndHorizontal();

            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

            // RT Preview
            if (_rtPreview != null)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Render Target", ShaderInspectorStyles.SectionHeader);
                float maxW = EditorGUIUtility.currentViewWidth - 440;
                if (maxW < 200) maxW = 200;
                float aspect = (float)_rtPreview.height / _rtPreview.width;
                float dispW = Mathf.Min(maxW, _rtPreview.width);
                float dispH = dispW * aspect;
                var imageRect = GUILayoutUtility.GetRect(dispW, dispH);
                EditorGUI.DrawPreviewTexture(imageRect, _rtPreview, null, ScaleMode.ScaleToFit);
                EditorGUILayout.LabelField($"{_rtPreview.width} x {_rtPreview.height}", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            if (string.IsNullOrEmpty(_selectedDetailJson))
            {
                EditorGUILayout.LabelField("(no detail loaded)", EditorStyles.miniLabel);
            }
            else
            {
                DrawStructuredDetail(_selectedDetailJson);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawStructuredDetail(string json)
        {
            string err = JsonHelper.GetString(json, "error");
            if (!string.IsNullOrEmpty(err))
            {
                EditorGUILayout.HelpBox($"{err}: {JsonHelper.GetString(json, "detail") ?? ""}", MessageType.Warning);
                return;
            }

            // -- Shader --
            string shaderObj = JsonHelper.GetObject(json, "shader");
            if (!string.IsNullOrEmpty(shaderObj))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Shader", ShaderInspectorStyles.SectionHeader);
                DetailRow("Name", JsonHelper.GetString(shaderObj, "name"));
                DetailRow("Pass", JsonHelper.GetString(shaderObj, "pass"));
                DetailRow("LightMode", JsonHelper.GetString(shaderObj, "lightMode"));
                DetailRow("Instance ID", JsonHelper.GetInt(shaderObj, "instanceID", 0).ToString());
                DetailRow("SubShader", JsonHelper.GetInt(shaderObj, "subShaderIndex", 0).ToString());
                DetailRow("Pass Index", JsonHelper.GetInt(shaderObj, "shaderPassIndex", 0).ToString());
                var kws = JsonHelper.GetStringArray(shaderObj, "keywords");
                if (kws != null && kws.Count > 0)
                    DetailRow("Keywords", string.Join("  ", kws));
                else
                    DetailRow("Keywords", "(none)");
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            // -- Render Target --
            string rtObj = JsonHelper.GetObject(json, "renderTarget");
            if (!string.IsNullOrEmpty(rtObj))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Render Target", ShaderInspectorStyles.SectionHeader);
                int w = JsonHelper.GetInt(rtObj, "width", 0);
                int h = JsonHelper.GetInt(rtObj, "height", 0);
                int cnt = JsonHelper.GetInt(rtObj, "count", 0);
                DetailRow("Size", $"{w} x {h}");
                DetailRow("MRT Count", cnt.ToString());
                DetailRow("Format", JsonHelper.GetInt(rtObj, "format", 0).ToString());
                DetailRow("Has Depth", JsonHelper.GetBool(rtObj, "hasDepthTexture", false) ? "Yes" : "No");
                DetailRow("Memoryless", JsonHelper.GetBool(rtObj, "memoryless", false) ? "Yes" : "No");
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            // -- Geometry --
            string geomObj = JsonHelper.GetObject(json, "geometry");
            if (!string.IsNullOrEmpty(geomObj))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Geometry", ShaderInspectorStyles.SectionHeader);
                DetailRow("Vertices", JsonHelper.GetInt(geomObj, "vertexCount", 0).ToString("N0"));
                DetailRow("Indices", JsonHelper.GetInt(geomObj, "indexCount", 0).ToString("N0"));
                DetailRow("Instances", JsonHelper.GetInt(geomObj, "instanceCount", 0).ToString("N0"));
                DetailRow("Draw Calls", JsonHelper.GetInt(geomObj, "drawCallCount", 0).ToString("N0"));
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            // -- Render State --
            string stateObj = JsonHelper.GetObject(json, "state");
            if (!string.IsNullOrEmpty(stateObj))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Render State", ShaderInspectorStyles.SectionHeader);
                string blend = JsonHelper.GetString(stateObj, "blendState");
                string raster = JsonHelper.GetString(stateObj, "rasterState");
                string depth = JsonHelper.GetString(stateObj, "depthState");
                string stencil = JsonHelper.GetString(stateObj, "stencilState");
                int stencilRefVal = JsonHelper.GetInt(stateObj, "stencilRef", -1);
                if (!string.IsNullOrEmpty(blend)) DetailRow("Blend", blend);
                if (!string.IsNullOrEmpty(raster)) DetailRow("Raster", raster);
                if (!string.IsNullOrEmpty(depth)) DetailRow("Depth", depth);
                if (!string.IsNullOrEmpty(stencil)) DetailRow("Stencil", stencil);
                if (stencilRefVal >= 0) DetailRow("Stencil Ref", stencilRefVal.ToString());
                if (string.IsNullOrEmpty(blend) && string.IsNullOrEmpty(depth))
                    EditorGUILayout.LabelField("(no state data)", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            // -- Batch --
            string batchObj = JsonHelper.GetObject(json, "batch");
            if (!string.IsNullOrEmpty(batchObj))
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("Batch", ShaderInspectorStyles.SectionHeader);
                int cause = JsonHelper.GetInt(batchObj, "batchCause", 0);
                if (cause != 0)
                {
                    var old = GUI.color;
                    GUI.color = ShaderInspectorStyles.YellowStatus;
                    DetailRow("Break Cause", cause.ToString());
                    GUI.color = old;
                }
                else
                {
                    DetailRow("Batched", "yes");
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }

            // -- Raw JSON (collapsed) --
            _showRawJson = EditorGUILayout.Foldout(_showRawJson, "Raw JSON");
            if (_showRawJson)
            {
                EditorGUILayout.SelectableLabel(PrettyPrintJson(json),
                    ShaderInspectorStyles.CodeArea,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            }
        }

        private static void DetailRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel, GUILayout.Width(100));
            EditorGUILayout.SelectableLabel(value ?? "", EditorStyles.label, GUILayout.Height(18));
            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region AI Handoff

        private void HandOffSelected()
        {
            if (_selectedIndex < 0 || string.IsNullOrEmpty(_selectedDetailJson)) return;

            // Save RT screenshot as temp file for AI to analyze
            string rtImagePath = null;
            if (_rtPreview != null)
            {
                try
                {
                    byte[] png = _rtPreview.EncodeToPNG();
                    rtImagePath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        $"unity-fd-rt-event{_selectedIndex}.png");
                    System.IO.File.WriteAllBytes(rtImagePath, png);
                }
                catch { rtImagePath = null; }
            }

            string ctx =
                $"Frame event #{_selectedIndex} detail:\n\n{_selectedDetailJson}\n\n" +
                $"Frame summary:\n{_window.LastSummaryJson ?? "(none)"}";

            if (!string.IsNullOrEmpty(rtImagePath))
                ctx += $"\n\n[Render Target screenshot saved at: {rtImagePath}]";

            string prompt =
                $"Frame event #{_selectedIndex} 의 렌더 타겟 스크린샷과 state를 분석해줘.\n" +
                "- 화면에 보이는 렌더링 결과가 정상인지\n" +
                "- 성능/정확성 관점에서 눈에 띄는 점\n" +
                "- 개선 포인트가 있다면 제안";

            _window.AskAIAboutFrame(prompt, ctx, $"Event #{_selectedIndex}", rtImagePath);
        }

        #endregion

        #region JSON Helpers

        private static string PrettyPrintJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;
            var sb = new StringBuilder(json.Length + json.Length / 4);
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
                if (!inString)
                {
                    if (c == '{' || c == '[')
                    {
                        sb.Append(c); sb.Append('\n');
                        indent++;
                        AppendIndent(sb, indent);
                        continue;
                    }
                    if (c == '}' || c == ']')
                    {
                        sb.Append('\n');
                        indent = System.Math.Max(0, indent - 1);
                        AppendIndent(sb, indent);
                        sb.Append(c);
                        continue;
                    }
                    if (c == ',')
                    {
                        sb.Append(c); sb.Append('\n');
                        AppendIndent(sb, indent);
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static void AppendIndent(StringBuilder sb, int level)
        {
            for (int i = 0; i < level; i++) sb.Append("  ");
        }

        #endregion
    }
}
