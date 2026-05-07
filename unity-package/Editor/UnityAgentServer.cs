using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Tracks Node.js availability for the headless AI runner.
    /// Each AI request spawns its own Node process via AIRequestHandler — this
    /// class only locates the node executable and the bundled Server~/headless.mjs
    /// entry point.
    ///
    /// The public API (EnsureRunning / IsRunning / IsClientConnected) is preserved
    /// so existing tool windows compile unchanged; they all now mean "can we run
    /// Claude headlessly on this machine".
    /// </summary>
    [InitializeOnLoad]
    public static class UnityAgentServer
    {
        private const string PackageRelativeScript = "Packages/com.unity-agent/Server~/headless.mjs";

        private static bool _ready;
        private static string _nodeDir;

        static UnityAgentServer()
        {
            EditorApplication.delayCall += () => EnsureRunning();
        }

        public static bool IsRunning => _ready;
        public static bool IsClientConnected => _ready;

        public static void EnsureRunning()
        {
            if (_ready) return;

            #if UNITY_EDITOR_WIN
            _nodeDir = FindNodeDirectory();
            #else
            _nodeDir = FindNodeDirectoryUnix();
            #endif

            if (string.IsNullOrEmpty(_nodeDir))
            {
                Debug.LogWarning(
                    "[UnityAgent] Node.js 18+ not found. AI features will be disabled.\n" +
                    "Install Node.js from https://nodejs.org — or set EditorPrefs 'UnityAgent_NodeDir' " +
                    "to your Node.js install folder.");
                return;
            }

            string script = GetHeadlessScriptPath();
            if (!File.Exists(script))
            {
                Debug.LogError($"[UnityAgent] Bundled headless runner missing at: {script}\n" +
                               "The Server~ folder may not have shipped with this package.");
                return;
            }

            _ready = true;
            Debug.Log($"[UnityAgent] Node.js found at: {_nodeDir}");
        }

        public static string GetNodeExecutable()
        {
            if (string.IsNullOrEmpty(_nodeDir)) return null;
            #if UNITY_EDITOR_WIN
            return Path.Combine(_nodeDir, "node.exe");
            #else
            return Path.Combine(_nodeDir, "node");
            #endif
        }

        public static string GetHeadlessScriptPath()
        {
            try { return Path.GetFullPath(PackageRelativeScript); }
            catch { return PackageRelativeScript; }
        }

        #region Node.js Discovery (Windows)

        private static string FindNodeDirectory()
        {
            string custom = EditorPrefs.GetString("UnityAgent_NodeDir", "");
            if (!string.IsNullOrEmpty(custom) && File.Exists(Path.Combine(custom, "node.exe")))
                return custom;

            string[] commonPaths =
            {
                @"C:\Program Files\nodejs",
                @"C:\Program Files (x86)\nodejs",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            };
            foreach (var dir in commonPaths)
                if (File.Exists(Path.Combine(dir, "node.exe")))
                    return dir;

            string nvmDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");
            if (Directory.Exists(nvmDir))
            {
                string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
                if (!string.IsNullOrEmpty(nvmSymlink) && File.Exists(Path.Combine(nvmSymlink, "node.exe")))
                    return nvmSymlink;

                string latestVersion = FindLatestVersionDir(nvmDir);
                if (latestVersion != null && File.Exists(Path.Combine(latestVersion, "node.exe")))
                    return latestVersion;
            }

            string voltaNode = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Volta", "tools", "image", "node");
            if (Directory.Exists(voltaNode))
            {
                string latest = FindLatestVersionDir(voltaNode);
                if (latest != null && File.Exists(Path.Combine(latest, "node.exe")))
                    return latest;
            }

            string fnmDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "fnm", "node-versions");
            if (Directory.Exists(fnmDir))
            {
                string latest = FindLatestVersionDir(fnmDir);
                if (latest != null)
                {
                    string installDir = Path.Combine(latest, "installation");
                    if (File.Exists(Path.Combine(installDir, "node.exe")))
                        return installDir;
                }
            }

            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Node.js"))
                {
                    if (key != null)
                    {
                        string installPath = key.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(installPath) &&
                            File.Exists(Path.Combine(installPath, "node.exe")))
                            return installPath;
                    }
                }
            }
            catch { }

            string foundInPath = FindExecutableInRegistryPath("node.exe");
            if (foundInPath != null)
                return Path.GetDirectoryName(foundInPath);

            string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in envPath.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir.Trim(), "node.exe");
                if (File.Exists(candidate))
                    return dir.Trim();
            }

            return null;
        }

        private static string FindExecutableInRegistryPath(string exeName)
        {
            try
            {
                string machinePath = Environment.GetEnvironmentVariable("PATH",
                    EnvironmentVariableTarget.Machine) ?? "";
                string userPath = Environment.GetEnvironmentVariable("PATH",
                    EnvironmentVariableTarget.User) ?? "";
                string combined = machinePath + ";" + userPath;

                foreach (var dir in combined.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }
            return null;
        }

        #endregion

        #region Node.js Discovery (Unix)

        private static string FindNodeDirectoryUnix()
        {
            string[] commonPaths =
            {
                "/usr/local/bin",
                "/usr/bin",
                "/opt/homebrew/bin",
            };
            foreach (var dir in commonPaths)
                if (File.Exists(Path.Combine(dir, "node")))
                    return dir;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string nvmVersions = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvmVersions))
            {
                string latest = FindLatestVersionDir(nvmVersions);
                if (latest != null)
                {
                    string binDir = Path.Combine(latest, "bin");
                    if (File.Exists(Path.Combine(binDir, "node")))
                        return binDir;
                }
            }

            string voltaBin = Path.Combine(home, ".volta", "bin");
            if (File.Exists(Path.Combine(voltaBin, "node")))
                return voltaBin;

            string fnmBase = Path.Combine(home, ".local", "share", "fnm", "node-versions");
            if (Directory.Exists(fnmBase))
            {
                string latest = FindLatestVersionDir(fnmBase);
                if (latest != null)
                {
                    string binDir = Path.Combine(latest, "installation", "bin");
                    if (File.Exists(Path.Combine(binDir, "node")))
                        return binDir;
                }
            }

            string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in envPath.Split(':'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                if (File.Exists(Path.Combine(dir.Trim(), "node")))
                    return dir.Trim();
            }

            return null;
        }

        #endregion

        private static string FindLatestVersionDir(string parentDir)
        {
            try
            {
                string[] dirs = Directory.GetDirectories(parentDir);
                if (dirs.Length == 0) return null;

                string best = null;
                Version bestVersion = null;

                foreach (var dir in dirs)
                {
                    string name = Path.GetFileName(dir);
                    string versionStr = name.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                        ? name.Substring(1) : name;

                    if (Version.TryParse(versionStr, out Version ver))
                    {
                        if (bestVersion == null || ver > bestVersion)
                        {
                            bestVersion = ver;
                            best = dir;
                        }
                    }
                }

                return best;
            }
            catch { return null; }
        }
    }
}
