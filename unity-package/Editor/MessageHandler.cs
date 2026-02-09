using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShaderMCP.Editor
{
    #region Protocol Types

    [Serializable]
    public class WebSocketRequest
    {
        public string id;
        public string method;
        public string @params; // Raw JSON string for params
    }

    [Serializable]
    public class WebSocketResponse
    {
        public string id;
        public string result; // Raw JSON string
    }

    [Serializable]
    public class WebSocketError
    {
        public int code;
        public string message;
    }

    #endregion

    /// <summary>
    /// Routes incoming WebSocket messages to registered handlers.
    /// </summary>
    public class MessageHandler
    {
        public delegate string HandlerFunc(string paramsJson);

        private readonly Dictionary<string, HandlerFunc> _handlers = new Dictionary<string, HandlerFunc>();

        /// <summary>
        /// Register a handler for a method name (e.g., "shader/list").
        /// </summary>
        public void RegisterHandler(string method, HandlerFunc handler)
        {
            _handlers[method] = handler;
        }

        /// <summary>
        /// Process a raw JSON message and return the response JSON.
        /// </summary>
        public string ProcessMessage(string rawJson)
        {
            string id = null;
            try
            {
                id = JsonHelper.GetString(rawJson, "id");
                string method = JsonHelper.GetString(rawJson, "method");
                string paramsJson = JsonHelper.GetObject(rawJson, "params");

                if (string.IsNullOrEmpty(id))
                {
                    return BuildErrorResponse("unknown", -1, "Missing 'id' field in request");
                }

                if (string.IsNullOrEmpty(method))
                {
                    return BuildErrorResponse(id, -1, "Missing 'method' field in request");
                }

                if (!_handlers.TryGetValue(method, out HandlerFunc handler))
                {
                    return BuildErrorResponse(id, -2, $"Unknown method: {method}");
                }

                string result = handler(paramsJson ?? "{}");

                return JsonHelper.StartObject()
                    .Key("id").Value(id)
                    .Key("result").RawValue(result)
                    .ToString();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderMCP] Error processing message: {ex}");
                return BuildErrorResponse(id ?? "unknown", -3, ex.Message);
            }
        }

        private string BuildErrorResponse(string id, int code, string message)
        {
            return JsonHelper.StartObject()
                .Key("id").Value(id)
                .Key("error").BeginObject()
                    .Key("code").Value(code)
                    .Key("message").Value(message)
                .EndObject()
                .ToString();
        }
    }
}
