# Unity Shader MCP Tools - Claude Code 작업 지시서

## 프로젝트 개요

Unity Editor와 Claude Code를 연결하는 MCP 기반 플러그인을 만든다. 셰이더 개발에 특화된 도구를 제공하며, 사용자는 Claude Code 플러그인 설치 + Unity 패키지 설치 두 단계로 셋업을 완료할 수 있어야 한다.

## 아키텍처

```
┌─────────────┐     WebSocket      ┌──────────────┐     MCP(stdio)     ┌─────────────┐
│ Unity Editor │◄─────────────────►│  Node.js     │◄──────────────────►│ Claude Code │
│ (C# Plugin)  │    localhost:8090  │  MCP Server  │                    │             │
└─────────────┘                    └──────────────┘                    └─────────────┘
```

- Unity Editor: C#으로 WebSocket 서버를 띄우고, ShaderUtil/CompilationPipeline 등 Unity API 데이터를 JSON으로 제공
- Node.js MCP Server: Unity WebSocket에 연결하고, MCP 프로토콜(stdio)로 Claude Code에 Tool/Resource를 노출
- Claude Code Plugin: MCP 서버를 번들링하여 플러그인 설치 시 자동 설정

## 프로젝트 구조

```
unity-shader-mcp/
├── README.md                         # 전체 설치 가이드 (한국어 + 영어)
├── LICENSE
│
├── claude-plugin/                    # Claude Code 플러그인
│   ├── .claude-plugin/
│   │   └── plugin.json
│   ├── servers/
│   │   └── shader-mcp-server/
│   │       ├── package.json
│   │       ├── tsconfig.json
│   │       ├── src/
│   │       │   ├── index.ts          # MCP 서버 진입점
│   │       │   ├── unity-bridge.ts   # Unity WebSocket 클라이언트
│   │       │   ├── tools/
│   │       │   │   ├── shader-compile.ts
│   │       │   │   ├── shader-analyze.ts
│   │       │   │   ├── shader-variants.ts
│   │       │   │   ├── shader-properties.ts
│   │       │   │   └── material-info.ts
│   │       │   └── resources/
│   │       │       ├── pipeline-info.ts
│   │       │       ├── shader-includes.ts
│   │       │       └── shader-keywords.ts
│   │       └── build/                # 컴파일된 JS
│   ├── commands/
│   │   └── shader-help.md            # /shader-help 슬래시 커맨드
│   └── skills/
│       └── unity-shader.md           # 셰이더 도메인 지식
│
└── unity-package/                    # Unity Editor 패키지
    ├── package.json                  # com.shader-mcp.bridge
    ├── Editor/
    │   ├── ShaderMCPServer.cs        # WebSocket 서버 + EditorWindow
    │   ├── ShaderAnalyzer.cs         # 셰이더 분석 로직
    │   ├── ShaderCompileWatcher.cs   # 컴파일 이벤트 감지
    │   ├── MaterialInspector.cs      # 머티리얼 정보 수집
    │   ├── PipelineDetector.cs       # 렌더 파이프라인 감지
    │   └── MessageHandler.cs         # WebSocket 메시지 라우팅
    └── Runtime/
        └── AssemblyInfo.cs
```

---

## Phase 1: Unity Editor C# 패키지

### 1-1. WebSocket 서버 (ShaderMCPServer.cs)

EditorWindow 기반으로 구현한다.

```
- WebSocket 서버를 localhost:8090에서 실행
- EditorWindow UI에 연결 상태, 로그 표시
- 메뉴: Tools > Shader MCP > Server Window
- EditorApplication.quitting 시 서버 종료
- Domain Reload 시에도 연결 유지하도록 [InitializeOnLoad] 활용
```

WebSocket 라이브러리는 NativeWebSocket 또는 System.Net.WebSockets를 사용한다. 외부 DLL 의존성을 최소화한다.

### 1-2. 메시지 프로토콜

Unity ↔ MCP Server 간 JSON 메시지 규격:

```json
// Request (MCP Server → Unity)
{
  "id": "uuid",
  "method": "shader/compile",
  "params": {
    "shaderPath": "Assets/Shaders/Character.shader"
  }
}

// Response (Unity → MCP Server)
{
  "id": "uuid",
  "result": {
    "success": true,
    "errors": [],
    "warnings": ["half precision truncation at line 42"],
    "variantCount": 128
  }
}

// Error
{
  "id": "uuid",
  "error": {
    "code": -1,
    "message": "Shader not found"
  }
}
```

### 1-3. 제공할 Unity API 기능

각 기능을 method 이름으로 노출한다:

