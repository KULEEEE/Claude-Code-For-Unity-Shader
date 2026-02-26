using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// JsonUtility wrapper that handles root-level arrays, dictionaries, and manual JSON building.
    /// Unity's JsonUtility cannot serialize root-level arrays or Dictionary types.
    /// </summary>
    public static class JsonHelper
    {
        #region Serialization Helpers

        public static string ToJsonArray<T>(T[] array)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < array.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonUtility.ToJson(array[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string ToJsonArray<T>(List<T> list)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonUtility.ToJson(list[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        #endregion

        #region Manual JSON Builder

        public static JsonBuilder StartObject()
        {
            return new JsonBuilder(JsonBuilder.ContainerType.Object);
        }

        public static JsonBuilder StartArray()
        {
            return new JsonBuilder(JsonBuilder.ContainerType.Array);
        }

        public class JsonBuilder
        {
            public enum ContainerType { Object, Array }

            private readonly StringBuilder _sb = new StringBuilder();
            private readonly Stack<ContainerType> _stack = new Stack<ContainerType>();
            private readonly Stack<bool> _hasItems = new Stack<bool>();

            public JsonBuilder(ContainerType type)
            {
                _stack.Push(type);
                _hasItems.Push(false);
                _sb.Append(type == ContainerType.Object ? '{' : '[');
            }

            private void AddSeparator()
            {
                if (_hasItems.Peek())
                    _sb.Append(',');
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
            }

            public JsonBuilder Key(string key)
            {
                AddSeparator();
                _sb.Append('"');
                _sb.Append(EscapeString(key));
                _sb.Append("\":");
                // Reset separator state — value will follow immediately
                _hasItems.Pop();
                _hasItems.Push(false);
                return this;
            }

            public JsonBuilder Value(string val)
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    // In object context, this is value after key, mark as having items
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }

                if (val == null)
                    _sb.Append("null");
                else
                {
                    _sb.Append('"');
                    _sb.Append(EscapeString(val));
                    _sb.Append('"');
                }
                return this;
            }

            public JsonBuilder Value(int val)
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
                _sb.Append(val);
                return this;
            }

            public JsonBuilder Value(long val)
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
                _sb.Append(val);
                return this;
            }

            public JsonBuilder Value(float val)
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
                _sb.Append(val.ToString(CultureInfo.InvariantCulture));
                return this;
            }

            public JsonBuilder Value(bool val)
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
                _sb.Append(val ? "true" : "false");
                return this;
            }

            public JsonBuilder RawValue(string rawJson)
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
                _sb.Append(rawJson);
                return this;
            }

            public JsonBuilder BeginObject()
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
                _stack.Push(ContainerType.Object);
                _hasItems.Push(false);
                _sb.Append('{');
                return this;
            }

            public JsonBuilder EndObject()
            {
                _stack.Pop();
                _hasItems.Pop();
                _sb.Append('}');
                return this;
            }

            public JsonBuilder BeginArray()
            {
                if (_stack.Peek() == ContainerType.Array)
                    AddSeparator();
                else
                {
                    _hasItems.Pop();
                    _hasItems.Push(true);
                }
                _stack.Push(ContainerType.Array);
                _hasItems.Push(false);
                _sb.Append('[');
                return this;
            }

            public JsonBuilder EndArray()
            {
                _stack.Pop();
                _hasItems.Pop();
                _sb.Append(']');
                return this;
            }

            public override string ToString()
            {
                // Close any remaining containers
                while (_stack.Count > 0)
                {
                    var type = _stack.Pop();
                    _hasItems.Pop();
                    _sb.Append(type == ContainerType.Object ? '}' : ']');
                }
                return _sb.ToString();
            }
        }

        #endregion

        #region Minimal JSON Parsing

        /// <summary>
        /// Extract a string value from JSON by key name (simple top-level extraction).
        /// </summary>
        public static string GetString(string json, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return null;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return null;

            int start = json.IndexOf('"', colonIndex + 1);
            if (start < 0) return null;
            start++;

            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    char next = json[i];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append('\\'); sb.Append(next); break;
                    }
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Extract a nested JSON object by key name (returns raw JSON string).
        /// </summary>
        public static string GetObject(string json, string key)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return null;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return null;

            int start = json.IndexOf('{', colonIndex + 1);
            if (start < 0) return null;

            int depth = 0;
            bool inString = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && inString) { i++; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return json.Substring(start, i - start + 1); }
            }
            return null;
        }

        /// <summary>
        /// Extract a boolean value from JSON by key name.
        /// </summary>
        public static bool GetBool(string json, string key, bool defaultValue = false)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return defaultValue;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return defaultValue;

            string rest = json.Substring(colonIndex + 1).TrimStart();
            if (rest.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (rest.StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }

        /// <summary>
        /// Extract an integer value from JSON by key name.
        /// </summary>
        public static int GetInt(string json, string key, int defaultValue = 0)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return defaultValue;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return defaultValue;

            string rest = json.Substring(colonIndex + 1).TrimStart();
            var sb = new StringBuilder();
            foreach (char c in rest)
            {
                if (c == '-' || (c >= '0' && c <= '9'))
                    sb.Append(c);
                else if (sb.Length > 0)
                    break;
            }
            if (sb.Length > 0 && int.TryParse(sb.ToString(), out int result))
                return result;
            return defaultValue;
        }

        /// <summary>
        /// Extract a float value from JSON by key name.
        /// </summary>
        public static float GetFloat(string json, string key, float defaultValue = 0f)
        {
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return defaultValue;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return defaultValue;

            string rest = json.Substring(colonIndex + 1).TrimStart();
            var sb = new StringBuilder();
            foreach (char c in rest)
            {
                if (c == '-' || c == '.' || (c >= '0' && c <= '9'))
                    sb.Append(c);
                else if (sb.Length > 0)
                    break;
            }
            if (sb.Length > 0 && float.TryParse(sb.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float result))
                return result;
            return defaultValue;
        }

        /// <summary>
        /// Extract a JSON array as a list of raw JSON object strings.
        /// </summary>
        public static List<string> GetArrayObjects(string json, string key)
        {
            var results = new List<string>();
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return results;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return results;

            int arrayStart = json.IndexOf('[', colonIndex + 1);
            if (arrayStart < 0) return results;

            int depth = 0;
            bool inString = false;
            int objStart = -1;
            for (int i = arrayStart + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && inString) { i++; continue; }
                if (c == '"') { inString = !inString; continue; }
                if (inString) continue;

                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        results.Add(json.Substring(objStart, i - objStart + 1));
                        objStart = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }
            return results;
        }

        /// <summary>
        /// Extract a JSON array of strings.
        /// </summary>
        public static List<string> GetStringArray(string json, string key)
        {
            var results = new List<string>();
            string searchKey = "\"" + key + "\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return results;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return results;

            int arrayStart = json.IndexOf('[', colonIndex + 1);
            if (arrayStart < 0) return results;

            bool inArray = true;
            for (int i = arrayStart + 1; i < json.Length && inArray; i++)
            {
                char c = json[i];
                if (c == ']') break;
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    for (; i < json.Length; i++)
                    {
                        c = json[i];
                        if (c == '\\' && i + 1 < json.Length) { i++; sb.Append(json[i]); }
                        else if (c == '"') break;
                        else sb.Append(c);
                    }
                    results.Add(sb.ToString());
                }
            }
            return results;
        }

        private static string EscapeString(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
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
                        if (c < 0x20)
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        #endregion
    }
}
