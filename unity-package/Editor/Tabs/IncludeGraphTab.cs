using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Graph tab: node-based visualization with pan/zoom.
    /// Supports two modes:
    /// - Include Graph: shader #include hierarchy
    /// - Dependency Graph: Shader → Material → Texture dependencies
    /// </summary>
    public class IncludeGraphTab
    {
        private readonly ShaderInspectorWindow _window;

        // Graph mode
        private enum GraphMode { Include, Dependency }
        private GraphMode _graphMode = GraphMode.Include;

        // Current shader context
        private string _shaderPath;
        private string _shaderName;

        // Tree data
        private IncludeTreeNode _includeTreeRoot;
        private DependencyTreeNode _dependencyTreeRoot;

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

        private class GraphNode
        {
            public string name;
            public string path;
            public int lineCount;
            public Rect rect;
            public bool isRoot;
            public int depth;
            public List<int> childIndices = new List<int>();

            // Extended fields for dependency graph
            public string nodeType; // "include", "shader", "material", "texture"
            public string extraInfo; // e.g., texture size, property name
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

            bool hasData = (_graphMode == GraphMode.Include && _includeTreeRoot != null) ||
                           (_graphMode == GraphMode.Dependency && _dependencyTreeRoot != null);

            if (!hasData)
            {
                string hint = _graphMode == GraphMode.Include
                    ? "Select a shader in the Shaders tab to view its include graph."
                    : "Select a shader in the Shaders tab to view its dependency graph.";
                EditorGUILayout.LabelField(hint,
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
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.MaxWidth(200));

            GUILayout.Space(8);

            // Mode toggle
            EditorGUI.BeginChangeCheck();
            bool isIncludeMode = GUILayout.Toggle(_graphMode == GraphMode.Include, "Include",
                EditorStyles.toolbarButton, GUILayout.Width(60));
            if (EditorGUI.EndChangeCheck() && isIncludeMode && _graphMode != GraphMode.Include)
            {
                _graphMode = GraphMode.Include;
                RebuildGraph();
            }

            EditorGUI.BeginChangeCheck();
            bool isDependencyMode = GUILayout.Toggle(_graphMode == GraphMode.Dependency, "Dependency",
                EditorStyles.toolbarButton, GUILayout.Width(75));
            if (EditorGUI.EndChangeCheck() && isDependencyMode && _graphMode != GraphMode.Dependency)
            {
                _graphMode = GraphMode.Dependency;
                RebuildGraph();
            }

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

                // Mode + zoom indicator
                string modeLabel = _graphMode == GraphMode.Include ? "Include" : "Dependency";
                GUI.Label(new Rect(6, graphRect.height - 20, 160, 18),
                    $"{modeLabel} | Zoom: {_zoom:F1}x", EditorStyles.miniLabel);
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
                            if (_selectedNodeIndex != clickedNode)
                            {
                                // Clear AI result when switching to a different node
                                if (_aiAnalyzedNodeIndex != clickedNode)
                                {
                                    _aiResult = "";
                                    _aiStatusText = null;
                                }
                            }
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

                // Node background color based on type
                Color bgColor;
                if (isSelected)
                    bgColor = ShaderInspectorStyles.GraphNodeSelected;
                else
                    bgColor = GetNodeColor(node);

                EditorGUI.DrawRect(r, bgColor);

                // Border
                DrawRectBorder(r, isSelected ?
                    ShaderInspectorStyles.CyanStatus : ShaderInspectorStyles.GraphNodeBorder, 1f);

                // Text: file name
                var nameRect = new Rect(r.x + 6, r.y + 4 * _zoom, r.width - 12, 18 * _zoom);
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.GraphNodeText;
                GUI.Label(nameRect, TruncateString(node.name, 22), EditorStyles.boldLabel);

                // Sub text
                var subRect = new Rect(r.x + 6, r.y + 22 * _zoom, r.width - 12, 16 * _zoom);
                GUI.color = ShaderInspectorStyles.GraphNodeSubText;
                string subText = GetNodeSubText(node);
                GUI.Label(subRect, subText, EditorStyles.miniLabel);
                GUI.color = oldColor;
            }
        }

        private Color GetNodeColor(GraphNode node)
        {
            switch (node.nodeType)
            {
                case "shader": return ShaderInspectorStyles.GraphNodeShader;
                case "material": return ShaderInspectorStyles.GraphNodeMaterial;
                case "texture": return ShaderInspectorStyles.GraphNodeTexture;
                default:
                    return node.isRoot ? ShaderInspectorStyles.GraphNodeRoot : ShaderInspectorStyles.GraphNodeNormal;
            }
        }

        private string GetNodeSubText(GraphNode node)
        {
            switch (node.nodeType)
            {
                case "shader": return $"{node.extraInfo}";
                case "material": return "Material";
                case "texture": return node.extraInfo ?? "Texture";
                default:
                    return $"{node.lineCount} lines";
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

                if (_graphMode == GraphMode.Dependency)
                    DrawDependencyNodeInfo(node);
                else
                    DrawIncludeNodeInfo(node);
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

        private void DrawIncludeNodeInfo(GraphNode node)
        {
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

            // AI section
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("AI", ShaderInspectorStyles.SectionHeader);

            EditorGUI.BeginDisabledGroup(_isAIAnalyzing || !_window.IsAIConnected);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Explain File", GUILayout.Height(24)))
                RunAIAnalysis(node, "explain");
            if (GUILayout.Button("Role in Shader", GUILayout.Height(24)))
                RunAIAnalysis(node, "role");
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            if (!_window.IsAIConnected)
            {
                EditorGUILayout.HelpBox("AI not available. Ensure MCP server is connected.", MessageType.Info);
            }

            DrawAIResult();

            if (node.childIndices.Count > 0)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Includes:", ShaderInspectorStyles.SectionHeader);
                DrawChildNodeList(node);
            }
        }

        private void DrawDependencyNodeInfo(GraphNode node)
        {
            // Type badge
            string typeLabel = node.nodeType == "shader" ? "Shader" :
                               node.nodeType == "material" ? "Material" :
                               node.nodeType == "texture" ? "Texture" : "Unknown";

            var oldColor = GUI.color;
            GUI.color = GetNodeColor(node);
            EditorGUILayout.LabelField(typeLabel, EditorStyles.miniBoldLabel);
            GUI.color = oldColor;

            EditorGUILayout.LabelField("Name:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(node.name, EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            EditorGUILayout.LabelField("Path:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(node.path, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(2);

            // Type-specific info
            if (node.nodeType == "texture")
            {
                if (!string.IsNullOrEmpty(node.extraInfo))
                    EditorGUILayout.LabelField($"Size: {node.extraInfo}");

                // Show texture preview
                var tex = AssetDatabase.LoadAssetAtPath<Texture>(node.path);
                if (tex != null)
                {
                    EditorGUILayout.Space(4);
                    Rect previewRect = GUILayoutUtility.GetRect(InfoPanelWidth - 20, 120);
                    EditorGUI.DrawPreviewTexture(previewRect, tex, null, ScaleMode.ScaleToFit);
                }
            }
            else if (node.nodeType == "material")
            {
                EditorGUILayout.LabelField($"Textures: {node.childIndices.Count}");
            }
            else if (node.nodeType == "shader")
            {
                EditorGUILayout.LabelField($"Materials: {node.childIndices.Count}");
            }

            EditorGUILayout.Space(8);

            // Action buttons
            if (GUILayout.Button("Ping in Project", GUILayout.Height(24)))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(node.path);
                if (asset != null) EditorGUIUtility.PingObject(asset);
            }

            if (node.nodeType == "shader")
            {
                if (GUILayout.Button("Open in Editor", GUILayout.Height(24)))
                    OpenFileInEditor(node.path);
            }
            else if (node.nodeType == "material")
            {
                if (GUILayout.Button("Select Material", GUILayout.Height(24)))
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(node.path);
                    if (mat != null) Selection.activeObject = mat;
                }
            }
            else if (node.nodeType == "texture")
            {
                if (GUILayout.Button("Select Texture", GUILayout.Height(24)))
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(node.path);
                    if (tex != null) Selection.activeObject = tex;
                }
            }

            // Children list
            if (node.childIndices.Count > 0)
            {
                EditorGUILayout.Space(8);
                string childLabel = node.nodeType == "shader" ? "Materials:" :
                                    node.nodeType == "material" ? "Textures:" : "Children:";
                EditorGUILayout.LabelField(childLabel, ShaderInspectorStyles.SectionHeader);
                DrawChildNodeList(node);
            }

            // Find parent for textures (show which property slot)
            if (node.nodeType == "texture" && _graphEdges != null)
            {
                foreach (var edge in _graphEdges)
                {
                    if (edge.toIndex == _selectedNodeIndex && edge.fromIndex < _graphNodes.Count)
                    {
                        EditorGUILayout.Space(4);
                        EditorGUILayout.LabelField($"Used by: {_graphNodes[edge.fromIndex].name}",
                            EditorStyles.miniLabel);
                        break;
                    }
                }
            }
        }

        private void DrawChildNodeList(GraphNode node)
        {
            foreach (int childIdx in node.childIndices)
            {
                if (childIdx < _graphNodes.Count)
                {
                    var child = _graphNodes[childIdx];
                    EditorGUILayout.BeginHorizontal();

                    // For texture nodes in dependency mode, show property name
                    string displayName = child.nodeType == "texture" && !string.IsNullOrEmpty(child.extraInfo)
                        ? $"{child.name} ({child.extraInfo})"
                        : child.name;

                    EditorGUILayout.LabelField(displayName, EditorStyles.miniLabel);
                    if (GUILayout.Button("Go", EditorStyles.miniButton, GUILayout.Width(28)))
                    {
                        if (_aiAnalyzedNodeIndex != childIdx)
                        {
                            _aiResult = "";
                            _aiStatusText = null;
                        }
                        _selectedNodeIndex = childIdx;
                        _window.Repaint();
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        private void DrawAIResult()
        {
            bool showAIWaiting = _isAIAnalyzing && string.IsNullOrEmpty(_aiResult);
            string aiResultCached = _aiResult;

            if (showAIWaiting)
            {
                EditorGUILayout.Space(4);
                string statusDisplay = !string.IsNullOrEmpty(_aiStatusText) ? _aiStatusText : "AI is analyzing...";
                EditorGUILayout.LabelField(statusDisplay, EditorStyles.centeredGreyMiniLabel);
            }
            else if (!string.IsNullOrEmpty(aiResultCached))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginVertical(ShaderInspectorStyles.ChatBubbleAI);
                MarkdownRenderer.Render(aiResultCached);
                EditorGUILayout.EndVertical();
            }
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
            _includeTreeRoot = null;
            _dependencyTreeRoot = null;
            _aiResult = "";
            _aiStatusText = null;
            _isAIAnalyzing = false;
            _aiAnalyzedNodeIndex = -1;

            if (string.IsNullOrEmpty(_shaderPath)) return;

            try
            {
                if (_graphMode == GraphMode.Include)
                    BuildIncludeGraph();
                else
                    BuildDependencyGraph();

                FitAll();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderInspector] Failed to build graph: {ex.Message}");
            }
        }

        private void BuildIncludeGraph()
        {
            string json = ShaderAnalyzer.GetIncludeTree(_shaderPath);
            if (JsonHelper.GetString(json, "error") != null)
            {
                Debug.LogWarning($"[ShaderInspector] Include tree error: {JsonHelper.GetString(json, "error")}");
                return;
            }

            _includeTreeRoot = IncludeTreeNode.Parse(json);

            _graphNodes = new List<GraphNode>();
            _graphEdges = new List<GraphEdge>();
            FlattenIncludeTree(_includeTreeRoot, 0, -1);
            ComputeLayout();
        }

        private void BuildDependencyGraph()
        {
            string json = MaterialInspector.GetDependencyTree(_shaderPath);
            if (JsonHelper.GetString(json, "error") != null)
            {
                Debug.LogWarning($"[ShaderInspector] Dependency tree error: {JsonHelper.GetString(json, "error")}");
                return;
            }

            _dependencyTreeRoot = DependencyTreeNode.Parse(json);

            _graphNodes = new List<GraphNode>();
            _graphEdges = new List<GraphEdge>();
            FlattenDependencyTree(_dependencyTreeRoot, 0, -1);
            ComputeLayout();
        }

        private int FlattenIncludeTree(IncludeTreeNode treeNode, int depth, int parentIndex)
        {
            int index = _graphNodes.Count;
            var gNode = new GraphNode
            {
                name = treeNode.name,
                path = treeNode.path,
                lineCount = treeNode.lineCount,
                isRoot = (depth == 0),
                depth = depth,
                nodeType = "include"
            };
            _graphNodes.Add(gNode);

            if (parentIndex >= 0)
            {
                _graphNodes[parentIndex].childIndices.Add(index);
                _graphEdges.Add(new GraphEdge { fromIndex = parentIndex, toIndex = index });
            }

            foreach (var child in treeNode.children)
            {
                FlattenIncludeTree(child, depth + 1, index);
            }

            return index;
        }

        private int FlattenDependencyTree(DependencyTreeNode treeNode, int depth, int parentIndex)
        {
            int index = _graphNodes.Count;

            string extra = "";
            if (treeNode.type == "shader")
                extra = $"{treeNode.materialCount} materials";
            else if (treeNode.type == "texture")
                extra = treeNode.textureSize;

            var gNode = new GraphNode
            {
                name = treeNode.type == "texture" && !string.IsNullOrEmpty(treeNode.propertyName)
                    ? $"{treeNode.name}" : treeNode.name,
                path = treeNode.path,
                isRoot = (depth == 0),
                depth = depth,
                nodeType = treeNode.type,
                extraInfo = treeNode.type == "texture"
                    ? treeNode.textureSize
                    : extra
            };
            _graphNodes.Add(gNode);

            if (parentIndex >= 0)
            {
                _graphNodes[parentIndex].childIndices.Add(index);
                _graphEdges.Add(new GraphEdge { fromIndex = parentIndex, toIndex = index });
            }

            foreach (var child in treeNode.children)
            {
                FlattenDependencyTree(child, depth + 1, index);
            }

            return index;
        }

        private void ComputeLayout()
        {
            if (_graphNodes == null || _graphNodes.Count == 0) return;

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

        #region AI Analysis

        private void RunAIAnalysis(GraphNode node, string analysisType)
        {
            _isAIAnalyzing = true;
            _aiResult = "";
            _aiStatusText = null;
            _aiAnalyzedNodeIndex = _selectedNodeIndex;

            string fileContext = GatherFileContext(node);
            string prompt = BuildAIPrompt(analysisType, node);

            AIRequestHandler.SendQuery(prompt, fileContext,
                onChunk: chunk =>
                {
                    _aiResult += chunk;
                    _window.Repaint();
                },
                onComplete: fullText =>
                {
                    _aiResult = fullText ?? "No response from AI.";
                    _aiStatusText = null;
                    _isAIAnalyzing = false;
                    _window.Repaint();
                },
                onStatus: status =>
                {
                    _aiStatusText = status;
                    _window.Repaint();
                },
                language: _window.SelectedLanguage
            );
        }

        private string GatherFileContext(GraphNode node)
        {
            var parts = new List<string>();
            parts.Add($"Root Shader: {_shaderName}");
            parts.Add($"Root Path: {_shaderPath}");
            parts.Add($"Selected File: {node.name}");
            parts.Add($"Selected Path: {node.path}");
            parts.Add($"Depth in include tree: {node.depth}");

            // Parent info
            if (!node.isRoot && _graphEdges != null)
            {
                foreach (var edge in _graphEdges)
                {
                    if (edge.toIndex == _selectedNodeIndex && edge.fromIndex < _graphNodes.Count)
                    {
                        parts.Add($"Included by: {_graphNodes[edge.fromIndex].name}");
                        break;
                    }
                }
            }

            // Children info
            if (node.childIndices.Count > 0)
            {
                var childNames = new List<string>();
                foreach (int idx in node.childIndices)
                {
                    if (idx < _graphNodes.Count)
                        childNames.Add(_graphNodes[idx].name);
                }
                parts.Add($"Includes: {string.Join(", ", childNames)}");
            }

            // File content (truncated to 4000 chars)
            try
            {
                string fullPath = Path.GetFullPath(node.path);
                if (File.Exists(fullPath))
                {
                    string content = File.ReadAllText(fullPath);
                    if (content.Length > 4000)
                        content = content.Substring(0, 4000) + "\n... (truncated)";
                    parts.Add($"\n--- File Content ---\n{content}");
                }
            }
            catch { }

            return string.Join("\n", parts);
        }

        private string BuildAIPrompt(string analysisType, GraphNode node)
        {
            if (analysisType == "role")
            {
                return $"This file \"{node.name}\" is part of the shader \"{_shaderName}\"'s include hierarchy. " +
                       $"Explain what role this file plays within the shader. " +
                       $"What functionality does it provide and why is it included? " +
                       $"Explain in a way that a non-developer (e.g., a technical artist) can understand.";
            }

            // "explain"
            return $"Explain the shader include file \"{node.name}\" in a way that a non-developer can understand. " +
                   $"What does this file do? What are its main features and functions? " +
                   $"Keep the explanation clear and accessible.";
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
