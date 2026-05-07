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
        private static MethodInfo _methodGetFrameEventInfoName;

        // Enable strategy: property setter vs. SetEnabled(bool, int)
        private static bool _hasSetEnabledMethod;

        private static string _lastReflectionError;

        // Event detail cache — populated in one synchronous pass after Summary()
        private static readonly Dictionary<int, string> _eventDetailCache = new Dictionary<int, string>();

        /// <summary>Force re-discovery of all reflection targets. Use after domain reload issues.</summary>
        public static void ResetReflection()
        {
            _reflectionInitialized = false;
            _fdUtil = null;
            _fdEvent = null;
            _fdEventData = null;
            _fdEventType = null;
            _propCount = null;
            _propLimit = null;
            _propEnabled = null;
            _methodSetEnabled = null;
            _methodGetFrameEvents = null;
            _methodGetFrameEventData = null;
            _hasSetEnabledMethod = false;
            _lastReflectionError = null;
        }

        private static void InitReflection()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;

            try
            {
                // FrameDebuggerUtility location differs by Unity version:
                //   Unity 2019–2022: UnityEditorInternal.FrameDebuggerUtility
                //   Unity 6 (6000.x)+: UnityEditor.FrameDebugger.FrameDebuggerUtility
                var unityEditorAsm = typeof(UnityEditor.Editor).Assembly;

                // Also scan all loaded assemblies for Unity 6 which may split into sub-assemblies
                System.Reflection.Assembly[] allAsm = AppDomain.CurrentDomain.GetAssemblies();

                _fdUtil = ResolveType(unityEditorAsm, allAsm,
                    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility",  // Unity 6 (6000.x)
                    "UnityEditorInternal.FrameDebuggerUtility");                       // Unity 2019–2022

                _fdEvent = ResolveType(unityEditorAsm, allAsm,
                    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEvent",
                    "UnityEditorInternal.FrameDebuggerEvent");

                _fdEventData = ResolveType(unityEditorAsm, allAsm,
                    "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData",
                    "UnityEditorInternal.FrameDebuggerEventData");

                _fdEventType = ResolveType(unityEditorAsm, allAsm,
                    "UnityEditorInternal.FrameDebuggerInternal.FrameEventType",
                    "UnityEngine.Rendering.FrameEventType",
                    "UnityEditorInternal.FrameEventType");

                if (_fdUtil == null)
                {
                    // Dump all FrameDebug-related types found for diagnosis
                    var candidates = new System.Text.StringBuilder();
                    foreach (var asm in allAsm)
                    {
                        try
                        {
                            foreach (var t in asm.GetTypes())
                                if (t.FullName != null && t.FullName.IndexOf("FrameDebug", StringComparison.OrdinalIgnoreCase) >= 0)
                                    candidates.AppendLine(t.FullName);
                        }
                        catch { }
                    }
                    _lastReflectionError =
                        $"FrameDebuggerUtility type not found (Unity {Application.unityVersion}). " +
                        $"Candidates:\n{candidates}";
                    Debug.LogWarning($"[UnityAgent/FrameDebug] {_lastReflectionError}");
                    return;
                }

                Debug.Log($"[UnityAgent/FrameDebug] Resolved: {_fdUtil.FullName} (Unity {Application.unityVersion})");

                const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                _propCount = _fdUtil.GetProperty("count", anyStatic);
                _propLimit = _fdUtil.GetProperty("limit", anyStatic);
                _propEnabled = _fdUtil.GetProperty("enabled", anyStatic)
                             ?? _fdUtil.GetProperty("IsLocalEnabled", anyStatic);

                _methodSetEnabled = _fdUtil.GetMethod("SetEnabled", anyStatic);
                _hasSetEnabledMethod = _methodSetEnabled != null;

                _methodGetFrameEvents = _fdUtil.GetMethod("GetFrameEvents", anyStatic);
                _methodGetFrameEventData = _fdUtil.GetMethod("GetFrameEventData", anyStatic);
                _methodGetFrameEventInfoName = _fdUtil.GetMethod("GetFrameEventInfoName", anyStatic);

            }
            catch (Exception ex)
            {
                _lastReflectionError = ex.Message;
                Debug.LogWarning($"[UnityAgent/FrameDebug] Reflection init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Try multiple fully-qualified type names across all loaded assemblies.
        /// Returns the first match, or null.
        /// </summary>
        private static Type ResolveType(System.Reflection.Assembly primaryAsm,
            System.Reflection.Assembly[] allAsm, params string[] candidates)
        {
            foreach (var name in candidates)
            {
                var t = primaryAsm.GetType(name);
                if (t != null) return t;
                t = Type.GetType(name + ", UnityEditor");
                if (t != null) return t;
                t = Type.GetType(name + ", UnityEngine.CoreModule");
                if (t != null) return t;
            }
            // Brute-force scan all assemblies
            foreach (var name in candidates)
            {
                foreach (var asm in allAsm)
                {
                    try
                    {
                        var t = asm.GetType(name);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            return null;
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

        /// <summary>
        /// Get the render target as a Texture2D for a given event.
        /// Reads m_RenderTargetRenderTexture from EventData directly (Unity 6 compatible).
        /// Returns null if RT is not available. Caller must destroy the texture when done.
        /// </summary>
        public static Texture2D GetRenderTargetTexture(int eventIndex, int maxWidth = 512)
        {
            InitReflection();
            if (_fdEventData == null || _methodGetFrameEventData == null) return null;

            try
            {
                SetLimit(eventIndex + 1);
                EditorApplication.QueuePlayerLoopUpdate();

                object dataObj = Activator.CreateInstance(_fdEventData);
                var args = new object[] { eventIndex, dataObj };
                _methodGetFrameEventData.Invoke(null, args);
                object data = args[1];
                if (data == null) return null;

                var rtField = FindField(data.GetType(), "renderTargetRenderTexture");
                if (rtField == null) return null;

                var rt = rtField.GetValue(data) as RenderTexture;
                if (rt == null) return null;

                int srcW = rt.width;
                int srcH = rt.height;
                int targetW = (maxWidth > 0 && srcW > maxWidth) ? maxWidth : srcW;
                int targetH = (int)Math.Round(srcH * ((double)targetW / srcW));

                var tempRt = RenderTexture.GetTemporary(targetW, targetH, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                try
                {
                    Graphics.Blit(rt, tempRt);
                    RenderTexture.active = tempRt;
                    var tex = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
                    tex.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                    tex.Apply(false, false);
                    return tex;
                }
                finally
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(tempRt);
                }
            }
            catch
            {
                return null;
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
                    var parameters = _methodSetEnabled.GetParameters();
                    if (parameters.Length == 1)
                    {
                        _methodSetEnabled.Invoke(null, new object[] { enable });
                    }
                    else if (parameters.Length >= 2)
                    {
                        // Unity 6: SetEnabled(bool enabled, int remotePlayerGUID)
                        // Must pass the correct GUID — 0 may not work for local debugging.
                        int guid = 0;
                        if (_fdUtil != null)
                        {
                            const BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                            var getGuid = _fdUtil.GetMethod("GetRemotePlayerGUID", sf);
                            if (getGuid != null)
                            {
                                object v = getGuid.Invoke(null, null);
                                if (v is int g) guid = g;
                            }
                        }
                        var args = new object[parameters.Length];
                        args[0] = enable;
                        args[1] = guid;
                        for (int i = 2; i < args.Length; i++) args[i] = GetDefault(parameters[i].ParameterType);
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

        /// <summary>
        /// Get the display name for a frame event (as shown in Unity's Frame Debugger tree).
        /// </summary>
        public static string GetEventName(int index)
        {
            InitReflection();
            if (_methodGetFrameEventInfoName == null) return null;
            try
            {
                return _methodGetFrameEventInfoName.Invoke(null, new object[] { index }) as string;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns all event names + types for building a tree view.
        /// Each entry: (index, name, eventType).
        /// </summary>
        public static List<(int index, string name, string type)> GetAllEventNames()
        {
            InitReflection();
            var result = new List<(int, string, string)>();
            int count = GetCount();
            if (count == 0) return result;
            var events = GetFrameEventsArray();
            for (int i = 0; i < count; i++)
            {
                string name = GetEventName(i) ?? $"Event {i}";
                string type = "";
                if (events != null && i < events.Length)
                {
                    object ev = events.GetValue(i);
                    if (ev != null) type = ReadEventType(ev, ev.GetType());
                }
                result.Add((i, name, type));
            }
            return result;
        }

        private static void SetLimit(int limit)
        {
            InitReflection();
            if (_propLimit != null && _propLimit.CanWrite)
                _propLimit.SetValue(null, limit);
        }

        /// <summary>Public wrapper for setting FD limit and forcing FD to process the change.</summary>
        public static void SetLimitPublic(int limit)
        {
            SetLimit(limit);
            // Force Unity to process the limit change — FD is paused so we need to
            // trigger a player loop update for the new limit to take effect.
            EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>
        /// Enable Frame Debugger capture via internal API.
        /// Use UnityEngine.FrameDebugger.enabled to verify if it worked.
        /// </summary>
        public static void EnableCapture()
        {
            InitReflection();
            SetEnabledSafe(true);
            EditorApplication.QueuePlayerLoopUpdate();
        }

        /// <summary>Check if FD is currently active (public API, no reflection).</summary>
        public static bool IsCaptureActive => UnityEngine.FrameDebugger.enabled;

        /// <summary>
        /// Cache all event details in one synchronous pass.
        /// Call after Summary() when FD is active. Uses sequential SetLimit + GetFrameEventData.
        /// </summary>
        public static void CacheAllEventDetails()
        {
            _eventDetailCache.Clear();
            InitReflection();
            int count = GetCount();
            if (count == 0) return;

            for (int i = 0; i < count; i++)
            {
                SetLimit(i + 1);
                object eventData = TryGetEventData(i);
                if (eventData != null)
                {
                    _eventDetailCache[i] = BuildEventDetail(i, eventData);
                }
            }
        }

        /// <summary>Get cached event detail. Returns null if not cached.</summary>
        public static string GetCachedEventDetail(int eventIndex)
        {
            return _eventDetailCache.TryGetValue(eventIndex, out var json) ? json : null;
        }

        public static void ClearCache()
        {
            _eventDetailCache.Clear();
        }

        /// <summary>Store a single event's detail JSON in the cache.</summary>
        public static void CacheEventDetail(int eventIndex, string json)
        {
            _eventDetailCache[eventIndex] = json;
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
                // NOTE: Caller must call SetLimit(eventIndex+1) and wait 1-2 frames
                // before calling this. Unity 6 FD needs time to process limit changes.
                // For batch reads (Summary), SetLimit is called per-event sequentially.

                // Signatures vary across Unity versions:
                //   Unity 2019–2022: bool GetFrameEventData(int index, out FrameDebuggerEventData data)
                //   Unity 6 (6000.x): bool GetFrameEventData(int index, FrameDebuggerEventData data)
                //                     where FrameDebuggerEventData may be a class (pass by ref)
                //   Legacy: FrameDebuggerEventData GetFrameEventData(int index)
                var parameters = _methodGetFrameEventData.GetParameters();
                if (parameters.Length == 2)
                {
                    // Create an instance of FrameDebuggerEventData to pass in.
                    // Unity 6: FrameDebuggerEventData is a class, method populates it
                    // and may return False for non-draw events while still filling data.
                    object dataObj;
                    if (parameters[1].IsOut)
                        dataObj = null; // out parameter — runtime creates it
                    else
                        dataObj = Activator.CreateInstance(_fdEventData);

                    var args = new object[] { eventIndex, dataObj };
                    _methodGetFrameEventData.Invoke(null, args);
                    // Always return the data — Unity 6 returns False for non-draw events
                    // but still populates the struct/class with valid info.
                    return args[1];
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

        // Unity 6 renamed most fields with m_ prefix and some with entirely new names.
        // This alias table maps old field names to Unity 6 equivalents.
        private static readonly Dictionary<string, string[]> _fieldAliases = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "type",               new[] { "m_Type" } },
            { "vertexCount",        new[] { "m_VertexCount" } },
            { "indexCount",         new[] { "m_IndexCount" } },
            { "instanceCount",      new[] { "m_InstanceCount" } },
            { "drawCallCount",      new[] { "m_DrawCallCount" } },
            { "shaderName",         new[] { "m_OriginalShaderName", "m_RealShaderName" } },
            { "passName",           new[] { "m_PassName" } },
            { "passLightMode",      new[] { "m_PassLightMode" } },
            { "shaderInstanceID",   new[] { "m_ShaderInstanceID" } },
            { "shaderKeywords",     new[] { "shaderKeywords" } }, // no m_ prefix in Unity 6
            { "rtWidth",            new[] { "m_RenderTargetWidth" } },
            { "rtHeight",           new[] { "m_RenderTargetHeight" } },
            { "rtCount",            new[] { "m_RenderTargetCount" } },
            { "batchBreakCauseStr", new[] { "m_BatchBreakCause" } },
            { "batchBreakCause",    new[] { "m_BatchBreakCause" } },
            { "rasterState",        new[] { "m_RasterState" } },
            { "blendState",         new[] { "m_BlendState" } },
            { "depthState",         new[] { "m_DepthState" } },
            { "stencilState",       new[] { "m_StencilState" } },
            { "stencilRef",         new[] { "m_StencilRef" } },
            { "renderTargetRenderTexture", new[] { "m_RenderTargetRenderTexture" } },
        };

        private static FieldInfo FindField(Type t, string name)
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
            // Exact match first
            var f = t.GetField(name, bf);
            if (f != null) return f;
            // Try m_ + PascalCase
            if (name.Length > 0)
            {
                string pascal = "m_" + char.ToUpper(name[0]) + name.Substring(1);
                f = t.GetField(pascal, bf);
                if (f != null) return f;
            }
            // Try alias table
            if (_fieldAliases.TryGetValue(name, out var aliases))
            {
                foreach (var alias in aliases)
                {
                    f = t.GetField(alias, bf);
                    if (f != null) return f;
                }
            }
            return null;
        }

        private static string ReadEventType(object fdEvent, Type t)
        {
            var field = FindField(t, "type");
            if (field == null) return "";
            object v = field.GetValue(fdEvent);
            if (v == null) return "";
            return v.ToString();
        }

        private static string ReadString(object obj, Type t, string name)
        {
            var f = FindField(t, name);
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
            var f = FindField(t, name);
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
            var f = FindField(t, name);
            if (f != null)
            {
                object v = f.GetValue(obj);
                if (v is string[] arr) return arr;
                if (v is string str)
                {
                    // Unity 6: shaderKeywords is a single space-separated string
                    if (string.IsNullOrEmpty(str)) return new string[0];
                    return str.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                }
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

        #region Tiki-taka Aggregates (Summary / Search / Compare)

        /// <summary>
        /// Bird's-eye aggregate of the current (or freshly captured) frame.
        /// Does NOT enumerate every event — groups by shader / event type,
        /// lists RT transitions and batch-break causes, plus top-N hotspots.
        ///
        /// Call without needing a prior capture: this will enable the Frame
        /// Debugger itself if no events are available yet.
        /// </summary>
        public static string Summary(int topHotspots, bool includeShaders)
        {
            if (!IsAvailable(out string err)) return err;

            try
            {
                if (GetCount() == 0)
                    SetEnabledSafe(true);

                int count = GetCount();
                var events = GetFrameEventsArray();
                if (events == null || count == 0)
                {
                    return JsonHelper.StartObject()
                        .Key("eventCount").Value(0)
                        .Key("warning").Value("Frame Debugger returned no events (scene may be idle or capture not ready)")
                        .ToString();
                }

                int total = Math.Min(count, events.Length);

                // Per-event-type counts + geometry totals.
                var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                long totalVerts = 0, totalIndices = 0, totalInstances = 0, totalDrawCalls = 0;

                // Hotspot ranking — cost proxy = max(vertexCount, indexCount/3) * max(1,instanceCount).
                var hotspotEntries = new List<HotspotEntry>(total);

                // Unity 6: FrameDebuggerEvent only has m_Type and m_Obj.
                // Geometry fields (vertexCount etc.) moved to FrameDebuggerEventData.
                // Detect by checking if the first event has a vertexCount field.
                bool eventHasGeometry = false;
                if (total > 0)
                {
                    object probe = events.GetValue(0);
                    if (probe != null)
                        eventHasGeometry = FindField(probe.GetType(), "vertexCount") != null;
                }

                for (int i = 0; i < total; i++)
                {
                    object fdEvent = events.GetValue(i);
                    if (fdEvent == null) continue;
                    var t = fdEvent.GetType();
                    string typeStr = ReadEventType(fdEvent, t);

                    int verts, idx, inst, draws;
                    if (eventHasGeometry)
                    {
                        // Legacy Unity: geometry on the event struct
                        verts = ReadInt(fdEvent, t, "vertexCount", 0);
                        idx = ReadInt(fdEvent, t, "indexCount", 0);
                        inst = ReadInt(fdEvent, t, "instanceCount", 0);
                        draws = ReadInt(fdEvent, t, "drawCallCount", 0);
                    }
                    else
                    {
                        // Unity 6: must read from EventData — set limit first
                        SetLimit(i + 1);
                        object ed = TryGetEventData(i);
                        if (ed != null)
                        {
                            var edT = ed.GetType();
                            verts = ReadInt(ed, edT, "vertexCount", 0);
                            idx = ReadInt(ed, edT, "indexCount", 0);
                            inst = ReadInt(ed, edT, "instanceCount", 0);
                            draws = ReadInt(ed, edT, "drawCallCount", 0);
                        }
                        else
                        {
                            verts = idx = inst = draws = 0;
                        }
                    }

                    if (!string.IsNullOrEmpty(typeStr))
                    {
                        typeCounts.TryGetValue(typeStr, out int c);
                        typeCounts[typeStr] = c + 1;
                    }
                    totalVerts += verts;
                    totalIndices += idx;
                    totalInstances += Math.Max(0, inst);
                    totalDrawCalls += Math.Max(0, draws);

                    long cost = (long)Math.Max(verts, idx / 3) * Math.Max(1, inst);
                    hotspotEntries.Add(new HotspotEntry {
                        index = i, type = typeStr, vertexCount = verts,
                        indexCount = idx, instanceCount = inst, cost = cost,
                    });
                }

                // Per-shader aggregation + RT transitions + batch breaks.
                // These need per-event GetFrameEventData — only iterate when caller opts in,
                // OR when the event count is modest (cap to protect editor responsiveness).
                var shaderStats = new Dictionary<string, ShaderStat>(StringComparer.Ordinal);
                var rtTransitions = new List<RtRange>();
                var batchCauses = new Dictionary<string, BatchCause>(StringComparer.Ordinal);

                bool deepSweep = includeShaders || total <= 256;
                int deepCap = 1024; // hard ceiling regardless of opt-in
                int deepEnd = Math.Min(total, deepCap);
                string currentRt = null;
                int currentRtStart = 0;

                if (deepSweep)
                {
                    for (int i = 0; i < deepEnd; i++)
                    {
                        SetLimit(i + 1);
                        object ed = TryGetEventData(i);
                        if (ed == null) continue;
                        var edT = ed.GetType();
                        string shader = ReadString(ed, edT, "shaderName");
                        string pass = ReadString(ed, edT, "passName");
                        int rtW = ReadInt(ed, edT, "rtWidth", 0);
                        int rtH = ReadInt(ed, edT, "rtHeight", 0);
                        int rtCount = ReadInt(ed, edT, "rtCount", 0);
                        string rtKey = $"{rtW}x{rtH} mrt={rtCount}";

                        if (!string.IsNullOrEmpty(shader))
                        {
                            string key = shader + "|" + (pass ?? "");
                            if (!shaderStats.TryGetValue(key, out var s))
                            {
                                s = new ShaderStat { shaderName = shader, passName = pass, firstIndex = i };
                                shaderStats[key] = s;
                            }
                            s.events++;
                            s.lastIndex = i;
                            // Pull verts again from the fdEvent (cheap — already read)
                            if (i < total)
                            {
                                var fe = events.GetValue(i);
                                var fet = fe?.GetType();
                                if (fet != null)
                                {
                                    s.totalVerts += ReadInt(fe, fet, "vertexCount", 0);
                                    s.totalDrawCalls += Math.Max(0, ReadInt(fe, fet, "drawCallCount", 0));
                                }
                            }
                        }

                        // RT transition tracking
                        if (currentRt == null) { currentRt = rtKey; currentRtStart = i; }
                        else if (!string.Equals(currentRt, rtKey, StringComparison.Ordinal))
                        {
                            rtTransitions.Add(new RtRange { rt = currentRt, startIndex = currentRtStart, endIndex = i - 1 });
                            currentRt = rtKey;
                            currentRtStart = i;
                        }

                        // Batch break cause
                        string cause = ReadString(ed, edT, "batchBreakCauseStr");
                        if (string.IsNullOrEmpty(cause))
                            cause = ReadString(ed, edT, "batchBreakCause");
                        if (!string.IsNullOrEmpty(cause) && cause != "0")
                        {
                            if (!batchCauses.TryGetValue(cause, out var bc))
                            {
                                bc = new BatchCause { cause = cause, sampleIndex = i };
                                batchCauses[cause] = bc;
                            }
                            bc.count++;
                        }
                    }
                    if (currentRt != null)
                        rtTransitions.Add(new RtRange { rt = currentRt, startIndex = currentRtStart, endIndex = deepEnd - 1 });
                }

                // Build JSON
                var builder = JsonHelper.StartObject()
                    .Key("eventCount").Value(total)
                    .Key("isPlaying").Value(EditorApplication.isPlaying)
                    .Key("deepSweep").Value(deepSweep)
                    .Key("deepSweepLimit").Value(deepEnd);

                builder.Key("totals").BeginObject()
                    .Key("vertices").Value(totalVerts)
                    .Key("indices").Value(totalIndices)
                    .Key("instances").Value(totalInstances)
                    .Key("drawCalls").Value(totalDrawCalls)
                    .EndObject();

                builder.Key("eventTypes").BeginArray();
                foreach (var kv in typeCounts)
                {
                    builder.BeginObject()
                        .Key("type").Value(kv.Key)
                        .Key("count").Value(kv.Value)
                        .EndObject();
                }
                builder.EndArray();

                builder.Key("byShader").BeginArray();
                // Sort shaders by totalVerts desc
                var shaderList = new List<ShaderStat>(shaderStats.Values);
                shaderList.Sort((a, b) => b.totalVerts.CompareTo(a.totalVerts));
                int shaderCap = Math.Min(shaderList.Count, 64);
                for (int i = 0; i < shaderCap; i++)
                {
                    var s = shaderList[i];
                    builder.BeginObject()
                        .Key("shader").Value(s.shaderName)
                        .Key("pass").Value(s.passName ?? "")
                        .Key("events").Value(s.events)
                        .Key("totalVerts").Value(s.totalVerts)
                        .Key("totalDrawCalls").Value(s.totalDrawCalls)
                        .Key("firstIndex").Value(s.firstIndex)
                        .Key("lastIndex").Value(s.lastIndex)
                        .EndObject();
                }
                builder.EndArray();
                if (shaderList.Count > shaderCap)
                    builder.Key("shaderTruncated").Value(shaderList.Count - shaderCap);

                builder.Key("rtTransitions").BeginArray();
                int rtCap = Math.Min(rtTransitions.Count, 32);
                for (int i = 0; i < rtCap; i++)
                {
                    var r = rtTransitions[i];
                    builder.BeginObject()
                        .Key("rt").Value(r.rt)
                        .Key("startIndex").Value(r.startIndex)
                        .Key("endIndex").Value(r.endIndex)
                        .EndObject();
                }
                builder.EndArray();
                if (rtTransitions.Count > rtCap)
                    builder.Key("rtTransitionsTruncated").Value(rtTransitions.Count - rtCap);

                builder.Key("batchBreaks").BeginArray();
                var causeList = new List<BatchCause>(batchCauses.Values);
                causeList.Sort((a, b) => b.count.CompareTo(a.count));
                foreach (var bc in causeList)
                {
                    builder.BeginObject()
                        .Key("cause").Value(bc.cause)
                        .Key("count").Value(bc.count)
                        .Key("sampleIndex").Value(bc.sampleIndex)
                        .EndObject();
                }
                builder.EndArray();

                // Hotspots
                hotspotEntries.Sort((a, b) => b.cost.CompareTo(a.cost));
                int hotN = topHotspots <= 0 ? 8 : Math.Min(topHotspots, 64);
                hotN = Math.Min(hotN, hotspotEntries.Count);
                builder.Key("hotspots").BeginArray();
                for (int i = 0; i < hotN; i++)
                {
                    var h = hotspotEntries[i];
                    if (h.cost <= 0) continue;
                    builder.BeginObject()
                        .Key("index").Value(h.index)
                        .Key("type").Value(h.type ?? "")
                        .Key("vertexCount").Value(h.vertexCount)
                        .Key("indexCount").Value(h.indexCount)
                        .Key("instanceCount").Value(h.instanceCount)
                        .Key("cost").Value(h.cost)
                        .EndObject();
                }
                builder.EndArray();

                if (!string.IsNullOrEmpty(_lastReflectionError))
                    builder.Key("warning").Value(_lastReflectionError);

                return builder.ToString();
            }
            catch (Exception ex)
            {
                return BuildError("summary-failed", ex.Message);
            }
        }

        /// <summary>
        /// Filter frame events by a JSON-encoded predicate. Returns matching
        /// event indices + light summaries. Keeps event-data reflection gated
        /// so we don't pay for per-event EventData unless the filter needs it.
        /// </summary>
        public static string Search(string filtersJson)
        {
            if (!IsAvailable(out string err)) return err;

            try
            {
                int count = GetCount();
                if (count == 0)
                    SetEnabledSafe(true);
                count = GetCount();
                if (count == 0)
                    return BuildError("no-events", "Frame Debugger has no events — call framedebug/capture or ensure editor is playing");

                var events = GetFrameEventsArray();
                if (events == null)
                    return BuildError("no-events", "GetFrameEvents returned null");

                string shaderContains = JsonHelper.GetString(filtersJson, "shaderNameContains");
                string passContains = JsonHelper.GetString(filtersJson, "passNameContains");
                string keyword = JsonHelper.GetString(filtersJson, "keyword");
                string eventTypeFilter = JsonHelper.GetString(filtersJson, "eventType");
                int minVerts = JsonHelper.GetInt(filtersJson, "minVertexCount", 0);
                int minInstances = JsonHelper.GetInt(filtersJson, "minInstanceCount", 0);
                bool onlyBatchBreaks = JsonHelper.GetBool(filtersJson, "batchBreaks", false);
                int limit = JsonHelper.GetInt(filtersJson, "limit", 64);
                if (limit <= 0) limit = 64;
                if (limit > 512) limit = 512;

                bool needEventData = !string.IsNullOrEmpty(shaderContains)
                    || !string.IsNullOrEmpty(passContains)
                    || !string.IsNullOrEmpty(keyword)
                    || onlyBatchBreaks;

                var builder = JsonHelper.StartObject()
                    .Key("eventCount").Value(count)
                    .Key("filter").BeginObject()
                        .Key("shaderNameContains").Value(shaderContains ?? "")
                        .Key("passNameContains").Value(passContains ?? "")
                        .Key("keyword").Value(keyword ?? "")
                        .Key("eventType").Value(eventTypeFilter ?? "")
                        .Key("minVertexCount").Value(minVerts)
                        .Key("minInstanceCount").Value(minInstances)
                        .Key("batchBreaks").Value(onlyBatchBreaks)
                        .Key("limit").Value(limit)
                    .EndObject();

                builder.Key("matches").BeginArray();

                int total = Math.Min(count, events.Length);
                int matched = 0;
                int scanned = 0;
                StringComparison sc = StringComparison.OrdinalIgnoreCase;

                for (int i = 0; i < total && matched < limit; i++)
                {
                    scanned++;
                    object fdEvent = events.GetValue(i);
                    if (fdEvent == null) continue;
                    var t = fdEvent.GetType();

                    string typeStr = ReadEventType(fdEvent, t);
                    int verts = ReadInt(fdEvent, t, "vertexCount", 0);
                    int inst = ReadInt(fdEvent, t, "instanceCount", 0);

                    if (!string.IsNullOrEmpty(eventTypeFilter) &&
                        (typeStr == null || typeStr.IndexOf(eventTypeFilter, sc) < 0))
                        continue;
                    if (verts < minVerts) continue;
                    if (inst < minInstances) continue;

                    string shader = null, pass = null, cause = null;
                    string[] keywords = null;
                    if (needEventData)
                    {
                        SetLimit(i + 1);
                        object ed = TryGetEventData(i);
                        if (ed == null) continue;
                        var edT = ed.GetType();
                        shader = ReadString(ed, edT, "shaderName");
                        pass = ReadString(ed, edT, "passName");
                        keywords = ReadStringArray(ed, edT, "shaderKeywords");
                        cause = ReadString(ed, edT, "batchBreakCauseStr");
                        if (string.IsNullOrEmpty(cause))
                            cause = ReadString(ed, edT, "batchBreakCause");

                        if (!string.IsNullOrEmpty(shaderContains) &&
                            (shader == null || shader.IndexOf(shaderContains, sc) < 0)) continue;
                        if (!string.IsNullOrEmpty(passContains) &&
                            (pass == null || pass.IndexOf(passContains, sc) < 0)) continue;
                        if (!string.IsNullOrEmpty(keyword))
                        {
                            if (keywords == null) continue;
                            bool found = false;
                            foreach (var kw in keywords)
                                if (!string.IsNullOrEmpty(kw) && kw.IndexOf(keyword, sc) >= 0) { found = true; break; }
                            if (!found) continue;
                        }
                        if (onlyBatchBreaks && (string.IsNullOrEmpty(cause) || cause == "0")) continue;
                    }

                    matched++;
                    builder.BeginObject()
                        .Key("index").Value(i)
                        .Key("type").Value(typeStr ?? "")
                        .Key("vertexCount").Value(verts)
                        .Key("instanceCount").Value(inst);
                    if (shader != null) builder.Key("shader").Value(shader);
                    if (pass != null) builder.Key("pass").Value(pass);
                    if (!string.IsNullOrEmpty(cause) && cause != "0")
                        builder.Key("batchBreakCause").Value(cause);
                    builder.EndObject();
                }

                builder.EndArray()
                    .Key("matched").Value(matched)
                    .Key("scanned").Value(scanned)
                    .Key("truncated").Value(matched >= limit);

                return builder.ToString();
            }
            catch (Exception ex)
            {
                return BuildError("search-failed", ex.Message);
            }
        }

        /// <summary>
        /// Diff two events — shader/pass change, keyword set delta,
        /// render-state diff, RT change, geometry delta.
        /// </summary>
        public static string Compare(int indexA, int indexB)
        {
            if (!IsAvailable(out string err)) return err;

            try
            {
                int count = GetCount();
                if (count <= 0)
                    return BuildError("no-events", "Frame Debugger has no events — call framedebug/capture first");
                if (indexA < 0 || indexA >= count) return BuildError("index-out-of-range", $"indexA {indexA} not in [0,{count - 1}]");
                if (indexB < 0 || indexB >= count) return BuildError("index-out-of-range", $"indexB {indexB} not in [0,{count - 1}]");

                object edA = TryGetEventData(indexA);
                object edB = TryGetEventData(indexB);
                if (edA == null || edB == null)
                    return BuildError("no-event-data", "GetFrameEventData returned null for one or both indices");

                var tA = edA.GetType();
                var tB = edB.GetType();

                string shaderA = ReadString(edA, tA, "shaderName");
                string shaderB = ReadString(edB, tB, "shaderName");
                string passA = ReadString(edA, tA, "passName");
                string passB = ReadString(edB, tB, "passName");

                var kwA = ReadStringArray(edA, tA, "shaderKeywords") ?? new string[0];
                var kwB = ReadStringArray(edB, tB, "shaderKeywords") ?? new string[0];
                var setA = new HashSet<string>(kwA, StringComparer.Ordinal);
                var setB = new HashSet<string>(kwB, StringComparer.Ordinal);
                var added = new List<string>();
                var removed = new List<string>();
                foreach (var k in kwB) if (!string.IsNullOrEmpty(k) && !setA.Contains(k)) added.Add(k);
                foreach (var k in kwA) if (!string.IsNullOrEmpty(k) && !setB.Contains(k)) removed.Add(k);

                int rtWA = ReadInt(edA, tA, "rtWidth", 0),   rtHA = ReadInt(edA, tA, "rtHeight", 0);
                int rtWB = ReadInt(edB, tB, "rtWidth", 0),   rtHB = ReadInt(edB, tB, "rtHeight", 0);
                int rtCA = ReadInt(edA, tA, "rtCount", 0),   rtCB = ReadInt(edB, tB, "rtCount", 0);

                int vA = ReadInt(edA, tA, "vertexCount", 0), vB = ReadInt(edB, tB, "vertexCount", 0);
                int iA = ReadInt(edA, tA, "indexCount", 0),  iB = ReadInt(edB, tB, "indexCount", 0);
                int nA = ReadInt(edA, tA, "instanceCount", 0), nB = ReadInt(edB, tB, "instanceCount", 0);

                string causeA = ReadString(edA, tA, "batchBreakCauseStr"); if (string.IsNullOrEmpty(causeA)) causeA = ReadString(edA, tA, "batchBreakCause");
                string causeB = ReadString(edB, tB, "batchBreakCauseStr"); if (string.IsNullOrEmpty(causeB)) causeB = ReadString(edB, tB, "batchBreakCause");

                var builder = JsonHelper.StartObject()
                    .Key("indexA").Value(indexA)
                    .Key("indexB").Value(indexB)
                    .Key("shaderChanged").Value(!string.Equals(shaderA, shaderB, StringComparison.Ordinal))
                    .Key("passChanged").Value(!string.Equals(passA, passB, StringComparison.Ordinal))
                    .Key("shader").BeginObject()
                        .Key("a").Value(shaderA ?? "")
                        .Key("b").Value(shaderB ?? "")
                        .EndObject()
                    .Key("pass").BeginObject()
                        .Key("a").Value(passA ?? "")
                        .Key("b").Value(passB ?? "")
                        .EndObject();

                builder.Key("keywordsDiff").BeginObject();
                builder.Key("added").BeginArray();
                foreach (var k in added) builder.Value(k);
                builder.EndArray();
                builder.Key("removed").BeginArray();
                foreach (var k in removed) builder.Value(k);
                builder.EndArray();
                builder.Key("unchangedCount").Value(setA.Count - removed.Count);
                builder.EndObject();

                builder.Key("renderTarget").BeginObject()
                    .Key("changed").Value(rtWA != rtWB || rtHA != rtHB || rtCA != rtCB)
                    .Key("a").BeginObject().Key("width").Value(rtWA).Key("height").Value(rtHA).Key("count").Value(rtCA).EndObject()
                    .Key("b").BeginObject().Key("width").Value(rtWB).Key("height").Value(rtHB).Key("count").Value(rtCB).EndObject()
                    .EndObject();

                builder.Key("geometry").BeginObject()
                    .Key("vertexDelta").Value(vB - vA)
                    .Key("indexDelta").Value(iB - iA)
                    .Key("instanceDelta").Value(nB - nA)
                    .Key("a").BeginObject().Key("vertexCount").Value(vA).Key("indexCount").Value(iA).Key("instanceCount").Value(nA).EndObject()
                    .Key("b").BeginObject().Key("vertexCount").Value(vB).Key("indexCount").Value(iB).Key("instanceCount").Value(nB).EndObject()
                    .EndObject();

                builder.Key("batchBreak").BeginObject()
                    .Key("a").Value(causeA ?? "")
                    .Key("b").Value(causeB ?? "")
                    .Key("becameBreak").Value(
                        (string.IsNullOrEmpty(causeA) || causeA == "0") &&
                        !(string.IsNullOrEmpty(causeB) || causeB == "0"))
                    .EndObject();

                // Simple render-state diff (raster/blend/depth/stencil via ToString for brevity)
                string[] stateFields = { "rasterState", "blendState", "depthState", "stencilState", "stencilRef" };
                builder.Key("stateDiff").BeginArray();
                foreach (var f in stateFields)
                {
                    string a = DescribeField(edA, tA, f);
                    string b = DescribeField(edB, tB, f);
                    if (!string.Equals(a, b, StringComparison.Ordinal))
                    {
                        builder.BeginObject()
                            .Key("field").Value(f)
                            .Key("a").Value(a ?? "")
                            .Key("b").Value(b ?? "")
                            .EndObject();
                    }
                }
                builder.EndArray();

                return builder.ToString();
            }
            catch (Exception ex)
            {
                return BuildError("compare-failed", ex.Message);
            }
        }

        private static string DescribeField(object obj, Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) return null;
            try
            {
                object v = f.GetValue(obj);
                if (v == null) return "null";
                if (v.GetType().IsPrimitive || v is string) return v.ToString();
                // Nested struct — concat public fields
                var sb = new System.Text.StringBuilder();
                var vt = v.GetType();
                bool first = true;
                foreach (var sub in vt.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    object sv = sub.GetValue(v);
                    sb.Append(sub.Name).Append('=').Append(sv);
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        private class HotspotEntry
        {
            public int index;
            public string type;
            public int vertexCount;
            public int indexCount;
            public int instanceCount;
            public long cost;
        }

        private class ShaderStat
        {
            public string shaderName;
            public string passName;
            public int events;
            public long totalVerts;
            public long totalDrawCalls;
            public int firstIndex;
            public int lastIndex;
        }

        private class RtRange
        {
            public string rt;
            public int startIndex;
            public int endIndex;
        }

        private class BatchCause
        {
            public string cause;
            public int count;
            public int sampleIndex;
        }

        #endregion
    }
}