**셰이더 분석**
- `shader/list` — 프로젝트 내 모든 셰이더 목록 반환 (경로, 이름, 패스 수, variant 수)
- `shader/compile` — 특정 셰이더 컴파일 실행, 에러/경고 반환. ShaderUtil.GetShaderMessages() 사용
- `shader/variants` — 셰이더의 keyword 조합과 variant 수 분석. ShaderUtil.GetShaderVariantCount() 등 사용
- `shader/properties` — 셰이더의 프로퍼티 목록 (이름, 타입, 기본값, 어트리뷰트)
- `shader/getCode` — 셰이더 소스 코드 읽기 (include resolve 포함)
- `shader/includes` — 프로젝트의 .cginc/.hlsl include 파일 목록과 내용

**머티리얼**
- `material/list` — 프로젝트 내 머티리얼 목록
- `material/info` — 특정 머티리얼의 셰이더, 프로퍼티 값, 키워드 상태
- `material/keywords` — 활성화된 shader keyword 목록

**파이프라인**
- `pipeline/info` — 현재 렌더 파이프라인 정보 (Built-in/URP/HDRP, 설정값)
- `pipeline/qualitySettings` — Quality Settings의 렌더링 관련 설정

**에디터 상태**
- `editor/logs` — Console 로그 중 셰이더 관련 로그 필터링
- `editor/platform` — 현재 빌드 타겟 플랫폼, Graphics API

### 1-4. ShaderAnalyzer.cs 핵심 로직

```csharp
// 참고할 Unity API들:
ShaderUtil.GetShaderMessageCount(shader)
ShaderUtil.GetShaderMessage(shader, index)
ShaderUtil.GetVariantCount(shader, passType)
ShaderUtil.GetShaderGlobalKeywords(shader)
ShaderUtil.GetShaderLocalKeywords(shader)
ShaderUtil.HasProceduralInstancing(shader)
shader.GetPropertyCount()
shader.GetPropertyName(index)
shader.GetPropertyType(index)
shader.GetPropertyDescription(index)
// 등등
```

ShaderUtil의 많은 메서드가 internal이므로 리플렉션이 필요할 수 있다. 리플렉션 사용 시 Unity 버전별 호환성에 주의한다.

---

## Phase 2: Node.js MCP Server

### 2-1. MCP Server 구현 (index.ts)

@modelcontextprotocol/sdk 패키지를 사용한다.

```bash
npm init -y
npm install @modelcontextprotocol/sdk ws
npm install -D typescript @types/node @types/ws
```

### 2-2. Tools 정의

각 Tool은 Unity의 method와 1:1 매핑된다:

```
Tool: compile_shader
  - input: { shaderPath: string }
  - Unity method: shader/compile
  - output: 컴파일 결과 (에러, 경고, variant 수)

Tool: analyze_shader_variants
  - input: { shaderPath: string }
  - Unity method: shader/variants
  - output: keyword 조합, variant 수, 예상 빌드 크기

Tool: get_shader_properties
  - input: { shaderPath: string }
  - Unity method: shader/properties
  - output: 프로퍼티 목록 (이름, 타입, 기본값)

Tool: get_material_info
  - input: { materialPath: string }
  - Unity method: material/info
  - output: 머티리얼 설정값

Tool: list_shaders
  - input: { filter?: string }
  - Unity method: shader/list
  - output: 셰이더 목록

Tool: get_shader_code
  - input: { shaderPath: string, resolveIncludes?: boolean }
  - Unity method: shader/getCode
  - output: 셰이더 소스 코드 (include 해결 포함)

Tool: get_shader_logs
  - input: { severity?: "error" | "warning" | "all" }
  - Unity method: editor/logs
  - output: 셰이더 관련 콘솔 로그
```

### 2-3. Resources 정의

Claude가 컨텍스트로 참조할 수 있는 정적/반정적 데이터:

```
Resource: unity://pipeline/info
  - 현재 렌더 파이프라인 종류와 설정

Resource: unity://shader/includes
  - 프로젝트의 include 파일 경로와 내용 목록

Resource: unity://shader/keywords
  - 프로젝트에서 사용 중인 global/local shader keyword 전체 목록

Resource: unity://editor/platform
  - 현재 빌드 타겟, Graphics API 정보
```

### 2-4. Unity Bridge (unity-bridge.ts)

```
- WebSocket 클라이언트로 Unity(localhost:8090)에 연결
- 자동 재연결 로직 (3초 간격, 최대 10회)
- request/response 매칭 (id 기반)
- 연결 끊김 시 Tool 호출에 적절한 에러 메시지 반환
- 타임아웃 처리 (기본 10초, 컴파일은 30초)
```

