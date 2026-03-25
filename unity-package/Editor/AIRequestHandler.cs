using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Manages AI query requests from the Inspector to the MCP server.
    /// Uses the existing WebSocket connection (Unity is server, MCP is client).
    /// Unity sends "ai/query" messages to the connected MCP client, which calls Claude CLI.
    /// Supports streaming responses via onChunk callback.
    /// </summary>
    public static class AIRequestHandler
    {
        private class PendingRequest
        {
            public Action<string> onChunk;
            public Action<string> onComplete;
            public Action<string> onStatus;
            public StringBuilder accumulated = new StringBuilder();
        }

        private static readonly Dictionary<string, PendingRequest> _pendingRequests =
            new Dictionary<string, PendingRequest>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Whether there are any pending AI requests awaiting responses.
        /// </summary>
        public static bool HasPendingRequests
        {
            get { lock (_lock) { return _pendingRequests.Count > 0; } }
        }

        /// <summary>
        /// Whether AI functionality is available (MCP server connected).
        /// </summary>
        public static bool IsAvailable => UnityAgentServer.IsClientConnected;

        /// <summary>
        /// Send an AI query (legacy single-callback overload for backward compatibility).
        /// </summary>
        public static void SendQuery(string prompt, string context, Action<string> onResponse)
        {
            SendQuery(prompt, context, null, onResponse);
        }

        /// <summary>
        /// Send an AI query with streaming support.
        /// </summary>
        /// <param name="prompt">The user's question or analysis prompt.</param>
        /// <param name="context">Optional context to include (asset code, info, etc.).</param>
        /// <param name="onChunk">Called for each streaming chunk (may be null).</param>
        /// <param name="onComplete">Called with the full response text when complete.</param>
        /// <param name="onStatus">Called with progress status updates (may be null).</param>
        public static void SendQuery(string prompt, string context, Action<string> onChunk, Action<string> onComplete, Action<string> onStatus = null, string language = null)
        {
            if (!IsAvailable)
            {
                onComplete?.Invoke("AI is not available. Ensure the MCP server is connected to the Unity WebSocket server.");
                return;
            }

            string id = Guid.NewGuid().ToString();

            // Build JSON message
            var msgBuilder = JsonHelper.StartObject()
                .Key("id").Value(id)
                .Key("method").Value("ai/query")
                .Key("params").BeginObject()
                    .Key("prompt").Value(prompt);

            if (!string.IsNullOrEmpty(context))
                msgBuilder.Key("context").Value(context);

            if (!string.IsNullOrEmpty(language))
                msgBuilder.Key("language").Value(language);

            // Send Unity project path so the agent can operate on project files
            string projectPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, ".."));
            msgBuilder.Key("projectPath").Value(projectPath);

            // Nano Banana (Gemini Image) settings
            string geminiApiKey = EditorPrefs.GetString("UnityAgent_GeminiApiKey", "");
            string geminiModel = EditorPrefs.GetString("UnityAgent_GeminiModel", "");
            if (!string.IsNullOrEmpty(geminiApiKey))
            {
                msgBuilder.Key("geminiApiKey").Value(geminiApiKey);
                msgBuilder.Key("geminiModel").Value(
                    !string.IsNullOrEmpty(geminiModel) ? geminiModel : "gemini-2.5-flash-image");
            }

            // Reference image (base64) if set
            string refImageBase64 = GetReferenceImageBase64();
            if (!string.IsNullOrEmpty(refImageBase64))
                msgBuilder.Key("referenceImage").Value(refImageBase64);

            msgBuilder.EndObject();
            string message = msgBuilder.ToString();

            lock (_lock)
            {
                _pendingRequests[id] = new PendingRequest
                {
                    onChunk = onChunk,
                    onComplete = onComplete,
                    onStatus = onStatus
                };
            }

            try
            {
                UnityAgentServer.SendToClient(message);
                Debug.Log($"[UnityAgent] AI query sent (id={id})");
            }
            catch (Exception ex)
            {
                lock (_lock) { _pendingRequests.Remove(id); }
                onComplete?.Invoke($"Failed to send AI query: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle a status update received from the MCP server.
        /// Called by UnityAgentServer when it receives an "ai/status" message.
        /// </summary>
        public static void HandleStatus(string id, string status)
        {
            PendingRequest request = null;
            lock (_lock)
            {
                _pendingRequests.TryGetValue(id, out request);
            }

            if (request != null)
            {
                request.onStatus?.Invoke(status);
            }
        }

        /// <summary>
        /// Handle a streaming chunk received from the MCP server.
        /// Called by UnityAgentServer when it receives an "ai/chunk" message.
        /// </summary>
        public static void HandleChunk(string id, string chunk)
        {
            PendingRequest request = null;
            lock (_lock)
            {
                _pendingRequests.TryGetValue(id, out request);
            }

            if (request != null)
            {
                request.accumulated.Append(chunk);
                request.onChunk?.Invoke(chunk);
            }
        }

        /// <summary>
        /// Handle an AI response received from the MCP server.
        /// Called by UnityAgentServer when it receives an "ai/response" message.
        /// </summary>
        public static void HandleResponse(string id, string responseText)
        {
            PendingRequest request = null;
            lock (_lock)
            {
                if (_pendingRequests.TryGetValue(id, out request))
                    _pendingRequests.Remove(id);
            }

            if (request != null)
            {
                request.onComplete?.Invoke(responseText);
            }
            else
            {
                Debug.LogWarning($"[UnityAgent] Received AI response for unknown id: {id}");
            }
        }

        /// <summary>
        /// Handle an AI error response.
        /// </summary>
        public static void HandleError(string id, string errorMessage)
        {
            PendingRequest request = null;
            lock (_lock)
            {
                if (_pendingRequests.TryGetValue(id, out request))
                    _pendingRequests.Remove(id);
            }

            request?.onComplete?.Invoke($"AI Error: {errorMessage}");
        }

        /// <summary>
        /// Get the reference image from the active AIChatWindow as base64 PNG.
        /// </summary>
        private static string GetReferenceImageBase64()
        {
            var windows = Resources.FindObjectsOfTypeAll<AIChatWindow>();
            if (windows.Length == 0) return null;

            var refImage = windows[0].ReferenceImage;
            if (refImage == null) return null;

            try
            {
                // Need readable texture — make a copy via RenderTexture
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
