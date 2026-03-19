using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Collects Unity console errors and exceptions in real-time.
    /// Hooks into Application.logMessageReceived to capture all log entries.
    /// Also captures compilation errors from CompilationPipeline.
    /// </summary>
    [InitializeOnLoad]
    public static class ErrorCollector
    {
        [Serializable]
        public class ErrorEntry
        {
            public string id;
            public string message;
            public string stackTrace;
            public string logType;       // "Error", "Exception", "Assert", "Warning"
            public string timestamp;
            public string sourceFile;    // Extracted from stack trace if possible
            public int sourceLine;       // Extracted from stack trace if possible
            public bool isCompileError;
        }

        private static readonly List<ErrorEntry> _errors = new List<ErrorEntry>();
        private static readonly List<ErrorEntry> _warnings = new List<ErrorEntry>();
        private static readonly object _lock = new object();
        private static int _errorIdCounter;

        /// <summary>
        /// Fired when a new error is collected. UI can subscribe to refresh.
        /// </summary>
        public static event Action OnErrorsChanged;

        static ErrorCollector()
        {
            Application.logMessageReceived -= OnLogMessageReceived;
            Application.logMessageReceived += OnLogMessageReceived;

#if UNITY_2019_1_OR_NEWER
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished -= OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += OnCompilationFinished;
#endif
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                AddEntry(condition, stackTrace, type.ToString(), false);
            }
            else if (type == LogType.Warning)
            {
                AddWarning(condition, stackTrace);
            }
        }

#if UNITY_2019_1_OR_NEWER
        private static void OnCompilationFinished(string assemblyPath,
            UnityEditor.Compilation.CompilerMessage[] messages)
        {
            foreach (var msg in messages)
            {
                if (msg.type == UnityEditor.Compilation.CompilerMessageType.Error)
                {
                    string sourceInfo = !string.IsNullOrEmpty(msg.file)
                        ? $"{msg.file}({msg.line},{msg.column})"
                        : "";
                    AddEntry(msg.message, sourceInfo, "CompileError", true);
                }
            }
        }
