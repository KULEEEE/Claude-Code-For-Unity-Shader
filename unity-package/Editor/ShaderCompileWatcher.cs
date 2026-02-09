using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// Watches for shader compilation events by filtering Unity console log messages.
    /// </summary>
    [InitializeOnLoad]
    public static class ShaderCompileWatcher
    {
        [Serializable]
        public class ShaderLogEntry
        {
            public string timestamp;
            public string severity;
            public string message;
            public string stackTrace;
        }

        private static readonly List<ShaderLogEntry> _logs = new List<ShaderLogEntry>();
        private static readonly object _lock = new object();
        private const int MaxLogEntries = 500;

        static ShaderCompileWatcher()
        {
            Application.logMessageReceived -= OnLogMessage;
            Application.logMessageReceived += OnLogMessage;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            // Filter for shader-related log messages
            if (!IsShaderRelated(condition))
                return;

            var entry = new ShaderLogEntry
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                severity = LogTypeToSeverity(type),
                message = condition,
                stackTrace = stackTrace
            };

            lock (_lock)
            {
                _logs.Add(entry);
                if (_logs.Count > MaxLogEntries)
                    _logs.RemoveAt(0);
            }
        }

        private static bool IsShaderRelated(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;

            string lower = message.ToLowerInvariant();
            return lower.Contains("shader")
                || lower.Contains("material")
                || lower.Contains("hlsl")
                || lower.Contains("cginc")
                || lower.Contains("compile error")
                || lower.Contains("gpu")
                || lower.Contains("rendering")
                || lower.Contains("srp")
                || lower.Contains("variant");
        }

        private static string LogTypeToSeverity(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                    return "error";
                case LogType.Warning:
                    return "warning";
                default:
                    return "info";
            }
        }

        /// <summary>
        /// Get filtered shader logs as JSON.
        /// </summary>
        public static string GetLogsJson(string severityFilter = "all")
        {
            var builder = JsonHelper.StartObject()
                .Key("logs").BeginArray();

            lock (_lock)
            {
                foreach (var entry in _logs)
                {
                    if (severityFilter != "all" && entry.severity != severityFilter)
                        continue;

                    builder.BeginObject()
                        .Key("timestamp").Value(entry.timestamp)
                        .Key("severity").Value(entry.severity)
                        .Key("message").Value(entry.message)
                        .Key("stackTrace").Value(entry.stackTrace)
                    .EndObject();
                }
            }

            builder.EndArray()
                .Key("totalCount").Value(_logs.Count);

            return builder.ToString();
        }

        /// <summary>
        /// Clear all stored logs.
        /// </summary>
        public static void ClearLogs()
        {
            lock (_lock)
            {
                _logs.Clear();
            }
        }
    }
}
