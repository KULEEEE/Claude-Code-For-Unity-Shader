# Unity Agent

**AI-powered Unity Editor tools — Error auto-fix & Shader analysis via Claude Code**

```
┌──────────────────────────────────┐
│        Unity Editor              │
│  ┌────────────────────────────┐  │     WebSocket      ┌──────────────┐     MCP(stdio)     ┌─────────────┐
│  │   Error Solver             │  │◄─────────────────► │  Node.js     │◄──────────────────►│  AI Coding  │
│  │   Shader Inspector         │  │   localhost:8090   │  MCP Server  │                    │  Assistant  │
│  └────────────────────────────┘  │                    └──────────────┘                    └─────────────┘
│                                  │                           ▲
│  Server auto-starts when any     │                           │ stdio
│  tool window opens               │                    ┌──────┴───────┐
└──────────────────────────────────┘                    │  shader-ls   │
                                                        │  (LSP Server)│
                                                        └──────────────┘
```

---

## Features

### Error Solver
Unity 에러를 자동으로 수집하고, AI가 분석 + 코드 수정까지 해주는 도구.

- **실시간 에러 수집** — `Application.logMessageReceived` + `CompilationPipeline` 훅
- **Solve 버튼** — 에러 선택 후 클릭하면 AI가 소스 읽고 → 원인 분석 → 코드 수정
- **스트리밍 응답** — AI 작업 진행 상황 실시간 표시
- **소스 바로가기** — 에러 발생 파일/라인 클릭으로 IDE 이동

[Error Solver]https://github.com/user-attachments/assets/e954d03e-828d-4401-b987-84d20703248a


### Shader Inspector
셰이더 분석, 머티리얼 검사, AI 채팅이 통합된 에디터 윈도우.

- **Shaders 탭** — 셰이더 목록, 컴파일, 배리언트 분석, AI 분석
- **Materials 탭** — 머티리얼 목록, 프로퍼티/키워드 조회
- **AI Chat 탭** — 셰이더 컨텍스트 기반 자유 대화
- **Include Graph** — #include 의존성 그래프 시각화

---

## Requirements

