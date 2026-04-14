using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Overview tab — pretty-prints FrameDebugBridge.Summary() JSON:
    /// totals, event-type histogram, per-shader stats, RT transitions,
    /// batch-break causes, top-N hotspots. Each row has an "Ask AI" handoff.
    /// </summary>
    public class FrameOverviewTab
    {
        private readonly FrameDebuggerAIWindow _window;
        private Vector2 _scroll;
        private string _summaryJson;

        // Parsed snapshots (refreshed when summary changes).
        private int _eventCount;
        private long _totalVerts, _totalIndices, _totalInstances, _totalDrawCalls;
        private readonly List<(string type, int count)> _typeHistogram = new List<(string, int)>();
        private readonly List<ShaderRow> _shaderRows = new List<ShaderRow>();
        private readonly List<RtRangeRow> _rtRows = new List<RtRangeRow>();
        private readonly List<BatchBreakRow> _batchRows = new List<BatchBreakRow>();
        private readonly List<HotspotRow> _hotspotRows = new List<HotspotRow>();

        private struct ShaderRow { public string shader, pass; public int events; public long totalVerts; public int firstIndex, lastIndex; }
        private struct RtRangeRow { public string rt; public int startIndex, endIndex; }
        private struct BatchBreakRow { public string cause; public int count, sampleIndex; }
        private struct HotspotRow { public int index; public string type; public int verts, indices, instances; public long cost; }

        public FrameOverviewTab(FrameDebuggerAIWindow window)
        {
            _window = window;
        }

        public void OnSummaryChanged(string json)
        {
            _summaryJson = json;
            Parse();
        }

        public void OnGUI()
        {
            if (string.IsNullOrEmpty(_window.LastSummaryJson))
            {
                EditorGUILayout.HelpBox(
                    "No frame captured yet.\n\n" +
                    "1) Enter Play Mode in Unity\n" +
                    "2) Click 'Capture Frame' in the toolbar",
                    MessageType.Info);
                return;
            }

            // Sync if the window captured a new summary while we were on another tab
            if (_summaryJson != _window.LastSummaryJson)
            {
                _summaryJson = _window.LastSummaryJson;
                Parse();
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawHeaderActions();
            EditorGUILayout.Space(4);

            DrawTotals();
            EditorGUILayout.Space(6);

            DrawEventTypeHistogram();
            EditorGUILayout.Space(6);

            DrawHotspots();
            EditorGUILayout.Space(6);

            DrawShaders();
            EditorGUILayout.Space(6);

            DrawRtTransitions();
            EditorGUILayout.Space(6);

            DrawBatchBreaks();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeaderActions()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Frame summary — {_eventCount} events", ShaderInspectorStyles.SectionHeader);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Ask AI about this frame", GUILayout.Width(220), GUILayout.Height(22)))
            {
                HandOffFullSummary();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTotals()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Totals", ShaderInspectorStyles.SectionHeader);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"Verts: {_totalVerts:N0}");
            EditorGUILayout.LabelField($"Indices: {_totalIndices:N0}");
            EditorGUILayout.LabelField($"Instances: {_totalInstances:N0}");
            EditorGUILayout.LabelField($"DrawCalls: {_totalDrawCalls:N0}");
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawEventTypeHistogram()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Event types", ShaderInspectorStyles.SectionHeader);

            if (_typeHistogram.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                int max = 1;
                foreach (var t in _typeHistogram) if (t.count > max) max = t.count;

                foreach (var t in _typeHistogram)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(t.type, GUILayout.Width(140));
                    EditorGUILayout.LabelField(t.count.ToString("N0"), GUILayout.Width(60));
                    var rect = GUILayoutUtility.GetRect(0f, 14f, GUILayout.ExpandWidth(true));
                    float frac = (float)t.count / max;
                    var barRect = new Rect(rect.x, rect.y + 2, rect.width * frac, rect.height - 4);
                    EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));
                    EditorGUI.DrawRect(barRect, ShaderInspectorStyles.CyanStatus);
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawHotspots()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Top cost hotspots", ShaderInspectorStyles.SectionHeader);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("cost = max(verts, idx/3) * max(1, instances)",
                EditorStyles.miniLabel, GUILayout.Width(300));
            EditorGUILayout.EndHorizontal();

            if (_hotspotRows.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                // Header row
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Idx", EditorStyles.miniBoldLabel, GUILayout.Width(50));
                EditorGUILayout.LabelField("Type", EditorStyles.miniBoldLabel, GUILayout.Width(120));
                EditorGUILayout.LabelField("Verts", EditorStyles.miniBoldLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField("Idx", EditorStyles.miniBoldLabel, GUILayout.Width(70));
                EditorGUILayout.LabelField("Inst", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                EditorGUILayout.LabelField("Cost", EditorStyles.miniBoldLabel, GUILayout.Width(100));
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                foreach (var h in _hotspotRows)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(h.index.ToString(), GUILayout.Width(50));
                    EditorGUILayout.LabelField(h.type ?? "", GUILayout.Width(120));
                    EditorGUILayout.LabelField(h.verts.ToString("N0"), GUILayout.Width(70));
                    EditorGUILayout.LabelField(h.indices.ToString("N0"), GUILayout.Width(70));
                    EditorGUILayout.LabelField(h.instances.ToString("N0"), GUILayout.Width(60));
                    EditorGUILayout.LabelField(h.cost.ToString("N0"), GUILayout.Width(100));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Inspect", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        _window.GoToEvent(h.index);
                    }
                    if (GUILayout.Button("Ask AI", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        HandOffEvent(h.index);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawShaders()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("By shader (top 64)", ShaderInspectorStyles.SectionHeader);

            if (_shaderRows.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Shader", EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(true));
                EditorGUILayout.LabelField("Pass", EditorStyles.miniBoldLabel, GUILayout.Width(120));
                EditorGUILayout.LabelField("Events", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                EditorGUILayout.LabelField("Verts", EditorStyles.miniBoldLabel, GUILayout.Width(90));
                EditorGUILayout.LabelField("Range", EditorStyles.miniBoldLabel, GUILayout.Width(80));
                EditorGUILayout.EndHorizontal();

                foreach (var s in _shaderRows)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(s.shader ?? "", GUILayout.ExpandWidth(true));
                    EditorGUILayout.LabelField(s.pass ?? "", GUILayout.Width(120));
                    EditorGUILayout.LabelField(s.events.ToString(), GUILayout.Width(60));
                    EditorGUILayout.LabelField(s.totalVerts.ToString("N0"), GUILayout.Width(90));
                    EditorGUILayout.LabelField($"{s.firstIndex}\u2013{s.lastIndex}", GUILayout.Width(80));
                    if (GUILayout.Button("First", EditorStyles.miniButton, GUILayout.Width(50)))
                        _window.GoToEvent(s.firstIndex);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRtTransitions()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Render target transitions", ShaderInspectorStyles.SectionHeader);

            if (_rtRows.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var r in _rtRows)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(r.rt, GUILayout.ExpandWidth(true));
                    EditorGUILayout.LabelField($"[{r.startIndex}\u2013{r.endIndex}]", GUILayout.Width(120));
                    if (GUILayout.Button("Start", EditorStyles.miniButton, GUILayout.Width(50)))
                        _window.GoToEvent(r.startIndex);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawBatchBreaks()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Batch-break causes", ShaderInspectorStyles.SectionHeader);

            if (_batchRows.Count == 0)
            {
                EditorGUILayout.LabelField("(none — or all batched successfully)", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var b in _batchRows)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(b.cause, GUILayout.ExpandWidth(true));
                    EditorGUILayout.LabelField($"x{b.count}", GUILayout.Width(60));
                    if (GUILayout.Button($"Sample @ {b.sampleIndex}", EditorStyles.miniButton, GUILayout.Width(110)))
                        _window.GoToEvent(b.sampleIndex);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        #region Parse

        private void Parse()
        {
            _typeHistogram.Clear();
            _shaderRows.Clear();
            _rtRows.Clear();
            _batchRows.Clear();
            _hotspotRows.Clear();
            _eventCount = 0;
            _totalVerts = _totalIndices = _totalInstances = _totalDrawCalls = 0;

            if (string.IsNullOrEmpty(_summaryJson)) return;

            _eventCount = JsonHelper.GetInt(_summaryJson, "eventCount", 0);

            string totals = JsonHelper.GetObject(_summaryJson, "totals");
            if (!string.IsNullOrEmpty(totals))
            {
                _totalVerts = JsonHelper.GetInt(totals, "vertices", 0);
                _totalIndices = JsonHelper.GetInt(totals, "indices", 0);
                _totalInstances = JsonHelper.GetInt(totals, "instances", 0);
                _totalDrawCalls = JsonHelper.GetInt(totals, "drawCalls", 0);
            }

            foreach (var obj in JsonHelper.GetArrayObjects(_summaryJson, "eventTypes"))
            {
                _typeHistogram.Add((
                    JsonHelper.GetString(obj, "type") ?? "",
                    JsonHelper.GetInt(obj, "count", 0)));
            }

            foreach (var obj in JsonHelper.GetArrayObjects(_summaryJson, "byShader"))
            {
                _shaderRows.Add(new ShaderRow
                {
                    shader = JsonHelper.GetString(obj, "shader"),
                    pass = JsonHelper.GetString(obj, "pass"),
                    events = JsonHelper.GetInt(obj, "events", 0),
                    totalVerts = JsonHelper.GetInt(obj, "totalVerts", 0),
                    firstIndex = JsonHelper.GetInt(obj, "firstIndex", 0),
                    lastIndex = JsonHelper.GetInt(obj, "lastIndex", 0),
                });
            }

            foreach (var obj in JsonHelper.GetArrayObjects(_summaryJson, "rtTransitions"))
            {
                _rtRows.Add(new RtRangeRow
                {
                    rt = JsonHelper.GetString(obj, "rt"),
                    startIndex = JsonHelper.GetInt(obj, "startIndex", 0),
                    endIndex = JsonHelper.GetInt(obj, "endIndex", 0),
                });
            }

            foreach (var obj in JsonHelper.GetArrayObjects(_summaryJson, "batchBreaks"))
            {
                _batchRows.Add(new BatchBreakRow
                {
                    cause = JsonHelper.GetString(obj, "cause"),
                    count = JsonHelper.GetInt(obj, "count", 0),
                    sampleIndex = JsonHelper.GetInt(obj, "sampleIndex", 0),
                });
            }

            foreach (var obj in JsonHelper.GetArrayObjects(_summaryJson, "hotspots"))
            {
                _hotspotRows.Add(new HotspotRow
                {
                    index = JsonHelper.GetInt(obj, "index", 0),
                    type = JsonHelper.GetString(obj, "type"),
                    verts = JsonHelper.GetInt(obj, "vertexCount", 0),
                    indices = JsonHelper.GetInt(obj, "indexCount", 0),
                    instances = JsonHelper.GetInt(obj, "instanceCount", 0),
                    cost = JsonHelper.GetInt(obj, "cost", 0),
                });
            }
        }

        #endregion

        #region AI Handoff

        private void HandOffFullSummary()
        {
            string ctx = "Frame Debugger summary JSON (from UnityEditorInternal.FrameDebuggerUtility):\n\n"
                         + (_summaryJson ?? "(empty)");
            string prompt =
                "위 프레임 캡처 요약을 분석해줘. 주목할 만한 점:\n" +
                "- draw call / vertex 비용이 튀는 이벤트가 있는가?\n" +
                "- RT 전환이 과도하지는 않은가?\n" +
                "- 배치 브레이크 원인 중 제거 가능한 게 있는가?\n" +
                "- 같은 셰이더가 여러 번 쪼개지지 않는가?\n" +
                "짧게 핵심만 요약하고, 의심스러운 이벤트 인덱스를 구체적으로 지목해줘.";
            _window.AskAIAboutFrame(prompt, ctx, "Frame summary");
        }

        private void HandOffEvent(int eventIndex)
        {
            string detail = FrameDebugBridge.GetEventDetail(eventIndex);
            string ctx =
                $"Frame event #{eventIndex} detail (UnityEditorInternal.FrameDebuggerEventData):\n\n{detail}\n\n" +
                $"Full frame summary:\n{_summaryJson ?? "(empty)"}";
            string prompt =
                $"Frame event #{eventIndex} 를 설명하고, 비용이 높다면 원인과 개선 아이디어를 제시해줘. " +
                "필요하면 프레임 전체 컨텍스트도 참고해.";
            _window.AskAIAboutFrame(prompt, ctx, $"Event #{eventIndex}");
        }

        #endregion
    }
}
