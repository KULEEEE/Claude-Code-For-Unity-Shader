using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using UnityEngine;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Static utility class that wraps SVN command-line operations.
    /// All commands run via System.Diagnostics.Process with --non-interactive flag.
    /// </summary>
    public static class SvnHelper
    {
        [Serializable]
        public class SvnLogEntry
        {
            public int revision;
            public string author;
            public string date;
            public string message;
        }

        [Serializable]
        public class SvnStatusEntry
        {
            public char statusCode;
            public string filePath;
            public string statusText;
        }

        private const int DefaultTimeoutMs = 15000;

        /// <summary>
        /// Check if SVN CLI is installed and available in PATH.
        /// </summary>
        public static bool IsSvnInstalled()
        {
            var (exitCode, _, _) = RunSvnCommand("--version --quiet");
            return exitCode == 0;
        }

        /// <summary>
        /// Check if a file is under SVN version control.
        /// </summary>
        public static bool IsFileUnderSvnControl(string absPath)
        {
            var (exitCode, _, _) = RunSvnCommand($"info \"{absPath}\"");
            return exitCode == 0;
        }

        /// <summary>
        /// Get SVN log entries for a file.
        /// </summary>
        public static List<SvnLogEntry> GetLog(string absPath, int limit = 50)
        {
            var entries = new List<SvnLogEntry>();
            var (exitCode, stdout, stderr) = RunSvnCommand($"log --xml -l {limit} \"{absPath}\"");

            if (exitCode != 0)
            {
                UnityEngine.Debug.LogWarning($"[SVN] log failed: {stderr}");
                return entries;
            }

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(stdout);

                var logEntries = doc.SelectNodes("//logentry");
                if (logEntries == null) return entries;

                foreach (XmlNode node in logEntries)
                {
                    var entry = new SvnLogEntry();

                    var revAttr = node.Attributes?["revision"];
                    if (revAttr != null && int.TryParse(revAttr.Value, out int rev))
                        entry.revision = rev;

                    var authorNode = node.SelectSingleNode("author");
                    entry.author = authorNode?.InnerText ?? "unknown";

                    var dateNode = node.SelectSingleNode("date");
                    if (dateNode != null)
                    {
                        if (DateTime.TryParse(dateNode.InnerText, out DateTime dt))
                            entry.date = dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                        else
                            entry.date = dateNode.InnerText;
                    }

                    var msgNode = node.SelectSingleNode("msg");
                    entry.message = msgNode?.InnerText ?? "";

                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[SVN] Failed to parse log XML: {ex.Message}");
            }

            return entries;
        }

        /// <summary>
        /// Get unified diff for a specific revision.
        /// </summary>
        public static string GetDiff(string absPath, int revision)
        {
            var (exitCode, stdout, stderr) = RunSvnCommand($"diff -c {revision} \"{absPath}\"", 30000);

            if (exitCode != 0)
            {
                return $"Error getting diff: {stderr}";
            }

            return stdout;
        }

        /// <summary>
        /// Get working copy diff (uncommitted changes) for a file.
        /// </summary>
        public static string GetWorkingDiff(string absPath)
        {
            var (exitCode, stdout, stderr) = RunSvnCommand($"diff \"{absPath}\"", 30000);

            if (exitCode != 0)
                return $"Error getting diff: {stderr}";

            return stdout;
        }

        /// <summary>
        /// SVN update on a file or directory.
        /// </summary>
        public static (bool success, string output) Update(string absPath)
        {
            var (exitCode, stdout, stderr) = RunSvnCommand($"update \"{absPath}\"", 30000);
            if (exitCode != 0)
                return (false, $"Update failed: {stderr}");
            return (true, stdout);
        }

        /// <summary>
        /// SVN update on multiple files.
        /// </summary>
        public static (bool success, string output) Update(List<string> absPaths)
        {
            string paths = BuildPathArgs(absPaths);
            var (exitCode, stdout, stderr) = RunSvnCommand($"update {paths}", 30000);
            if (exitCode != 0)
                return (false, $"Update failed: {stderr}");
            return (true, stdout);
        }

        /// <summary>
        /// SVN commit a file with a message.
        /// </summary>
        public static (bool success, string output) Commit(string absPath, string message)
        {
            // Escape quotes in message
            string escapedMsg = message.Replace("\"", "\\\"");
            var (exitCode, stdout, stderr) = RunSvnCommand($"commit -m \"{escapedMsg}\" \"{absPath}\"", 30000);
            if (exitCode != 0)
                return (false, $"Commit failed: {stderr}");
            return (true, stdout);
        }

        /// <summary>
        /// SVN commit multiple files with a message.
        /// </summary>
        public static (bool success, string output) Commit(List<string> absPaths, string message)
        {
            string escapedMsg = message.Replace("\"", "\\\"");
            string paths = BuildPathArgs(absPaths);
            var (exitCode, stdout, stderr) = RunSvnCommand($"commit -m \"{escapedMsg}\" {paths}", 30000);
            if (exitCode != 0)
                return (false, $"Commit failed: {stderr}");
            return (true, stdout);
        }

        /// <summary>
        /// SVN revert a file.
        /// </summary>
        public static (bool success, string output) Revert(string absPath)
        {
            var (exitCode, stdout, stderr) = RunSvnCommand($"revert \"{absPath}\"");
            if (exitCode != 0)
                return (false, $"Revert failed: {stderr}");
            return (true, stdout);
        }

        /// <summary>
        /// SVN revert multiple files.
        /// </summary>
        public static (bool success, string output) Revert(List<string> absPaths)
        {
            string paths = BuildPathArgs(absPaths);
            var (exitCode, stdout, stderr) = RunSvnCommand($"revert {paths}");
            if (exitCode != 0)
                return (false, $"Revert failed: {stderr}");
            return (true, stdout);
        }

        private static string BuildPathArgs(List<string> absPaths)
        {
            var sb = new StringBuilder();
            foreach (var p in absPaths)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('"');
                sb.Append(p);
                sb.Append('"');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Get SVN status for a file or directory.
        /// </summary>
        public static List<SvnStatusEntry> GetStatus(string absPath)
        {
            var entries = new List<SvnStatusEntry>();
            var (exitCode, stdout, stderr) = RunSvnCommand($"status \"{absPath}\"");

            if (exitCode != 0)
            {
                UnityEngine.Debug.LogWarning($"[SVN] status failed: {stderr}");
                return entries;
            }

            if (string.IsNullOrWhiteSpace(stdout))
                return entries;

            var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Length < 2) continue;

                char code = line[0];
                string path = line.Length > 7 ? line.Substring(7).Trim() : line.Substring(1).Trim();

                entries.Add(new SvnStatusEntry
                {
                    statusCode = code,
                    filePath = path,
                    statusText = GetStatusText(code)
                });
            }

            return entries;
        }

        /// <summary>
        /// Get SVN info for a file (current revision, URL, etc.) as raw text.
        /// </summary>
        public static string GetInfo(string absPath)
        {
            var (exitCode, stdout, stderr) = RunSvnCommand($"info \"{absPath}\"");
            if (exitCode != 0)
                return $"Error: {stderr}";
            return stdout;
        }

        /// <summary>
        /// Convert Unity asset path to absolute disk path.
        /// </summary>
        public static string AssetPathToAbsolute(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        }

        private static string GetStatusText(char code)
        {
            switch (code)
            {
                case 'M': return "Modified";
                case 'A': return "Added";
                case 'D': return "Deleted";
                case 'C': return "Conflicted";
                case '?': return "Unversioned";
                case '!': return "Missing";
                case 'R': return "Replaced";
                case 'X': return "External";
                case 'I': return "Ignored";
                case '~': return "Obstructed";
                default: return code.ToString();
            }
        }

        private static (int exitCode, string stdout, string stderr) RunSvnCommand(string arguments, int timeoutMs = DefaultTimeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = GetSvnExecutable(),
                    Arguments = $"--non-interactive {arguments}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (var process = new Process { StartInfo = psi })
                {
                    var stdoutBuilder = new StringBuilder();
                    var stderrBuilder = new StringBuilder();

                    process.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
                    process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        process.Kill();
                        return (-1, "", "SVN command timed out");
                    }

                    return (process.ExitCode, stdoutBuilder.ToString().TrimEnd(), stderrBuilder.ToString().TrimEnd());
                }
            }
            catch (Exception ex)
            {
                return (-1, "", $"Failed to run SVN: {ex.Message}");
            }
        }

        private static string GetSvnExecutable()
        {
            // Check EditorPrefs for custom SVN path
            string customPath = UnityEditor.EditorPrefs.GetString("UnityAgent_SvnPath", "");
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
                return customPath;

            // Check common TortoiseSVN location on Windows
            string tortoisePath = @"C:\Program Files\TortoiseSVN\bin\svn.exe";
            if (File.Exists(tortoisePath))
                return tortoisePath;

            // Fall back to PATH
            return "svn";
        }
    }
}