#endif

        private static void AddEntry(string condition, string stackTrace, string logType, bool isCompileError)
        {
            var entry = new ErrorEntry
            {
                id = $"err_{_errorIdCounter++}",
                message = condition,
                stackTrace = stackTrace ?? "",
                logType = logType,
                timestamp = DateTime.Now.ToString("HH:mm:ss"),
                isCompileError = isCompileError
            };

            // Try to extract source file and line from stack trace
            ExtractSourceInfo(entry);

            lock (_lock)
            {
                _errors.Add(entry);
                // Cap at 200 errors
                if (_errors.Count > 200)
                    _errors.RemoveAt(0);
            }

            OnErrorsChanged?.Invoke();
        }

        private static void AddWarning(string condition, string stackTrace)
        {
            var entry = new ErrorEntry
            {
                id = $"warn_{_errorIdCounter++}",
                message = condition,
                stackTrace = stackTrace ?? "",
                logType = "Warning",
                timestamp = DateTime.Now.ToString("HH:mm:ss"),
                isCompileError = false
            };

            ExtractSourceInfo(entry);

            lock (_lock)
            {
                _warnings.Add(entry);
                if (_warnings.Count > 200)
                    _warnings.RemoveAt(0);
            }

            OnErrorsChanged?.Invoke();
        }

        private static void ExtractSourceInfo(ErrorEntry entry)
        {
            // Try to extract file:line from stack trace or compile error format
            // Common patterns:
            //   Assets/Scripts/Foo.cs(42,10): error CS1001: ...
            //   at Namespace.Class.Method () [0x00000] in /path/to/file.cs:42
            string text = entry.isCompileError ? entry.stackTrace : entry.stackTrace;
            if (string.IsNullOrEmpty(text)) text = entry.message;

            // Pattern: filename.cs(line,col)
            var match = System.Text.RegularExpressions.Regex.Match(text,
                @"([\w/\\]+\.cs)\((\d+)");
            if (match.Success)
            {
                entry.sourceFile = match.Groups[1].Value;
                int.TryParse(match.Groups[2].Value, out entry.sourceLine);
                return;
            }

            // Pattern: in /path/file.cs:line
            match = System.Text.RegularExpressions.Regex.Match(text,
                @"in\s+([\w/\\.]+\.cs):(\d+)");
            if (match.Success)
            {
                entry.sourceFile = match.Groups[1].Value;
                int.TryParse(match.Groups[2].Value, out entry.sourceLine);
            }
        }

        /// <summary>
        /// Get all collected errors as JSON.
        /// </summary>
        public static string GetErrorsJson(bool includeWarnings = false, int limit = 50)
        {
            lock (_lock)
            {
                var builder = JsonHelper.StartObject();
                builder.Key("errors").BeginArray();

                int start = Math.Max(0, _errors.Count - limit);
                for (int i = start; i < _errors.Count; i++)
                {
                    var e = _errors[i];
                    builder.BeginObject()
                        .Key("id").Value(e.id)
                        .Key("message").Value(e.message)
                        .Key("stackTrace").Value(e.stackTrace)
                        .Key("logType").Value(e.logType)
                        .Key("timestamp").Value(e.timestamp)
                        .Key("sourceFile").Value(e.sourceFile ?? "")
                        .Key("sourceLine").Value(e.sourceLine)
                        .Key("isCompileError").Value(e.isCompileError)
                    .EndObject();
                }

                builder.EndArray();
                builder.Key("errorCount").Value(_errors.Count);

                if (includeWarnings)
                {
                    builder.Key("warnings").BeginArray();
                    int wStart = Math.Max(0, _warnings.Count - limit);
                    for (int i = wStart; i < _warnings.Count; i++)
                    {
                        var w = _warnings[i];
                        builder.BeginObject()
                            .Key("id").Value(w.id)
                            .Key("message").Value(w.message)
                            .Key("stackTrace").Value(w.stackTrace)
                            .Key("logType").Value(w.logType)
                            .Key("timestamp").Value(w.timestamp)
                            .Key("sourceFile").Value(w.sourceFile ?? "")
                            .Key("sourceLine").Value(w.sourceLine)
                        .EndObject();
                    }
                    builder.EndArray();
                    builder.Key("warningCount").Value(_warnings.Count);
                }

                return builder.ToString();
            }
        }

        /// <summary>
        /// Get a specific error entry by ID.
        /// </summary>
        public static ErrorEntry GetError(string id)
        {
            lock (_lock)
            {
                foreach (var e in _errors)
                    if (e.id == id) return e;
                foreach (var w in _warnings)
                    if (w.id == id) return w;
            }
            return null;
        }

        /// <summary>
        /// Get all errors as a list (snapshot).
        /// </summary>
        public static List<ErrorEntry> GetErrors()
        {
            lock (_lock)
            {
                return new List<ErrorEntry>(_errors);
            }
        }

        /// <summary>
        /// Get all warnings as a list (snapshot).
        /// </summary>
        public static List<ErrorEntry> GetWarnings()
        {
            lock (_lock)
            {
                return new List<ErrorEntry>(_warnings);
            }
        }

        /// <summary>
        /// Check if there are any unresolved compilation errors.
        /// </summary>
        public static bool HasCompileErrors
        {
            get
            {
                lock (_lock)
                {
                    foreach (var e in _errors)
                        if (e.isCompileError) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Clear all collected errors and warnings.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _errors.Clear();
                _warnings.Clear();
            }
            OnErrorsChanged?.Invoke();
        }

        /// <summary>
        /// Clear only errors (keep warnings).
        /// </summary>
        public static void ClearErrors()
        {
            lock (_lock)
            {
                _errors.Clear();
            }
            OnErrorsChanged?.Invoke();
        }

        /// <summary>
        /// Handle project/readFile request.
        /// </summary>
        public static string ReadProjectFile(string filePath)
        {
            try
            {
                string projectRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, ".."));

                // Resolve relative paths from project root
                string fullPath;
                if (System.IO.Path.IsPathRooted(filePath))
                    fullPath = filePath;
                else
                    fullPath = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(projectRoot, filePath));

                // Security: ensure path is within project
                if (!fullPath.StartsWith(projectRoot))
                {
                    return JsonHelper.StartObject()
                        .Key("error").Value("Path is outside the project directory")
                        .ToString();
                }

                if (!System.IO.File.Exists(fullPath))
                {
                    return JsonHelper.StartObject()
                        .Key("error").Value($"File not found: {filePath}")
                        .ToString();
                }

                string content = System.IO.File.ReadAllText(fullPath);
                return JsonHelper.StartObject()
                    .Key("path").Value(filePath)
                    .Key("content").Value(content)
                    .Key("lineCount").Value(content.Split('\n').Length)
                    .ToString();
            }
            catch (Exception ex)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value(ex.Message)
                    .ToString();
            }
        }

        /// <summary>
        /// Handle project/writeFile request.
        /// </summary>
        public static string WriteProjectFile(string filePath, string content)
        {
            try
            {
                string projectRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, ".."));

                string fullPath;
                if (System.IO.Path.IsPathRooted(filePath))
                    fullPath = filePath;
                else
                    fullPath = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(projectRoot, filePath));

                // Security: ensure path is within project
                if (!fullPath.StartsWith(projectRoot))
                {
                    return JsonHelper.StartObject()
                        .Key("error").Value("Path is outside the project directory")
                        .ToString();
                }

                // Create directory if needed
                string dir = System.IO.Path.GetDirectoryName(fullPath);
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                System.IO.File.WriteAllText(fullPath, content);

                // Trigger Unity asset refresh
                AssetDatabase.Refresh();

                return JsonHelper.StartObject()
                    .Key("success").Value(true)
                    .Key("path").Value(filePath)
                    .ToString();
            }
            catch (Exception ex)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value(ex.Message)
                    .ToString();
            }
        }

        /// <summary>
        /// Handle project/listFiles request.
        /// </summary>
        public static string ListProjectFiles(string directory, string pattern)
        {
            try
            {
                string projectRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, ".."));

                string searchDir;
                if (string.IsNullOrEmpty(directory))
                    searchDir = System.IO.Path.Combine(projectRoot, "Assets");
                else if (System.IO.Path.IsPathRooted(directory))
                    searchDir = directory;
                else
                    searchDir = System.IO.Path.GetFullPath(
                        System.IO.Path.Combine(projectRoot, directory));

                if (!searchDir.StartsWith(projectRoot))
                {
                    return JsonHelper.StartObject()
                        .Key("error").Value("Path is outside the project directory")
                        .ToString();
                }

                if (!System.IO.Directory.Exists(searchDir))
                {
                    return JsonHelper.StartObject()
                        .Key("error").Value($"Directory not found: {directory}")
                        .ToString();
                }

                string searchPattern = string.IsNullOrEmpty(pattern) ? "*.cs" : pattern;
                var files = System.IO.Directory.GetFiles(searchDir, searchPattern,
                    System.IO.SearchOption.AllDirectories);

                var builder = JsonHelper.StartObject();
                builder.Key("directory").Value(directory ?? "Assets");
                builder.Key("pattern").Value(searchPattern);
                builder.Key("files").BeginArray();

                // Limit to 200 files
                int count = Math.Min(files.Length, 200);
                for (int i = 0; i < count; i++)
                {
                    // Convert to relative path
                    string relativePath = files[i].Substring(projectRoot.Length)
                        .TrimStart(System.IO.Path.DirectorySeparatorChar,
                                   System.IO.Path.AltDirectorySeparatorChar);
                    builder.Value(relativePath.Replace('\\', '/'));
                }

                builder.EndArray();
                builder.Key("totalCount").Value(files.Length);
                builder.Key("truncated").Value(files.Length > 200);

                return builder.ToString();
            }
            catch (Exception ex)
            {
                return JsonHelper.StartObject()
                    .Key("error").Value(ex.Message)
                    .ToString();
            }
        }
    }
}
