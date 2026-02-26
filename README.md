# Unity Shader MCP Tools

**MCP-based shader development tools connecting Unity Editor and AI coding assistants**

```
┌─────────────┐     WebSocket      ┌──────────────┐     MCP(stdio)     ┌─────────────┐
│ Unity Editor│◄─────────────────► │  Node.js     │◄──────────────────►│  AI Coding  │
│ (C# Plugin) │   localhost:8090   │  MCP Server  │                    │  Assistant  │
└─────────────┘                    └──────────────┘                    └─────────────┘
                                          ▲
                                          │ stdio
                                   ┌──────┴───────┐
                                   │  shader-ls   │
                                   │  (LSP Server)│
                                   └──────────────┘
```

---

## Requirements

- **Unity** 2021.3 LTS or higher (Unity 6.0+ recommended)
- **Node.js** 18+
- **AI Coding Assistant** (any one of the following):
  - [Claude Code](https://docs.anthropic.com/en/docs/claude-code)
  - [OpenCode](https://github.com/opencode-ai/opencode)
  - [Gemini CLI](https://github.com/google-gemini/gemini-cli)
  - [Cursor](https://cursor.com)
  - Any MCP-compatible client
- **.NET 7.0+ SDK** (optional, for LSP features — [download](https://dotnet.microsoft.com/download))

---

## Installation

### Step 1: Install Unity Package

Open Unity Editor, go to Window > Package Manager, click `+` > **Add package from git URL**, and enter:
```
https://github.com/KULEEEE/Claude-Code-For-Unity-Shader.git?path=unity-package
```

Or install from disk:
Unity Package Manager > `+` > **Add package from disk** > `unity-package/package.json`

### Step 2: Configure MCP Server

The MCP server is published on npm as [`unity-shader-mcp`](https://www.npmjs.com/package/unity-shader-mcp). No build required — just add the config for your AI assistant:

#### Claude Code

Add to `.mcp.json` (project root or `~/.claude/.mcp.json`):

**macOS / Linux:**
```json
{
  "mcpServers": {
    "unity-shader": {
      "command": "npx",
      "args": ["-y", "unity-shader-mcp"]
    }
  }
}
```

**Windows:**
```json
{
  "mcpServers": {
    "unity-shader": {
      "command": "cmd",
      "args": ["/c", "npx", "-y", "unity-shader-mcp"]
    }
  }
}
```

#### OpenCode

Add to `opencode.json` (project root or `~/.config/opencode/opencode.json`):

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "unity-shader": {
      "type": "local",
      "command": ["npx", "-y", "unity-shader-mcp"],
      "enabled": true
    }
  }
}
```

#### Gemini CLI

Add to `settings.json` (`~/.gemini/settings.json` or `.gemini/settings.json`):

```json
{
  "mcpServers": {
    "unity-shader": {
      "command": "npx",
      "args": ["-y", "unity-shader-mcp"]
    }
  }
}
```

#### Cursor

Add to `.cursor/mcp.json` (project root):

```json
{
  "mcpServers": {
    "unity-shader": {
      "command": "npx",
      "args": ["-y", "unity-shader-mcp"]
    }
  }
}
```

> **Auto-update**: Since the server runs via `npx`, you always get the latest version automatically. No manual update needed.

### Step 3: Verify Connection

1. Open **Tools > Shader MCP > Server Window** in Unity Editor
2. Click **Start Server**
3. Test tool calls from your AI assistant:

```
"Show me the list of shaders in the project"
```

---

## Shader Inspector (Unity Editor GUI)

The Unity package includes a built-in **Shader Inspector** window with AI integration.

Open via **Tools > Shader MCP > Shader Inspector**

### Tabs

| Tab | Description |
|-----|-------------|
| **Shaders** | Browse shaders with search/sort/filter, view details, run local analysis (compile, variants, properties, code) and AI analysis (error analysis, optimization, explain, diagnose) |
| **Materials** | Browse materials grouped by shader, view properties/keywords, navigate to shader |
| **Pipeline** | Read-only dashboard of render pipeline, quality settings, and platform info |
| **Logs** | Shader-related log viewer with severity filter and AI error analysis |
| **AI Chat** | Free-form chat with Claude about shaders, auto-attaches selected shader as context |

### Features

- **Drag & drop** shaders/materials into the window
- **Basic analysis** runs locally in Unity (instant)
- **AI analysis** routes through MCP server to Claude CLI (requires MCP server connection)
- **Cross-tab navigation** (e.g., "Go to Shader" from Materials tab)
- **Quick presets** for common AI queries

---

## Usage Examples

### Shader Analysis
```
"Analyze the variant count of this shader"

"Compile Character.shader and check for errors"
```

### Optimization
```
"Find areas to optimize for mobile"

"Find shaders with too many variants"
```

### Inspection
```
"Show me the shader properties used by this material"

"Tell me the current render pipeline settings"
```

### LSP Code Intelligence
```
"What does saturate() do?" (hover on a function)

"Show completions at line 15, column 8 of this shader"

"Show the signature help for lerp()"
```

### Slash Commands

| Command | Description |
|---------|-------------|
| `/shader` | Interactive tool menu |
| `/shader-help` | Full project analysis |
| `/shader-list` | List shaders |
| `/shader-compile` | Compile shader |
| `/shader-analyze` | Analyze variants |
| `/shader-code` | View source code |
| `/shader-props` | View properties |
| `/material-info` | Material info |
| `/shader-logs` | Console logs |
| `/shader-status` | Check connection status |
| `/shader-hover` | Symbol hover info (LSP) |
| `/shader-completion` | Code completions (LSP) |
| `/shader-signature` | Function signature help (LSP) |
| `/shader-diagnostics` | Shader diagnostics (LSP) |

---

## Tools

| Tool | Description |
|------|-------------|
| `compile_shader` | Compile shader and return errors/warnings |
| `analyze_shader_variants` | Analyze keyword combinations and variant count |
| `get_shader_properties` | List shader properties |
| `get_shader_code` | Read shader source code (with include resolution) |
| `get_material_info` | Get material details |
| `list_shaders` | List project shaders |
| `get_shader_logs` | Get shader-related console logs |
| `shader_hover` | Get type/documentation info for a symbol (LSP) |
| `shader_completion` | Get code completion suggestions (LSP) |
| `shader_signature_help` | Get function signature help (LSP) |
| `shader_diagnostics` | Get shader diagnostics (LSP, limited) |

## Resources

| Resource URI | Description |
|-------------|-------------|
| `unity://pipeline/info` | Render pipeline info |
| `unity://shader/includes` | Include file list |
| `unity://shader/keywords` | Shader keyword list |
| `unity://editor/platform` | Build target and platform info |

---

## LSP Code Intelligence (shader-ls)

The MCP server integrates [shader-language-server](https://github.com/shader-ls/shader-language-server) (shader-ls) to provide IDE-level code intelligence for ShaderLab/HLSL:

- **Hover**: Type and documentation info for functions, variables, keywords
- **Completion**: Context-aware code completion suggestions
- **Signature Help**: Parameter info when inside function calls
- **Diagnostics**: Placeholder for future shader-ls support (use `compile_shader` for now)

### How it works

- **Lazy startup**: shader-ls only launches on the first LSP tool call. Existing tools are unaffected.
- **Auto-install**: If shader-ls is not found, it is automatically installed via `dotnet tool install --global shader-ls` (requires .NET 7.0+ SDK).
- **Crash recovery**: If shader-ls crashes, it restarts automatically on the next request.

### Prerequisites

| Requirement | Required for |
|-------------|-------------|
| .NET 7.0+ SDK | LSP tools only |
| Unity Editor | Unity tools only |

> LSP tools and Unity tools are independent. If .NET is not installed, only LSP tools will show an error — all other tools work normally.

---

## Troubleshooting

### Server won't start
- Check if port 8090 is already in use
- Port can be changed in the Server Window

### Can't connect from AI assistant
- Verify the server is in Running state in Unity Editor
- Test connection directly with `wscat -c ws://localhost:8090`

### Disconnected after Domain Reload
- The MCP server attempts auto-reconnect every 3 seconds
- To prevent disconnections, disable Reload Domain in Enter Play Mode Settings

### ShaderUtil API warnings
- Some internal APIs may be restricted in certain Unity versions
- Reflection fallback is applied automatically
- More stable on Unity 6.0+ with public APIs

---

## Architecture

### Unity C# Package (`unity-package/`)
- **ShaderMCPServer.cs**: TcpListener-based WebSocket server (RFC 6455) + EditorWindow UI
- **ShaderAnalyzer.cs**: Shader analysis (listing, compilation, variants, properties, source code)
- **MaterialInspector.cs**: Material information gathering
- **PipelineDetector.cs**: Render pipeline detection (avoids hard URP/HDRP dependencies via reflection)
- **ShaderCompileWatcher.cs**: Shader-related log filtering
- **MessageHandler.cs**: JSON message routing
- **JsonHelper.cs**: Supplements for JsonUtility limitations
- **ShaderInspectorWindow.cs**: Shader Inspector EditorWindow with tabbed UI
- **AIRequestHandler.cs**: AI query routing (Unity → MCP → Claude CLI)
- **Tabs/**: ShaderBrowser, MaterialBrowser, PipelineDashboard, ShaderLogs, AIChat

### Node.js MCP Server (`claude-plugin/servers/shader-mcp-server/`)
- **unity-bridge.ts**: Unity WebSocket client (auto-reconnect, UUID matching, AI message relay)
- **lsp-client.ts**: ShaderLab LSP client (wraps [shader-ls](https://github.com/shader-ls/shader-language-server), auto-install)
- **ai-handler.ts**: Claude CLI invocation for AI analysis features
- **tools/**: MCP Tool definitions (7 Unity tools + 4 LSP tools)
- **resources/**: MCP Resource definitions (4 resources)
- **index.ts**: McpServer + StdioServerTransport entry point

### Unity Version Compatibility
- `#if UNITY_6000_0_OR_NEWER`: Unity 6.0-specific APIs (`shader.keywordSpace`, etc.)
- Fallback: ShaderUtil internal API reflection + try-catch

---

## Contributing

1. Fork this repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---
