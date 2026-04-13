using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityAgent.Editor
{
    /// <summary>
    /// Background WebSocket server (RFC 6455) on localhost:8090.
    /// No EditorWindow — starts automatically when any tool window needs it.
    /// Registers handlers for both Shader tools and Error Solver tools.
    /// </summary>
    [InitializeOnLoad]
    public static class UnityAgentServer
    {
        private const int DefaultPort = 8090;
        private const string WebSocketMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private class ClientConnection
        {
            public TcpClient client;
            public NetworkStream stream;
        }

        // Server state
        private static TcpListener _listener;
        private static readonly List<ClientConnection> _clients = new List<ClientConnection>();
        private static bool _isRunning;
        private static int _port = DefaultPort;
        private static readonly MessageHandler _messageHandler = new MessageHandler();
        private static readonly object _logLock = new object();
        private static bool _handlersRegistered;

        // MCP Server process
        private static Process _mcpProcess;
        private static bool _mcpRunning;

        static UnityAgentServer()
        {
            RegisterHandlers();
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            EditorApplication.quitting -= OnQuitting;
            EditorApplication.quitting += OnQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // Auto-restart after domain reload
            EditorApplication.delayCall += () =>
            {
                if (SessionState.GetBool("UnityAgent_WasRunning", false))
                {
                    StartServer();
                }
            };
        }

        private static void OnBeforeAssemblyReload()
        {
            SessionState.SetBool("UnityAgent_WasRunning", _isRunning);
            StopMCPServer();

            try
            {
                foreach (var conn in _clients)
                {
                    try { conn.stream?.Close(); conn.client?.Close(); }
                    catch { }
                }
                _clients.Clear();

                if (_listener != null)
                {
                    _listener.Server.Close();
                    _listener.Stop();
                    _listener = null;
                }
            }
            catch { }

            _isRunning = false;
        }

        #region Public API

        /// <summary>
        /// Whether the WebSocket server is running.
        /// </summary>
        public static bool IsRunning => _isRunning;

        /// <summary>
        /// Whether any MCP client is connected.
        /// </summary>
        public static bool IsClientConnected
        {
            get
            {
                foreach (var conn in _clients)
                    if (conn.client != null && conn.client.Connected)
                        return true;
                return false;
            }
        }

        /// <summary>
        /// Call this from any tool window's OnEnable to ensure the server is running.
        /// Automatically starts both the WebSocket server and the MCP bridge process.
        /// </summary>
        public static void EnsureRunning()
        {
            if (!_isRunning)
                StartServer();
            if (!_mcpRunning)
                StartMCPServer();
        }

        /// <summary>
        /// Send a message to the first available MCP client.
        /// </summary>
        public static void SendToClient(string message)
        {
            foreach (var conn in _clients)
            {
                if (conn.client != null && conn.client.Connected && conn.stream != null)
                {
                    SendWebSocketMessage(conn.stream, message);
                    return;
                }
            }
            throw new InvalidOperationException("No client connected");
        }

        #endregion

        #region Server Lifecycle

        public static void StartServer()
        {
            if (_isRunning) return;

            try
            {
                RegisterHandlers();
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Server.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start();
                _isRunning = true;
                Debug.Log($"[UnityAgent] Server started on ws://localhost:{_port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityAgent] Failed to start server: {ex}");
            }
        }

        public static void StopServer()
        {
            _isRunning = false;
            StopMCPServer();

            try
            {
                foreach (var conn in _clients)
                {
                    try
                    {
                        conn.client.Client.LingerState = new LingerOption(true, 0);
                        conn.stream?.Close();
                        conn.client?.Close();
                    }
                    catch { }
                }
                _clients.Clear();

                if (_listener != null)
                {
                    try { _listener.Server.LingerState = new LingerOption(true, 0); } catch { }
                    try { _listener.Server.Close(); } catch { }
                    _listener.Stop();
                    _listener = null;
                }
                Debug.Log("[UnityAgent] Server stopped");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityAgent] Error stopping server: {ex.Message}");
            }
        }

        public static void StartMCPServer()
        {
            if (_mcpRunning) return;

            try
            {
                var startInfo = new ProcessStartInfo();

                #if UNITY_EDITOR_WIN
                // Find node.exe absolutely — never rely on Unity's PATH
                string nodeDir = FindNodeDirectory();
                if (nodeDir == null)
                {
                    Debug.LogError(
                        "[UnityAgent] Node.js not found. Install Node.js 18+ from https://nodejs.org\n" +
                        "Or set EditorPrefs key 'UnityAgent_NodeDir' to your Node.js install folder.");
                    return;
                }

                // Create a temp .bat launcher that sets PATH before running npx.
                // This is the only fully reliable way to propagate PATH to ALL
                // child/grandchild processes on Windows (cmd, node, npm, npx shims).
                string nodeExe = Path.Combine(nodeDir, "node.exe");
                string launcherPath = Path.Combine(Path.GetTempPath(), "unity-agent-mcp-launcher.bat");

                // Build a complete PATH from all sources
                var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                allPaths.Add(nodeDir);
                string machinePath = Environment.GetEnvironmentVariable("PATH",
                    EnvironmentVariableTarget.Machine) ?? "";
                string userPath = Environment.GetEnvironmentVariable("PATH",
                    EnvironmentVariableTarget.User) ?? "";
                string processPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var p in machinePath.Split(';'))
                    if (!string.IsNullOrWhiteSpace(p)) allPaths.Add(p.Trim());
                foreach (var p in userPath.Split(';'))
                    if (!string.IsNullOrWhiteSpace(p)) allPaths.Add(p.Trim());
                foreach (var p in processPath.Split(';'))
                    if (!string.IsNullOrWhiteSpace(p)) allPaths.Add(p.Trim());
                string fullPath = string.Join(";", allPaths);

                // Try npx-cli.js first, fall back to npx.cmd
                string npxScript = FindNpxScript(nodeDir);
                string npxCmd;
                if (npxScript != null)
                    npxCmd = $"\"{nodeExe}\" \"{npxScript}\" -y unity-agent-tools";
                else
                    npxCmd = $"\"{Path.Combine(nodeDir, "npx.cmd")}\" -y unity-agent-tools";

                string batContent =
                    "@echo off\r\n" +
                    $"set \"PATH={fullPath}\"\r\n" +
                    $"{npxCmd}\r\n";
                File.WriteAllText(launcherPath, batContent, Encoding.Default);

                startInfo.FileName = "cmd";
                startInfo.Arguments = $"/c \"{launcherPath}\"";

                Debug.Log($"[UnityAgent] Using node: {nodeExe}");
                #else
                // macOS / Linux: find node directory
                string unixNodeDir = FindNodeDirectoryUnix();
                if (unixNodeDir != null)
                {
                    // Create a launcher shell script for reliable PATH propagation
                    string unixLauncher = Path.Combine(Path.GetTempPath(), "unity-agent-mcp-launcher.sh");
                    string npxPath = Path.Combine(unixNodeDir, "npx");
                    string unixNpxScript = FindNpxScript(unixNodeDir);

                    string shellCmd;
                    if (File.Exists(npxPath))
                        shellCmd = $"\"{npxPath}\" -y unity-agent-tools";
                    else if (unixNpxScript != null)
                        shellCmd = $"\"{Path.Combine(unixNodeDir, "node")}\" \"{unixNpxScript}\" -y unity-agent-tools";
                    else
                        shellCmd = "npx -y unity-agent-tools";

                    string curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    string shContent =
                        "#!/bin/sh\n" +
                        $"export PATH=\"{unixNodeDir}:{curPath}\"\n" +
                        $"{shellCmd}\n";
                    File.WriteAllText(unixLauncher, shContent);

                    startInfo.FileName = "/bin/sh";
                    startInfo.Arguments = $"\"{unixLauncher}\"";
                }
                else
                {
                    startInfo.FileName = "npx";
                    startInfo.Arguments = "-y unity-agent-tools";
                }
                #endif

                startInfo.UseShellExecute = false;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardInput = true;
                startInfo.CreateNoWindow = true;

                _mcpProcess = new Process();
                _mcpProcess.StartInfo = startInfo;
                _mcpProcess.EnableRaisingEvents = true;
                _mcpProcess.Exited += (sender, args) =>
                {
                    _mcpRunning = false;
                };

                _mcpProcess.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                        Debug.Log($"[UnityAgent] [MCP] {args.Data}");
                };

                _mcpProcess.Start();
                _mcpProcess.BeginErrorReadLine();
                _mcpRunning = true;

                Debug.Log("[UnityAgent] MCP server process started");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityAgent] Failed to start MCP server: {ex.Message}. " +
                    "Ensure Node.js 18+ is installed. " +
                    "You can set EditorPrefs 'UnityAgent_NodeDir' to your Node.js folder.");
                _mcpRunning = false;
            }
        }

        #region Node.js Discovery

        /// <summary>
        /// Find Node.js installation directory on Windows.
        /// Searches: EditorPrefs → common paths → NVM → Volta → fnm → registry → system PATH.
        /// </summary>
        private static string FindNodeDirectory()
        {
            // 1. User-configured override
            string custom = EditorPrefs.GetString("UnityAgent_NodeDir", "");
            if (!string.IsNullOrEmpty(custom) && File.Exists(Path.Combine(custom, "node.exe")))
                return custom;

            // 2. Common install locations
            string[] commonPaths =
            {
                @"C:\Program Files\nodejs",
                @"C:\Program Files (x86)\nodejs",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            };
            foreach (var dir in commonPaths)
                if (File.Exists(Path.Combine(dir, "node.exe")))
                    return dir;

            // 3. NVM for Windows: %APPDATA%\nvm\<version>\node.exe
            string nvmDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");
            if (Directory.Exists(nvmDir))
            {
                // Also check NVM symlink location
                string nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
                if (!string.IsNullOrEmpty(nvmSymlink) && File.Exists(Path.Combine(nvmSymlink, "node.exe")))
                    return nvmSymlink;

                // Find latest version folder
                string latestVersion = FindLatestVersionDir(nvmDir);
                if (latestVersion != null && File.Exists(Path.Combine(latestVersion, "node.exe")))
                    return latestVersion;
            }

            // 4. Volta: %LOCALAPPDATA%\Volta\tools\image\node\<version>\
            string voltaNode = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Volta", "tools", "image", "node");
            if (Directory.Exists(voltaNode))
            {
                string latest = FindLatestVersionDir(voltaNode);
                if (latest != null && File.Exists(Path.Combine(latest, "node.exe")))
                    return latest;
            }

            // 5. fnm: %LOCALAPPDATA%\fnm_multishells\<id>\ or %APPDATA%\fnm\node-versions\<ver>\installation
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

            // 6. Windows Registry
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

            // 7. Search system + user PATH from registry (not from Unity process!)
            string foundInPath = FindExecutableInRegistryPath("node.exe");
            if (foundInPath != null)
                return Path.GetDirectoryName(foundInPath);

            // 8. Last resort: current process PATH
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

        /// <summary>
        /// Find Node.js installation directory on macOS/Linux.
        /// </summary>
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

            // NVM
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

            // Volta
            string voltaBin = Path.Combine(home, ".volta", "bin");
            if (File.Exists(Path.Combine(voltaBin, "node")))
                return voltaBin;

            // fnm
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

            // PATH
            string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in envPath.Split(':'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                if (File.Exists(Path.Combine(dir.Trim(), "node")))
                    return dir.Trim();
            }

            return null;
        }

        /// <summary>
        /// Find npx-cli.js script relative to node directory.
        /// </summary>
        private static string FindNpxScript(string nodeDir)
        {
            string[] candidates =
            {
                Path.Combine(nodeDir, "node_modules", "npm", "bin", "npx-cli.js"),
                Path.Combine(nodeDir, "node_modules", "npm", "bin", "npx-cli.cjs"),
                // npm 10+
                Path.Combine(nodeDir, "node_modules", "npm", "bin", "npx-cli.mjs"),
                // Older npm
                Path.Combine(nodeDir, "node_modules", "npm", "bin", "npx"),
                // lib folder variant
                Path.Combine(nodeDir, "lib", "node_modules", "npm", "bin", "npx-cli.js"),
                Path.Combine(nodeDir, "lib", "node_modules", "npm", "bin", "npx-cli.cjs"),
            };

            foreach (var path in candidates)
                if (File.Exists(path))
                    return path;

            return null;
        }

        /// <summary>
        /// Search the system + user PATH from Windows registry for an executable.
        /// This bypasses Unity's process PATH which may be stale.
        /// </summary>
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

        /// <summary>
        /// Find the latest semver-like directory in a parent folder (e.g., "v20.11.0").
        /// </summary>
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
                    // Strip 'v' prefix if present
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

        #endregion

        public static void StopMCPServer()
        {
            if (!_mcpRunning || _mcpProcess == null) return;

            try
            {
                if (!_mcpProcess.HasExited)
                {
                    #if UNITY_EDITOR_WIN
                    try
                    {
                        var killProc = Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/PID {_mcpProcess.Id} /T /F",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        });
                        killProc?.WaitForExit(5000);
                    }
                    catch { _mcpProcess.Kill(); }
                    #else
                    _mcpProcess.Kill();
                    #endif
                    _mcpProcess.WaitForExit(3000);
                }
                _mcpProcess.Dispose();
            }
            catch { }
            finally
            {
                _mcpProcess = null;
                _mcpRunning = false;
            }
        }

        private static void OnQuitting()
        {
            StopServer();
        }

        #endregion

        #region Main Thread Update

        private static void Update()
        {
            if (!_isRunning || _listener == null) return;

            try
            {
                if (_listener.Pending())
                {
                    var newClient = _listener.AcceptTcpClient();
                    HandleNewConnection(newClient);
                }

                for (int i = _clients.Count - 1; i >= 0; i--)
                {
                    var conn = _clients[i];
                    if (conn.client == null || !conn.client.Connected)
                    {
                        try { conn.stream?.Close(); conn.client?.Close(); } catch { }
                        _clients.RemoveAt(i);
                        continue;
                    }

                    if (conn.client.Available > 0)
                    {
                        ReadWebSocketFrames(conn);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityAgent] Update error: {ex.Message}");
            }
        }

        #endregion

        #region WebSocket Handshake

        private static void HandleNewConnection(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                client.ReceiveTimeout = 5000;
                client.NoDelay = true;

                var requestBuilder = new StringBuilder();
                byte[] buffer = new byte[4096];
                while (true)
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    requestBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    if (requestBuilder.ToString().Contains("\r\n\r\n")) break;
                }
                string request = requestBuilder.ToString();

                if (!request.ToLowerInvariant().Contains("upgrade: websocket"))
                {
                    client.Close();
                    return;
                }

                string key = null;
                string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    {
                        int colonPos = line.IndexOf(':');
                        string raw = line.Substring(colonPos + 1);
                        var sb = new StringBuilder();
                        foreach (char c in raw)
                        {
                            if (c > ' ') sb.Append(c);
                        }
                        key = sb.ToString().Trim();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(key))
                {
                    client.Close();
                    return;
                }

                string acceptKey = ComputeWebSocketAcceptKey(key);

                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                  "Upgrade: websocket\r\n" +
                                  "Connection: Upgrade\r\n" +
                                  "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                                  "\r\n";

                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();

                client.ReceiveTimeout = 0;
                _clients.Add(new ClientConnection { client = client, stream = stream });
                Debug.Log($"[UnityAgent] Client connected (total: {_clients.Count})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityAgent] Handshake failed: {ex.Message}");
                client.Close();
            }
        }

        private static string ComputeWebSocketAcceptKey(string key)
        {
            string combined = key + WebSocketMagicGuid;
            byte[] inputBytes = Encoding.ASCII.GetBytes(combined);
            byte[] hash = ComputeSHA1(inputBytes);
            return Convert.ToBase64String(hash);
        }

        private static byte[] ComputeSHA1(byte[] message)
        {
            uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;
            long msgBitLen = (long)message.Length * 8;

            int padLen = (56 - (message.Length + 1) % 64);
            if (padLen < 0) padLen += 64;
            byte[] padded = new byte[message.Length + 1 + padLen + 8];
            Array.Copy(message, padded, message.Length);
            padded[message.Length] = 0x80;

            for (int i = 0; i < 8; i++)
                padded[padded.Length - 1 - i] = (byte)(msgBitLen >> (i * 8));

            uint[] w = new uint[80];
            for (int offset = 0; offset < padded.Length; offset += 64)
            {
                for (int i = 0; i < 16; i++)
                    w[i] = ((uint)padded[offset + i * 4] << 24) |
                            ((uint)padded[offset + i * 4 + 1] << 16) |
                            ((uint)padded[offset + i * 4 + 2] << 8) |
                            ((uint)padded[offset + i * 4 + 3]);

                for (int i = 16; i < 80; i++)
                    w[i] = RotateLeft(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);

                uint a = h0, b = h1, c = h2, d = h3, e = h4;
                for (int i = 0; i < 80; i++)
                {
                    uint f, k;
                    if (i < 20)      { f = (b & c) | (~b & d);           k = 0x5A827999; }
                    else if (i < 40) { f = b ^ c ^ d;                    k = 0x6ED9EBA1; }
                    else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
                    else             { f = b ^ c ^ d;                    k = 0xCA62C1D6; }

                    uint temp = RotateLeft(a, 5) + f + e + k + w[i];
                    e = d; d = c; c = RotateLeft(b, 30); b = a; a = temp;
                }
                h0 += a; h1 += b; h2 += c; h3 += d; h4 += e;
            }

            byte[] result = new byte[20];
            WriteBE(result, 0, h0); WriteBE(result, 4, h1); WriteBE(result, 8, h2);
            WriteBE(result, 12, h3); WriteBE(result, 16, h4);
            return result;
        }

        private static uint RotateLeft(uint v, int c) => (v << c) | (v >> (32 - c));
        private static void WriteBE(byte[] b, int o, uint v) {
            b[o] = (byte)(v >> 24); b[o+1] = (byte)(v >> 16);
            b[o+2] = (byte)(v >> 8); b[o+3] = (byte)v;
        }

        #endregion

        #region WebSocket Frame Read/Write

        private static void ReadWebSocketFrames(ClientConnection conn)
        {
            try
            {
                while (conn.client != null && conn.client.Available > 0)
                {
                    string message = ReadWebSocketMessage(conn);
                    if (message == null) break;
                    if (message.Length == 0) continue;

                    // AI messages (reverse direction: MCP → Unity)
                    string msgMethod = JsonHelper.GetString(message, "method");
                    if (msgMethod == "ai/status")
                    {
                        AIRequestHandler.HandleStatus(
                            JsonHelper.GetString(message, "id"),
                            JsonHelper.GetString(message, "status"));
                        continue;
                    }
                    if (msgMethod == "ai/chunk")
                    {
                        AIRequestHandler.HandleChunk(
                            JsonHelper.GetString(message, "id"),
                            JsonHelper.GetString(message, "chunk"));
                        continue;
                    }
                    if (msgMethod == "ai/response")
                    {
                        string aiId = JsonHelper.GetString(message, "id");
                        string aiError = JsonHelper.GetString(message, "error");
                        if (!string.IsNullOrEmpty(aiError))
                            AIRequestHandler.HandleError(aiId, aiError);
                        else
                            AIRequestHandler.HandleResponse(aiId, JsonHelper.GetString(message, "result") ?? "");
                        continue;
                    }

                    // Image generated by Nano Banana (MCP → Unity)
                    if (msgMethod == "image/generated")
                    {
                        string imageBase64 = JsonHelper.GetString(message, "imageData");
                        string description = JsonHelper.GetString(message, "description");
                        if (!string.IsNullOrEmpty(imageBase64))
                            NanoBananaReceiver.HandleImageReceived(imageBase64, description);
                        continue;
                    }

                    // Normal request → handler → response
                    string responseJson = _messageHandler.ProcessMessage(message);
                    SendWebSocketMessage(conn.stream, responseJson);
                }
            }
            catch (Exception)
            {
                try { conn.stream?.Close(); conn.client?.Close(); } catch { }
                conn.client = null;
                conn.stream = null;
            }
        }

        private static string ReadWebSocketMessage(ClientConnection conn)
        {
            var stream = conn.stream;
            if (stream == null || !stream.CanRead) return null;

            byte[] header = new byte[2];
            if (ReadFully(stream, header, 0, 2) < 2) return null;

            byte opcode = (byte)(header[0] & 0x0F);
            bool masked = (header[1] & 0x80) != 0;
            long payloadLength = header[1] & 0x7F;

            if (payloadLength == 126)
            {
                byte[] ext = new byte[2];
                ReadFully(stream, ext, 0, 2);
                payloadLength = (ext[0] << 8) | ext[1];
            }
            else if (payloadLength == 127)
            {
                byte[] ext = new byte[8];
                ReadFully(stream, ext, 0, 8);
                payloadLength = 0;
                for (int i = 0; i < 8; i++)
                    payloadLength = (payloadLength << 8) | ext[i];
            }

            byte[] maskKey = null;
            if (masked)
            {
                maskKey = new byte[4];
                ReadFully(stream, maskKey, 0, 4);
            }

            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
                ReadFully(stream, payload, 0, (int)payloadLength);

            if (masked && maskKey != null)
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= maskKey[i % 4];

            switch (opcode)
            {
                case 0x1: return Encoding.UTF8.GetString(payload);
                case 0x8:
                    SendCloseFrame(stream);
                    try { stream.Close(); conn.client?.Close(); } catch { }
                    conn.client = null; conn.stream = null;
                    return null;
                case 0x9:
                    SendPongFrame(stream, payload);
                    return "";
                case 0xA: return "";
                default: return Encoding.UTF8.GetString(payload);
            }
        }

        private static void SendWebSocketMessage(NetworkStream stream, string message)
        {
            if (stream == null || !stream.CanWrite) return;

            byte[] payload = Encoding.UTF8.GetBytes(message);
            byte[] frame;

            if (payload.Length < 126)
            {
                frame = new byte[2 + payload.Length];
                frame[0] = 0x81;
                frame[1] = (byte)payload.Length;
                Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            }
            else if (payload.Length < 65536)
            {
                frame = new byte[4 + payload.Length];
                frame[0] = 0x81; frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)(payload.Length & 0xFF);
                Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            }
            else
            {
                frame = new byte[10 + payload.Length];
                frame[0] = 0x81; frame[1] = 127;
                long len = payload.Length;
                for (int i = 7; i >= 0; i--)
                    frame[2 + (7 - i)] = (byte)(len >> (i * 8));
                Buffer.BlockCopy(payload, 0, frame, 10, payload.Length);
            }

            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        private static void SendCloseFrame(NetworkStream stream)
        {
            try { stream?.Write(new byte[] { 0x88, 0x00 }, 0, 2); stream?.Flush(); } catch { }
        }

        private static void SendPongFrame(NetworkStream stream, byte[] payload)
        {
            try
            {
                byte[] frame;
                if (payload.Length < 126)
                {
                    frame = new byte[2 + payload.Length];
                    frame[0] = 0x8A;
                    frame[1] = (byte)payload.Length;
                    Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
                }
                else frame = new byte[] { 0x8A, 0x00 };
                stream?.Write(frame, 0, frame.Length);
                stream?.Flush();
            }
            catch { }
        }

        private static int ReadFully(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead;
        }

        #endregion

        #region Handler Registration

        private static void RegisterHandlers()
        {
            if (_handlersRegistered) return;
            _handlersRegistered = true;

            // ── Shader handlers ──
            _messageHandler.RegisterHandler("shader/list", paramsJson =>
            {
                string filter = JsonHelper.GetString(paramsJson, "filter");
                return ShaderAnalyzer.ListAllShaders(filter);
            });

            _messageHandler.RegisterHandler("shader/compile", paramsJson =>
            {
                string shaderPath = JsonHelper.GetString(paramsJson, "shaderPath");
                if (string.IsNullOrEmpty(shaderPath))
                    return "{\"error\":\"Missing shaderPath parameter\"}";
                return ShaderAnalyzer.CompileShader(shaderPath);
            });

            _messageHandler.RegisterHandler("shader/variants", paramsJson =>
            {
                string shaderPath = JsonHelper.GetString(paramsJson, "shaderPath");
                if (string.IsNullOrEmpty(shaderPath))
                    return "{\"error\":\"Missing shaderPath parameter\"}";
                return ShaderAnalyzer.GetVariantInfo(shaderPath);
            });

            _messageHandler.RegisterHandler("shader/properties", paramsJson =>
            {
                string shaderPath = JsonHelper.GetString(paramsJson, "shaderPath");
                if (string.IsNullOrEmpty(shaderPath))
                    return "{\"error\":\"Missing shaderPath parameter\"}";
                return ShaderAnalyzer.GetShaderProperties(shaderPath);
            });

            _messageHandler.RegisterHandler("shader/getCode", paramsJson =>
            {
                string shaderPath = JsonHelper.GetString(paramsJson, "shaderPath");
                if (string.IsNullOrEmpty(shaderPath))
                    return "{\"error\":\"Missing shaderPath parameter\"}";
                bool resolveIncludes = JsonHelper.GetBool(paramsJson, "resolveIncludes", false);
                return ShaderAnalyzer.GetShaderCode(shaderPath, resolveIncludes);
            });

            _messageHandler.RegisterHandler("shader/includes", _ =>
            {
                return ShaderAnalyzer.GetIncludeFiles();
            });

            // ── Material handlers ──
            _messageHandler.RegisterHandler("material/list", paramsJson =>
            {
                string filter = JsonHelper.GetString(paramsJson, "filter");
                return MaterialInspector.ListAllMaterials(filter);
            });

            _messageHandler.RegisterHandler("material/info", paramsJson =>
            {
                string materialPath = JsonHelper.GetString(paramsJson, "materialPath");
                if (string.IsNullOrEmpty(materialPath))
                    return "{\"error\":\"Missing materialPath parameter\"}";
                return MaterialInspector.GetMaterialInfo(materialPath);
            });

            _messageHandler.RegisterHandler("material/keywords", paramsJson =>
            {
                string materialPath = JsonHelper.GetString(paramsJson, "materialPath");
                return MaterialInspector.GetMaterialKeywords(materialPath);
            });

            // ── Pipeline handlers ──
            _messageHandler.RegisterHandler("pipeline/info", _ =>
            {
                return PipelineDetector.GetPipelineInfoJson();
            });

            _messageHandler.RegisterHandler("pipeline/qualitySettings", _ =>
            {
                return PipelineDetector.GetQualitySettingsJson();
            });

            // ── Error Solver handlers ──
            _messageHandler.RegisterHandler("console/getErrors", paramsJson =>
            {
                bool includeWarnings = JsonHelper.GetBool(paramsJson, "includeWarnings", false);
                int limit = JsonHelper.GetInt(paramsJson, "limit", 50);
                return ErrorCollector.GetErrorsJson(includeWarnings, limit);
            });

            _messageHandler.RegisterHandler("project/readFile", paramsJson =>
            {
                string filePath = JsonHelper.GetString(paramsJson, "filePath");
                if (string.IsNullOrEmpty(filePath))
                    return "{\"error\":\"Missing filePath parameter\"}";
                return ErrorCollector.ReadProjectFile(filePath);
            });

            _messageHandler.RegisterHandler("project/writeFile", paramsJson =>
            {
                string filePath = JsonHelper.GetString(paramsJson, "filePath");
                string content = JsonHelper.GetString(paramsJson, "content");
                if (string.IsNullOrEmpty(filePath))
                    return "{\"error\":\"Missing filePath parameter\"}";
                if (content == null)
                    return "{\"error\":\"Missing content parameter\"}";
                return ErrorCollector.WriteProjectFile(filePath, content);
            });

            _messageHandler.RegisterHandler("project/listFiles", paramsJson =>
            {
                string directory = JsonHelper.GetString(paramsJson, "directory");
                string pattern = JsonHelper.GetString(paramsJson, "pattern");
                return ErrorCollector.ListProjectFiles(directory, pattern);
            });

            // ── Editor info ──
            _messageHandler.RegisterHandler("editor/logs", paramsJson =>
            {
                string severity = JsonHelper.GetString(paramsJson, "severity") ?? "all";
                return ShaderCompileWatcher.GetLogsJson(severity);
            });

            _messageHandler.RegisterHandler("editor/platform", _ =>
            {
                return JsonHelper.StartObject()
                    .Key("platform").Value(EditorUserBuildSettings.activeBuildTarget.ToString())
                    .Key("graphicsApi").Value(SystemInfo.graphicsDeviceType.ToString())
                    .Key("graphicsDeviceName").Value(SystemInfo.graphicsDeviceName)
                    .Key("unityVersion").Value(Application.unityVersion)
                    .Key("operatingSystem").Value(SystemInfo.operatingSystem)
                    .Key("scriptingBackend").Value(
                        PlayerSettings.GetScriptingBackend(
                            EditorUserBuildSettings.selectedBuildTargetGroup).ToString())
                    .ToString();
            });

            // ── Frame Debugger handlers ──
            _messageHandler.RegisterHandler("framedebug/capture", paramsJson =>
            {
                int maxEvents = JsonHelper.GetInt(paramsJson, "maxEvents", 0);
                bool includeShaders = JsonHelper.GetBool(paramsJson, "includeShaders", false);
                return FrameDebugBridge.Capture(maxEvents, includeShaders);
            });

            _messageHandler.RegisterHandler("framedebug/disable", _ =>
            {
                return FrameDebugBridge.Disable();
            });

            _messageHandler.RegisterHandler("framedebug/status", _ =>
            {
                return FrameDebugBridge.Status();
            });

            _messageHandler.RegisterHandler("framedebug/event", paramsJson =>
            {
                int eventIndex = JsonHelper.GetInt(paramsJson, "eventIndex", -1);
                if (eventIndex < 0)
                    return "{\"error\":\"missing-eventIndex\"}";
                return FrameDebugBridge.GetEventDetail(eventIndex);
            });

            _messageHandler.RegisterHandler("framedebug/shader", paramsJson =>
            {
                int eventIndex = JsonHelper.GetInt(paramsJson, "eventIndex", -1);
                if (eventIndex < 0)
                    return "{\"error\":\"missing-eventIndex\"}";
                return FrameDebugBridge.GetEventShader(eventIndex);
            });

            _messageHandler.RegisterHandler("framedebug/rt", paramsJson =>
            {
                int eventIndex = JsonHelper.GetInt(paramsJson, "eventIndex", -1);
                int maxWidth = JsonHelper.GetInt(paramsJson, "maxWidth", 512);
                if (eventIndex < 0)
                    return "{\"error\":\"missing-eventIndex\"}";
                return FrameDebugBridge.GetRenderTargetSnapshot(eventIndex, maxWidth);
            });
        }

        #endregion
    }
}
