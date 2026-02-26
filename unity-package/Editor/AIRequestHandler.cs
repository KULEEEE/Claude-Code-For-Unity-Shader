using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Manages AI query requests from the Shader Inspector to the MCP server.
    /// Uses the existing WebSocket connection (Unity is server, MCP is client).
    /// Unity sends "ai/query" messages to the connected MCP client, which calls Claude CLI.
    /// </summary>
    public static class AIRequestHandler
    {
        private static readonly Dictionary<string, Action<string>> _pendingCallbacks =
            new Dictionary<string, Action<string>>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Whether there are any pending AI requests awaiting responses.
        /// </summary>
        public static bool HasPendingRequests
        {
            get { lock (_lock) { return _pendingCallbacks.Count > 0; } }
        }

        /// <summary>
        /// Whether AI functionality is available (MCP server connected).
        /// </summary>
        public static bool IsAvailable => ShaderMCPServer.IsClientConnected;

        /// <summary>
        /// Send an AI query through the WebSocket to the MCP server.
        /// The MCP server will call Claude CLI and return the response.
        /// </summary>
        /// <param name="prompt">The user's question or analysis prompt.</param>
        /// <param name="shaderContext">Optional shader code/info context to include.</param>
        /// <param name="onResponse">Callback invoked with the AI response text.</param>
        public static void SendQuery(string prompt, string shaderContext, Action<string> onResponse)
        {
            if (!IsAvailable)
            {
                onResponse?.Invoke("AI is not available. Ensure the MCP server is connected to the Unity WebSocket server.");
                return;
            }

            string id = Guid.NewGuid().ToString();

            // Build JSON message
            var msgBuilder = JsonHelper.StartObject()
                .Key("id").Value(id)
                .Key("method").Value("ai/query")
                .Key("params").BeginObject()
                    .Key("prompt").Value(prompt);

            if (!string.IsNullOrEmpty(shaderContext))
                msgBuilder.Key("shaderContext").Value(shaderContext);

            msgBuilder.EndObject();
            string message = msgBuilder.ToString();

            lock (_lock)
            {
                _pendingCallbacks[id] = onResponse;
            }

            try
            {
                ShaderMCPServer.SendToClient(message);
                Debug.Log($"[ShaderInspector] AI query sent (id={id})");
            }
            catch (Exception ex)
            {
                lock (_lock) { _pendingCallbacks.Remove(id); }
                onResponse?.Invoke($"Failed to send AI query: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle an AI response received from the MCP server.
        /// Called by ShaderMCPServer when it receives an "ai/response" message.
        /// </summary>
        public static void HandleResponse(string id, string responseText)
        {
            Action<string> callback = null;
            lock (_lock)
            {
                if (_pendingCallbacks.TryGetValue(id, out callback))
                    _pendingCallbacks.Remove(id);
            }

            if (callback != null)
            {
                callback.Invoke(responseText);
            }
            else
            {
                Debug.LogWarning($"[ShaderInspector] Received AI response for unknown id: {id}");
            }
        }

        /// <summary>
        /// Handle an AI error response.
        /// </summary>
        public static void HandleError(string id, string errorMessage)
        {
            Action<string> callback = null;
            lock (_lock)
            {
                if (_pendingCallbacks.TryGetValue(id, out callback))
                    _pendingCallbacks.Remove(id);
            }

            callback?.Invoke($"AI Error: {errorMessage}");
        }
    }
}
