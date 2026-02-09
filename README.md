# Unity Shader MCP Tools

**Unity Editor와 Claude Code를 연결하는 MCP 기반 셰이더 개발 도구**
**MCP-based shader development tools connecting Unity Editor and Claude Code**

```
┌─────────────┐     WebSocket      ┌──────────────┐     MCP(stdio)     ┌─────────────┐
│ Unity Editor │◄─────────────────►│  Node.js     │◄──────────────────►│ Claude Code │
│ (C# Plugin)  │    localhost:8090  │  MCP Server  │                    │             │
└─────────────┘                    └──────────────┘                    └─────────────┘
```

---

## Requirements / 요구사항

- **Unity** 2021.3 LTS 이상 (Unity 6.0+ 권장 / recommended)
- **Node.js** 18+
- **Claude Code** CLI

---

## Installation / 설치

### Step 1: Unity Package 설치

Unity Editor에서 Window > Package Manager를 열고, `+` 버튼 > **Add package from git URL**을 선택한 후 아래 URL을 입력합니다.

Open Unity Editor, go to Window > Package Manager, click `+` > **Add package from git URL**, and enter:
```
https://github.com/YOUR_USERNAME/unity-shader-mcp.git?path=unity-package
```

또는 로컬 설치 / Or install from disk:
Unity Package Manager > `+` > **Add package from disk** > `unity-package/package.json`

### Step 2: Claude Code Plugin 설치

Claude Code에서 아래 명령어를 실행합니다 (빌드 불필요, 번들 파일 포함).

Run these commands in Claude Code (no build needed, bundled file included):

```bash
/plugin marketplace add YOUR_USERNAME/unity-shader-mcp
/plugin install unity-shader-tools@unity-shader-mcp
```

> **Note**: `npm install`이나 `npm run build`는 필요 없습니다. 번들된 단일 파일(`dist/server.mjs`)이 포함되어 있습니다.
> No `npm install` or `npm run build` required. A pre-bundled file (`dist/server.mjs`) is included.

### Step 3: 연결 확인 / Verify Connection

1. Unity Editor에서 **Tools > Shader MCP > Server Window** 열기
2. **Start Server** 클릭
3. Claude Code에서 도구 호출 테스트:

1. Open **Tools > Shader MCP > Server Window** in Unity Editor
2. Click **Start Server**
3. Test tool calls from Claude Code:

```
"프로젝트의 셰이더 목록을 보여줘"
"Show me the list of shaders in the project"
```

---

## Usage Examples / 사용 예시

### Shader Analysis / 셰이더 분석
```
"이 셰이더의 variant 수를 분석해줘"
"Analyze the variant count of this shader"

"Character.shader를 컴파일하고 에러 확인해줘"
"Compile Character.shader and check for errors"
```

### Optimization / 최적화
```
"모바일용으로 최적화할 부분을 찾아줘"
"Find areas to optimize for mobile"

"variant 수가 너무 많은 셰이더를 찾아줘"
"Find shaders with too many variants"
```

### Inspection / 검사
```
"이 머티리얼이 사용하는 셰이더 프로퍼티를 보여줘"
"Show me the shader properties used by this material"

"현재 렌더 파이프라인 설정을 알려줘"
"Tell me the current render pipeline settings"
```

### Slash Commands

| Command | Description |
|---------|-------------|
| `/shader` | 대화형 도구 메뉴 / Interactive tool menu |
| `/shader-help` | 전체 프로젝트 분석 / Full project analysis |
| `/shader-list` | 셰이더 목록 / List shaders |
| `/shader-compile` | 셰이더 컴파일 / Compile shader |
| `/shader-analyze` | 배리언트 분석 / Analyze variants |
| `/shader-code` | 소스 코드 보기 / View source code |
| `/shader-props` | 프로퍼티 조회 / View properties |
| `/material-info` | 머티리얼 정보 / Material info |
| `/shader-logs` | 콘솔 로그 / Console logs |
| `/shader-status` | 연결 상태 확인 / Check connection |

---

## Tools / 도구 목록

