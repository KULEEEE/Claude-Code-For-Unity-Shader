using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Graph tab: node-based include tree visualization with pan/zoom.
    /// Shows shader #include hierarchy as an interactive node graph.
    /// </summary>
    public class IncludeGraphTab
    {
        private readonly ShaderInspectorWindow _window;

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

        public IncludeGraphTab(ShaderInspectorWindow window)
        {
            _window = window;
        }

        public void OnGUI()
        {
            DrawToolbar();

            if (_treeRoot == null)
            {
                EditorGUILayout.LabelField("Select a shader in the Shaders tab to view its include graph.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandHeight(true));
                return;
            }

            // Calculate remaining area after toolbar
            Rect lastRect = GUILayoutUtility.GetLastRect();
            float topY = lastRect.yMax;
            Rect winPos = _window.position;
            float windowWidth = winPos.width;
            float remainingHeight = winPos.height - topY - 26; // 26 for status bar
            if (remainingHeight < 100) remainingHeight = 100;

            // Reserve the space in GUILayout so status bar renders correctly below
            GUILayoutUtility.GetRect(windowWidth, remainingHeight);

            float splitterWidth = 2f;
            float infoWidth = InfoPanelWidth;
            float graphWidth = windowWidth - infoWidth - splitterWidth;
            if (graphWidth < 100) graphWidth = 100;

            Rect graphRect = new Rect(0, topY, graphWidth, remainingHeight);
            Rect splitterRect = new Rect(graphRect.xMax, topY, splitterWidth, remainingHeight);
            Rect infoRect = new Rect(splitterRect.xMax, topY, infoWidth, remainingHeight);

            // Graph area
            DrawGraphArea(graphRect);

            // Splitter
            EditorGUI.DrawRect(splitterRect, ShaderInspectorStyles.SplitterColor);

            // Info panel
            DrawInfoPanel(infoRect);
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            string label = string.IsNullOrEmpty(_shaderName) ? "No shader selected" : _shaderName;
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.MaxWidth(300));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Rebuild", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                RebuildGraph();
            }
            if (GUILayout.Button("Fit All", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                FitAll();
            }

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Graph Area

        // Cached graph rect for coordinate transforms
        private Rect _currentGraphRect;

        private void DrawGraphArea(Rect graphRect)
        {
            _currentGraphRect = graphRect;

            // Draw background
            EditorGUI.DrawRect(graphRect, ShaderInspectorStyles.GraphBackground);

            // Handle input (screen coords)
            HandleGraphInput(graphRect);

            // Draw content clipped to graph area - no GUI.matrix, manual transform
            GUI.BeginClip(graphRect);
            {
                DrawEdges(graphRect);
                DrawNodes(graphRect);

                // Zoom indicator
                GUI.Label(new Rect(6, graphRect.height - 20, 80, 18),
                    $"Zoom: {_zoom:F1}x", EditorStyles.miniLabel);
            }
            GUI.EndClip();
        }

        /// <summary>Transform a node-space point to local clip-space for rendering.</summary>
        private Vector2 NodeToLocal(Vector2 nodePos, Rect graphRect)
        {
            Vector2 pivot = new Vector2(graphRect.width * 0.5f, graphRect.height * 0.5f);
            Vector2 shifted = nodePos + _panOffset;
            return (shifted - pivot) * _zoom + pivot;
        }

        /// <summary>Transform a node-space rect to local clip-space for rendering.</summary>
        private Rect NodeRectToLocal(Rect nodeRect, Rect graphRect)
        {
            Vector2 topLeft = NodeToLocal(new Vector2(nodeRect.x, nodeRect.y), graphRect);
            float w = nodeRect.width * _zoom;
            float h = nodeRect.height * _zoom;
            return new Rect(topLeft.x, topLeft.y, w, h);
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
                        float delta = -e.delta.y * 0.05f;
                        _zoom = Mathf.Clamp(_zoom + delta, MinZoom, MaxZoom);
                        e.Use();
                        _window.Repaint();
                    }
                    break;

                case EventType.MouseDown:
                    if (!graphRect.Contains(e.mousePosition)) break;

                    if (e.button == 0)
                    {
                        // Check node click
                        int clickedNode = HitTestNode(localMouse);
                        if (clickedNode >= 0)
                        {
                            _selectedNodeIndex = clickedNode;

                            // Double-click: open file
                            if (e.clickCount == 2)
                            {
                                var node = _graphNodes[clickedNode];
                                OpenFileInEditor(node.path);
                            }
                            e.Use();
                            _window.Repaint();
                        }
                        else
                        {
                            // Left-click on empty space: start panning
                            _selectedNodeIndex = -1;
                            _isPanning = true;
                            _lastMousePos = e.mousePosition;
                            e.Use();
                            _window.Repaint();
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
                        Vector2 delta2 = e.mousePosition - _lastMousePos;
                        _panOffset += delta2 / _zoom;
                        _lastMousePos = e.mousePosition;
                        e.Use();
                        _window.Repaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (_isPanning)
                    {
                        _isPanning = false;
                        e.Use();
                    }
                    break;
            }
        }

        private Vector2 TransformMouseToGraph(Vector2 mousePos, Rect graphRect)
        {
            // Reverse of NodeToLocal: screenPos -> nodePos
            Vector2 pivot = new Vector2(graphRect.width * 0.5f, graphRect.height * 0.5f);
            Vector2 local = mousePos - graphRect.position;
            return (local - pivot) / _zoom + pivot - _panOffset;
        }

        private int HitTestNode(Vector2 pos)
        {
            if (_graphNodes == null) return -1;
            for (int i = _graphNodes.Count - 1; i >= 0; i--)
            {
                if (_graphNodes[i].rect.Contains(pos))
                    return i;
            }
            return -1;
        }

        private void DrawNodes(Rect graphRect)
        {
            if (_graphNodes == null) return;

            for (int i = 0; i < _graphNodes.Count; i++)
            {
                var node = _graphNodes[i];
                Rect r = NodeRectToLocal(node.rect, graphRect);

                // Skip if completely off-screen
                if (r.xMax < 0 || r.x > graphRect.width || r.yMax < 0 || r.y > graphRect.height)
                    continue;

                bool isSelected = i == _selectedNodeIndex;

                // Node background
                Color bgColor = node.isRoot ? ShaderInspectorStyles.GraphNodeRoot :
                    isSelected ? ShaderInspectorStyles.GraphNodeSelected :
                    ShaderInspectorStyles.GraphNodeNormal;

                EditorGUI.DrawRect(r, bgColor);

                // Border
                DrawRectBorder(r, isSelected ?
                    ShaderInspectorStyles.CyanStatus : ShaderInspectorStyles.GraphNodeBorder, 1f);

                // Text: file name
                var nameRect = new Rect(r.x + 6, r.y + 4 * _zoom, r.width - 12, 18 * _zoom);
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.GraphNodeText;
                GUI.Label(nameRect, TruncateString(node.name, 22), EditorStyles.boldLabel);

                // Sub text: line count
                var subRect = new Rect(r.x + 6, r.y + 22 * _zoom, r.width - 12, 16 * _zoom);
                GUI.color = ShaderInspectorStyles.GraphNodeSubText;
                GUI.Label(subRect, $"{node.lineCount} lines", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }
        }

        private void DrawEdges(Rect graphRect)
        {
            if (_graphEdges == null || _graphNodes == null) return;

            Color lineColor = ShaderInspectorStyles.GraphConnection;

            foreach (var edge in _graphEdges)
            {
                if (edge.fromIndex >= _graphNodes.Count || edge.toIndex >= _graphNodes.Count)
                    continue;

                var fromNode = _graphNodes[edge.fromIndex];
                var toNode = _graphNodes[edge.toIndex];

                Rect fromRect = NodeRectToLocal(fromNode.rect, graphRect);
                Rect toRect = NodeRectToLocal(toNode.rect, graphRect);

                float startX = fromRect.xMax;
                float startY = fromRect.center.y;
                float endX = toRect.xMin;
                float endY = toRect.center.y;
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
            {
                // Vertical line
                float minY = Mathf.Min(y1, y2);
                float maxY = Mathf.Max(y1, y2);
                EditorGUI.DrawRect(new Rect(x1 - thickness * 0.5f, minY, thickness, maxY - minY), color);
            }
            else
            {
                // Horizontal line
                float minX = Mathf.Min(x1, x2);
                float maxX = Mathf.Max(x1, x2);
                EditorGUI.DrawRect(new Rect(minX, y1 - thickness * 0.5f, maxX - minX, thickness), color);
            }
        }

        private static void DrawRectBorder(Rect rect, Color color, float width)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, width), color); // top
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - width, rect.width, width), color); // bottom
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, width, rect.height), color); // left
            EditorGUI.DrawRect(new Rect(rect.xMax - width, rect.y, width, rect.height), color); // right
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
                {
                    OpenFileInEditor(node.path);
                }

                if (GUILayout.Button("Ping in Project", GUILayout.Height(24)))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(node.path);
                    if (asset != null) EditorGUIUtility.PingObject(asset);
                }

                if (node.childIndices.Count > 0)
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Includes:", ShaderInspectorStyles.SectionHeader);
                    foreach (int childIdx in node.childIndices)
                    {
                        if (childIdx < _graphNodes.Count)
                        {
                            var child = _graphNodes[childIdx];
                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(child.name, EditorStyles.miniLabel);
                            if (GUILayout.Button("Go", EditorStyles.miniButton, GUILayout.Width(28)))
                            {
                                _selectedNodeIndex = childIdx;
                                _window.Repaint();
                            }
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                }
            }
            else
            {
                EditorGUILayout.LabelField("Click a node to view details.",
                    EditorStyles.centeredGreyMiniLabel);

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

        public void SetShader(string shaderPath, string shaderName)
        {
            if (_shaderPath == shaderPath) return;
            _shaderPath = shaderPath;
            _shaderName = shaderName;
            RebuildGraph();
        }

        public void Refresh()
        {
            if (!string.IsNullOrEmpty(_shaderPath))
                RebuildGraph();
        }

        private void RebuildGraph()
        {
            _selectedNodeIndex = -1;
            _graphNodes = null;
            _graphEdges = null;
            _treeRoot = null;

            if (string.IsNullOrEmpty(_shaderPath)) return;

            try
            {
                string json = ShaderAnalyzer.GetIncludeTree(_shaderPath);
                if (JsonHelper.GetString(json, "error") != null)
                {
                    Debug.LogWarning($"[ShaderInspector] Include tree error: {JsonHelper.GetString(json, "error")}");
                    return;
                }

                _treeRoot = IncludeTreeNode.Parse(json);
                BuildGraphFromTree();
                FitAll();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderInspector] Failed to build include graph: {ex.Message}");
            }
        }

        private void BuildGraphFromTree()
        {
            _graphNodes = new List<GraphNode>();
            _graphEdges = new List<GraphEdge>();

            if (_treeRoot == null) return;

            // Flatten tree to nodes, compute layout
            FlattenTree(_treeRoot, 0, -1);
            ComputeLayout();
        }

        private int FlattenTree(IncludeTreeNode treeNode, int depth, int parentIndex)
        {
            int index = _graphNodes.Count;
            var gNode = new GraphNode
            {
                name = treeNode.name,
                path = treeNode.path,
                lineCount = treeNode.lineCount,
                isRoot = (depth == 0),
                depth = depth
            };
            _graphNodes.Add(gNode);

            if (parentIndex >= 0)
            {
                _graphNodes[parentIndex].childIndices.Add(index);
                _graphEdges.Add(new GraphEdge { fromIndex = parentIndex, toIndex = index });
            }

            foreach (var child in treeNode.children)
            {
                FlattenTree(child, depth + 1, index);
            }

            return index;
        }

        private void ComputeLayout()
        {
            if (_graphNodes == null || _graphNodes.Count == 0) return;

            // Group nodes by depth
            var depthGroups = new Dictionary<int, List<int>>();
            int maxDepth = 0;
            foreach (var node in _graphNodes)
            {
                if (!depthGroups.ContainsKey(node.depth))
                    depthGroups[node.depth] = new List<int>();
                depthGroups[node.depth].Add(_graphNodes.IndexOf(node));
                if (node.depth > maxDepth) maxDepth = node.depth;
            }

            // Simple tree layout: X by depth, Y by recursive centering
            float[] yPositions = new float[_graphNodes.Count];
            float currentY = 0;
            ComputeYPositions(0, ref currentY, yPositions);

            for (int i = 0; i < _graphNodes.Count; i++)
            {
                float x = _graphNodes[i].depth * (NodeWidth + HorizontalGap) + 40;
                float y = yPositions[i];
                _graphNodes[i].rect = new Rect(x, y, NodeWidth, NodeHeight);
            }
        }

        private float ComputeYPositions(int nodeIndex, ref float currentY, float[] yPositions)
        {
            var node = _graphNodes[nodeIndex];

            if (node.childIndices.Count == 0)
            {
                // Leaf node: place at current Y
                yPositions[nodeIndex] = currentY;
                currentY += NodeHeight + VerticalGap;
                return yPositions[nodeIndex];
            }

            // Compute children first
            float firstChildY = float.MaxValue;
            float lastChildY = float.MinValue;

            foreach (int childIdx in node.childIndices)
            {
                float childY = ComputeYPositions(childIdx, ref currentY, yPositions);
                firstChildY = Mathf.Min(firstChildY, childY);
                lastChildY = Mathf.Max(lastChildY, childY);
            }

            // Center parent among its children
            yPositions[nodeIndex] = (firstChildY + lastChildY) / 2f;
            return yPositions[nodeIndex];
        }

        private void FitAll()
        {
            if (_graphNodes == null || _graphNodes.Count == 0) return;

            // Reset pan and zoom to show all nodes
            _panOffset = Vector2.zero;
            _zoom = 1f;

            // Compute bounding box
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

            // Center the content
            _panOffset = new Vector2(
                -(minX + maxX) / 2f + 200,
                -(minY + maxY) / 2f + 200
            );

            // Auto-zoom if content is too large
            float availWidth = Screen.width - InfoPanelWidth - 40;
            float availHeight = Screen.height - 120;
            if (availWidth > 0 && availHeight > 0)
            {
                float zoomX = availWidth / contentWidth;
                float zoomY = availHeight / contentHeight;
                _zoom = Mathf.Clamp(Mathf.Min(zoomX, zoomY), MinZoom, 1.5f);
            }
        }

        #endregion

        #region Helpers

        private static void OpenFileInEditor(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null)
            {
                AssetDatabase.OpenAsset(asset);
            }
            else
            {
                // Try opening as an external file
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    EditorUtility.OpenWithDefaultApp(fullPath);
                }
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
