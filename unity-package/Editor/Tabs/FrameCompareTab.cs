using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Compare tab — pick event indices A and B, call FrameDebugBridge.Compare,
    /// and render the diff (shader/pass, keyword set delta, RT change,
    /// geometry delta, render-state field diff, batch-break transition).
    /// </summary>
    public class FrameCompareTab
    {
        private readonly FrameDebuggerAIWindow _window;

        private int _indexA = -1;
        private int _indexB = -1;
        private string _lastCompareJson;
        private Vector2 _scroll;

        // Parsed view
        private bool _shaderChanged, _passChanged;
        private string _shaderA, _shaderB, _passA, _passB;
        private readonly List<string> _kwAdded = new List<string>();
        private readonly List<string> _kwRemoved = new List<string>();
        private string _rtA, _rtB;
        private bool _rtChanged;
        private int _vA, _vB, _iA, _iB, _nA, _nB;
        private string _causeA, _causeB;
        private readonly List<(string field, string a, string b)> _stateDiff = new List<(string, string, string)>();

        public FrameCompareTab(FrameDebuggerAIWindow window)
        {
            _window = window;
        }

        public void SetPair(int a, int b)
        {
            _indexA = a;
            _indexB = b;
            RunCompare();
        }

        public void SetSlot(int eventIndex, bool isSlotA)
        {
            if (isSlotA) _indexA = eventIndex;
            else _indexB = eventIndex;
            if (_indexA >= 0 && _indexB >= 0) RunCompare();
        }

        public void OnGUI()
        {
            if (!_window.IsCaptured)
            {
                EditorGUILayout.HelpBox("Capture a frame first, then pick two events in the Events tab.",
                    MessageType.Info);
                return;
            }

            DrawSlots();
            EditorGUILayout.Space(4);

            if (string.IsNullOrEmpty(_lastCompareJson))
            {
                EditorGUILayout.HelpBox("Set both A and B (e.g. from Events tab ‘To Compare A / B’), then Compare.",
                    MessageType.None);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawSummaryLine();
            EditorGUILayout.Space(4);
            DrawGeometryDiff();
            EditorGUILayout.Space(4);
            DrawShaderAndPass();
            EditorGUILayout.Space(4);
            DrawKeywordDelta();
            EditorGUILayout.Space(4);
            DrawRtDiff();
            EditorGUILayout.Space(4);
            DrawBatchBreakDiff();
            EditorGUILayout.Space(4);
            DrawStateDiff();
            EditorGUILayout.Space(8);
            DrawAskAIButton();
            EditorGUILayout.EndScrollView();
        }

        #region Slots

        private void DrawSlots()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("A:", GUILayout.Width(20));
            _indexA = EditorGUILayout.IntField(_indexA, GUILayout.Width(80));
            EditorGUILayout.LabelField("B:", GUILayout.Width(20));
            _indexB = EditorGUILayout.IntField(_indexB, GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            EditorGUI.BeginDisabledGroup(_indexA < 0 || _indexB < 0 || _indexA == _indexB);
            if (GUILayout.Button("Compare", GUILayout.Width(100), GUILayout.Height(20)))
                RunCompare();
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Swap A↔B", GUILayout.Width(90), GUILayout.Height(20)))
            {
                (_indexA, _indexB) = (_indexB, _indexA);
                if (_indexA >= 0 && _indexB >= 0) RunCompare();
            }
            if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(20)))
            {
                _indexA = _indexB = -1;
                _lastCompareJson = null;
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Compare

        private void RunCompare()
        {
            if (_indexA < 0 || _indexB < 0) return;

            string json = FrameDebugBridge.Compare(_indexA, _indexB);
            _lastCompareJson = json;

            string err = JsonHelper.GetString(json, "error");
            if (!string.IsNullOrEmpty(err))
            {
                Debug.LogWarning($"[FrameDebuggerAI] Compare error: {err}");
                return;
            }

            Parse(json);
        }

        private void Parse(string json)
        {
            _shaderChanged = JsonHelper.GetBool(json, "shaderChanged", false);
            _passChanged = JsonHelper.GetBool(json, "passChanged", false);

            string shader = JsonHelper.GetObject(json, "shader");
            if (!string.IsNullOrEmpty(shader))
            {
                _shaderA = JsonHelper.GetString(shader, "a");
                _shaderB = JsonHelper.GetString(shader, "b");
            }
            string pass = JsonHelper.GetObject(json, "pass");
            if (!string.IsNullOrEmpty(pass))
            {
                _passA = JsonHelper.GetString(pass, "a");
                _passB = JsonHelper.GetString(pass, "b");
            }

            _kwAdded.Clear();
            _kwRemoved.Clear();
            string kwDelta = JsonHelper.GetObject(json, "keywordsDiff");
            if (!string.IsNullOrEmpty(kwDelta))
            {
                foreach (var k in JsonHelper.GetStringArray(kwDelta, "added")) _kwAdded.Add(k);
                foreach (var k in JsonHelper.GetStringArray(kwDelta, "removed")) _kwRemoved.Add(k);
            }

            string rt = JsonHelper.GetObject(json, "renderTarget");
            if (!string.IsNullOrEmpty(rt))
            {
                _rtChanged = JsonHelper.GetBool(rt, "changed", false);
                string rtA = JsonHelper.GetObject(rt, "a");
                string rtB = JsonHelper.GetObject(rt, "b");
                _rtA = FormatRt(rtA);
                _rtB = FormatRt(rtB);
            }

            string geom = JsonHelper.GetObject(json, "geometry");
            if (!string.IsNullOrEmpty(geom))
            {
                string a = JsonHelper.GetObject(geom, "a");
                string b = JsonHelper.GetObject(geom, "b");
                if (a != null) { _vA = JsonHelper.GetInt(a, "vertexCount", 0); _iA = JsonHelper.GetInt(a, "indexCount", 0); _nA = JsonHelper.GetInt(a, "instanceCount", 0); }
                if (b != null) { _vB = JsonHelper.GetInt(b, "vertexCount", 0); _iB = JsonHelper.GetInt(b, "indexCount", 0); _nB = JsonHelper.GetInt(b, "instanceCount", 0); }
            }

            string bb = JsonHelper.GetObject(json, "batchBreak");
            if (!string.IsNullOrEmpty(bb))
            {
                _causeA = JsonHelper.GetString(bb, "a");
                _causeB = JsonHelper.GetString(bb, "b");
            }

            _stateDiff.Clear();
            foreach (var obj in JsonHelper.GetArrayObjects(json, "stateDiff"))
            {
                _stateDiff.Add((
                    JsonHelper.GetString(obj, "field") ?? "",
                    JsonHelper.GetString(obj, "a") ?? "",
                    JsonHelper.GetString(obj, "b") ?? ""));
            }
        }

        private static string FormatRt(string rtObj)
        {
            if (string.IsNullOrEmpty(rtObj)) return "";
            int w = JsonHelper.GetInt(rtObj, "width", 0);
            int h = JsonHelper.GetInt(rtObj, "height", 0);
            int c = JsonHelper.GetInt(rtObj, "count", 0);
            return $"{w}x{h} mrt={c}";
        }

        #endregion

        #region Draw sections

        private void DrawSummaryLine()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Comparing #{_indexA} vs #{_indexB}", ShaderInspectorStyles.SectionHeader);
            GUILayout.FlexibleSpace();
            if (_shaderChanged) DrawTag("shader", ShaderInspectorStyles.CyanStatus);
            if (_passChanged) DrawTag("pass", ShaderInspectorStyles.CyanStatus);
            if (_rtChanged) DrawTag("RT", ShaderInspectorStyles.YellowStatus);
            if (_kwAdded.Count > 0 || _kwRemoved.Count > 0) DrawTag("keywords", ShaderInspectorStyles.AIColor);
            if (_stateDiff.Count > 0) DrawTag($"state x{_stateDiff.Count}", ShaderInspectorStyles.RedStatus);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTag(string label, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUILayout.Label(label, EditorStyles.miniBoldLabel);
            GUI.color = old;
        }

        private void DrawGeometryDiff()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Geometry", ShaderInspectorStyles.SectionHeader);
            DrawRow("Verts",     _vA, _vB);
            DrawRow("Indices",   _iA, _iB);
            DrawRow("Instances", _nA, _nB);
            EditorGUILayout.EndVertical();
        }

        private void DrawRow(string label, int a, int b)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(80));
            EditorGUILayout.LabelField($"A: {a:N0}", GUILayout.Width(110));
            EditorGUILayout.LabelField($"B: {b:N0}", GUILayout.Width(110));
            int delta = b - a;
            if (delta != 0)
            {
                var old = GUI.color;
                GUI.color = delta > 0 ? ShaderInspectorStyles.RedStatus : ShaderInspectorStyles.GreenStatus;
                EditorGUILayout.LabelField((delta > 0 ? "+" : "") + delta.ToString("N0"), GUILayout.Width(80));
                GUI.color = old;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawShaderAndPass()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Shader / Pass", ShaderInspectorStyles.SectionHeader);
            EditorGUILayout.LabelField($"A: {_shaderA ?? ""} / {_passA ?? ""}");
            EditorGUILayout.LabelField($"B: {_shaderB ?? ""} / {_passB ?? ""}");
            EditorGUILayout.EndVertical();
        }

        private void DrawKeywordDelta()
        {
            if (_kwAdded.Count == 0 && _kwRemoved.Count == 0) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Shader keyword delta (A → B)", ShaderInspectorStyles.SectionHeader);

            if (_kwAdded.Count > 0)
            {
                var old = GUI.color;
                GUI.color = ShaderInspectorStyles.GreenStatus;
                EditorGUILayout.LabelField("added:", EditorStyles.miniBoldLabel);
                GUI.color = old;
                foreach (var k in _kwAdded)
                    EditorGUILayout.LabelField("  + " + k);
            }
            if (_kwRemoved.Count > 0)
            {
                var old = GUI.color;
                GUI.color = ShaderInspectorStyles.RedStatus;
                EditorGUILayout.LabelField("removed:", EditorStyles.miniBoldLabel);
                GUI.color = old;
                foreach (var k in _kwRemoved)
                    EditorGUILayout.LabelField("  - " + k);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawRtDiff()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Render target", ShaderInspectorStyles.SectionHeader);
            EditorGUILayout.LabelField($"A: {_rtA ?? ""}");
            EditorGUILayout.LabelField($"B: {_rtB ?? ""}");
            if (_rtChanged)
            {
                var old = GUI.color;
                GUI.color = ShaderInspectorStyles.YellowStatus;
                EditorGUILayout.LabelField("→ RT transition occurred between these events", EditorStyles.miniBoldLabel);
                GUI.color = old;
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawBatchBreakDiff()
        {
            if (string.IsNullOrEmpty(_causeA) && string.IsNullOrEmpty(_causeB)) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Batch break cause", ShaderInspectorStyles.SectionHeader);
            EditorGUILayout.LabelField($"A: {_causeA ?? "-"}");
            EditorGUILayout.LabelField($"B: {_causeB ?? "-"}");
            EditorGUILayout.EndVertical();
        }

        private void DrawStateDiff()
        {
            if (_stateDiff.Count == 0) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Render state field diff", ShaderInspectorStyles.SectionHeader);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Field", EditorStyles.miniBoldLabel, GUILayout.Width(180));
            EditorGUILayout.LabelField("A", EditorStyles.miniBoldLabel, GUILayout.Width(180));
            EditorGUILayout.LabelField("B", EditorStyles.miniBoldLabel, GUILayout.Width(180));
            EditorGUILayout.EndHorizontal();

            foreach (var d in _stateDiff)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(d.field, GUILayout.Width(180));
                EditorGUILayout.LabelField(d.a, GUILayout.Width(180));
                EditorGUILayout.LabelField(d.b, GUILayout.Width(180));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawAskAIButton()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("왜 다른지 AI에게 물어보기", GUILayout.Width(260), GUILayout.Height(24)))
            {
                string ctx = $"Frame event diff JSON (A=#{_indexA}, B=#{_indexB}):\n\n{_lastCompareJson}";
                string prompt =
                    $"event #{_indexA} 와 #{_indexB} 의 차이를 설명해줘. " +
                    "특히 배치가 왜 안 묶였는지, 추가/제거된 keyword나 state 변경이 이 이벤트가 별도 draw call로 분리된 원인인지 짚어줘. " +
                    "가능하다면 하나의 draw call로 합치는 방법도 제안해줘.";
                _window.AskAIAboutFrame(prompt, ctx, $"Compare #{_indexA}↔#{_indexB}");
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        #endregion
    }
}