- **Unity** 2021.3 LTS 이상
- **Node.js** 18+
- **Claude Code** 설치 및 인증 (Solve 기능은 [Claude Agent SDK](https://www.npmjs.com/package/@anthropic-ai/claude-agent-sdk) 사용)
- **.NET 7.0+ SDK** (선택, LSP 기능용)

---

## Installation

### 방법 1: Git URL (권장)

Unity Editor → Window > Package Manager → `+` → **Add package from git URL**:
```
https://github.com/KULEEEE/Claude-Code-For-Unity.git?path=unity-package
```

### 방법 2: 로컬 폴더

ZIP 다운로드 후 `unity-package/` 폴더를 Unity 프로젝트의 `Packages/UnityAgent/`에 복사:
```
Packages/
  UnityAgent/
    package.json
    Editor/
      UnityAgentServer.cs
      ErrorCollector.cs
      ErrorSolverWindow.cs
      ...
    Runtime/
      AssemblyInfo.cs
```

### 방법 3: 디스크에서 추가

Package Manager → `+` → **Add package from disk** → `unity-package/package.json` 선택

---

## MCP Server 설정

npm에 퍼블리시된 [`unity-agent-tools`](https://www.npmjs.com/package/unity-agent-tools) 패키지를 사용합니다. AI 어시스턴트 설정에 추가하세요.

### Claude Code

`.mcp.json` (프로젝트 루트 또는 `~/.claude/.mcp.json`):

**macOS / Linux:**
```json
{
  "mcpServers": {
    "unity-agent": {
      "command": "npx",
      "args": ["-y", "unity-agent-tools"]
    }
  }
}
```

**Windows:**
```json
{
  "mcpServers": {
    "unity-agent": {
      "command": "cmd",
      "args": ["/c", "npx", "-y", "unity-agent-tools"]
    }
  }
}
```

### Cursor

`.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "unity-agent": {
      "command": "npx",
      "args": ["-y", "unity-agent-tools"]
    }
  }
}
```

### OpenCode

`opencode.json`:
```json
{
  "mcp": {
    "unity-agent": {
      "type": "local",
      "command": ["npx", "-y", "unity-agent-tools"],
      "enabled": true
    }
  }
}
```

---

## Usage

### Error Solver

1. **Tools** → **Unity Agent** → **Error Solver** 열기 (서버 자동 시작)
2. Unity에서 에러 발생 → 목록에 자동 표시
3. 에러 선택 → **Solve** 클릭
4. AI가 자동으로:
   - 관련 소스 파일 읽기
   - 원인 분석
   - 코드 수정
   - 결과 설명

### Shader Inspector

1. **Tools** → **Unity Agent** → **Shader Inspector** 열기
2. Shaders 탭에서 셰이더 선택
3. 로컬 분석 (Compile, Variants, Properties) 또는 AI 분석 실행
4. AI Chat 탭에서 셰이더 관련 자유 질문

### AI 어시스턴트에서 직접 사용

MCP 설정 후 AI 어시스턴트에서 자연어로 요청:
```
"Unity 프로젝트에서 에러 목록 보여줘"

"Assets/Scripts/Player.cs 파일 읽어줘"

"이 셰이더 컴파일하고 에러 확인해줘"

"프로젝트 셰이더 목록 보여줘"
```

---

## Tools

### Error Solver Tools

| Tool | Description |
|------|-------------|
| `get_unity_errors` | Unity 콘솔 에러/경고 조회 |
| `read_project_file` | Unity 프로젝트 파일 읽기 |
| `write_project_file` | Unity 프로젝트 파일 쓰기 (자동 recompile) |
| `list_project_files` | 프로젝트 파일 목록 조회 |

### Shader Tools

| Tool | Description |
|------|-------------|
| `compile_shader` | 셰이더 컴파일, 에러/경고 반환 |
| `analyze_shader_variants` | 키워드 조합 및 배리언트 수 분석 |
| `get_shader_properties` | 셰이더 프로퍼티 목록 |
| `get_shader_code` | 셰이더 소스 코드 (include 해석 포함) |
| `get_material_info` | 머티리얼 상세 정보 |
| `list_shaders` | 프로젝트 셰이더 목록 |

### LSP Tools

| Tool | Description |
|------|-------------|
| `shader_hover` | 심볼 타입/문서 정보 |
| `shader_completion` | 코드 자동완성 |
| `shader_signature_help` | 함수 시그니처 도움말 |
| `shader_diagnostics` | 셰이더 진단 |

### Resources

| Resource URI | Description |
|-------------|-------------|
| `unity://pipeline/info` | 렌더 파이프라인 정보 |
| `unity://shader/includes` | Include 파일 목록 |
| `unity://shader/keywords` | 셰이더 키워드 목록 |
| `unity://editor/platform` | 빌드 타겟 및 플랫폼 정보 |

---

## Architecture

```
Unity Editor (C#)                    Node.js MCP Server
├── UnityAgentServer.cs              ├── index.ts (entry point)
│   WebSocket server (:8090)         ├── unity-bridge.ts (WebSocket client)
│   Auto-start on tool open          ├── ai-handler.ts (Claude Agent SDK)
├── ErrorCollector.cs                ├── lsp-client.ts (shader-ls)
│   Console log capture              ├── tools/
├── ErrorSolverWindow.cs             │   ├── get-unity-errors.ts
│   Error list + Solve button        │   ├── read-project-file.ts
├── ShaderInspectorWindow.cs         │   ├── write-project-file.ts
│   Shader/Material/AI tabs          │   ├── list-project-files.ts
├── ShaderAnalyzer.cs                │   ├── shader-compile.ts
├── MaterialInspector.cs             │   ├── shader-analyze.ts
├── AIRequestHandler.cs              │   └── ... (13 tools total)
├── MarkdownRenderer.cs              └── resources/ (4 resources)
└── JsonHelper.cs
```

### 통신 흐름

**Error Solver (Solve 버튼):**
```
Error Solver UI → AIRequestHandler → WebSocket → MCP Server
→ Claude Agent SDK → Claude API → 파일 읽기/수정 → 결과 스트리밍
```

**AI 어시스턴트 직접 사용:**
```
AI Assistant → MCP(stdio) → MCP Server → WebSocket → Unity Editor
→ 에러 조회 / 파일 읽기 / 셰이더 분석 → 응답
```

---

## Troubleshooting

### AI Offline 표시
- 툴 창을 닫았다 다시 열어보세요 (서버 재시작)
- Node.js 18+가 설치되어 있는지 확인
- `npx -y unity-agent-tools` 가 터미널에서 실행되는지 확인

### 에러 목록이 안 보임
- **Clear** 버튼으로 기존 에러 초기화 후 새 에러 발생시키기
- **Refresh** 버튼 클릭

### Domain Reload 후 연결 끊김
- MCP 서버가 3초마다 자동 재연결 시도
- Enter Play Mode Settings에서 Reload Domain 비활성화하면 방지 가능

### 포트 충돌
- 기본 포트: 8090
- 다른 프로세스가 사용 중이면 Unity 콘솔에 에러 표시

---

## License

MIT
