using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Standalone window for shader #include hierarchy visualization.
    /// Opened from the Shaders tab via "View Include Graph" button.
    /// </summary>
    public class IncludeGraphWindow : EditorWindow
    {
        // Current shader context
        private string _shaderPath;
        private string _shaderName;

        // Tree data
        private IncludeTreeNode _treeRoot;

        // Graph nodes (layout computed)
        private List<GraphNode> _graphNodes;
        private List<GraphEdge> _graphEdges;

        // Interaction state
        private int _selectedNodeIndex = -1;
        private Vector2 _panOffset;
        private float _zoom = 1f;
        private bool _isPanning;
        private Vector2 _lastMousePos;

        // Layout constants
        private const float NodeWidth = 180f;
        private const float NodeHeight = 44f;
        private const float HorizontalGap = 60f;
        private const float VerticalGap = 16f;
        private const float InfoPanelWidth = 280f;
        private const float MinZoom = 0.3f;
        private const float MaxZoom = 2f;

        // Scroll for info panel
        private Vector2 _infoScrollPos;

        // AI analysis state
        private string _aiResult = "";
        private bool _isAIAnalyzing;
        private string _aiStatusText;
        private int _aiAnalyzedNodeIndex = -1;
        private string _selectedLanguage;

        private class GraphNode
        {
            public string name;
            public string path;
            public int lineCount;
            public Rect rect;
            public bool isRoot;
            public int depth;
            public List<int> childIndices = new List<int>();
        }

        private class GraphEdge
        {
            public int fromIndex;
            public int toIndex;
        }

        /// <summary>Open the include graph window for a specific shader.</summary>
        public static void Open(string shaderPath, string shaderName, string language = null)
        {
            var window = GetWindow<IncludeGraphWindow>("Include Graph");
            window.minSize = new Vector2(600, 400);
            window._shaderPath = shaderPath;
            window._shaderName = shaderName;
            window._selectedLanguage = language;
            window.RebuildGraph();
            window.Show();
            window.Focus();
        }

        private void OnGUI()
        {
            DrawToolbar();

            if (_treeRoot == null)
            {
                EditorGUILayout.LabelField("No include graph data.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandHeight(true));
                return;
            }

            Rect lastRect = GUILayoutUtility.GetLastRect();
            float topY = lastRect.yMax;
            float windowWidth = position.width;
            float remainingHeight = position.height - topY;
            if (remainingHeight < 100) remainingHeight = 100;

            GUILayoutUtility.GetRect(windowWidth, remainingHeight);

            float splitterWidth = 2f;
            float infoWidth = InfoPanelWidth;
            float graphWidth = windowWidth - infoWidth - splitterWidth;
            if (graphWidth < 100) graphWidth = 100;

            Rect graphRect = new Rect(0, topY, graphWidth, remainingHeight);
            Rect splitterRect = new Rect(graphRect.xMax, topY, splitterWidth, remainingHeight);
            Rect infoRect = new Rect(splitterRect.xMax, topY, infoWidth, remainingHeight);

            DrawGraphArea(graphRect);
            EditorGUI.DrawRect(splitterRect, ShaderInspectorStyles.SplitterColor);
            DrawInfoPanel(infoRect);
        }

        private void Update()
        {
            if (AIRequestHandler.HasPendingRequests)
                Repaint();
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            string label = string.IsNullOrEmpty(_shaderName) ? "No shader" : _shaderName;
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.MaxWidth(300));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Rebuild", EditorStyles.toolbarButton, GUILayout.Width(60)))
                RebuildGraph();
            if (GUILayout.Button("Fit All", EditorStyles.toolbarButton, GUILayout.Width(50)))
                FitAll();

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Graph Area

        private void DrawGraphArea(Rect graphRect)
        {
            EditorGUI.DrawRect(graphRect, ShaderInspectorStyles.GraphBackground);
            HandleGraphInput(graphRect);

            GUI.BeginClip(graphRect);
            {
                DrawEdges(graphRect);
                DrawNodes(graphRect);
                GUI.Label(new Rect(6, graphRect.height - 20, 80, 18),
                    $"Zoom: {_zoom:F1}x", EditorStyles.miniLabel);
            }
            GUI.EndClip();
        }

        private Vector2 NodeToLocal(Vector2 nodePos, Rect graphRect)
        {
            Vector2 pivot = new Vector2(graphRect.width * 0.5f, graphRect.height * 0.5f);
            Vector2 shifted = nodePos + _panOffset;
            return (shifted - pivot) * _zoom + pivot;
        }

        private Rect NodeRectToLocal(Rect nodeRect, Rect graphRect)
        {
            Vector2 topLeft = NodeToLocal(new Vector2(nodeRect.x, nodeRect.y), graphRect);
            return new Rect(topLeft.x, topLeft.y, nodeRect.width * _zoom, nodeRect.height * _zoom);
        }

        private void HandleGraphInput(Rect graphRect)
        {
            Event e = Event.current;
            if (!graphRect.Contains(e.mousePosition) &&
                e.type != EventType.MouseUp && e.type != EventType.MouseDrag)
                return;

            Vector2 localMouse = TransformMouseToGraph(e.mousePosition, graphRect);

            switch (e.type)
            {
                case EventType.ScrollWheel:
                    if (graphRect.Contains(e.mousePosition))
                    {
                        _zoom = Mathf.Clamp(_zoom + -e.delta.y * 0.05f, MinZoom, MaxZoom);
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.MouseDown:
                    if (!graphRect.Contains(e.mousePosition)) break;
                    if (e.button == 0)
                    {
                        int clickedNode = HitTestNode(localMouse);
                        if (clickedNode >= 0)
                        {
                            if (_selectedNodeIndex != clickedNode && _aiAnalyzedNodeIndex != clickedNode)
                            {
                                _aiResult = "";
                                _aiStatusText = null;
                            }
                            _selectedNodeIndex = clickedNode;
                            if (e.clickCount == 2)
                                OpenFileInEditor(_graphNodes[clickedNode].path);
                            e.Use();
                            Repaint();
                        }
                        else
                        {
                            _selectedNodeIndex = -1;
                            _isPanning = true;
                            _lastMousePos = e.mousePosition;
                            e.Use();
                            Repaint();
                        }
                    }
                    else if (e.button == 2)
                    {
                        _isPanning = true;
                        _lastMousePos = e.mousePosition;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_isPanning)
                    {
                        _panOffset += (e.mousePosition - _lastMousePos) / _zoom;
                        _lastMousePos = e.mousePosition;
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (_isPanning) { _isPanning = false; e.Use(); }
                    break;
            }
        }

        private Vector2 TransformMouseToGraph(Vector2 mousePos, Rect graphRect)
        {
            Vector2 pivot = new Vector2(graphRect.width * 0.5f, graphRect.height * 0.5f);
            Vector2 local = mousePos - graphRect.position;
            return (local - pivot) / _zoom + pivot - _panOffset;
        }

        private int HitTestNode(Vector2 pos)
        {
            if (_graphNodes == null) return -1;
            for (int i = _graphNodes.Count - 1; i >= 0; i--)
                if (_graphNodes[i].rect.Contains(pos)) return i;
            return -1;
        }

        private void DrawNodes(Rect graphRect)
        {
            if (_graphNodes == null) return;
            for (int i = 0; i < _graphNodes.Count; i++)
            {
                var node = _graphNodes[i];
                Rect r = NodeRectToLocal(node.rect, graphRect);
                if (r.xMax < 0 || r.x > graphRect.width || r.yMax < 0 || r.y > graphRect.height) continue;

                bool isSelected = i == _selectedNodeIndex;
                Color bgColor = node.isRoot ? ShaderInspectorStyles.GraphNodeRoot :
                    isSelected ? ShaderInspectorStyles.GraphNodeSelected : ShaderInspectorStyles.GraphNodeNormal;

                EditorGUI.DrawRect(r, bgColor);
                DrawRectBorder(r, isSelected ? ShaderInspectorStyles.CyanStatus : ShaderInspectorStyles.GraphNodeBorder, 1f);

                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.GraphNodeText;
                GUI.Label(new Rect(r.x + 6, r.y + 4 * _zoom, r.width - 12, 18 * _zoom),
                    TruncateString(node.name, 22), EditorStyles.boldLabel);
                GUI.color = ShaderInspectorStyles.GraphNodeSubText;
                GUI.Label(new Rect(r.x + 6, r.y + 22 * _zoom, r.width - 12, 16 * _zoom),
                    $"{node.lineCount} lines", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }
        }

        private void DrawEdges(Rect graphRect)
        {
            if (_graphEdges == null || _graphNodes == null) return;
            Color lineColor = ShaderInspectorStyles.GraphConnection;

            foreach (var edge in _graphEdges)
            {
                if (edge.fromIndex >= _graphNodes.Count || edge.toIndex >= _graphNodes.Count) continue;
                Rect fromRect = NodeRectToLocal(_graphNodes[edge.fromIndex].rect, graphRect);
                Rect toRect = NodeRectToLocal(_graphNodes[edge.toIndex].rect, graphRect);

                float startX = fromRect.xMax, startY = fromRect.center.y;
                float endX = toRect.xMin, endY = toRect.center.y;
                float midX = (startX + endX) * 0.5f;

                DrawLine(startX, startY, midX, startY, lineColor);
                DrawLine(midX, startY, midX, endY, lineColor);
                DrawLine(midX, endY, endX, endY, lineColor);
            }
        }

        private static void DrawLine(float x1, float y1, float x2, float y2, Color color)
        {
            const float thickness = 2f;
            if (Mathf.Abs(x2 - x1) < 0.1f)
                EditorGUI.DrawRect(new Rect(x1 - thickness * 0.5f, Mathf.Min(y1, y2), thickness, Mathf.Abs(y2 - y1)), color);
            else
                EditorGUI.DrawRect(new Rect(Mathf.Min(x1, x2), y1 - thickness * 0.5f, Mathf.Abs(x2 - x1), thickness), color);
        }

        private static void DrawRectBorder(Rect rect, Color color, float width)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, width), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, width, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
        }

        #endregion

        #region Info Panel

        private void DrawInfoPanel(Rect infoRect)
        {
            GUILayout.BeginArea(infoRect);
            _infoScrollPos = EditorGUILayout.BeginScrollView(_infoScrollPos);

            EditorGUILayout.LabelField("Node Info", ShaderInspectorStyles.SectionHeader);
            EditorGUILayout.Space(4);

            if (_selectedNodeIndex >= 0 && _selectedNodeIndex < _graphNodes?.Count)
            {
                var node = _graphNodes[_selectedNodeIndex];

                EditorGUILayout.LabelField("Name:", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(node.name, EditorStyles.boldLabel);
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Path:", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(node.path, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField($"Lines: {node.lineCount}");
                EditorGUILayout.LabelField($"Depth: {node.depth}");
                EditorGUILayout.LabelField($"Children: {node.childIndices.Count}");
                EditorGUILayout.Space(8);

                if (GUILayout.Button("Open in Editor", GUILayout.Height(24)))
                    OpenFileInEditor(node.path);
                if (GUILayout.Button("Ping in Project", GUILayout.Height(24)))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(node.path);
                    if (asset != null) EditorGUIUtility.PingObject(asset);
                }

                // AI section
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("AI", ShaderInspectorStyles.SectionHeader);

                bool aiAvailable = AIRequestHandler.IsAvailable;
                EditorGUI.BeginDisabledGroup(_isAIAnalyzing || !aiAvailable);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Explain File", GUILayout.Height(24)))
                    RunAIAnalysis(node, "explain");
                if (GUILayout.Button("Role in Shader", GUILayout.Height(24)))
                    RunAIAnalysis(node, "role");
                EditorGUILayout.EndHorizontal();
                EditorGUI.EndDisabledGroup();

                if (!aiAvailable)
                    EditorGUILayout.HelpBox("AI not available. Ensure MCP server is connected.", MessageType.Info);

                // AI result
                if (_isAIAnalyzing && string.IsNullOrEmpty(_aiResult))
                {
                    EditorGUILayout.Space(4);
                    string status = !string.IsNullOrEmpty(_aiStatusText) ? _aiStatusText : "AI is analyzing...";
                    EditorGUILayout.LabelField(status, EditorStyles.centeredGreyMiniLabel);
                }
                else if (!string.IsNullOrEmpty(_aiResult))
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.BeginVertical(ShaderInspectorStyles.ChatBubbleAI);
                    MarkdownRenderer.Render(_aiResult);
                    EditorGUILayout.EndVertical();
                }

                // Children list
                if (node.childIndices.Count > 0)
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Includes:", ShaderInspectorStyles.SectionHeader);
                    foreach (int childIdx in node.childIndices)
                    {
                        if (childIdx >= _graphNodes.Count) continue;
                        var child = _graphNodes[childIdx];
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(child.name, EditorStyles.miniLabel);
                        if (GUILayout.Button("Go", EditorStyles.miniButton, GUILayout.Width(28)))
                        {
                            if (_aiAnalyzedNodeIndex != childIdx) { _aiResult = ""; _aiStatusText = null; }
                            _selectedNodeIndex = childIdx;
                            Repaint();
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
            else
            {
                EditorGUILayout.LabelField("Click a node to view details.", EditorStyles.centeredGreyMiniLabel);
                if (_graphNodes != null)
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Graph Stats", ShaderInspectorStyles.SectionHeader);
                    EditorGUILayout.LabelField($"Total nodes: {_graphNodes.Count}");
                    EditorGUILayout.LabelField($"Total edges: {_graphEdges?.Count ?? 0}");
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Controls", ShaderInspectorStyles.SectionHeader);
            EditorGUILayout.LabelField("Scroll: Zoom", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Drag empty area: Pan", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Click: Select node", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Double-click: Open file", EditorStyles.miniLabel);

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        #endregion

        #region Build Graph

        private void RebuildGraph()
        {
            _selectedNodeIndex = -1;
            _graphNodes = null;
            _graphEdges = null;
            _treeRoot = null;
            _aiResult = "";
            _aiStatusText = null;
            _isAIAnalyzing = false;
            _aiAnalyzedNodeIndex = -1;

            if (string.IsNullOrEmpty(_shaderPath)) return;

            try
            {
                string json = ShaderAnalyzer.GetIncludeTree(_shaderPath);
                if (JsonHelper.GetString(json, "error") != null) return;

                _treeRoot = IncludeTreeNode.Parse(json);
                _graphNodes = new List<GraphNode>();
                _graphEdges = new List<GraphEdge>();
                FlattenTree(_treeRoot, 0, -1);
                ComputeLayout();
                FitAll();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IncludeGraph] Failed to build graph: {ex.Message}");
            }
        }

        private int FlattenTree(IncludeTreeNode treeNode, int depth, int parentIndex)
        {
            int index = _graphNodes.Count;
            _graphNodes.Add(new GraphNode
            {
                name = treeNode.name, path = treeNode.path, lineCount = treeNode.lineCount,
                isRoot = (depth == 0), depth = depth
            });

            if (parentIndex >= 0)
            {
                _graphNodes[parentIndex].childIndices.Add(index);
                _graphEdges.Add(new GraphEdge { fromIndex = parentIndex, toIndex = index });
            }

            foreach (var child in treeNode.children)
                FlattenTree(child, depth + 1, index);
            return index;
        }

        private void ComputeLayout()
        {
            if (_graphNodes == null || _graphNodes.Count == 0) return;
            float[] yPositions = new float[_graphNodes.Count];
            float currentY = 0;
            ComputeYPositions(0, ref currentY, yPositions);

            for (int i = 0; i < _graphNodes.Count; i++)
            {
                float x = _graphNodes[i].depth * (NodeWidth + HorizontalGap) + 40;
                _graphNodes[i].rect = new Rect(x, yPositions[i], NodeWidth, NodeHeight);
            }
        }

        private float ComputeYPositions(int nodeIndex, ref float currentY, float[] yPositions)
        {
            var node = _graphNodes[nodeIndex];
            if (node.childIndices.Count == 0)
            {
                yPositions[nodeIndex] = currentY;
                currentY += NodeHeight + VerticalGap;
                return yPositions[nodeIndex];
            }

            float firstChildY = float.MaxValue, lastChildY = float.MinValue;
            foreach (int childIdx in node.childIndices)
            {
                float childY = ComputeYPositions(childIdx, ref currentY, yPositions);
                firstChildY = Mathf.Min(firstChildY, childY);
                lastChildY = Mathf.Max(lastChildY, childY);
            }
            yPositions[nodeIndex] = (firstChildY + lastChildY) / 2f;
            return yPositions[nodeIndex];
        }

        private void FitAll()
        {
            if (_graphNodes == null || _graphNodes.Count == 0) return;
            _panOffset = Vector2.zero;
            _zoom = 1f;

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var node in _graphNodes)
            {
                minX = Mathf.Min(minX, node.rect.x);
                minY = Mathf.Min(minY, node.rect.y);
                maxX = Mathf.Max(maxX, node.rect.xMax);
                maxY = Mathf.Max(maxY, node.rect.yMax);
            }

            float contentWidth = maxX - minX + 80;
            float contentHeight = maxY - minY + 80;
            _panOffset = new Vector2(-(minX + maxX) / 2f + 200, -(minY + maxY) / 2f + 200);

            float availWidth = position.width - InfoPanelWidth - 40;
            float availHeight = position.height - 60;
            if (availWidth > 0 && availHeight > 0)
            {
                float zoomX = availWidth / contentWidth;
                float zoomY = availHeight / contentHeight;
                _zoom = Mathf.Clamp(Mathf.Min(zoomX, zoomY), MinZoom, 1.5f);
            }
        }

        #endregion

        #region AI Analysis

        private void RunAIAnalysis(GraphNode node, string analysisType)
        {
            _isAIAnalyzing = true;
            _aiResult = "";
            _aiStatusText = null;
            _aiAnalyzedNodeIndex = _selectedNodeIndex;

            string fileContext = GatherFileContext(node);
            string prompt = analysisType == "role"
                ? $"This file \"{node.name}\" is part of the shader \"{_shaderName}\"'s include hierarchy. " +
                  "Explain what role this file plays within the shader. " +
                  "Explain in a way that a non-developer can understand."
                : $"Explain the shader include file \"{node.name}\" in a way that a non-developer can understand. " +
                  "What does this file do? What are its main features?";

            AIRequestHandler.SendQuery(prompt, fileContext,
                onChunk: chunk => { _aiResult += chunk; Repaint(); },
                onComplete: fullText =>
                {
                    _aiResult = fullText ?? "No response from AI.";
                    _aiStatusText = null;
                    _isAIAnalyzing = false;
                    Repaint();
                },
                onStatus: status => { _aiStatusText = status; Repaint(); },
                language: _selectedLanguage
            );
        }

        private string GatherFileContext(GraphNode node)
        {
            var parts = new List<string>
            {
                $"Root Shader: {_shaderName}", $"Root Path: {_shaderPath}",
                $"Selected File: {node.name}", $"Selected Path: {node.path}",
                $"Depth: {node.depth}"
            };

            try
            {
                string fullPath = Path.GetFullPath(node.path);
                if (File.Exists(fullPath))
                {
                    string content = File.ReadAllText(fullPath);
                    if (content.Length > 4000) content = content.Substring(0, 4000) + "\n... (truncated)";
                    parts.Add($"\n--- File Content ---\n{content}");
                }
            }
            catch { }

            return string.Join("\n", parts);
        }

        #endregion

        #region Helpers

        private static void OpenFileInEditor(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null) AssetDatabase.OpenAsset(asset);
            else
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath)) EditorUtility.OpenWithDefaultApp(fullPath);
            }
        }

        private static string TruncateString(string s, int maxLength)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLength) return s;
            return s.Substring(0, maxLength - 2) + "..";
        }

        #endregion
    }
}
