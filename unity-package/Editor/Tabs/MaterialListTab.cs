using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Materials tab: material list (left), detail panel with textures and AI (right).
    /// </summary>
    public class MaterialListTab
    {
        private readonly ShaderInspectorWindow _window;

        // Material list
        private List<MaterialEntry> _allMaterials = new List<MaterialEntry>();
        private List<MaterialEntry> _filteredMaterials = new List<MaterialEntry>();
        private int _selectedIndex = -1;
        private Vector2 _listScrollPos;
        private Vector2 _detailScrollPos;

        // Search/Filter
        private string _searchText = "";
        private int _filterMode; // 0=All, 1=Assets Only
        private static readonly string[] FilterOptions = { "All", "Assets Only" };

        // Detail cache
        private string _cachedDetailPath;
        private List<TextureSlot> _cachedTextures;
        private List<PropertyEntry> _cachedProperties;
        private List<string> _cachedKeywords;

        // AI
        private bool _isAIAnalyzing;
        private string _aiResult = "";
        private string _aiStatusText;

        // Texture foldout
        private bool _texturesFoldout = true;
        private bool _propertiesFoldout;
        private bool _keywordsFoldout;

        private class MaterialEntry
        {
            public string name;
            public string path;
            public string shaderName;
            public int renderQueue;
        }

        private class TextureSlot
        {
            public string propertyName;
            public string textureName;
            public string texturePath;
            public string textureSize;
            public Texture texture;
        }

        private class PropertyEntry
        {
            public string name;
            public string type;
            public string value;
        }

        public MaterialListTab(ShaderInspectorWindow window)
        {
            _window = window;
            Refresh();
        }

        public void OnGUI()
        {
            DrawSearchBar();

            EditorGUILayout.BeginHorizontal();

            // Left panel - material list (35%)
            EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(200, Screen.width * 0.35f)));
            DrawMaterialList();
            EditorGUILayout.EndVertical();

            // Splitter
            var splitterRect = EditorGUILayout.GetControlRect(false, GUILayout.Width(2));
            EditorGUI.DrawRect(splitterRect, ShaderInspectorStyles.SplitterColor);

            // Right panel - detail (65%)
            EditorGUILayout.BeginVertical();
            DrawMaterialDetail();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        #region Search Bar

        private void DrawSearchBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
            string newSearch = EditorGUILayout.TextField(_searchText, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(100));
            if (newSearch != _searchText)
            {
                _searchText = newSearch;
                ApplyFilter();
            }

            EditorGUILayout.LabelField("Filter:", GUILayout.Width(38));
            int newFilter = EditorGUILayout.Popup(_filterMode, FilterOptions, EditorStyles.toolbarPopup,
                GUILayout.Width(100));
            if (newFilter != _filterMode)
            {
                _filterMode = newFilter;
                ApplyFilter();
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField($"{_filteredMaterials.Count} materials", EditorStyles.miniLabel,
                GUILayout.Width(80));

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Material List (Left Panel)

        private void DrawMaterialList()
        {
            _listScrollPos = EditorGUILayout.BeginScrollView(_listScrollPos);

            if (_filteredMaterials.Count == 0)
            {
                EditorGUILayout.LabelField("No materials found.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                for (int i = 0; i < _filteredMaterials.Count; i++)
                {
                    var mat = _filteredMaterials[i];
                    bool isSelected = i == _selectedIndex;
                    var style = isSelected ? ShaderInspectorStyles.ListItemSelected : ShaderInspectorStyles.ListItem;

                    EditorGUILayout.BeginHorizontal(style);

                    if (GUILayout.Button(mat.name, EditorStyles.label, GUILayout.ExpandWidth(true)))
                    {
                        _selectedIndex = i;
                        _cachedDetailPath = null;
                        _aiResult = "";
                        _window.SetAIContext(mat.path, mat.name);
                    }

                    // Shader name badge
                    var oldColor = GUI.color;
                    GUI.color = ShaderInspectorStyles.DimText;
                    string shortShader = mat.shaderName;
                    if (shortShader.Length > 20) shortShader = ".." + shortShader.Substring(shortShader.Length - 18);
                    GUILayout.Label(shortShader, EditorStyles.miniLabel, GUILayout.Width(120));
                    GUI.color = oldColor;

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        #endregion

        #region Material Detail (Right Panel)

        private void DrawMaterialDetail()
        {
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);

            if (_selectedIndex < 0 || _selectedIndex >= _filteredMaterials.Count)
            {
                EditorGUILayout.LabelField("Select a material from the list.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
                return;
            }

            var entry = _filteredMaterials[_selectedIndex];

            // Load detail if not cached
            if (_cachedDetailPath != entry.path)
                LoadMaterialDetail(entry);

            // Header
            EditorGUILayout.LabelField(entry.name, ShaderInspectorStyles.HeaderLabel);
            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Path: " + entry.path, EditorStyles.miniLabel);
            if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Material>(entry.path);
                if (asset != null) EditorGUIUtility.PingObject(asset);
            }
            if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Material>(entry.path);
                if (asset != null) Selection.activeObject = asset;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField($"Shader: {entry.shaderName}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Render Queue: {entry.renderQueue}", EditorStyles.miniLabel);

            // Textures section
            EditorGUILayout.Space(8);
            DrawTexturesSection();

            // Properties section
            EditorGUILayout.Space(4);
            DrawPropertiesSection();

            // Keywords section
            EditorGUILayout.Space(4);
            DrawKeywordsSection();

            // AI section
            EditorGUILayout.Space(8);
            DrawAISection(entry);

            EditorGUILayout.EndScrollView();
        }

        private void DrawTexturesSection()
        {
            int texCount = _cachedTextures?.Count ?? 0;
            _texturesFoldout = EditorGUILayout.Foldout(_texturesFoldout,
                $"Textures ({texCount})", true, EditorStyles.foldoutHeader);

            if (!_texturesFoldout || _cachedTextures == null) return;

            if (texCount == 0)
            {
                EditorGUILayout.LabelField("  No textures assigned.", EditorStyles.miniLabel);
                return;
            }

            EditorGUI.indentLevel++;
            foreach (var slot in _cachedTextures)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                EditorGUILayout.BeginHorizontal();

                // Texture preview thumbnail
                if (slot.texture != null)
                {
                    Rect thumbRect = GUILayoutUtility.GetRect(48, 48, GUILayout.Width(48));
                    EditorGUI.DrawPreviewTexture(thumbRect, slot.texture, null, ScaleMode.ScaleToFit);
                }

                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(slot.propertyName, EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(slot.textureName, EditorStyles.miniLabel);
                EditorGUILayout.LabelField(slot.textureSize, EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    if (slot.texture != null) EditorGUIUtility.PingObject(slot.texture);
                }
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    if (slot.texture != null) Selection.activeObject = slot.texture;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawPropertiesSection()
        {
            int propCount = _cachedProperties?.Count ?? 0;
            _propertiesFoldout = EditorGUILayout.Foldout(_propertiesFoldout,
                $"Properties ({propCount})", true, EditorStyles.foldoutHeader);

            if (!_propertiesFoldout || _cachedProperties == null) return;

            EditorGUI.indentLevel++;
            foreach (var prop in _cachedProperties)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(prop.name, EditorStyles.miniLabel, GUILayout.Width(150));
                var oldColor = GUI.color;
                GUI.color = ShaderInspectorStyles.DimText;
                EditorGUILayout.LabelField(prop.type, EditorStyles.miniLabel, GUILayout.Width(60));
                GUI.color = oldColor;
                EditorGUILayout.LabelField(prop.value, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
        }

        private void DrawKeywordsSection()
        {
            int kwCount = _cachedKeywords?.Count ?? 0;
            _keywordsFoldout = EditorGUILayout.Foldout(_keywordsFoldout,
                $"Keywords ({kwCount})", true, EditorStyles.foldoutHeader);

            if (!_keywordsFoldout || _cachedKeywords == null || kwCount == 0) return;

            EditorGUI.indentLevel++;
            foreach (var kw in _cachedKeywords)
                EditorGUILayout.LabelField(kw, EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        private void DrawAISection(MaterialEntry entry)
        {
            EditorGUILayout.LabelField("AI Analysis", ShaderInspectorStyles.SectionHeader);

            EditorGUI.BeginDisabledGroup(_isAIAnalyzing || !_window.IsAIConnected);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Analyze Material", GUILayout.Height(26)))
                RunAIAnalysis(entry, "analyze");
            if (GUILayout.Button("Optimize Textures", GUILayout.Height(26)))
                RunAIAnalysis(entry, "optimize_textures");
            if (GUILayout.Button("Find Issues", GUILayout.Height(26)))
                RunAIAnalysis(entry, "issues");

            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            if (!_window.IsAIConnected)
                EditorGUILayout.HelpBox("AI not available. Ensure MCP server is connected.", MessageType.Info);

            // AI result display
            bool showWaiting = _isAIAnalyzing && string.IsNullOrEmpty(_aiResult);
            if (showWaiting)
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
        }

        #endregion

        #region Data Loading

        private void LoadMaterialDetail(MaterialEntry entry)
        {
            _cachedDetailPath = entry.path;
            _cachedTextures = new List<TextureSlot>();
            _cachedProperties = new List<PropertyEntry>();
            _cachedKeywords = new List<string>();

            var material = AssetDatabase.LoadAssetAtPath<Material>(entry.path);
            if (material == null || material.shader == null) return;

            var shader = material.shader;
            int propCount = shader.GetPropertyCount();

            for (int i = 0; i < propCount; i++)
            {
                string propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);

                try
                {
                    if (propType == ShaderPropertyType.Texture)
                    {
                        var tex = material.GetTexture(propName);
                        if (tex != null)
                        {
                            _cachedTextures.Add(new TextureSlot
                            {
                                propertyName = propName,
                                textureName = tex.name,
                                texturePath = AssetDatabase.GetAssetPath(tex),
                                textureSize = $"{tex.width}x{tex.height}",
                                texture = tex
                            });
                        }
                    }
                    else
                    {
                        string value = "";
                        switch (propType)
                        {
                            case ShaderPropertyType.Float:
                            case ShaderPropertyType.Range:
                                value = material.GetFloat(propName).ToString("F3");
                                break;
                            case ShaderPropertyType.Color:
                                var c = material.GetColor(propName);
                                value = $"({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})";
                                break;
                            case ShaderPropertyType.Vector:
                                var v = material.GetVector(propName);
                                value = $"({v.x:F2}, {v.y:F2}, {v.z:F2}, {v.w:F2})";
                                break;
                            case ShaderPropertyType.Int:
                                value = material.GetInt(propName).ToString();
                                break;
                        }

                        _cachedProperties.Add(new PropertyEntry
                        {
                            name = propName,
                            type = propType.ToString(),
                            value = value
                        });
                    }
                }
                catch { }
            }

            _cachedKeywords = new List<string>(material.shaderKeywords);
        }

        #endregion

        #region AI Analysis

        private void RunAIAnalysis(MaterialEntry entry, string analysisType)
        {
            _isAIAnalyzing = true;
            _aiResult = "";
            _aiStatusText = null;

            string context = GatherMaterialContext(entry);
            string prompt = BuildAIPrompt(analysisType, entry);

            AIRequestHandler.SendQuery(prompt, context,
                onChunk: chunk => { _aiResult += chunk; _window.Repaint(); },
                onComplete: fullText =>
                {
                    _aiResult = fullText ?? "No response from AI.";
                    _aiStatusText = null;
                    _isAIAnalyzing = false;
                    _window.Repaint();
                },
                onStatus: status => { _aiStatusText = status; _window.Repaint(); },
                language: _window.SelectedLanguage
            );
        }

        private string GatherMaterialContext(MaterialEntry entry)
        {
            var parts = new List<string>
            {
                $"Material: {entry.name}",
                $"Path: {entry.path}",
                $"Shader: {entry.shaderName}",
                $"Render Queue: {entry.renderQueue}"
            };

            if (_cachedTextures != null && _cachedTextures.Count > 0)
            {
                parts.Add($"\nTextures ({_cachedTextures.Count}):");
                foreach (var t in _cachedTextures)
                    parts.Add($"  {t.propertyName}: {t.textureName} ({t.textureSize}) [{t.texturePath}]");
            }

            if (_cachedKeywords != null && _cachedKeywords.Count > 0)
                parts.Add("\nKeywords: " + string.Join(", ", _cachedKeywords));

            if (_cachedProperties != null && _cachedProperties.Count > 0)
            {
                parts.Add($"\nProperties ({_cachedProperties.Count}):");
                foreach (var p in _cachedProperties)
                    parts.Add($"  {p.name} ({p.type}): {p.value}");
            }

            return string.Join("\n", parts);
        }

        private string BuildAIPrompt(string analysisType, MaterialEntry entry)
        {
            switch (analysisType)
            {
                case "optimize_textures":
                    return $"Analyze the texture usage of material '{entry.name}' (shader: {entry.shaderName}). " +
                           "Check texture sizes, formats, and suggest optimizations for memory and performance. " +
                           "Consider texture compression, resolution, and whether any textures could be combined or removed.";
                case "issues":
                    return $"Analyze material '{entry.name}' (shader: {entry.shaderName}) for potential issues. " +
                           "Check for: missing textures, unusual property values, keyword conflicts, " +
                           "render queue problems, and any other common material setup mistakes.";
                default: // "analyze"
                    return $"Provide a comprehensive analysis of material '{entry.name}' (shader: {entry.shaderName}). " +
                           "Explain what this material does visually, its texture setup, key properties, " +
                           "and any recommendations for improvement.";
            }
        }

        #endregion

        #region Public API

        public void Refresh()
        {
            _allMaterials.Clear();

            string[] guids = AssetDatabase.FindAssets("t:Material");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/")) continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;

                _allMaterials.Add(new MaterialEntry
                {
                    name = mat.name,
                    path = path,
                    shaderName = mat.shader != null ? mat.shader.name : "None",
                    renderQueue = mat.renderQueue
                });
            }

            _allMaterials = _allMaterials.OrderBy(m => m.name).ToList();
            ApplyFilter();
        }

        public void SelectMaterial(string path)
        {
            for (int i = 0; i < _filteredMaterials.Count; i++)
            {
                if (_filteredMaterials[i].path == path)
                {
                    _selectedIndex = i;
                    _cachedDetailPath = null;
                    _aiResult = "";
                    return;
                }
            }
        }

        #endregion

        #region Helpers

        private void ApplyFilter()
        {
            _filteredMaterials = new List<MaterialEntry>();

            foreach (var m in _allMaterials)
            {
                if (!string.IsNullOrEmpty(_searchText))
                {
                    string lower = _searchText.ToLowerInvariant();
                    if (!m.name.ToLowerInvariant().Contains(lower) &&
                        !m.shaderName.ToLowerInvariant().Contains(lower) &&
                        !m.path.ToLowerInvariant().Contains(lower))
                        continue;
                }

                if (_filterMode == 1 && !m.path.StartsWith("Assets/")) continue;

                _filteredMaterials.Add(m);
            }

            if (_selectedIndex >= _filteredMaterials.Count)
                _selectedIndex = _filteredMaterials.Count > 0 ? 0 : -1;
        }

        #endregion
    }
}
