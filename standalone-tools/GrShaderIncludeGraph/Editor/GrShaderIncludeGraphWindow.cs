using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ShaderIncludeGraph.Editor
{
    /// <summary>
    /// Standalone shader #include hierarchy visualization window.
    /// Drop this into any Unity project's Editor folder to use.
    /// Menu: Window > Shader Include Graph, or right-click a shader asset.
    /// </summary>
    public class ShaderIncludeGraphWindow : EditorWindow
    {
        // Current shader context
        private string _shaderPath;
        private string _shaderName;
        private Shader _currentShader;

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
        private const float MinZoom = 0.3f;
        private const float MaxZoom = 2f;

        // Resizable info panel
        private float _infoPanelWidth = 280f;
        private const float MinInfoPanelWidth = 180f;
        private const float MaxInfoPanelWidth = 500f;
        private const float SplitterHitWidth = 6f;
        private bool _isDraggingSplitter;

        // Scroll for info panel
        private Vector2 _infoScrollPos;

        // Cached file analysis
        private int _analyzedNodeIndex = -1;
        private ShaderFileInfo _fileInfo;
        private bool _showProperties = true;
        private bool _showKeywords = true;
        private bool _showFunctions = true;
        private bool _showStructs = true;
        private bool _showDefines;
        private bool _showCodePreview;

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

        #region Menu Items

        [MenuItem("Window/Shader Include Graph")]
        private static void OpenEmpty()
        {
            var window = GetWindow<ShaderIncludeGraphWindow>("Include Graph");
            window.minSize = new Vector2(600, 400);
            window.Show();
            window.Focus();
        }

        [MenuItem("Assets/View Shader Include Graph", true)]
        private static bool ValidateOpenFromAsset()
        {
            var obj = Selection.activeObject;
            if (obj == null) return false;
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".shader" || ext == ".cginc" || ext == ".hlsl" || ext == ".glslinc" || ext == ".compute";
        }

        [MenuItem("Assets/View Shader Include Graph")]
        private static void OpenFromAsset()
        {
            var obj = Selection.activeObject;
            if (obj == null) return;
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return;
            Open(path, obj.name);
        }

        #endregion

        /// <summary>Open the include graph window for a specific shader.</summary>
        public static void Open(string shaderPath, string shaderName)
        {
            var window = GetWindow<ShaderIncludeGraphWindow>("Include Graph");
            window.minSize = new Vector2(600, 400);
            window._shaderPath = shaderPath;
            window._shaderName = shaderName;
            window._currentShader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            window.RebuildGraph();
            window.Show();
            window.Focus();
        }

        private void OnGUI()
        {
            DrawToolbar();

            if (_treeRoot == null)
            {
                EditorGUILayout.LabelField("No include graph data. Select a shader from the toolbar or right-click a shader asset.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandHeight(true));
                return;
            }

            Rect lastRect = GUILayoutUtility.GetLastRect();
            float topY = lastRect.yMax;
            float windowWidth = position.width;
            float remainingHeight = position.height - topY;
            if (remainingHeight < 100) remainingHeight = 100;

            GUILayoutUtility.GetRect(windowWidth, remainingHeight);

            // Clamp info panel width
            _infoPanelWidth = Mathf.Clamp(_infoPanelWidth, MinInfoPanelWidth, Mathf.Min(MaxInfoPanelWidth, windowWidth - 200));

            float splitterWidth = 2f;
            float graphWidth = windowWidth - _infoPanelWidth - splitterWidth;
            if (graphWidth < 100) graphWidth = 100;

            Rect graphRect = new Rect(0, topY, graphWidth, remainingHeight);
            Rect splitterRect = new Rect(graphRect.xMax, topY, splitterWidth, remainingHeight);
            Rect infoRect = new Rect(splitterRect.xMax, topY, _infoPanelWidth, remainingHeight);

            // Splitter drag handling
            Rect splitterHitRect = new Rect(splitterRect.x - SplitterHitWidth * 0.5f, splitterRect.y,
                SplitterHitWidth, splitterRect.height);
            EditorGUIUtility.AddCursorRect(splitterHitRect, MouseCursor.ResizeHorizontal);
            HandleSplitterDrag(splitterHitRect);

            DrawGraphArea(graphRect);
            EditorGUI.DrawRect(splitterRect, Styles.SplitterColor);
            DrawInfoPanel(infoRect);
        }

        #region Toolbar

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            string label = string.IsNullOrEmpty(_shaderName) ? "No shader selected" : _shaderName;
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.MaxWidth(300));
            GUILayout.FlexibleSpace();

            // Shader picker via object field
            var newShader = (Shader)EditorGUILayout.ObjectField(
                GUIContent.none, _currentShader, typeof(Shader), false, GUILayout.Width(180));
            if (newShader != _currentShader)
            {
                _currentShader = newShader;
                if (newShader != null)
                {
                    string path = AssetDatabase.GetAssetPath(newShader);
                    if (!string.IsNullOrEmpty(path))
                    {
                        _shaderPath = path;
                        _shaderName = newShader.name;
                        RebuildGraph();
                    }
                }
                else
                {
                    _shaderPath = null;
                    _shaderName = null;
                    _treeRoot = null;
                    _graphNodes = null;
                    _graphEdges = null;
                    Repaint();
                }
            }

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
            EditorGUI.DrawRect(graphRect, Styles.GraphBackground);
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
                Color bgColor = node.isRoot ? Styles.GraphNodeRoot :
                    isSelected ? Styles.GraphNodeSelected : Styles.GraphNodeNormal;

                EditorGUI.DrawRect(r, bgColor);
                DrawRectBorder(r, isSelected ? Styles.CyanAccent : Styles.GraphNodeBorder, 1f);

                var oldColor = GUI.color;
                GUI.color = Styles.GraphNodeText;
                GUI.Label(new Rect(r.x + 6, r.y + 4 * _zoom, r.width - 12, 18 * _zoom),
                    TruncateString(node.name, 22), EditorStyles.boldLabel);
                GUI.color = Styles.GraphNodeSubText;
                GUI.Label(new Rect(r.x + 6, r.y + 22 * _zoom, r.width - 12, 16 * _zoom),
                    $"{node.lineCount} lines", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }
        }

        private void DrawEdges(Rect graphRect)
        {
            if (_graphEdges == null || _graphNodes == null) return;
            Color lineColor = Styles.GraphConnection;

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

            if (_selectedNodeIndex >= 0 && _selectedNodeIndex < _graphNodes?.Count)
            {
                var node = _graphNodes[_selectedNodeIndex];

                // Lazy analyze on selection change
                if (_analyzedNodeIndex != _selectedNodeIndex)
                {
                    _analyzedNodeIndex = _selectedNodeIndex;
                    _fileInfo = ShaderIncludeAnalyzer.AnalyzeFile(node.path);
                }

                DrawNodeHeader(node);
                DrawFileOverview(node);
                DrawActionButtons(node);

                if (_fileInfo != null && _fileInfo.error == null)
                {
                    DrawShaderInfo();
                    DrawEntryPoints();
                    DrawTags();
                    DrawKeywords();
                    DrawProperties();
                    DrawFunctions();
                    DrawStructs();
                    DrawDefines();
                    DrawChildrenList(node);
                    DrawCodePreview();
                }
            }
            else
            {
                DrawEmptyState();
            }

            EditorGUILayout.Space(8);
            DrawControls();

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawNodeHeader(GraphNode node)
        {
            // File type badge + name
            if (_fileInfo != null && _fileInfo.error == null)
            {
                EditorGUILayout.BeginHorizontal();
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = GetFileTypeColor(_fileInfo.extension);
                GUILayout.Label(_fileInfo.FileTypeLabel, Styles.Badge, GUILayout.ExpandWidth(false));
                GUI.backgroundColor = oldBg;
                if (node.isRoot)
                {
                    GUI.backgroundColor = Styles.CyanAccent;
                    GUILayout.Label("ROOT", Styles.Badge, GUILayout.ExpandWidth(false));
                    GUI.backgroundColor = oldBg;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.LabelField(node.name, Styles.NodeTitle);

            if (_fileInfo != null && !string.IsNullOrEmpty(_fileInfo.shaderName))
            {
                var oldColor = GUI.color;
                GUI.color = Styles.DimText;
                EditorGUILayout.LabelField($"\"{_fileInfo.shaderName}\"", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }

            EditorGUILayout.Space(2);
        }

        private void DrawFileOverview(GraphNode node)
        {
            EditorGUILayout.LabelField("Path:", EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(node.path, EditorStyles.wordWrappedMiniLabel,
                GUILayout.Height(EditorGUIUtility.singleLineHeight * 1.5f));

            EditorGUILayout.Space(4);

            // Stats row
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (_fileInfo != null && _fileInfo.error == null)
            {
                DrawStatRow("Lines", node.lineCount.ToString());
                DrawStatRow("Size", _fileInfo.FileSizeLabel);
                DrawStatRow("Depth", node.depth.ToString());
                DrawStatRow("Includes", node.childIndices.Count.ToString());
            }
            else
            {
                DrawStatRow("Lines", node.lineCount.ToString());
                DrawStatRow("Depth", node.depth.ToString());
                DrawStatRow("Includes", node.childIndices.Count.ToString());
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4);
        }

        private void DrawStatRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            var oldColor = GUI.color;
            GUI.color = Styles.DimText;
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(60));
            GUI.color = Color.white;
            EditorGUILayout.LabelField(value, EditorStyles.miniLabel);
            GUI.color = oldColor;
            EditorGUILayout.EndHorizontal();
        }

        private void DrawActionButtons(GraphNode node)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open in Editor", GUILayout.Height(22)))
                OpenFileInEditor(node.path);
            if (GUILayout.Button("Ping", GUILayout.Height(22), GUILayout.Width(44)))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(node.path);
                if (asset != null) EditorGUIUtility.PingObject(asset);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void DrawShaderInfo()
        {
            if (_fileInfo.extension != ".shader" && _fileInfo.extension != ".compute") return;

            DrawSectionSeparator();
            EditorGUILayout.LabelField("Shader Structure", Styles.SectionHeader);

            if (_fileInfo.subShaderCount > 0)
                DrawStatRow("SubShaders", _fileInfo.subShaderCount.ToString());
            if (_fileInfo.passCount > 0)
                DrawStatRow("Passes", _fileInfo.passCount.ToString());
            if (!string.IsNullOrEmpty(_fileInfo.target))
                DrawStatRow("Target", _fileInfo.target);
            if (_fileInfo.programBlocks.Count > 0)
                DrawStatRow("Language", string.Join(", ", _fileInfo.programBlocks));

            EditorGUILayout.Space(2);
        }

        private void DrawEntryPoints()
        {
            if (_fileInfo.entryPoints.Count == 0) return;

            DrawSectionSeparator();
            EditorGUILayout.LabelField("Entry Points", Styles.SectionHeader);

            foreach (var ep in _fileInfo.entryPoints)
            {
                EditorGUILayout.BeginHorizontal();
                var oldColor = GUI.color;
                GUI.color = Styles.CyanAccent;
                EditorGUILayout.LabelField(ep.Key, EditorStyles.miniLabel, GUILayout.Width(60));
                GUI.color = Color.white;
                EditorGUILayout.LabelField(ep.Value, EditorStyles.miniLabel);
                GUI.color = oldColor;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);
        }

        private void DrawTags()
        {
            if (_fileInfo.tags.Count == 0 && _fileInfo.lightModes.Count == 0) return;

            DrawSectionSeparator();
            EditorGUILayout.LabelField("Tags", Styles.SectionHeader);

            foreach (var tag in _fileInfo.tags)
                DrawStatRow(tag.Key, tag.Value);

            if (_fileInfo.lightModes.Count > 0)
                DrawStatRow("LightMode", string.Join(", ", _fileInfo.lightModes));

            EditorGUILayout.Space(2);
        }

        private void DrawKeywords()
        {
            if (_fileInfo.keywords.Count == 0) return;

            DrawSectionSeparator();
            _showKeywords = EditorGUILayout.Foldout(_showKeywords,
                $"Keywords ({_fileInfo.keywords.Count})", true, Styles.FoldoutHeader);

            if (!_showKeywords) return;

            foreach (var kw in _fileInfo.keywords)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                var oldColor = GUI.color;
                GUI.color = new Color(1f, 0.8f, 0.3f);
                EditorGUILayout.LabelField(kw.directive, EditorStyles.miniLabel);
                GUI.color = Color.white;
                if (kw.variants.Count > 0)
                {
                    string variantText = string.Join("  ", kw.variants);
                    EditorGUILayout.LabelField(variantText, Styles.WordWrapMini);
                }
                GUI.color = oldColor;
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(2);
        }

        private void DrawProperties()
        {
            if (_fileInfo.properties.Count == 0) return;

            DrawSectionSeparator();
            _showProperties = EditorGUILayout.Foldout(_showProperties,
                $"Properties ({_fileInfo.properties.Count})", true, Styles.FoldoutHeader);

            if (!_showProperties) return;

            foreach (var prop in _fileInfo.properties)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(prop.name, EditorStyles.miniLabel, GUILayout.MinWidth(80));
                var oldColor = GUI.color;
                GUI.color = Styles.DimText;
                EditorGUILayout.LabelField(prop.type, EditorStyles.miniLabel, GUILayout.Width(50));
                GUI.color = oldColor;
                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(prop.displayName) && prop.displayName != prop.name)
                {
                    var oldC = GUI.color;
                    GUI.color = Styles.DimText;
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField($"\"{prop.displayName}\"", EditorStyles.miniLabel);
                    EditorGUI.indentLevel--;
                    GUI.color = oldC;
                }
            }

            EditorGUILayout.Space(2);
        }

        private void DrawFunctions()
        {
            if (_fileInfo.functions.Count == 0) return;

            DrawSectionSeparator();
            _showFunctions = EditorGUILayout.Foldout(_showFunctions,
                $"Functions ({_fileInfo.functions.Count})", true, Styles.FoldoutHeader);

            if (!_showFunctions) return;

            foreach (var func in _fileInfo.functions)
            {
                EditorGUILayout.BeginHorizontal();
                var oldColor = GUI.color;
                GUI.color = new Color(0.4f, 0.8f, 1f);
                EditorGUILayout.LabelField(func.returnType, EditorStyles.miniLabel, GUILayout.Width(50));
                GUI.color = Color.white;
                EditorGUILayout.LabelField(func.name, EditorStyles.miniLabel);
                GUI.color = oldColor;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);
        }

        private void DrawStructs()
        {
            if (_fileInfo.structs.Count == 0 && _fileInfo.cbuffers.Count == 0) return;

            DrawSectionSeparator();
            _showStructs = EditorGUILayout.Foldout(_showStructs,
                $"Structs & Buffers ({_fileInfo.structs.Count + _fileInfo.cbuffers.Count})", true, Styles.FoldoutHeader);

            if (!_showStructs) return;

            foreach (var s in _fileInfo.structs)
            {
                var oldColor = GUI.color;
                GUI.color = new Color(0.6f, 1f, 0.6f);
                EditorGUILayout.LabelField($"struct {s}", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }

            foreach (var cb in _fileInfo.cbuffers)
            {
                var oldColor = GUI.color;
                GUI.color = new Color(1f, 0.7f, 0.5f);
                EditorGUILayout.LabelField($"CBUFFER {cb}", EditorStyles.miniLabel);
                GUI.color = oldColor;
            }

            EditorGUILayout.Space(2);
        }

        private void DrawDefines()
        {
            if (_fileInfo.defines.Count == 0) return;

            DrawSectionSeparator();
            _showDefines = EditorGUILayout.Foldout(_showDefines,
                $"Defines ({_fileInfo.defines.Count})", true, Styles.FoldoutHeader);

            if (!_showDefines) return;

            foreach (var d in _fileInfo.defines)
            {
                var oldColor = GUI.color;
                GUI.color = new Color(0.9f, 0.6f, 0.9f);
                EditorGUILayout.LabelField(d, EditorStyles.miniLabel);
                GUI.color = oldColor;
            }

            EditorGUILayout.Space(2);
        }

        private void DrawChildrenList(GraphNode node)
        {
            if (node.childIndices.Count == 0) return;

            DrawSectionSeparator();
            EditorGUILayout.LabelField($"Includes ({node.childIndices.Count})", Styles.SectionHeader);

            foreach (int childIdx in node.childIndices)
            {
                if (childIdx >= _graphNodes.Count) continue;
                var child = _graphNodes[childIdx];
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(child.name, EditorStyles.miniLabel);
                if (GUILayout.Button("Go", EditorStyles.miniButton, GUILayout.Width(28)))
                {
                    _selectedNodeIndex = childIdx;
                    Repaint();
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawCodePreview()
        {
            if (_fileInfo == null || string.IsNullOrEmpty(_fileInfo.codePreview)) return;

            DrawSectionSeparator();
            _showCodePreview = EditorGUILayout.Foldout(_showCodePreview,
                "Code Preview", true, Styles.FoldoutHeader);

            if (!_showCodePreview) return;

            EditorGUILayout.BeginVertical(Styles.CodeBox);
            EditorGUILayout.LabelField(_fileInfo.codePreview, Styles.CodeText);
            EditorGUILayout.EndVertical();
        }

        private void DrawEmptyState()
        {
            EditorGUILayout.LabelField("Click a node to view details.", EditorStyles.centeredGreyMiniLabel);
            if (_graphNodes != null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Graph Stats", Styles.SectionHeader);
                EditorGUILayout.LabelField($"Total nodes: {_graphNodes.Count}");
                EditorGUILayout.LabelField($"Total edges: {_graphEdges?.Count ?? 0}");

                // Summary of file types
                int shaderCount = 0, cgincCount = 0, hlslCount = 0, otherCount = 0;
                foreach (var n in _graphNodes)
                {
                    string ext = Path.GetExtension(n.name).ToLowerInvariant();
                    if (ext == ".shader") shaderCount++;
                    else if (ext == ".cginc") cgincCount++;
                    else if (ext == ".hlsl") hlslCount++;
                    else otherCount++;
                }
                EditorGUILayout.Space(4);
                if (shaderCount > 0) EditorGUILayout.LabelField($"  .shader: {shaderCount}", EditorStyles.miniLabel);
                if (cgincCount > 0) EditorGUILayout.LabelField($"  .cginc: {cgincCount}", EditorStyles.miniLabel);
                if (hlslCount > 0) EditorGUILayout.LabelField($"  .hlsl: {hlslCount}", EditorStyles.miniLabel);
                if (otherCount > 0) EditorGUILayout.LabelField($"  other: {otherCount}", EditorStyles.miniLabel);
            }
        }

        private void DrawControls()
        {
            EditorGUILayout.LabelField("Controls", Styles.SectionHeader);
            EditorGUILayout.LabelField("Scroll: Zoom  |  Drag: Pan", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Click: Select  |  Double-click: Open", EditorStyles.miniLabel);
        }

        private static void DrawSectionSeparator()
        {
            EditorGUILayout.Space(2);
            Rect r = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.3f, 0.3f, 0.3f));
            EditorGUILayout.Space(2);
        }

        private static Color GetFileTypeColor(string ext)
        {
            switch (ext)
            {
                case ".shader": return new Color(0.3f, 0.6f, 1f);
                case ".cginc": return new Color(0.3f, 0.8f, 0.5f);
                case ".hlsl": return new Color(0.9f, 0.5f, 0.3f);
                case ".compute": return new Color(0.8f, 0.3f, 0.8f);
                default: return new Color(0.6f, 0.6f, 0.6f);
            }
        }

        #endregion

        #region Build Graph

        private void RebuildGraph()
        {
            _selectedNodeIndex = -1;
            _analyzedNodeIndex = -1;
            _fileInfo = null;
            _graphNodes = null;
            _graphEdges = null;
            _treeRoot = null;

            if (string.IsNullOrEmpty(_shaderPath)) return;

            try
            {
                _treeRoot = ShaderIncludeAnalyzer.BuildIncludeTree(_shaderPath);
                if (_treeRoot == null) return;

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

            float availWidth = position.width - _infoPanelWidth - 40;
            float availHeight = position.height - 60;
            if (availWidth > 0 && availHeight > 0)
            {
                float zoomX = availWidth / contentWidth;
                float zoomY = availHeight / contentHeight;
                _zoom = Mathf.Clamp(Mathf.Min(zoomX, zoomY), MinZoom, 1.5f);
            }
        }

        #endregion

        #region Splitter

        private void HandleSplitterDrag(Rect splitterHitRect)
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && splitterHitRect.Contains(e.mousePosition))
                    {
                        _isDraggingSplitter = true;
                        e.Use();
                    }
                    break;

                case EventType.MouseDrag:
                    if (_isDraggingSplitter)
                    {
                        _infoPanelWidth -= e.delta.x;
                        _infoPanelWidth = Mathf.Clamp(_infoPanelWidth, MinInfoPanelWidth,
                            Mathf.Min(MaxInfoPanelWidth, position.width - 200));
                        e.Use();
                        Repaint();
                    }
                    break;

                case EventType.MouseUp:
                    if (_isDraggingSplitter)
                    {
                        _isDraggingSplitter = false;
                        e.Use();
                    }
                    break;
            }
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

        #region Inline Styles

        private static class Styles
        {
            // Graph colors
            public static readonly Color GraphNodeNormal = new Color(0.22f, 0.22f, 0.28f);
            public static readonly Color GraphNodeRoot = new Color(0.18f, 0.32f, 0.48f);
            public static readonly Color GraphNodeSelected = new Color(0.30f, 0.45f, 0.65f);
            public static readonly Color GraphConnection = new Color(0.5f, 0.5f, 0.6f, 0.8f);
            public static readonly Color GraphBackground = new Color(0.16f, 0.16f, 0.18f);
            public static readonly Color GraphNodeBorder = new Color(0.4f, 0.4f, 0.5f);
            public static readonly Color GraphNodeText = new Color(0.9f, 0.9f, 0.9f);
            public static readonly Color GraphNodeSubText = new Color(0.6f, 0.6f, 0.7f);
            public static readonly Color CyanAccent = new Color(0.3f, 0.8f, 0.9f);
            public static readonly Color SplitterColor = new Color(0.15f, 0.15f, 0.15f);
            public static readonly Color DimText = new Color(0.6f, 0.6f, 0.6f);

            private static GUIStyle _sectionHeader;
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

            private static GUIStyle _nodeTitle;
            public static GUIStyle NodeTitle
            {
                get
                {
                    if (_nodeTitle == null)
                    {
                        _nodeTitle = new GUIStyle(EditorStyles.boldLabel)
                        {
                            fontSize = 13,
                            wordWrap = true
                        };
                    }
                    return _nodeTitle;
                }
            }

            private static GUIStyle _badge;
            public static GUIStyle Badge
            {
                get
                {
                    if (_badge == null)
                    {
                        _badge = new GUIStyle(EditorStyles.miniLabel)
                        {
                            alignment = TextAnchor.MiddleCenter,
                            fontSize = 9,
                            fontStyle = FontStyle.Bold,
                            padding = new RectOffset(6, 6, 2, 2),
                            margin = new RectOffset(0, 4, 2, 2)
                        };
                        var tex = new Texture2D(1, 1);
                        tex.SetPixel(0, 0, new Color(0.25f, 0.25f, 0.3f));
                        tex.Apply();
                        _badge.normal.background = tex;
                        _badge.normal.textColor = Color.white;
                    }
                    return _badge;
                }
            }

            private static GUIStyle _foldoutHeader;
            public static GUIStyle FoldoutHeader
            {
                get
                {
                    if (_foldoutHeader == null)
                    {
                        _foldoutHeader = new GUIStyle(EditorStyles.foldout)
                        {
                            fontStyle = FontStyle.Bold,
                            fontSize = 11
                        };
                    }
                    return _foldoutHeader;
                }
            }

            private static GUIStyle _wordWrapMini;
            public static GUIStyle WordWrapMini
            {
                get
                {
                    if (_wordWrapMini == null)
                    {
                        _wordWrapMini = new GUIStyle(EditorStyles.miniLabel)
                        {
                            wordWrap = true
                        };
                    }
                    return _wordWrapMini;
                }
            }

            private static GUIStyle _codeBox;
            public static GUIStyle CodeBox
            {
                get
                {
                    if (_codeBox == null)
                    {
                        _codeBox = new GUIStyle(EditorStyles.helpBox)
                        {
                            padding = new RectOffset(6, 6, 4, 4)
                        };
                        var tex = new Texture2D(1, 1);
                        tex.SetPixel(0, 0, new Color(0.12f, 0.12f, 0.14f));
                        tex.Apply();
                        _codeBox.normal.background = tex;
                    }
                    return _codeBox;
                }
            }

            private static GUIStyle _codeText;
            public static GUIStyle CodeText
            {
                get
                {
                    if (_codeText == null)
                    {
                        _codeText = new GUIStyle(EditorStyles.label)
                        {
                            wordWrap = true,
                            richText = false,
                            fontSize = 10,
                            padding = new RectOffset(2, 2, 2, 2)
                        };
                        _codeText.normal.textColor = new Color(0.78f, 0.78f, 0.78f);
                        var monoFont = Font.CreateDynamicFontFromOSFont("Consolas", 10);
                        if (monoFont != null) _codeText.font = monoFont;
                    }
                    return _codeText;
                }
            }
        }

        #endregion
    }
}
