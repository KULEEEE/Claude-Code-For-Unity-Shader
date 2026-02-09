using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace ShaderMCP.Editor
{
    /// <summary>
    /// WebSocket server (RFC 6455) running on localhost:8090.
    /// Uses TcpListener + manual handshake (Unity Mono lacks HttpListener WebSocket support).
    /// Provides an EditorWindow UI for monitoring connections.
    /// </summary>
    [InitializeOnLoad]
    public class ShaderMCPServer : EditorWindow
    {
        private const int DefaultPort = 8090;
        private const string WebSocketMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        // Server state (static to survive domain reload)
        private static TcpListener _listener;
        private static TcpClient _connectedClient;
        private static NetworkStream _networkStream;
        private static bool _isRunning;
        private static int _port = DefaultPort;
        private static readonly MessageHandler _messageHandler = new MessageHandler();
        private static readonly List<string> _logEntries = new List<string>();
        private static readonly object _logLock = new object();
        private static bool _handlersRegistered;

        // EditorWindow state
        private Vector2 _logScrollPos;
        private bool _autoScroll = true;

        static ShaderMCPServer()
        {
            RegisterHandlers();
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            EditorApplication.quitting -= OnQuitting;
            EditorApplication.quitting += OnQuitting;
        }

        [MenuItem("Tools/Shader MCP/Server Window")]
        public static void ShowWindow()
        {
            GetWindow<ShaderMCPServer>("Shader MCP Server");
        }

        #region EditorWindow UI

        private void OnGUI()
        {
            EditorGUILayout.Space(5);

            // Connection status
            EditorGUILayout.BeginHorizontal();
            var statusColor = _isRunning ? Color.green : Color.red;
            var oldColor = GUI.color;
            GUI.color = statusColor;
            EditorGUILayout.LabelField(_isRunning ? "● Running" : "● Stopped",
                EditorStyles.boldLabel, GUILayout.Width(100));
            GUI.color = oldColor;

            if (_connectedClient != null && _connectedClient.Connected)
            {
                GUI.color = Color.cyan;
                EditorGUILayout.LabelField("Client Connected", GUILayout.Width(120));
                GUI.color = oldColor;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(3);

            // Port setting
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_isRunning);
            _port = EditorGUILayout.IntField("Port", _port, GUILayout.Width(250));
            EditorGUI.EndDisabledGroup();

            // Start/Stop buttons
            if (!_isRunning)
            {
                if (GUILayout.Button("Start Server", GUILayout.Width(120)))
                    StartServer();
            }
            else
            {
                if (GUILayout.Button("Stop Server", GUILayout.Width(120)))
                    StopServer();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);

            // Log controls
            EditorGUILayout.BeginHorizontal();
            _autoScroll = EditorGUILayout.ToggleLeft("Auto-scroll", _autoScroll, GUILayout.Width(100));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear Log", GUILayout.Width(80)))
            {
                lock (_logLock) { _logEntries.Clear(); }
            }
            EditorGUILayout.EndHorizontal();

            // Log view
            _logScrollPos = EditorGUILayout.BeginScrollView(_logScrollPos,
                GUILayout.ExpandHeight(true));

            lock (_logLock)
            {
                foreach (var entry in _logEntries)
                {
                    EditorGUILayout.LabelField(entry, EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.EndScrollView();

            if (_autoScroll)
                _logScrollPos.y = float.MaxValue;

            // Repaint periodically when running
            if (_isRunning)
                Repaint();
        }

        private void OnDestroy()
        {
            // Window closed, but server keeps running
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
                _listener.Start();
                _isRunning = true;
                AddLog($"Server started on ws://localhost:{_port}");
            }
            catch (Exception ex)
            {
                AddLog($"Failed to start server: {ex.Message}");
                Debug.LogError($"[ShaderMCP] Failed to start server: {ex}");
            }
        }

        public static void StopServer()
        {
            _isRunning = false;

            try
            {
                if (_connectedClient != null)
                {
                    _networkStream?.Close();
                    _connectedClient.Close();
                    _connectedClient = null;
                    _networkStream = null;
                }

                _listener?.Stop();
                _listener = null;
                AddLog("Server stopped");
            }
            catch (Exception ex)
            {
                AddLog($"Error stopping server: {ex.Message}");
            }
        }

        private static void OnQuitting()
        {
            StopServer();
        }

        #endregion

        #region Main Thread Update (Non-blocking Polling)

        private static void Update()
        {
            if (!_isRunning || _listener == null) return;

            try
            {
                // Accept new connections
                if (_listener.Pending())
                {
                    var newClient = _listener.AcceptTcpClient();
                    HandleNewConnection(newClient);
                }

                // Read from connected client
                if (_connectedClient != null && _connectedClient.Connected && _networkStream != null)
                {
                    if (_connectedClient.Available > 0)
                    {
                        ReadWebSocketFrames();
                    }
                }
                else if (_connectedClient != null && !_connectedClient.Connected)
                {
                    AddLog("Client disconnected");
                    _connectedClient = null;
                    _networkStream = null;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Update error: {ex.Message}");
            }
        }

        #endregion

        #region WebSocket Handshake (RFC 6455)

        private static void HandleNewConnection(TcpClient client)
        {
            // Close existing connection
            if (_connectedClient != null)
            {
                try
                {
                    _networkStream?.Close();
                    _connectedClient.Close();
                }
                catch { }
            }

            try
            {
                var stream = client.GetStream();
                client.ReceiveTimeout = 5000;
                client.NoDelay = true;

                // Read HTTP upgrade request (loop until we get the full header ending with \r\n\r\n)
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

                AddLog($"Handshake request received ({request.Length} bytes)");

                string requestLower = request.ToLowerInvariant();
                if (!requestLower.Contains("upgrade: websocket"))
                {
                    client.Close();
                    AddLog("Non-WebSocket connection rejected");
                    return;
                }

                // Extract Sec-WebSocket-Key
                string key = null;
                string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                foreach (var line in lines)
                {
                    if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    {
                        int colonPos = line.IndexOf(':');
                        string raw = line.Substring(colonPos + 1);
                        // Strip all whitespace and control characters
                        var sb = new StringBuilder();
                        foreach (char c in raw)
                        {
                            if (c > ' ') sb.Append(c);
                            else if (c == ' ' && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                            {
                                // keep single internal space — but base64 keys have no spaces
                            }
                        }
                        key = sb.ToString().Trim();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(key))
                {
                    client.Close();
                    AddLog("Missing Sec-WebSocket-Key");
                    return;
                }

                AddLog($"Sec-WebSocket-Key: [{key}] (len={key.Length})");

                // Compute accept hash
                string acceptKey = ComputeWebSocketAcceptKey(key);
                AddLog($"Sec-WebSocket-Accept: [{acceptKey}]");

                // Send handshake response — build as explicit byte array to avoid encoding issues
                string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                  "Upgrade: websocket\r\n" +
                                  "Connection: Upgrade\r\n" +
                                  "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                                  "\r\n";

                AddLog($"Response ({response.Length} chars): {response.Replace("\r", "\\r").Replace("\n", "\\n")}");
                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                AddLog($"Response bytes: {responseBytes.Length}, last4=[{responseBytes[responseBytes.Length-4]:X2} {responseBytes[responseBytes.Length-3]:X2} {responseBytes[responseBytes.Length-2]:X2} {responseBytes[responseBytes.Length-1]:X2}]");
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();

                client.ReceiveTimeout = 0; // Non-blocking after handshake
                _connectedClient = client;
                _networkStream = stream;
                AddLog("Client connected (WebSocket handshake complete)");
            }
            catch (Exception ex)
            {
                AddLog($"Handshake failed: {ex.Message}");
                client.Close();
            }
        }

        private static string ComputeWebSocketAcceptKey(string key)
        {
            string combined = key + WebSocketMagicGuid;
            AddLog($"SHA1 input: [{combined}] (len={combined.Length})");
            byte[] inputBytes = Encoding.ASCII.GetBytes(combined);
            byte[] hash = ComputeSHA1(inputBytes);
            string hex = "";
            foreach (byte b in hash) hex += b.ToString("x2");
            AddLog($"SHA1 hash hex: {hex}");
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Pure managed SHA-1 implementation (RFC 3174).
        /// Avoids Unity Mono runtime issues with System.Security.Cryptography.
        /// </summary>
        private static byte[] ComputeSHA1(byte[] message)
        {
            uint h0 = 0x67452301;
            uint h1 = 0xEFCDAB89;
            uint h2 = 0x98BADCFE;
            uint h3 = 0x10325476;
            uint h4 = 0xC3D2E1F0;

            long msgBitLen = (long)message.Length * 8;

            // Pre-processing: add padding
            int padLen = (56 - (message.Length + 1) % 64);
            if (padLen < 0) padLen += 64;
            byte[] padded = new byte[message.Length + 1 + padLen + 8];
            Array.Copy(message, padded, message.Length);
            padded[message.Length] = 0x80;

            // Append length in bits as big-endian 64-bit
            for (int i = 0; i < 8; i++)
                padded[padded.Length - 1 - i] = (byte)(msgBitLen >> (i * 8));

            // Process each 512-bit (64-byte) block
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
            WriteBigEndian(result, 0, h0);
            WriteBigEndian(result, 4, h1);
            WriteBigEndian(result, 8, h2);
            WriteBigEndian(result, 12, h3);
            WriteBigEndian(result, 16, h4);
            return result;
        }

        private static uint RotateLeft(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }

        private static void WriteBigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)(value);
        }

        #endregion

        #region WebSocket Frame Read/Write (RFC 6455)

        private static void ReadWebSocketFrames()
        {
            try
            {
                while (_connectedClient != null && _connectedClient.Available > 0)
                {
                    string message = ReadWebSocketMessage();
                    if (message == null) break;
                    if (message.Length == 0) continue; // Control frame handled

                    AddLog($"← {(message.Length > 200 ? message.Substring(0, 200) + "..." : message)}");

                    // Process and respond
                    string responseJson = _messageHandler.ProcessMessage(message);
                    SendWebSocketMessage(responseJson);
                    AddLog($"→ {(responseJson.Length > 200 ? responseJson.Substring(0, 200) + "..." : responseJson)}");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Frame read error: {ex.Message}");
                // Connection may be broken
                try
                {
                    _networkStream?.Close();
                    _connectedClient?.Close();
                }
                catch { }
                _connectedClient = null;
                _networkStream = null;
            }
        }

        private static string ReadWebSocketMessage()
        {
            if (_networkStream == null || !_networkStream.CanRead) return null;

            // Read first 2 bytes
            byte[] header = new byte[2];
            int read = ReadFully(_networkStream, header, 0, 2);
            if (read < 2) return null;

            byte opcode = (byte)(header[0] & 0x0F);
            bool masked = (header[1] & 0x80) != 0;
            long payloadLength = header[1] & 0x7F;

            // Extended payload length
            if (payloadLength == 126)
            {
                byte[] extLen = new byte[2];
                ReadFully(_networkStream, extLen, 0, 2);
                payloadLength = (extLen[0] << 8) | extLen[1];
            }
            else if (payloadLength == 127)
            {
                byte[] extLen = new byte[8];
                ReadFully(_networkStream, extLen, 0, 8);
                payloadLength = 0;
                for (int i = 0; i < 8; i++)
                    payloadLength = (payloadLength << 8) | extLen[i];
            }

            // Masking key
            byte[] maskKey = null;
            if (masked)
            {
                maskKey = new byte[4];
                ReadFully(_networkStream, maskKey, 0, 4);
            }

            // Payload
            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
            {
                ReadFully(_networkStream, payload, 0, (int)payloadLength);
            }

            // Unmask
            if (masked && maskKey != null)
            {
                for (int i = 0; i < payload.Length; i++)
                    payload[i] ^= maskKey[i % 4];
            }

            // Handle opcodes
            switch (opcode)
            {
                case 0x1: // Text
                    return Encoding.UTF8.GetString(payload);
                case 0x8: // Close
                    AddLog("Client sent close frame");
                    SendCloseFrame();
                    _networkStream?.Close();
                    _connectedClient?.Close();
                    _connectedClient = null;
                    _networkStream = null;
                    return null;
                case 0x9: // Ping
                    SendPongFrame(payload);
                    return ""; // Signal control frame handled
                case 0xA: // Pong
                    return ""; // Ignore pong
                default:
                    return Encoding.UTF8.GetString(payload);
            }
        }

        private static void SendWebSocketMessage(string message)
        {
            if (_networkStream == null || !_networkStream.CanWrite) return;

            byte[] payload = Encoding.UTF8.GetBytes(message);
            byte[] frame;

            if (payload.Length < 126)
            {
                frame = new byte[2 + payload.Length];
                frame[0] = 0x81; // FIN + Text
                frame[1] = (byte)payload.Length;
                Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            }
            else if (payload.Length < 65536)
            {
                frame = new byte[4 + payload.Length];
                frame[0] = 0x81;
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)(payload.Length & 0xFF);
                Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            }
            else
            {
                frame = new byte[10 + payload.Length];
                frame[0] = 0x81;
                frame[1] = 127;
                long len = payload.Length;
                for (int i = 7; i >= 0; i--)
                {
                    frame[2 + (7 - i)] = (byte)(len >> (i * 8));
                }
                Buffer.BlockCopy(payload, 0, frame, 10, payload.Length);
            }

            _networkStream.Write(frame, 0, frame.Length);
            _networkStream.Flush();
        }

        private static void SendCloseFrame()
        {
            try
            {
                byte[] frame = new byte[] { 0x88, 0x00 };
                _networkStream?.Write(frame, 0, frame.Length);
                _networkStream?.Flush();
            }
            catch { }
        }

        private static void SendPongFrame(byte[] payload)
        {
            try
            {
                byte[] frame;
                if (payload.Length < 126)
                {
                    frame = new byte[2 + payload.Length];
                    frame[0] = 0x8A; // FIN + Pong
                    frame[1] = (byte)payload.Length;
                    Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
                }
                else
                {
                    frame = new byte[] { 0x8A, 0x00 };
                }
                _networkStream?.Write(frame, 0, frame.Length);
                _networkStream?.Flush();
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

            // Shader handlers
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

            // Material handlers
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

            // Pipeline handlers
            _messageHandler.RegisterHandler("pipeline/info", _ =>
            {
                return PipelineDetector.GetPipelineInfoJson();
            });

            _messageHandler.RegisterHandler("pipeline/qualitySettings", _ =>
            {
                return PipelineDetector.GetQualitySettingsJson();
            });

            // Editor handlers
            _messageHandler.RegisterHandler("editor/logs", paramsJson =>
            {
                string severity = JsonHelper.GetString(paramsJson, "severity") ?? "all";
                return ShaderCompileWatcher.GetLogsJson(severity);
            });

            _messageHandler.RegisterHandler("editor/platform", _ =>
            {
                var builder = JsonHelper.StartObject()
                    .Key("platform").Value(EditorUserBuildSettings.activeBuildTarget.ToString())
                    .Key("graphicsApi").Value(UnityEngine.SystemInfo.graphicsDeviceType.ToString())
                    .Key("graphicsDeviceName").Value(UnityEngine.SystemInfo.graphicsDeviceName)
                    .Key("unityVersion").Value(Application.unityVersion)
                    .Key("operatingSystem").Value(SystemInfo.operatingSystem)
                    .Key("scriptingBackend").Value(
                        PlayerSettings.GetScriptingBackend(
                            EditorUserBuildSettings.selectedBuildTargetGroup).ToString());

                return builder.ToString();
            });
        }

        #endregion

        #region Logging

        private static void AddLog(string message)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (_logLock)
            {
                _logEntries.Add(entry);
                if (_logEntries.Count > 500)
                    _logEntries.RemoveAt(0);
            }
            Debug.Log($"[ShaderMCP] {message}");
        }

        #endregion
    }
}