| Tool | Description |
|------|-------------|
| `compile_shader` | 셰이더 컴파일 및 에러/경고 반환 / Compile shader, return errors and warnings |
| `analyze_shader_variants` | 키워드 조합 및 variant 수 분석 / Analyze keyword combinations and variant count |
| `get_shader_properties` | 셰이더 프로퍼티 목록 조회 / List shader properties |
| `get_shader_code` | 셰이더 소스 코드 읽기 (include 해결) / Read shader source (with include resolution) |
| `get_material_info` | 머티리얼 상세 정보 / Material details |
| `list_shaders` | 프로젝트 셰이더 목록 / List project shaders |
| `get_shader_logs` | 셰이더 관련 콘솔 로그 / Shader console logs |

## Resources / 리소스

| Resource URI | Description |
|-------------|-------------|
| `unity://pipeline/info` | 렌더 파이프라인 정보 / Render pipeline info |
| `unity://shader/includes` | Include 파일 목록 / Include file list |
| `unity://shader/keywords` | 셰이더 키워드 목록 / Shader keyword list |
| `unity://editor/platform` | 빌드 타겟 및 플랫폼 정보 / Build target and platform info |

---

## Troubleshooting / 트러블슈팅

### Unity 서버가 시작되지 않는 경우 / Server won't start
- 포트 8090이 이미 사용 중인지 확인 / Check if port 8090 is already in use
- Server Window에서 포트를 변경 가능 / Port can be changed in Server Window

### Claude Code에서 Unity에 연결되지 않는 경우 / Can't connect from Claude Code
- Unity Editor에서 Server가 Running 상태인지 확인 / Verify Server is Running in Unity
- `wscat -c ws://localhost:8090`으로 직접 연결 테스트 / Test connection directly with wscat
- MCP 서버가 빌드되었는지 확인 (`npm run build`) / Ensure MCP server is built

### Domain Reload 후 연결 끊김 / Disconnected after Domain Reload
- MCP 서버의 자동 재연결이 3초 간격으로 시도됩니다 / Auto-reconnect retries every 3 seconds
- Unity Editor에서 Enter Play Mode Settings > Reload Domain을 비활성화하면 방지 가능 / Disable Reload Domain in Enter Play Mode Settings to prevent

### ShaderUtil API 관련 경고 / ShaderUtil API warnings
- 일부 Unity 버전에서 internal API 접근이 제한될 수 있음 / Some internal APIs may be restricted in certain Unity versions
- 리플렉션 fallback이 자동으로 적용됨 / Reflection fallback is applied automatically
- Unity 6.0+ 에서는 공개 API를 사용하여 더 안정적 / More stable on Unity 6.0+ with public APIs

---

## Architecture / 아키텍처

### Unity C# Package (`unity-package/`)
- **ShaderMCPServer.cs**: TcpListener 기반 WebSocket 서버 (RFC 6455) + EditorWindow UI
- **ShaderAnalyzer.cs**: 셰이더 분석 (목록, 컴파일, variant, 프로퍼티, 소스코드)
- **MaterialInspector.cs**: 머티리얼 정보 수집
- **PipelineDetector.cs**: 렌더 파이프라인 감지 (리플렉션으로 URP/HDRP 하드 의존성 회피)
- **ShaderCompileWatcher.cs**: 셰이더 관련 로그 필터링
- **MessageHandler.cs**: JSON 메시지 라우팅
- **JsonHelper.cs**: JsonUtility 한계 보완

### Node.js MCP Server (`claude-plugin/servers/shader-mcp-server/`)
- **unity-bridge.ts**: Unity WebSocket 클라이언트 (자동 재연결, UUID 매칭)
- **tools/**: MCP Tool 정의 (7개)
- **resources/**: MCP Resource 정의 (4개)
- **index.ts**: McpServer + StdioServerTransport 진입점

### Unity Version Compatibility / 버전 호환성
- `#if UNITY_6000_0_OR_NEWER`: Unity 6.0 전용 API (`shader.keywordSpace` 등)
- Fallback: ShaderUtil internal API 리플렉션 + try-catch

---

## Contributing

1. Fork this repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## License

MIT License
