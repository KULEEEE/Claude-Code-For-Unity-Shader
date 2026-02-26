using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Shaders tab: shader list (left), detail + analysis buttons (right).
    /// Supports search, sort, filter, basic analysis (local), and AI analysis (Claude).
    /// </summary>
    public class ShaderBrowserTab
    {
        private readonly ShaderInspectorWindow _window;

        // Shader list
        private ShaderListData _shaderList;
        private List<ShaderInfo> _filteredShaders = new List<ShaderInfo>();
        private int _selectedIndex = -1;
        private Vector2 _listScrollPos;
        private Vector2 _detailScrollPos;

        // Search/Filter
        private string _searchText = "";
        private int _sortMode; // 0=Name, 1=Variants, 2=Path
        private int _filterMode; // 0=All, 1=Assets Only, 2=Packages Only
        private static readonly string[] SortOptions = { "Name", "Variants", "Path" };
        private static readonly string[] FilterOptions = { "All", "Assets Only", "Packages Only" };

        // Analysis results
        private string _lastResultTitle = "";
        private string _lastResult = "";
        private bool _isAnalyzing;
        private bool _isAIAnalyzing;
        private string _aiResult = "";

        public ShaderBrowserTab(ShaderInspectorWindow window)
        {
            _window = window;
            Refresh();
        }

        public void OnGUI()
        {
            DrawSearchBar();

            EditorGUILayout.BeginHorizontal();

            // Left panel - shader list (35%)
            EditorGUILayout.BeginVertical(GUILayout.Width(Mathf.Max(200, Screen.width * 0.35f)));
            DrawShaderList();
            EditorGUILayout.EndVertical();

            // Splitter
            var splitterRect = EditorGUILayout.GetControlRect(false, GUILayout.Width(2));
            EditorGUI.DrawRect(splitterRect, ShaderInspectorStyles.SplitterColor);

            // Right panel - detail (65%)
            EditorGUILayout.BeginVertical();
            DrawShaderDetail();
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

            EditorGUILayout.LabelField("Sort:", GUILayout.Width(32));
            int newSort = EditorGUILayout.Popup(_sortMode, SortOptions, EditorStyles.toolbarPopup,
                GUILayout.Width(80));
            if (newSort != _sortMode)
            {
                _sortMode = newSort;
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

            EditorGUILayout.EndHorizontal();
        }

        #endregion

        #region Shader List (Left Panel)

        private void DrawShaderList()
        {
            _listScrollPos = EditorGUILayout.BeginScrollView(_listScrollPos);

            if (_filteredShaders == null || _filteredShaders.Count == 0)
            {
                EditorGUILayout.LabelField("No shaders found.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                for (int i = 0; i < _filteredShaders.Count; i++)
                {
                    var shader = _filteredShaders[i];
                    bool isSelected = i == _selectedIndex;
                    var style = isSelected ? ShaderInspectorStyles.ListItemSelected : ShaderInspectorStyles.ListItem;

                    EditorGUILayout.BeginHorizontal(style);

                    // Shader name
                    if (GUILayout.Button(shader.name, EditorStyles.label, GUILayout.ExpandWidth(true)))
                    {
                        _selectedIndex = i;
                        _lastResult = "";
                        _lastResultTitle = "";
                        _aiResult = "";
                        _window.SetAIContext(shader.path, shader.name);
                    }

                    // Variant count badge
                    var oldColor = GUI.color;
                    GUI.color = ShaderInspectorStyles.GetVariantColor(shader.variantCount);
                    string badge = shader.variantCount >= 0 ? shader.variantCount.ToString() : "?";
                    GUILayout.Label(badge, EditorStyles.miniLabel, GUILayout.Width(45));
                    GUI.color = oldColor;

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        #endregion

        #region Shader Detail (Right Panel)

        private void DrawShaderDetail()
        {
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);

            if (_selectedIndex < 0 || _selectedIndex >= _filteredShaders.Count)
            {
                EditorGUILayout.LabelField("Select a shader from the list.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
                return;
            }

            var shader = _filteredShaders[_selectedIndex];

            // Shader info header
            EditorGUILayout.LabelField("Shader: " + shader.name, ShaderInspectorStyles.HeaderLabel);
            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Path: " + shader.path, EditorStyles.miniLabel);
            if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Shader>(shader.path);
                if (asset != null) EditorGUIUtility.PingObject(asset);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Passes: {shader.passCount}  |  Variants: ", GUILayout.Width(150));
            var oldColor = GUI.color;
            GUI.color = ShaderInspectorStyles.GetVariantColor(shader.variantCount);
            EditorGUILayout.LabelField(shader.variantCount >= 0 ? shader.variantCount.ToString() : "Unknown",
                EditorStyles.boldLabel, GUILayout.Width(60));
            GUI.color = oldColor;
            EditorGUILayout.LabelField("  |  Supported: " + (shader.isSupported ? "Yes" : "No"));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            // Basic analysis buttons
            EditorGUILayout.LabelField("Basic Analysis (instant)", ShaderInspectorStyles.SectionHeader);
            EditorGUI.BeginDisabledGroup(_isAnalyzing);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Compile", GUILayout.Height(26)))
                RunBasicAnalysis("Compile", () => ShaderAnalyzer.CompileShader(shader.path));
            if (GUILayout.Button("Variants", GUILayout.Height(26)))
                RunBasicAnalysis("Variants", () => ShaderAnalyzer.GetVariantInfo(shader.path));
            if (GUILayout.Button("Properties", GUILayout.Height(26)))
                RunBasicAnalysis("Properties", () => ShaderAnalyzer.GetShaderProperties(shader.path));
            if (GUILayout.Button("Code", GUILayout.Height(26)))
                RunBasicAnalysis("Code", () => ShaderAnalyzer.GetShaderCode(shader.path));
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(4);

            // AI analysis buttons
            EditorGUILayout.LabelField("AI Analysis (via Claude)", ShaderInspectorStyles.SectionHeader);
            EditorGUI.BeginDisabledGroup(_isAIAnalyzing || !_window.IsAIConnected);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Error Analysis", GUILayout.Height(26)))
                RunAIAnalysis(shader, "error_analysis");
            if (GUILayout.Button("Optimize", GUILayout.Height(26)))
                RunAIAnalysis(shader, "optimization");
            if (GUILayout.Button("Explain Code", GUILayout.Height(26)))
                RunAIAnalysis(shader, "explain");
            if (GUILayout.Button("Diagnose", GUILayout.Height(26)))
                RunAIAnalysis(shader, "diagnose");

            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            if (!_window.IsAIConnected)
            {
                EditorGUILayout.HelpBox("AI not available. Ensure MCP server is connected.", MessageType.Info);
            }

            EditorGUILayout.Space(8);

            // Results area
            if (_isAnalyzing)
            {
                EditorGUILayout.LabelField("Analyzing...", EditorStyles.centeredGreyMiniLabel);
            }
            else if (!string.IsNullOrEmpty(_lastResult))
            {
                EditorGUILayout.LabelField(_lastResultTitle, ShaderInspectorStyles.SectionHeader);
                EditorGUILayout.TextArea(_lastResult, ShaderInspectorStyles.ResultArea,
                    GUILayout.ExpandHeight(true));
            }

            if (_isAIAnalyzing)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("AI is analyzing... (10~30 seconds)",
                    EditorStyles.centeredGreyMiniLabel);
            }
            else if (!string.IsNullOrEmpty(_aiResult))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("AI Analysis Result", ShaderInspectorStyles.SectionHeader);
                EditorGUILayout.TextArea(_aiResult, ShaderInspectorStyles.AIResponseArea,
                    GUILayout.ExpandHeight(true));
            }

            EditorGUILayout.EndScrollView();
        }

        #endregion

        #region Analysis

        private void RunBasicAnalysis(string title, Func<string> analysisFunc)
        {
            _isAnalyzing = true;
            _lastResultTitle = title + " Result";
            try
            {
                string json = analysisFunc();
                _lastResult = FormatJson(json);
            }
            catch (Exception ex)
            {
                _lastResult = "Error: " + ex.Message;
            }
            finally
            {
                _isAnalyzing = false;
            }
        }

        private void RunAIAnalysis(ShaderInfo shader, string analysisType)
        {
            _isAIAnalyzing = true;
            _aiResult = "";

            string shaderContext = GatherShaderContext(shader);
            string prompt = BuildAIPrompt(analysisType, shader.name);

            AIRequestHandler.SendQuery(prompt, shaderContext, response =>
            {
                _aiResult = response ?? "No response from AI.";
                _isAIAnalyzing = false;
                _window.Repaint();
            });
        }

        private string GatherShaderContext(ShaderInfo shader)
        {
            try
            {
                var parts = new List<string>();
                parts.Add($"Shader: {shader.name}");
                parts.Add($"Path: {shader.path}");
                parts.Add($"Variants: {shader.variantCount}, Passes: {shader.passCount}");

                // Add compile info
                string compileJson = ShaderAnalyzer.CompileShader(shader.path);
                var compileResult = CompileResult.Parse(compileJson);
                if (compileResult.errors.Count > 0)
                    parts.Add("Errors: " + string.Join("; ", compileResult.errors));
                if (compileResult.warnings.Count > 0)
                    parts.Add("Warnings: " + string.Join("; ", compileResult.warnings));

                // Add variant info
                string variantJson = ShaderAnalyzer.GetVariantInfo(shader.path);
                var variantInfo = VariantInfo.Parse(variantJson);
                if (variantInfo.globalKeywords.Count > 0)
                    parts.Add("Global Keywords: " + string.Join(", ", variantInfo.globalKeywords));
                if (variantInfo.localKeywords.Count > 0)
                    parts.Add("Local Keywords: " + string.Join(", ", variantInfo.localKeywords));

                // Add shader code (truncated for context)
                string codeJson = ShaderAnalyzer.GetShaderCode(shader.path);
                var codeData = ShaderCodeData.Parse(codeJson);
                if (!string.IsNullOrEmpty(codeData.code))
                {
                    string code = codeData.code;
                    if (code.Length > 4000) code = code.Substring(0, 4000) + "\n... (truncated)";
                    parts.Add("\nShader Code:\n" + code);
                }

                return string.Join("\n", parts);
            }
            catch (Exception ex)
            {
                return $"Shader: {shader.name}\nPath: {shader.path}\n(Error gathering context: {ex.Message})";
            }
        }

        private string BuildAIPrompt(string analysisType, string shaderName)
        {
            switch (analysisType)
            {
                case "error_analysis":
                    return $"Analyze the compilation errors of shader '{shaderName}'. " +
                           "Explain each error's root cause and suggest specific fixes. " +
                           "If there are no errors, confirm the shader compiles correctly.";
                case "optimization":
                    return $"Analyze shader '{shaderName}' for performance optimization. " +
                           "Focus on: variant count reduction, keyword consolidation, " +
                           "instruction count optimization, and render pass efficiency. " +
                           "Provide specific, actionable suggestions with code examples.";
                case "explain":
                    return $"Explain shader '{shaderName}' in simple terms that a non-programmer (artist/designer) " +
                           "can understand. Describe what the shader does visually, its main parameters, " +
                           "and how it affects rendering.";
                case "diagnose":
                    return $"Perform a comprehensive diagnostic of shader '{shaderName}'. " +
                           "Check for: compilation issues, excessive variants, unused properties, " +
                           "compatibility problems, performance concerns, and best practice violations. " +
                           "Generate a structured report with severity levels.";
                default:
                    return $"Analyze shader '{shaderName}'.";
            }
        }

        #endregion

        #region Public API

        public void Refresh()
        {
            try
            {
                string json = ShaderAnalyzer.ListAllShaders();
                _shaderList = ShaderListData.Parse(json);
                _window.UpdateShaderCount(_shaderList.totalCount);
                ApplyFilter();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderInspector] Failed to refresh shaders: {ex.Message}");
            }
        }

        public void SelectShader(string path)
        {
            if (_filteredShaders == null) return;
            for (int i = 0; i < _filteredShaders.Count; i++)
            {
                if (_filteredShaders[i].path == path)
                {
                    _selectedIndex = i;
                    _lastResult = "";
                    _aiResult = "";
                    return;
                }
            }
        }

        #endregion

        #region Helpers

        private void ApplyFilter()
        {
            _filteredShaders = new List<ShaderInfo>();
            if (_shaderList == null) return;

            foreach (var s in _shaderList.shaders)
            {
                // Filter by search text
                if (!string.IsNullOrEmpty(_searchText))
                {
                    string lower = _searchText.ToLowerInvariant();
                    if (!s.name.ToLowerInvariant().Contains(lower) &&
                        !s.path.ToLowerInvariant().Contains(lower))
                        continue;
                }

                // Filter by location
                if (_filterMode == 1 && !s.path.StartsWith("Assets/")) continue;
                if (_filterMode == 2 && !s.path.StartsWith("Packages/")) continue;

                _filteredShaders.Add(s);
            }

            // Sort
            switch (_sortMode)
            {
                case 0: _filteredShaders.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase)); break;
                case 1: _filteredShaders.Sort((a, b) => b.variantCount.CompareTo(a.variantCount)); break;
                case 2: _filteredShaders.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase)); break;
            }

            // Reset selection if out of range
            if (_selectedIndex >= _filteredShaders.Count)
                _selectedIndex = _filteredShaders.Count > 0 ? 0 : -1;
        }

        private static string FormatJson(string json)
        {
            // Simple JSON pretty-printer
            if (string.IsNullOrEmpty(json)) return "";

            var sb = new System.Text.StringBuilder();
            int indent = 0;
            bool inString = false;

            foreach (char c in json)
            {
                if (c == '\\' && inString) { sb.Append(c); continue; }

                if (c == '"') inString = !inString;

                if (!inString)
                {
                    if (c == '{' || c == '[')
                    {
                        sb.Append(c);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (c == '}' || c == ']')
                    {
                        sb.AppendLine();
                        indent--;
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                        continue;
                    }
                    if (c == ',')
                    {
                        sb.Append(c);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (c == ':')
                    {
                        sb.Append(": ");
                        continue;
                    }
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        #endregion
    }
}
