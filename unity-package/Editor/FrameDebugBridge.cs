using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Reflection wrapper around UnityEditorInternal.FrameDebuggerUtility.
    /// Exposes a tiki-taka style API: lightweight overview for AI to scan the frame,
    /// plus lazy deep-dive accessors for picking specific events/state/RTs.
    ///
    /// All reflection is defensive — if Unity changes internals, we return a structured
    /// JSON error rather than crash the server.
    /// </summary>
    public static class FrameDebugBridge
    {
        #region Reflection Cache

        private static bool _reflectionInitialized;
        private static Type _fdUtil;             // UnityEditorInternal.FrameDebuggerUtility
        private static Type _fdEvent;            // UnityEditorInternal.FrameDebuggerEvent
        private static Type _fdEventData;        // UnityEditorInternal.FrameDebuggerEventData
        private static Type _fdEventType;        // UnityEditorInternal.FrameEventType (enum)

        // FrameDebuggerUtility members
        private static PropertyInfo _propCount;
        private static PropertyInfo _propLimit;
        private static PropertyInfo _propEnabled;
        private static MethodInfo _methodSetEnabled;
        private static MethodInfo _methodGetFrameEvents;
        private static MethodInfo _methodGetFrameEventData;

        // Enable strategy: property setter vs. SetEnabled(bool, int)
        private static bool _hasSetEnabledMethod;

        private static string _lastReflectionError;

        private static void InitReflection()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;

            try
            {
                // FrameDebuggerUtility lives in UnityEditor assembly, UnityEditorInternal namespace.
                var unityEditorAsm = typeof(UnityEditor.Editor).Assembly;

                _fdUtil = unityEditorAsm.GetType("UnityEditorInternal.FrameDebuggerUtility")
                       ?? Type.GetType("UnityEditorInternal.FrameDebuggerUtility, UnityEditor");
                _fdEvent = unityEditorAsm.GetType("UnityEditorInternal.FrameDebuggerEvent")
                       ?? Type.GetType("UnityEditorInternal.FrameDebuggerEvent, UnityEditor");
                _fdEventData = unityEditorAsm.GetType("UnityEditorInternal.FrameDebuggerEventData")
                       ?? Type.GetType("UnityEditorInternal.FrameDebuggerEventData, UnityEditor");
                _fdEventType = unityEditorAsm.GetType("UnityEngine.Rendering.FrameEventType")
                       ?? unityEditorAsm.GetType("UnityEditorInternal.FrameEventType")
                       ?? Type.GetType("UnityEngine.Rendering.FrameEventType, UnityEngine.CoreModule");

                if (_fdUtil == null)
                {
                    _lastReflectionError = "FrameDebuggerUtility type not found (Unity internal API unavailable)";
                    return;
                }

                const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                _propCount = _fdUtil.GetProperty("count", anyStatic);
                _propLimit = _fdUtil.GetProperty("limit", anyStatic);
                _propEnabled = _fdUtil.GetProperty("enabled", anyStatic)
                             ?? _fdUtil.GetProperty("IsLocalEnabled", anyStatic);

                _methodSetEnabled = _fdUtil.GetMethod("SetEnabled", anyStatic);
                _hasSetEnabledMethod = _methodSetEnabled != null;

                _methodGetFrameEvents = _fdUtil.GetMethod("GetFrameEvents", anyStatic);
                _methodGetFrameEventData = _fdUtil.GetMethod("GetFrameEventData", anyStatic);
            }
            catch (Exception ex)
            {
                _lastReflectionError = ex.Message;
                Debug.LogWarning($"[UnityAgent/FrameDebug] Reflection init failed: {ex.Message}");
            }
        }

        private static bool IsAvailable(out string errorJson)
        {
            InitReflection();
            if (_fdUtil == null)
            {
                errorJson = JsonHelper.StartObject()
                    .Key("error").Value("frame-debugger-unavailable")
                    .Key("detail").Value(_lastReflectionError ?? "Reflection initialization failed")
                    .Key("unityVersion").Value(Application.unityVersion)
                    .ToString();
                return false;
            }
            errorJson = null;
            return true;
        }

        #endregion

        #region Enable / Capture

        /// <summary>
        /// Enables the Frame Debugger and captures the next frame.
        /// Returns overview JSON with event list.
        /// </summary>
        public static string Capture(int maxEvents, bool includeShaders)
        {
            if (!IsAvailable(out string err)) return err;

            try
            {
                // Enable FD. Prefer SetEnabled(true, localClientId=0) when available.
                SetEnabledSafe(true);

                // Read count
                int count = GetCount();

                // Unity fills events after one editor tick under play mode; surface what's available.
                var overview = JsonHelper.StartObject()
                    .Key("enabled").Value(true)
                    .Key("eventCount").Value(count)
                    .Key("isPlaying").Value(EditorApplication.isPlaying)
                    .Key("events").BeginArray();

                if (count > 0)
                {
                    int limit = Math.Min(count, maxEvents <= 0 ? count : maxEvents);
                    var events = GetFrameEventsArray();
                    if (events != null)
                    {
                        int n = Math.Min(limit, events.Length);
                        for (int i = 0; i < n; i++)
                        {
                            overview.RawValue(BuildEventSummary(i, events.GetValue(i), includeShaders));
                        }
                    }
                }

                overview.EndArray();
                if (!string.IsNullOrEmpty(_lastReflectionError))
                    overview.Key("warning").Value(_lastReflectionError);

                return overview.ToString();
            }
            catch (Exception ex)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value("capture-failed")
                    .Key("detail").Value(ex.Message)
                    .ToString();
            }
        }

        /// <summary>
        /// Disable the Frame Debugger.
        /// </summary>
        public static string Disable()
        {
            if (!IsAvailable(out string err)) return err;
            try
            {
                SetEnabledSafe(false);
                return JsonHelper.StartObject().Key("enabled").Value(false).ToString();
            }
            catch (Exception ex)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value("disable-failed")
                    .Key("detail").Value(ex.Message)
                    .ToString();
            }
        }

        /// <summary>
        /// Current capture status (no side effects).
        /// </summary>
        public static string Status()
        {
            InitReflection();
            var builder = JsonHelper.StartObject()
                .Key("available").Value(_fdUtil != null)
                .Key("unityVersion").Value(Application.unityVersion)
                .Key("isPlaying").Value(EditorApplication.isPlaying);

            if (_fdUtil != null)
            {
                try { builder.Key("enabled").Value(IsEnabled()); } catch { }
                try { builder.Key("eventCount").Value(GetCount()); } catch { }
            }
            if (!string.IsNullOrEmpty(_lastReflectionError))
                builder.Key("warning").Value(_lastReflectionError);
            return builder.ToString();
        }

        #endregion

        #region Event Detail

        /// <summary>
        /// Returns the detailed state for a single event (bindings, shader props, render state).
        /// </summary>
        public static string GetEventDetail(int eventIndex)
        {
            if (!IsAvailable(out string err)) return err;

            try
            {
                int count = GetCount();
                if (count <= 0)
                    return BuildError("no-events", "Frame Debugger has no events — call framedebug/capture first");
                if (eventIndex < 0 || eventIndex >= count)
                    return BuildError("index-out-of-range", $"eventIndex {eventIndex} not in [0,{count - 1}]");

                object eventData = TryGetEventData(eventIndex);
                if (eventData == null)
                    return BuildError("no-event-data", "GetFrameEventData returned null/false");

                return BuildEventDetail(eventIndex, eventData);
            }
            catch (Exception ex)
            {
                return BuildError("detail-failed", ex.Message);
            }
        }

        /// <summary>
        /// Returns the shader/variant info for an event. Lighter than GetEventDetail.
        /// </summary>
        public static string GetEventShader(int eventIndex)
        {
            if (!IsAvailable(out string err)) return err;

            try
            {
                int count = GetCount();
                if (eventIndex < 0 || eventIndex >= count)
                    return BuildError("index-out-of-range", $"eventIndex {eventIndex} not in [0,{count - 1}]");

                object eventData = TryGetEventData(eventIndex);
                if (eventData == null)
                    return BuildError("no-event-data", "GetFrameEventData returned null/false");

                var t = eventData.GetType();
                string shaderName = ReadString(eventData, t, "shaderName");
                string passName = ReadString(eventData, t, "passName");
                string passLightMode = ReadString(eventData, t, "passLightMode");
                int shaderInstanceID = ReadInt(eventData, t, "shaderInstanceID", 0);
                string[] keywords = ReadStringArray(eventData, t, "shaderKeywords");

                var builder = JsonHelper.StartObject()
                    .Key("eventIndex").Value(eventIndex)
                    .Key("shaderName").Value(shaderName)
                    .Key("passName").Value(passName)
                    .Key("passLightMode").Value(passLightMode)
                    .Key("shaderInstanceID").Value(shaderInstanceID)
                    .Key("keywords").BeginArray();
                if (keywords != null)
                    foreach (var kw in keywords) builder.Value(kw ?? "");
                builder.EndArray();

                // Try to resolve asset path for the shader (if it's a project asset)
                if (shaderInstanceID != 0)
                {
                    try
                    {
                        var obj = EditorUtility.InstanceIDToObject(shaderInstanceID);
                        if (obj != null)
                        {
                            string path = AssetDatabase.GetAssetPath(obj);
                            builder.Key("shaderAssetPath").Value(path);
                        }
                    }
                    catch { }
                }

                return builder.ToString();
            }
            catch (Exception ex)
            {
                return BuildError("shader-fetch-failed", ex.Message);
            }
        }

        /// <summary>
        /// Captures the current render target as PNG base64. Expensive — AI should request
        /// only when it wants visual evidence. Sets FrameDebugger limit to (eventIndex+1) first.
        /// </summary>
        public static string GetRenderTargetSnapshot(int eventIndex, int maxWidth)
        {
            if (!IsAvailable(out string err)) return err;

            try
            {
                int count = GetCount();
                if (eventIndex < 0 || eventIndex >= count)
                    return BuildError("index-out-of-range", $"eventIndex {eventIndex} not in [0,{count - 1}]");

                // FrameDebugger limit is 1-based and selects "events up to this one"
                SetLimit(eventIndex + 1);

                // Force the debugger to update the displayed RT
                EditorApplication.QueuePlayerLoopUpdate();

                // Read back from the current active RenderTexture.
                // Note: Unity internally redirects the game view to the FD capture at this step.
                var src = UnityEngine.RenderTexture.active;
                if (src == null)
                    return BuildError("no-active-rt", "No active RenderTexture after limit set");

                int srcW = src.width;
                int srcH = src.height;
                int targetW = (maxWidth > 0 && srcW > maxWidth) ? maxWidth : srcW;
                int targetH = (int)Math.Round(srcH * ((double)targetW / srcW));

                byte[] pngBytes;
                var tempRt = UnityEngine.RenderTexture.GetTemporary(
                    targetW, targetH, 0,
                    UnityEngine.RenderTextureFormat.ARGB32,
                    UnityEngine.RenderTextureReadWrite.Linear);

                var prev = UnityEngine.RenderTexture.active;
                try
                {
                    UnityEngine.Graphics.Blit(src, tempRt);
                    UnityEngine.RenderTexture.active = tempRt;
                    var tex = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false, true);
                    tex.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                    tex.Apply(false, false);
                    pngBytes = tex.EncodeToPNG();
                    UnityEngine.Object.DestroyImmediate(tex);
                }
                finally
                {
                    UnityEngine.RenderTexture.active = prev;
                    UnityEngine.RenderTexture.ReleaseTemporary(tempRt);
                }

                string base64 = Convert.ToBase64String(pngBytes);

                return JsonHelper.StartObject()
                    .Key("eventIndex").Value(eventIndex)
                    .Key("format").Value("png-base64")
                    .Key("width").Value(targetW)
                    .Key("height").Value(targetH)
                    .Key("srcWidth").Value(srcW)
                    .Key("srcHeight").Value(srcH)
                    .Key("bytes").Value(pngBytes.Length)
                    .Key("data").Value(base64)
                    .ToString();
            }
            catch (Exception ex)
            {
                return BuildError("rt-snapshot-failed", ex.Message);
            }
        }

        #endregion

        #region Reflection helpers

        private static void SetEnabledSafe(bool enable)
        {
            try
            {
                if (_hasSetEnabledMethod)
                {
                    // signature: static void SetEnabled(bool enable, int frameEventLimit = -1) in modern Unity
                    var parameters = _methodSetEnabled.GetParameters();
                    if (parameters.Length == 1)
                        _methodSetEnabled.Invoke(null, new object[] { enable });
                    else if (parameters.Length >= 2)
                    {
                        var args = new object[parameters.Length];
                        args[0] = enable;
                        for (int i = 1; i < args.Length; i++) args[i] = GetDefault(parameters[i].ParameterType);
                        _methodSetEnabled.Invoke(null, args);
                    }
                    return;
                }
                if (_propEnabled != null && _propEnabled.CanWrite)
                {
                    _propEnabled.SetValue(null, enable);
                    return;
                }
                throw new MissingMethodException("No way to toggle FrameDebugger (SetEnabled / enabled setter missing)");
            }
            catch (Exception ex)
            {
                _lastReflectionError = "SetEnabled failed: " + ex.Message;
            }
        }

        private static bool IsEnabled()
        {
            if (_propEnabled != null && _propEnabled.CanRead)
                return (bool)_propEnabled.GetValue(null);
            return false;
        }

        private static int GetCount()
        {
            if (_propCount != null && _propCount.CanRead)
            {
                object v = _propCount.GetValue(null);
                if (v is int i) return i;
            }
            return 0;
        }

        private static void SetLimit(int limit)
        {
            if (_propLimit != null && _propLimit.CanWrite)
                _propLimit.SetValue(null, limit);
        }

        private static Array GetFrameEventsArray()
        {
            if (_methodGetFrameEvents == null) return null;
            try
            {
                return _methodGetFrameEvents.Invoke(null, null) as Array;
            }
            catch { return null; }
        }

        private static object TryGetEventData(int eventIndex)
        {
            if (_methodGetFrameEventData == null || _fdEventData == null) return null;
            try
            {
                // Signatures vary across Unity versions:
                //   bool GetFrameEventData(int index, out FrameDebuggerEventData data)
                //   FrameDebuggerEventData GetFrameEventData(int index)
                var parameters = _methodGetFrameEventData.GetParameters();
                if (parameters.Length == 2 && parameters[1].IsOut)
                {
                    var args = new object[] { eventIndex, null };
                    bool ok = (bool)_methodGetFrameEventData.Invoke(null, args);
                    return ok ? args[1] : null;
                }
                if (parameters.Length == 1)
                {
                    return _methodGetFrameEventData.Invoke(null, new object[] { eventIndex });
                }
            }
            catch (Exception ex)
            {
                _lastReflectionError = "GetFrameEventData failed: " + ex.Message;
            }
            return null;
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Lightweight summary — one entry per event in the overview.
        /// </summary>
        private static string BuildEventSummary(int index, object fdEvent, bool includeShaderName)
        {
            var t = fdEvent.GetType();
            string typeStr = ReadEventType(fdEvent, t);
            int vertexCount = ReadInt(fdEvent, t, "vertexCount", 0);
            int indexCount = ReadInt(fdEvent, t, "indexCount", 0);
            int instanceCount = ReadInt(fdEvent, t, "instanceCount", 0);
            int drawCallCount = ReadInt(fdEvent, t, "drawCallCount", 0);

            var builder = JsonHelper.StartObject()
                .Key("index").Value(index)
                .Key("type").Value(typeStr)
                .Key("vertexCount").Value(vertexCount)
                .Key("indexCount").Value(indexCount)
                .Key("instanceCount").Value(instanceCount)
                .Key("drawCallCount").Value(drawCallCount);

            if (includeShaderName)
            {
                // Pull just the shader label via event data (heavier call, opt-in)
                try
                {
                    object ed = TryGetEventData(index);
                    if (ed != null)
                    {
                        var edT = ed.GetType();
                        builder.Key("shader").Value(ReadString(ed, edT, "shaderName"));
                        builder.Key("pass").Value(ReadString(ed, edT, "passName"));
                    }
                }
                catch { }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Full per-event detail — expensive, only on demand.
        /// </summary>
        private static string BuildEventDetail(int index, object eventData)
        {
            var t = eventData.GetType();
            var builder = JsonHelper.StartObject()
                .Key("eventIndex").Value(index);

            // Shader identity
            builder.Key("shader").BeginObject()
                .Key("name").Value(ReadString(eventData, t, "shaderName"))
                .Key("pass").Value(ReadString(eventData, t, "passName"))
                .Key("lightMode").Value(ReadString(eventData, t, "passLightMode"))
                .Key("instanceID").Value(ReadInt(eventData, t, "shaderInstanceID", 0))
                .Key("subShaderIndex").Value(ReadInt(eventData, t, "subShaderIndex", -1))
                .Key("shaderPassIndex").Value(ReadInt(eventData, t, "shaderPassIndex", -1));

            var kws = ReadStringArray(eventData, t, "shaderKeywords");
            builder.Key("keywords").BeginArray();
            if (kws != null) foreach (var k in kws) builder.Value(k ?? "");
            builder.EndArray();
            builder.EndObject();

            // Render target
            builder.Key("renderTarget").BeginObject()
                .Key("width").Value(ReadInt(eventData, t, "rtWidth", 0))
                .Key("height").Value(ReadInt(eventData, t, "rtHeight", 0))
                .Key("count").Value(ReadInt(eventData, t, "rtCount", 0))
                .Key("format").Value(ReadInt(eventData, t, "rtFormat", 0))
                .Key("hasDepthTexture").Value(ReadInt(eventData, t, "rtHasDepthTexture", 0) != 0)
                .Key("memberless").Value(ReadInt(eventData, t, "rtMemoryless", 0) != 0)
                .EndObject();

            // Mesh / geometry
            builder.Key("geometry").BeginObject()
                .Key("vertexCount").Value(ReadInt(eventData, t, "vertexCount", 0))
                .Key("indexCount").Value(ReadInt(eventData, t, "indexCount", 0))
                .Key("instanceCount").Value(ReadInt(eventData, t, "instanceCount", 0))
                .Key("drawCallCount").Value(ReadInt(eventData, t, "drawCallCount", 0))
                .Key("meshInstanceID").Value(ReadInt(eventData, t, "meshInstanceID", 0))
                .Key("meshSubset").Value(ReadInt(eventData, t, "meshSubset", 0))
                .EndObject();

            // Render state
            string[] stateFields = new[]
            {
                "rasterState", "blendState", "depthState", "stencilState",
                "stencilRef",
            };
            builder.Key("state").BeginObject();
            foreach (var f in stateFields)
            {
                string val = TryDescribeValue(eventData, t, f);
                if (val != null) builder.Key(f).RawValue(val);
            }
            builder.EndObject();

            // Batching
            builder.Key("batch").BeginObject()
                .Key("break").Value(ReadString(eventData, t, "batchBreakCause"))
                .Key("batchCause").Value(ReadString(eventData, t, "batchBreakCauseStr"))
                .EndObject();

            // Shader property snapshot (floats/vectors/matrices/textures)
            string props = TryDescribeShaderProperties(eventData, t);
            if (props != null) builder.Key("shaderProperties").RawValue(props);

            return builder.ToString();
        }

        private static string ReadEventType(object fdEvent, Type t)
        {
            var field = t.GetField("type", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return "";
            object v = field.GetValue(fdEvent);
            if (v == null) return "";
            return v.ToString();
        }

        private static string ReadString(object obj, Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
            {
                object v = f.GetValue(obj);
                return v?.ToString() ?? "";
            }
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (p != null && p.CanRead)
            {
                object v = p.GetValue(obj);
                return v?.ToString() ?? "";
            }
            return "";
        }

        private static int ReadInt(object obj, Type t, string name, int fallback)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
            {
                object v = f.GetValue(obj);
                if (v is int i) return i;
                if (v is short s) return s;
                if (v is long l) return (int)l;
                if (v is byte b) return b;
                if (v != null)
                {
                    try { return Convert.ToInt32(v); } catch { }
                }
            }
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (p != null && p.CanRead)
            {
                object v = p.GetValue(obj);
                if (v is int i2) return i2;
                if (v != null) { try { return Convert.ToInt32(v); } catch { } }
            }
            return fallback;
        }

        private static string[] ReadStringArray(object obj, Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
            {
                object v = f.GetValue(obj);
                if (v is string[] arr) return arr;
                if (v is Array genArr)
                {
                    var result = new string[genArr.Length];
                    for (int i = 0; i < genArr.Length; i++)
                        result[i] = genArr.GetValue(i)?.ToString() ?? "";
                    return result;
                }
            }
            return null;
        }

        /// <summary>
        /// Serialize any field's value to JSON (fields, nested struct, primitives).
        /// </summary>
        private static string TryDescribeValue(object obj, Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) return null;
            object v;
            try { v = f.GetValue(obj); } catch { return null; }
            if (v == null) return "null";
            if (v is string s) return "\"" + EscapeJson(s) + "\"";
            if (v is bool bv) return bv ? "true" : "false";
            if (v.GetType().IsPrimitive) return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);

            // Nested struct — dump public fields shallowly
            var ft = v.GetType();
            if (ft.IsValueType || ft.IsClass)
            {
                var inner = JsonHelper.StartObject();
                foreach (var sub in ft.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        object sv = sub.GetValue(v);
                        string label = sub.Name;
                        if (sv == null) inner.Key(label).RawValue("null");
                        else if (sv is string ss) inner.Key(label).Value(ss);
                        else if (sv is bool sb) inner.Key(label).Value(sb);
                        else if (sv is int si) inner.Key(label).Value(si);
                        else if (sv is float sfv) inner.Key(label).Value(sfv);
                        else if (sv.GetType().IsPrimitive) inner.Key(label).RawValue(
                            Convert.ToString(sv, System.Globalization.CultureInfo.InvariantCulture));
                        else inner.Key(label).Value(sv.ToString());
                    }
                    catch { }
                }
                return inner.ToString();
            }
            return "\"" + EscapeJson(v.ToString()) + "\"";
        }

        private static string TryDescribeShaderProperties(object eventData, Type t)
        {
            // Try eventData.shaderProperties (type ShaderProperties struct with arrays)
            var f = t.GetField("shaderProperties", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) return null;
            object sp = f.GetValue(eventData);
            if (sp == null) return "null";

            var spType = sp.GetType();
            var builder = JsonHelper.StartObject();

            string[] categories = { "floats", "ints", "vectors", "matrices", "buffers", "textures" };
            foreach (var cat in categories)
            {
                var cf = spType.GetField(cat, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (cf == null) continue;
                object arrObj = cf.GetValue(sp);
                if (arrObj is Array arr)
                {
                    builder.Key(cat).BeginArray();
                    int lim = Math.Min(arr.Length, 64); // cap for overview
                    for (int i = 0; i < lim; i++)
                    {
                        object item = arr.GetValue(i);
                        builder.RawValue(DescribeShaderProp(item));
                    }
                    if (arr.Length > lim) builder.Value("…truncated").BeginObject().Key("total").Value(arr.Length).EndObject();
                    builder.EndArray();
                }
            }
            return builder.ToString();
        }

        private static string DescribeShaderProp(object item)
        {
            if (item == null) return "null";
            var t = item.GetType();
            var b = JsonHelper.StartObject();
            foreach (var fld in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    object v = fld.GetValue(item);
                    if (v == null) b.Key(fld.Name).RawValue("null");
                    else if (v is string s) b.Key(fld.Name).Value(s);
                    else if (v is bool bv) b.Key(fld.Name).Value(bv);
                    else if (v is int iv) b.Key(fld.Name).Value(iv);
                    else if (v is float fv) b.Key(fld.Name).Value(fv);
                    else if (v is Vector4 vec4)
                    {
                        b.Key(fld.Name).BeginArray()
                            .Value(vec4.x).Value(vec4.y).Value(vec4.z).Value(vec4.w)
                            .EndArray();
                    }
                    else if (v is Matrix4x4 m)
                    {
                        b.Key(fld.Name).BeginArray();
                        for (int r = 0; r < 4; r++)
                            for (int c = 0; c < 4; c++) b.Value(m[r, c]);
                        b.EndArray();
                    }
                    else if (v is UnityEngine.Object uo)
                        b.Key(fld.Name).Value(uo != null ? uo.name : "");
                    else if (v.GetType().IsEnum)
                        b.Key(fld.Name).Value(v.ToString());
                    else
                        b.Key(fld.Name).Value(v.ToString());
                }
                catch { }
            }
            return b.ToString();
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string BuildError(string code, string detail)
        {
            return JsonHelper.StartObject()
                .Key("error").Value(code)
                .Key("detail").Value(detail)
                .ToString();
        }

        private static object GetDefault(Type t)
        {
            if (t == typeof(int)) return -1;
            if (t == typeof(bool)) return false;
            if (t == typeof(float)) return 0f;
            if (t.IsValueType) return Activator.CreateInstance(t);
            return null;
        }

        #endregion
    }
}