---

## Phase 3: Claude Code 플러그인 패키징

### 3-1. plugin.json

```json
{
  "name": "unity-shader-tools",
  "version": "0.1.0",
  "description": "Unity shader analysis and compilation tools via MCP",
  "mcpServers": {
    "unity-shader": {
      "command": "node",
      "args": ["${CLAUDE_PLUGIN_ROOT}/servers/shader-mcp-server/build/index.js"]
    }
  }
}
```

### 3-2. Skills 파일 (skills/unity-shader.md)

Claude가 셰이더 관련 질문에 답할 때 참고할 도메인 지식을 포함한다:

```markdown
# Unity Shader Development Context

## 사용 가능한 도구
- compile_shader: 셰이더 컴파일 및 에러 확인
- analyze_shader_variants: variant 폭발 분석
- get_shader_properties: 프로퍼티 조회
- get_material_info: 머티리얼 설정 확인
- get_shader_code: 소스 코드 읽기 (include resolve)
- list_shaders: 프로젝트 셰이더 목록

## 워크플로우 가이드
- 셰이더 수정 후 반드시 compile_shader로 컴파일 확인
- variant 수가 1000개 이상이면 경고하고 keyword 최적화 제안
- 모바일 타겟일 때 half precision 사용 권장
- get_shader_code로 소스를 읽은 후 수정 제안

## 모바일 최적화 체크리스트
- float → half 변환 가능한 변수 식별
- 불필요한 shader_feature → multi_compile 전환
- 텍스처 샘플링 횟수 최소화
- 복잡한 수학 연산 근사치 대체
```

### 3-3. 슬래시 커맨드 (commands/shader-help.md)

```markdown
---
name: shader-help
description: Unity 셰이더 분석 및 최적화 도우미
---

프로젝트의 셰이더를 분석합니다.

1. list_shaders로 프로젝트의 셰이더 목록을 확인합니다
2. 각 셰이더의 variant 수를 분석하고 과도한 경우 경고합니다
3. 컴파일 에러/경고가 있는 셰이더를 보고합니다
4. 현재 빌드 타겟에 맞는 최적화 제안을 합니다
```

---

## Phase 4: README.md 작성

이중 언어(한국어/영어)로 작성한다. 다음 내용을 포함:

```
1. 프로젝트 소개 (한 줄 설명 + 스크린샷/GIF)
2. 요구사항 (Unity 2021.3+, Node.js 18+, Claude Code)
3. 설치 방법
   - Step 1: Unity 패키지 설치 (UPM git URL)
   - Step 2: Claude Code 플러그인 설치 (/plugin install)
   - Step 3: 연결 확인
4. 사용 예시
   - "이 셰이더의 variant 수를 분석해줘"
   - "Character.shader를 컴파일하고 에러 확인해줘"
   - "모바일용으로 최적화할 부분을 찾아줘"
5. 제공 도구 목록 (Tools, Resources)
6. 트러블슈팅
7. Contributing 가이드
8. License
```

---

## 구현 순서 (권장)

1. **Unity C# 패키지부터 시작** — WebSocket 서버 + ShaderAnalyzer 기본 기능
2. **Node.js MCP Server** — Unity와 연결 + Tool 1~2개 (compile_shader, list_shaders)
3. **연동 테스트** — Claude Code에서 수동으로 MCP 추가하여 동작 확인
4. **나머지 Tool/Resource 추가** — variant 분석, 머티리얼 정보 등
5. **Claude Code 플러그인 패키징** — plugin.json, skills, commands
6. **README 작성 및 GitHub 배포**

## 기술 제약사항

- Unity의 ShaderUtil 중 일부 API는 internal이므로 리플렉션 필요. 리플렉션 사용 시 try-catch로 감싸고 지원 불가 시 graceful fallback
- Domain Reload 시 WebSocket 연결이 끊길 수 있음. Enter Play Mode Settings에서 Reload Domain을 끄거나, 자동 재연결 로직 구현
- WebSocket 통신은 Main Thread에서 처리해야 하므로 EditorApplication.update 콜백 활용
- Unity 2021.3 LTS 이상 지원 목표
- Node.js 18+ 필요 (MCP SDK 요구사항)

## 코드 스타일

- C#: Unity 컨벤션 (PascalCase for public, _camelCase for private fields)
- TypeScript: ESM, strict mode, async/await 패턴
- 에러 처리: 모든 외부 통신에 try-catch + 적절한 에러 메시지
- 로깅: Unity쪽은 Debug.Log with [ShaderMCP] prefix, Node쪽은 console.error (stdout 사용 금지 — MCP stdio 충돌)
