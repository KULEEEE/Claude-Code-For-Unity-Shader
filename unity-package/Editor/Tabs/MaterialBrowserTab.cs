using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Materials tab: material list grouped by shader (left), material detail (right).
    /// </summary>
    public class MaterialBrowserTab
    {
        private readonly ShaderInspectorWindow _window;

        private MaterialListData _materialList;
        private List<MaterialInfo> _filteredMaterials = new List<MaterialInfo>();
        private int _selectedIndex = -1;
        private Vector2 _listScrollPos;
        private Vector2 _detailScrollPos;
        private string _searchText = "";

        // Detail data (loaded on selection)
        private MaterialDetailData _detailData;

        // Grouped view
        private Dictionary<string, List<MaterialInfo>> _groupedMaterials = new Dictionary<string, List<MaterialInfo>>();
        private HashSet<string> _expandedGroups = new HashSet<string>();

        public MaterialBrowserTab(ShaderInspectorWindow window)
        {
            _window = window;
            Refresh();
        }

        public void OnGUI()
        {
            // Search bar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
            string newSearch = EditorGUILayout.TextField(_searchText, EditorStyles.toolbarSearchField);
            if (newSearch != _searchText)
            {
                _searchText = newSearch;
                ApplyFilter();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            // Left panel - material list (40%)
            EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(220, Screen.width * 0.4f)));
            DrawMaterialList();
            EditorGUILayout.EndVertical();

            var splitterRect = EditorGUILayout.GetControlRect(false, GUILayout.Width(2));
            EditorGUI.DrawRect(splitterRect, ShaderInspectorStyles.SplitterColor);

            // Right panel - detail (60%)
            EditorGUILayout.BeginVertical();
            DrawMaterialDetail();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        #region Material List

        private void DrawMaterialList()
        {
            _listScrollPos = EditorGUILayout.BeginScrollView(_listScrollPos);

            if (_groupedMaterials.Count == 0)
            {
                EditorGUILayout.LabelField("No materials found.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                foreach (var kvp in _groupedMaterials)
                {
                    string shaderName = kvp.Key;
                    var materials = kvp.Value;
                    bool expanded = _expandedGroups.Contains(shaderName);

                    EditorGUILayout.BeginHorizontal();
                    bool newExpanded = EditorGUILayout.Foldout(expanded, $"{shaderName} ({materials.Count})", true);
                    EditorGUILayout.EndHorizontal();

                    if (newExpanded != expanded)
                    {
                        if (newExpanded) _expandedGroups.Add(shaderName);
                        else _expandedGroups.Remove(shaderName);
                    }

                    if (newExpanded)
                    {
                        foreach (var mat in materials)
                        {
                            int globalIdx = _filteredMaterials.IndexOf(mat);
                            bool isSelected = globalIdx == _selectedIndex;
                            var style = isSelected ? ShaderInspectorStyles.ListItemSelected : ShaderInspectorStyles.ListItem;

                            EditorGUILayout.BeginHorizontal(style);
                            GUILayout.Space(20);
                            if (GUILayout.Button(mat.name, EditorStyles.label, GUILayout.ExpandWidth(true)))
                            {
                                _selectedIndex = globalIdx;
                                LoadMaterialDetail(mat.path);
                            }
                            EditorGUILayout.EndHorizontal();
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        #endregion

        #region Material Detail

        private void DrawMaterialDetail()
        {
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);

            if (_selectedIndex < 0 || _selectedIndex >= _filteredMaterials.Count || _detailData == null)
            {
                EditorGUILayout.LabelField("Select a material from the list.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField("Material: " + _detailData.name, ShaderInspectorStyles.HeaderLabel);
            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Shader: " + _detailData.shaderName);
            if (GUILayout.Button("Go to Shader", EditorStyles.miniButton, GUILayout.Width(90)))
            {
                // Find shader path from shader name
                string json = ShaderAnalyzer.ListAllShaders(_detailData.shaderName);
                var list = ShaderListData.Parse(json);
                if (list.shaders.Count > 0)
                    _window.NavigateToShader(list.shaders[0].path, list.shaders[0].name);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField($"Render Queue: {_detailData.renderQueue}  |  Instancing: {(_detailData.enableInstancing ? "Yes" : "No")}  |  Passes: {_detailData.passCount}");

            EditorGUILayout.Space(4);

            // Keywords
            if (_detailData.keywords.Count > 0)
            {
                EditorGUILayout.LabelField("Keywords", ShaderInspectorStyles.SectionHeader);
                EditorGUILayout.LabelField(string.Join("  ", _detailData.keywords), EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.Space(4);

            // Properties
            if (_detailData.properties.Count > 0)
            {
                EditorGUILayout.LabelField($"Properties ({_detailData.properties.Count})", ShaderInspectorStyles.SectionHeader);
                foreach (var prop in _detailData.properties)
                {
                    EditorGUILayout.LabelField($"  {prop.name}  ({prop.type})", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.Space(8);

            // Action buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open in Inspector", GUILayout.Height(26)))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(_detailData.path);
                if (mat != null) Selection.activeObject = mat;
            }

            EditorGUI.BeginDisabledGroup(!_window.IsAIConnected);
            if (GUILayout.Button("AI: Analyze Material", GUILayout.Height(26)))
            {
                string context = $"Material: {_detailData.name}\nShader: {_detailData.shaderName}\n" +
                                 $"Keywords: {string.Join(", ", _detailData.keywords)}\n" +
                                 $"Properties: {_detailData.properties.Count}\nRender Queue: {_detailData.renderQueue}";
                _window.AskAI($"Analyze this material and suggest improvements:\n{context}", context);
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        #endregion

        #region Data

        private void LoadMaterialDetail(string path)
        {
            try
            {
                string json = MaterialInspector.GetMaterialInfo(path);
                _detailData = MaterialDetailData.Parse(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderInspector] Failed to load material: {ex.Message}");
                _detailData = null;
            }
        }

        public void Refresh()
        {
            try
            {
                string json = MaterialInspector.ListAllMaterials();
                _materialList = MaterialListData.Parse(json);
                ApplyFilter();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderInspector] Failed to refresh materials: {ex.Message}");
            }
        }

        public void SelectMaterial(string path)
        {
            if (_filteredMaterials == null) return;
            for (int i = 0; i < _filteredMaterials.Count; i++)
            {
                if (_filteredMaterials[i].path == path)
                {
                    _selectedIndex = i;
                    LoadMaterialDetail(path);
                    // Expand the group
                    _expandedGroups.Add(_filteredMaterials[i].shaderName);
                    return;
                }
            }
        }

        private void ApplyFilter()
        {
            _filteredMaterials = new List<MaterialInfo>();
            _groupedMaterials = new Dictionary<string, List<MaterialInfo>>();
            if (_materialList == null) return;

            foreach (var m in _materialList.materials)
            {
                if (!string.IsNullOrEmpty(_searchText))
                {
                    string lower = _searchText.ToLowerInvariant();
                    if (!m.name.ToLowerInvariant().Contains(lower) &&
                        !m.shaderName.ToLowerInvariant().Contains(lower) &&
                        !m.path.ToLowerInvariant().Contains(lower))
                        continue;
                }

                _filteredMaterials.Add(m);

                if (!_groupedMaterials.ContainsKey(m.shaderName))
                    _groupedMaterials[m.shaderName] = new List<MaterialInfo>();
                _groupedMaterials[m.shaderName].Add(m);
            }

            if (_selectedIndex >= _filteredMaterials.Count)
                _selectedIndex = _filteredMaterials.Count > 0 ? 0 : -1;
        }

        #endregion
    }
}
