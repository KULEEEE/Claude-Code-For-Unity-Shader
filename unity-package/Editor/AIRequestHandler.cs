using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Dispatches AI queries by spawning the bundled headless Node runner
    /// (Server~/headless.mjs) per request. One line of JSON goes down stdin;
    /// JSON-line events stream back on stdout and are dispatched on the main
    /// thread to chunk/status/complete/image callbacks.
    /// </summary>
    public static class AIRequestHandler
    {
        private class PendingRequest
        {
            public Action<string> onChunk;
            public Action<string> onComplete;
            public Action<string> onStatus;
            public Process process;
            public StringBuilder accumulated = new StringBuilder();
            public bool finished;
        }

        private static readonly Dictionary<string, PendingRequest> _pendingRequests =
            new Dictionary<string, PendingRequest>();
        private static readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private static readonly object _lock = new object();
        private static bool _updateHooked;

        public static bool HasPendingRequests
        {
            get { lock (_lock) { return _pendingRequests.Count > 0; } }
        }

        /// <summary>
        /// AI is available whenever Node.js is discoverable.
        /// </summary>
        public static bool IsAvailable => UnityAgentServer.IsClientConnected;

        public static void SendQuery(string prompt, string context, Action<string> onResponse)
        {
            SendQuery(prompt, context, null, onResponse);
        }

        public static void SendQuery(
            string prompt,
            string context,
            Action<string> onChunk,
            Action<string> onComplete,
            Action<string> onStatus = null,
            string language = null)
        {
            if (!IsAvailable)
            {
                onComplete?.Invoke("AI is not available. Ensure Node.js 18+ is installed.");
                return;
            }

            string id = Guid.NewGuid().ToString();
            var msgBuilder = JsonHelper.StartObject()
                .Key("id").Value(id)
                .Key("method").Value("ai/query")
                .Key("params").BeginObject()
                    .Key("prompt").Value(prompt);

            if (!string.IsNullOrEmpty(context))
                msgBuilder.Key("context").Value(context);
            if (!string.IsNullOrEmpty(language))
                msgBuilder.Key("language").Value(language);

            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            msgBuilder.Key("projectPath").Value(projectPath);

            AppendBackendOptions(msgBuilder, id);

            msgBuilder.EndObject();
            Launch(id, msgBuilder.ToString(), onChunk, onComplete, onStatus);
        }

        public static void SendImageEnhance(
            string prompt,
            Action<string> onStatus,
            Action<string> onComplete,
            string language = null)
        {
            if (!IsAvailable)
            {
                onComplete?.Invoke("AI is not available.");
                return;
            }

            string id = Guid.NewGuid().ToString();
            var msgBuilder = JsonHelper.StartObject()
                .Key("id").Value(id)
                .Key("method").Value("image/enhance")
                .Key("params").BeginObject()
                    .Key("prompt").Value(prompt);

            if (!string.IsNullOrEmpty(language))
                msgBuilder.Key("language").Value(language);

            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            msgBuilder.Key("projectPath").Value(projectPath);

            AppendBackendOptions(msgBuilder, id);

            msgBuilder.EndObject();
            Launch(id, msgBuilder.ToString(), null, onComplete, onStatus);
        }

        private static void AppendBackendOptions(JsonHelper.JsonBuilder msgBuilder, string id)
        {
            string imageBackend = EditorPrefs.GetString("UnityAgent_ImageBackend", "gemini");
            msgBuilder.Key("imageBackend").Value(imageBackend);

            if (imageBackend == "comfyui")
            {
                string comfyUrl = EditorPrefs.GetString("UnityAgent_ComfyUIUrl", "http://127.0.0.1:8188");
                msgBuilder.Key("comfyuiUrl").Value(comfyUrl);
            }

            string geminiApiKey = EditorPrefs.GetString("UnityAgent_GeminiApiKey", "");
            string geminiModel = EditorPrefs.GetString("UnityAgent_GeminiModel", "");
            if (!string.IsNullOrEmpty(geminiApiKey))
            {
                msgBuilder.Key("geminiApiKey").Value(geminiApiKey);
                msgBuilder.Key("geminiModel").Value(
                    !string.IsNullOrEmpty(geminiModel) ? geminiModel : "gemini-2.5-flash-image");
            }

            string refImageBase64 = GetReferenceImageBase64();
            if (!string.IsNullOrEmpty(refImageBase64))
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"unity-agent-ref-{id}.b64");
                File.WriteAllText(tempPath, refImageBase64);
                msgBuilder.Key("referenceImagePath").Value(tempPath);
            }
        }

        private static void Launch(
            string id,
            string payload,
            Action<string> onChunk,
            Action<string> onComplete,
            Action<string> onStatus)
        {
            string nodeExe = UnityAgentServer.GetNodeExecutable();
            string scriptPath = UnityAgentServer.GetHeadlessScriptPath();

            if (nodeExe == null || !File.Exists(nodeExe))
            {
                onComplete?.Invoke("AI Error: Node.js executable not found.");
                return;
            }
            if (!File.Exists(scriptPath))
            {
                onComplete?.Invoke($"AI Error: Headless script missing at {scriptPath}");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = nodeExe,
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var pending = new PendingRequest
            {
                onChunk = onChunk,
                onComplete = onComplete,
                onStatus = onStatus,
                process = proc,
            };

            proc.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;
                string line = args.Data;
                _mainThreadActions.Enqueue(() => HandleEvent(id, line));
            };
            proc.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.Data)) return;
                Debug.Log($"[UnityAgent] [headless] {args.Data}");
            };
            proc.Exited += (_, __) =>
            {
                _mainThreadActions.Enqueue(() => FinalizeIfNeeded(id, null));
            };

            lock (_lock) { _pendingRequests[id] = pending; }
            EnsureUpdateHook();

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.StandardInput.WriteLine(payload);
                proc.StandardInput.Close();
                Debug.Log($"[UnityAgent] AI query sent (id={id})");
            }
            catch (Exception ex)
            {
                lock (_lock) { _pendingRequests.Remove(id); }
                onComplete?.Invoke($"Failed to launch headless runner: {ex.Message}");
            }
        }

        private static void HandleEvent(string id, string jsonLine)
        {
            string type = JsonHelper.GetString(jsonLine, "type");
            if (string.IsNullOrEmpty(type)) return;

            PendingRequest request;
            lock (_lock) { _pendingRequests.TryGetValue(id, out request); }
            if (request == null) return;

            switch (type)
            {
                case "status":
                    request.onStatus?.Invoke(JsonHelper.GetString(jsonLine, "data") ?? "");
                    break;
                case "chunk":
                    {
                        string chunk = JsonHelper.GetString(jsonLine, "data") ?? "";
                        request.accumulated.Append(chunk);
                        request.onChunk?.Invoke(chunk);
                    }
                    break;
                case "image":
                    {
                        string data = JsonHelper.GetString(jsonLine, "data") ?? "";
                        string description = JsonHelper.GetString(jsonLine, "description") ?? "";
                        if (!string.IsNullOrEmpty(data))
                            NanoBananaReceiver.HandleImageReceived(data, description);
                    }
                    break;
                case "result":
                    FinalizeIfNeeded(id, JsonHelper.GetString(jsonLine, "data") ?? "");
                    break;
                case "error":
                    FinalizeIfNeeded(id, $"AI Error: {JsonHelper.GetString(jsonLine, "data") ?? ""}");
                    break;
            }
        }

        private static void FinalizeIfNeeded(string id, string finalText)
        {
            PendingRequest request;
            lock (_lock)
            {
                if (!_pendingRequests.TryGetValue(id, out request)) return;
                if (request.finished && finalText == null) return;
                if (finalText != null) request.finished = true;
                if (request.finished) _pendingRequests.Remove(id);
            }

            if (finalText == null)
            {
                if (request.finished) return;
                finalText = request.accumulated.Length > 0
                    ? request.accumulated.ToString()
                    : "(headless runner exited without a result)";
                request.finished = true;
                lock (_lock) { _pendingRequests.Remove(id); }
            }

            try { request.onComplete?.Invoke(finalText); }
            catch (Exception ex) { Debug.LogError($"[UnityAgent] onComplete threw: {ex}"); }

            try
            {
                if (request.process != null && !request.process.HasExited)
                {
                    try { request.process.Kill(); } catch { }
                }
                request.process?.Dispose();
            }
            catch { }
        }

        private static void EnsureUpdateHook()
        {
            if (_updateHooked) return;
            _updateHooked = true;
            EditorApplication.update += PumpMainThread;
        }

        private static void PumpMainThread()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError($"[UnityAgent] Dispatch error: {ex}"); }
            }
        }

        private static string GetReferenceImageBase64()
        {
            var windows = Resources.FindObjectsOfTypeAll<AIChatWindow>();
            if (windows.Length == 0) return null;

            var refImage = windows[0].ReferenceImage;
            if (refImage == null) return null;

            try
            {
                RenderTexture rt = RenderTexture.GetTemporary(refImage.width, refImage.height);
                Graphics.Blit(refImage, rt);
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;

                Texture2D readable = new Texture2D(refImage.width, refImage.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, refImage.width, refImage.height), 0, 0);
                readable.Apply();

                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                byte[] pngData = readable.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(readable);
                return Convert.ToBase64String(pngData);
            }
            catch
            {
                return null;
            }
        }
    }
}
