using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Events tab — AND-filter search on FrameDebugBridge.Search + detail panel
    /// for the selected event (full GetEventDetail JSON). Right-side actions let
    /// the user push an event to Compare slot A/B or hand it off to AI chat.
    /// </summary>
    public class FrameEventsTab
    {
        private readonly FrameDebuggerAIWindow _window;

        // Filter state
        private string _shaderContains = "";
        private string _passContains = "";
        private string _keyword = "";
        private string _eventType = "";
        private int _minVerts;
        private int _minInstances;
        private bool _onlyBatchBreaks;
        private int _limit = 64;

        private string _lastSearchJson;
        private int _scanned, _matched;
        private bool _truncated;
        private readonly List<EventRow> _rows = new List<EventRow>();

        private int _selectedIndex = -1;
        private string _selectedDetailJson;

        private Vector2 _listScroll;
        private Vector2 _detailScroll;

        private struct EventRow
        {
            public int index;
            public string type;
            public int verts;
            public int instances;
            public string shader;
            public string pass;
            public string batchBreakCause;
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

            DrawFilterBar();
            EditorGUILayout.Space(2);

            EditorGUILayout.BeginHorizontal();
            DrawResultList();
            DrawDetailPanel();
            EditorGUILayout.EndHorizontal();
        }

        public void SelectEvent(int eventIndex)
        {
            _selectedIndex = eventIndex;
            _selectedDetailJson = FrameDebugBridge.GetEventDetail(eventIndex);
        }

        #region Filter

        private void DrawFilterBar()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Row 1
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Shader:", GUILayout.Width(55));
            _shaderContains = EditorGUILayout.TextField(_shaderContains, GUILayout.Width(160));
            EditorGUILayout.LabelField("Pass:", GUILayout.Width(40));
            _passContains = EditorGUILayout.TextField(_passContains, GUILayout.Width(120));
            EditorGUILayout.LabelField("Keyword:", GUILayout.Width(60));
            _keyword = EditorGUILayout.TextField(_keyword, GUILayout.Width(120));
            EditorGUILayout.LabelField("Type:", GUILayout.Width(40));
            _eventType = EditorGUILayout.TextField(_eventType, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            // Row 2
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Min verts:", GUILayout.Width(65));
            _minVerts = EditorGUILayout.IntField(_minVerts, GUILayout.Width(80));
            EditorGUILayout.LabelField("Min inst:", GUILayout.Width(55));
            _minInstances = EditorGUILayout.IntField(_minInstances, GUILayout.Width(60));
            _onlyBatchBreaks = GUILayout.Toggle(_onlyBatchBreaks, "Batch breaks only", GUILayout.Width(135));
            EditorGUILayout.LabelField("Limit:", GUILayout.Width(40));
            _limit = EditorGUILayout.IntField(_limit, GUILayout.Width(50));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Search", GUILayout.Width(70), GUILayout.Height(20)))
                RunSearch();
            if (GUILayout.Button("Clear", GUILayout.Width(60), GUILayout.Height(20)))
                ClearFilters();

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_lastSearchJson))
            {
                EditorGUILayout.LabelField(
                    $"matched {_matched} / scanned {_scanned}" + (_truncated ? " (truncated)" : ""),
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void ClearFilters()
        {
            _shaderContains = _passContains = _keyword = _eventType = "";
            _minVerts = _minInstances = 0;
            _onlyBatchBreaks = false;
            _limit = 64;
            _rows.Clear();
            _lastSearchJson = null;
            _selectedIndex = -1;
            _selectedDetailJson = null;
        }

        private void RunSearch()
        {
            var filter = JsonHelper.StartObject()
                .Key("shaderNameContains").Value(_shaderContains ?? "")
                .Key("passNameContains").Value(_passContains ?? "")
                .Key("keyword").Value(_keyword ?? "")
                .Key("eventType").Value(_eventType ?? "")
                .Key("minVertexCount").Value(_minVerts)
                .Key("minInstanceCount").Value(_minInstances)
                .Key("batchBreaks").Value(_onlyBatchBreaks)
                .Key("limit").Value(_limit <= 0 ? 64 : _limit)
                .ToString();

            string json = FrameDebugBridge.Search(filter);
            _lastSearchJson = json;
            _rows.Clear();
            _selectedIndex = -1;
            _selectedDetailJson = null;

            string err = JsonHelper.GetString(json, "error");
            if (!string.IsNullOrEmpty(err))
            {
                Debug.LogWarning($"[FrameDebuggerAI] Search error: {err}");
                return;
            }

            _scanned = JsonHelper.GetInt(json, "scanned", 0);
            _matched = JsonHelper.GetInt(json, "matched", 0);
            _truncated = JsonHelper.GetBool(json, "truncated", false);

            foreach (var obj in JsonHelper.GetArrayObjects(json, "matches"))
            {
                _rows.Add(new EventRow
                {
                    index = JsonHelper.GetInt(obj, "index", -1),
                    type = JsonHelper.GetString(obj, "type"),
                    verts = JsonHelper.GetInt(obj, "vertexCount", 0),
                    instances = JsonHelper.GetInt(obj, "instanceCount", 0),
                    shader = JsonHelper.GetString(obj, "shader"),
                    pass = JsonHelper.GetString(obj, "pass"),
                    batchBreakCause = JsonHelper.GetString(obj, "batchBreakCause"),
                });
            }
        }

        #endregion

        #region List

        private void DrawResultList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(360), GUILayout.ExpandHeight(true));

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, EditorStyles.helpBox);

            if (_rows.Count == 0)
            {
                EditorGUILayout.LabelField(
                    _lastSearchJson == null ? "Run a search to populate." : "No events match.",
                    EditorStyles.miniLabel);
            }
            else
            {
                foreach (var r in _rows)
                {
                    var style = (r.index == _selectedIndex)
                        ? ShaderInspectorStyles.ListItemSelected
                        : ShaderInspectorStyles.ListItem;

                    EditorGUILayout.BeginVertical(style);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"#{r.index} {r.type}", GUILayout.Width(140));
                    EditorGUILayout.LabelField($"v:{r.verts} i:{r.instances}", EditorStyles.miniLabel,
                        GUILayout.Width(100));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("View", EditorStyles.miniButton, GUILayout.Width(44)))
                        SelectEvent(r.index);
                    EditorGUILayout.EndHorizontal();

                    string sub = string.IsNullOrEmpty(r.shader) ? "(non-draw)" : $"{r.shader} / {r.pass}";
                    if (!string.IsNullOrEmpty(r.batchBreakCause) && r.batchBreakCause != "0")
                        sub += $"  ! {r.batchBreakCause}";
                    EditorGUILayout.LabelField(sub, EditorStyles.miniLabel);
                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        #endregion

        #region Detail

        private void DrawDetailPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            if (_selectedIndex < 0)
            {
                EditorGUILayout.HelpBox("Select an event on the left to see its full state.", MessageType.None);
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
            if (string.IsNullOrEmpty(_selectedDetailJson))
            {
                EditorGUILayout.LabelField("(no detail loaded)", EditorStyles.miniLabel);
            }
            else
            {
                // Unity's TextArea chokes on 15k+ chars with richtext — use SelectableLabel
                // in a CodeArea-style box for readability.
                EditorGUILayout.SelectableLabel(PrettyPrintJson(_selectedDetailJson),
                    ShaderInspectorStyles.CodeArea,
                    GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void HandOffSelected()
        {
            if (_selectedIndex < 0 || string.IsNullOrEmpty(_selectedDetailJson)) return;
            string ctx =
                $"Frame event #{_selectedIndex} detail:\n\n{_selectedDetailJson}\n\n" +
                $"Frame summary:\n{_window.LastSummaryJson ?? "(none)"}";
            string prompt =
                $"Frame event #{_selectedIndex} 의 state 를 읽고, " +
                "성능/정확성 관점에서 눈에 띄는 점 또는 개선 포인트를 알려줘.";
            _window.AskAIAboutFrame(prompt, ctx, $"Event #{_selectedIndex}");
        }

        /// <summary>Very lightweight JSON indenter — good enough for read-only display.</summary>
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
                        sb.Append(c);
                        sb.Append('\n');
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
                        sb.Append(c);
                        sb.Append('\n');
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
