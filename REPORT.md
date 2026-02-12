# Unity Shader MCP Tools - 기술 보고서

> Unity Editor와 AI 코딩 어시스턴트를 연결하여 셰이더 개발을 자동화하는 통합 도구

---

## 1. 해결하려는 문제

Unity 셰이더 개발 시 다음과 같은 반복적인 불편함이 존재한다:

- 셰이더 컴파일 에러 확인을 위해 Unity Editor와 IDE를 왔다 갔다 해야 함
- 배리언트 수, 키워드 조합 분석이 수동적이고 번거로움
- HLSL/ShaderLab 함수의 시그니처나 문서를 매번 검색해야 함
- AI 어시스턴트가 Unity 프로젝트의 실시간 상태(파이프라인, 빌드 타겟 등)를 알 수 없음

**목표**: AI 코딩 어시스턴트가 Unity Editor의 셰이더 관련 기능에 직접 접근할 수 있게 하여, 자연어 한 마디로 분석/컴파일/코드 지원까지 처리하는 환경을 구축한다.

---

## 2. 전체 아키텍처

```
┌─────────────┐     WebSocket      ┌──────────────┐     MCP(stdio)     ┌─────────────┐
│ Unity Editor │◄─────────────────►│  Node.js     │◄──────────────────►│ Claude Code │
│ (C# Plugin)  │   localhost:8090  │  MCP Server  │                    │ / OpenCode  │
└─────────────┘                    └──────────────┘                    │ / Gemini CLI│
                                          ▲                            └─────────────┘
                                          │ stdio
                                   ┌──────┴───────┐
                                   │  shader-ls   │
                                   │ (LSP Server) │
                                   └──────────────┘
```

시스템은 세 개의 독립적인 계층으로 구성된다:

| 계층 | 기술 | 역할 |
|------|------|------|
| **Unity C# Plugin** | C#, TcpListener, WebSocket | 셰이더 데이터 수집 및 컴파일 실행 |
| **Node.js MCP Server** | TypeScript, MCP SDK, WebSocket | 프로토콜 변환 브릿지 (WebSocket ↔ MCP stdio) |
| **shader-ls (LSP)** | .NET, LSP | ShaderLab/HLSL 코드 인텔리전스 (hover, completion, signature) |

각 계층은 독립적으로 동작하며, 하나가 없어도 나머지는 정상 작동한다.

---

## 3. Unity C# Plugin — WebSocket 서버 구현

### 3.1 왜 WebSocket인가

MCP는 stdio 기반 프로토콜이다. Unity Editor는 독립 프로세스이므로, MCP 서버(Node.js)와 Unity 사이에 별도 통신 채널이 필요하다. WebSocket을 선택한 이유:

- 양방향 통신 (요청/응답 패턴에 적합)
- Unity의 `EditorApplication.update` 루프에서 non-blocking으로 처리 가능
- 디버깅이 쉬움 (`wscat`으로 직접 테스트 가능)

### 3.2 TcpListener 수동 구현

Unity의 Mono 런타임에서 `HttpListener.AcceptWebSocketAsync`를 사용할 수 없다. 따라서 **TcpListener 위에 RFC 6455 WebSocket 핸드셰이크를 직접 구현**했다.

```
[클라이언트 요청]                    [서버 응답]
GET / HTTP/1.1                      HTTP/1.1 101 Switching Protocols
Upgrade: websocket                  Upgrade: websocket
Sec-WebSocket-Key: dGhlIHN...       Sec-WebSocket-Accept: s3pPLMB...
```

핸드셰이크의 핵심은 `Sec-WebSocket-Accept` 계산이다:
```
Accept = Base64(SHA1(Key + GUID))
```

### 3.3 SHA-1 직접 구현 (Mono 버그 우회)

핸드셰이크 구현 중 가장 어려웠던 문제: Unity Mono의 `SHA1.Create()`와 `SHA1Managed` 모두 **잘못된 해시 값을 반환**했다. openssl 결과와 비교하여 확인했고, 결국 **RFC 3174 기반 순수 C# SHA-1**을 직접 구현하여 해결했다.

### 3.4 ws 패키지 GUID 불일치

SHA-1을 해결한 후에도 핸드셰이크가 실패했다. Raw TCP 테스트로 SHA-1 정확성을 확인한 뒤, Node.js측 `ws@8.19.0` 패키지 소스를 디버깅하여 원인을 찾았다:

| 항목 | GUID 값 |
|------|---------|
| RFC 6455 표준 | `258EAFA5-E914-47DA-95CA-5AB5DC11115E` |
| ws 패키지 사용 | `258EAFA5-E914-47DA-95CA-C5AB0DC85B11` |

Unity 서버의 GUID 상수를 ws 패키지에 맞춰 해결했다.

### 3.5 메인 스레드 처리

Unity API는 메인 스레드에서만 호출 가능하다. WebSocket 메시지 수신은 비동기이므로, 요청을 큐에 넣고 `EditorApplication.update` 콜백에서 메인 스레드에서 처리한다.

```
WebSocket 수신 (백그라운드) → 큐에 적재 → EditorApplication.update에서 소비 → Unity API 호출 → 응답 전송
```

### 3.6 Unity 버전 호환성

| 전략 | 적용 |
|------|------|
| `#if UNITY_6000_0_OR_NEWER` | Unity 6.0+ 전용 API (`shader.keywordSpace` 등) |
| ShaderUtil 리플렉션 폴백 | internal API 접근이 제한된 버전 대응 |
| try-catch 래핑 | API 변경에 대한 안전한 폴백 |

---

## 4. Node.js MCP Server — 프로토콜 브릿지

### 4.1 MCP란

**Model Context Protocol (MCP)** 은 AI 어시스턴트가 외부 도구를 호출할 수 있게 하는 표준 프로토콜이다. stdio 기반으로 동작하며, 도구(Tool)와 리소스(Resource)를 정의하면 AI가 자연어 요청에 맞는 도구를 자동으로 선택하여 호출한다.

### 4.2 서버 구조

```
index.ts (진입점)
├── UnityBridge (WebSocket 클라이언트)
│   └── Unity Editor와 통신
├── ShaderLspClient (LSP 클라이언트)
│   └── shader-ls와 통신
├── tools/ (도구 정의)
│   ├── shader-compile.ts    → compile_shader
│   ├── shader-analyze.ts    → analyze_shader_variants, list_shaders
│   ├── shader-variants.ts   → get_shader_code
│   ├── shader-properties.ts → get_shader_properties
│   ├── material-info.ts     → get_material_info, get_shader_logs
│   ├── lsp-hover.ts         → shader_hover
│   ├── lsp-completion.ts    → shader_completion
│   ├── lsp-signature.ts     → shader_signature_help
│   └── lsp-diagnostics.ts   → shader_diagnostics
└── resources/ (리소스 정의)
    ├── pipeline-info.ts     → unity://pipeline/info
    ├── shader-includes.ts   → unity://shader/includes
    ├── shader-keywords.ts   → unity://shader/keywords
    └── editor-platform.ts   → unity://editor/platform
```

### 4.3 UnityBridge — WebSocket 클라이언트

Node.js 측에서 Unity에 연결하는 WebSocket 클라이언트이다. 핵심 설계:

- **UUID 기반 요청/응답 매칭**: 각 요청에 UUID를 부여하고, 응답이 돌아올 때 UUID로 매칭한다. 동시 다중 요청을 지원한다.
- **자동 재연결**: 3초 간격, 최대 10회 시도. Unity Domain Reload 시 자동 복구된다.
- **Non-blocking 시작**: Unity가 꺼져 있어도 MCP 서버는 정상 시작된다.
- **타임아웃**: 기본 10초, 컴파일은 30초.

### 4.4 도구 등록 패턴

모든 도구는 동일한 패턴을 따른다:

```typescript
export function registerXxxTool(server: McpServer, bridge: UnityBridge): void {
  server.tool(
    "tool_name",              // AI가 호출할 도구 이름
    "Tool description",       // AI가 도구를 선택할 때 참고하는 설명
    { /* Zod 스키마 */ },     // 파라미터 검증
    async (params) => {       // 실행 핸들러
      const result = await bridge.request("method", params);
      return { content: [{ type: "text", text: JSON.stringify(result) }] };
    }
  );
}
```

AI 어시스턴트는 도구의 이름과 설명을 보고, 사용자의 자연어 요청에 적합한 도구를 스스로 선택하여 호출한다.

### 4.5 번들링

esbuild로 전체 TypeScript 소스를 **단일 파일** (`dist/server.mjs`, ~31KB)로 번들링한다. 사용자는 `npm install` 없이 `node dist/server.mjs`로 바로 실행할 수 있다.

```bash
esbuild build/index.js --bundle --platform=node --format=esm --outfile=dist/server.mjs --packages=external
```

`--packages=external` 옵션으로 npm 패키지는 외부 의존성으로 남기되, 프로젝트 소스는 모두 인라인한다.

---

## 5. LSP 통합 — shader-ls 래핑

### 5.1 왜 LSP를 추가했는가

기존 도구들은 Unity Editor에 의존하여 셰이더를 분석한다. 하지만 코드 수준의 지원(함수 문서, 자동완성, 시그니처 도움말)은 Unity API로 제공되지 않는다. 오픈소스 [shader-language-server](https://github.com/shader-ls/shader-language-server) (shader-ls)가 이 기능을 제공하므로, MCP 서버에서 래핑하여 노출한다.

### 5.2 설계 원칙

| 원칙 | 구현 |
|------|------|
| **Lazy 초기화** | 첫 LSP 도구 호출 시에만 shader-ls 프로세스 시작. 기존 도구에 영향 없음 |
| **자동 설치** | shader-ls 미설치 시 `dotnet tool install --global shader-ls` 자동 실행 |
| **크래시 복구** | shader-ls 프로세스 종료 시 다음 요청에서 자동 재시작 |
| **독립성** | .NET이 없어도 기존 Unity 도구는 정상 동작 |

### 5.3 자동 설치 흐름

```
LSP 도구 호출
  → shader-ls spawn 시도
  → 실패?
    → dotnet 존재 확인 (where/which)
    → dotnet 있음 → dotnet tool install --global shader-ls
    → 설치 성공 → shader-ls spawn 재시도
    → dotnet 없음 → ".NET SDK를 먼저 설치하세요" 에러 반환
```

### 5.4 LSP 프로토콜 통신

shader-ls는 stdio 기반 LSP 서버이다. `vscode-languageserver-protocol` 패키지로 JSON-RPC 통신을 처리한다.

```
MCP Server                           shader-ls
    │                                    │
    │──── initialize ──────────────────►│
    │◄─── initializeResult ────────────│
    │──── initialized ─────────────────►│
    │                                    │
    │──── textDocument/didOpen ────────►│
    │──── textDocument/hover ──────────►│
    │◄─── hover result ────────────────│
    │                                    │
    │──── shutdown ────────────────────►│
    │──── exit ────────────────────────►│
```

### 5.5 문서 관리

LSP는 상태 기반 프로토콜이다. 서버가 파일 내용을 추적하려면 `didOpen`/`didChange` 알림을 보내야 한다.

`ShaderLspClient`는 열린 문서를 내부 Map으로 관리한다:
- 새 파일 → `didOpen` 전송
- 내용 변경 → `didChange` + 버전 번호 증가
- 같은 내용 → 알림 생략

### 5.6 제공 도구 (4개)

| 도구 | 설명 | shader-ls 지원 |
|------|------|---------------|
| `shader_hover` | 심볼의 타입/문서 정보 | v0.1.3+ |
| `shader_completion` | 코드 자동완성 제안 | v0.1.3+ |
| `shader_signature_help` | 함수 시그니처 도움말 | v0.1.3+ |
| `shader_diagnostics` | 진단(에러/경고) | 미지원 (placeholder) |

`shader_diagnostics`는 shader-ls에서 아직 미구현이므로 placeholder로 등록했다. 현재는 `compile_shader`로 대체 안내한다.

---

## 6. Claude Code Plugin 시스템

### 6.1 플러그인 구조

```
claude-plugin/
├── .claude-plugin/
│   └── plugin.json              ← 플러그인 메타데이터 + MCP 서버 경로
├── .mcp.json                    ← MCP 서버 실행 설정
├── commands/                    ← 슬래시 커맨드 정의 (14개 .md 파일)
│   ├── shader.md                ← /shader (대화형 메뉴)
│   ├── shader-compile.md        ← /shader-compile
│   ├── shader-hover.md          ← /shader-hover (LSP)
│   ├── shader-completion.md     ← /shader-completion (LSP)
│   ├── shader-signature.md      ← /shader-signature (LSP)
│   ├── shader-diagnostics.md    ← /shader-diagnostics (LSP)
│   └── ...
└── skills/
    └── unity-shader/SKILL.md    ← 셰이더 도메인 지식
```

### 6.2 커맨드와 도구의 관계

- **MCP Tool** (`compile_shader`): AI가 자연어를 분석하여 자동 선택하는 저수준 도구
- **Slash Command** (`/shader-compile`): 사용자가 명시적으로 호출하는 고수준 워크플로우

커맨드 `.md` 파일은 AI에게 "이 커맨드가 호출되면 어떤 도구를 어떤 순서로 쓰라"는 지침을 제공한다.

### 6.3 배포

GitHub 레포를 그대로 마켓플레이스로 사용한다:

```bash
# 사용자가 설치할 때
/plugin marketplace add KULEEEE/Claude-Code-For-Unity-Shader
/plugin install unity-shader-tools@unity-shader-mcp
```

`.claude-plugin/marketplace.json`이 마켓플레이스 메타데이터를 정의하고, `claude-plugin/` 디렉토리가 실제 플러그인 소스이다.

---

## 7. 전체 기능 목록

### 7.1 MCP Tools (11개)

#### Unity 연동 도구 (7개)

| 도구 | 설명 | 파라미터 |
|------|------|----------|
| `compile_shader` | 셰이더 컴파일 + 에러/경고 반환 | `shaderPath` |
| `list_shaders` | 프로젝트 내 전체 셰이더 목록 | `filter` (선택) |
| `analyze_shader_variants` | 키워드 조합/배리언트 수 분석 | `shaderPath` |
| `get_shader_code` | 셰이더 소스 코드 읽기 | `shaderPath`, `resolveIncludes` |
| `get_shader_properties` | 셰이더 프로퍼티 정의 조회 | `shaderPath` |
| `get_material_info` | 머티리얼 상세 정보 | `materialPath` |
| `get_shader_logs` | Unity 콘솔의 셰이더 관련 로그 | `severity` (선택) |

#### LSP 코드 인텔리전스 도구 (4개)

| 도구 | 설명 | 파라미터 |
|------|------|----------|
| `shader_hover` | 심볼의 타입/문서 정보 | `shaderPath`, `line`, `character`, `content`(선택) |
| `shader_completion` | 코드 자동완성 제안 | `shaderPath`, `line`, `character`, `content`(선택) |
| `shader_signature_help` | 함수 시그니처 도움말 | `shaderPath`, `line`, `character`, `content`(선택) |
| `shader_diagnostics` | 진단 (현재 placeholder) | `shaderPath`, `content`(선택) |

### 7.2 MCP Resources (4개)

| URI | 설명 |
|-----|------|
| `unity://pipeline/info` | 렌더 파이프라인 (Built-in/URP/HDRP) |
| `unity://shader/includes` | .cginc/.hlsl 포함 파일 목록 |
| `unity://shader/keywords` | 전역/로컬 셰이더 키워드 |
| `unity://editor/platform` | 빌드 타겟, Graphics API, Unity 버전 |

### 7.3 Slash Commands (14개)

| 커맨드 | 설명 |
|--------|------|
| `/shader` | 대화형 도구 메뉴 |
| `/shader-help` | 전체 프로젝트 셰이더 분석 |
| `/shader-list` | 셰이더 목록 |
| `/shader-compile` | 셰이더 컴파일 + 에러 확인 |
| `/shader-analyze` | 배리언트 분석 |
| `/shader-code` | 소스 코드 보기 |
| `/shader-props` | 프로퍼티 조회 |
| `/material-info` | 머티리얼 정보 |
| `/shader-logs` | 콘솔 로그 |
| `/shader-status` | 연결 상태 + 환경 대시보드 |
| `/shader-hover` | 심볼 호버 정보 (LSP) |
| `/shader-completion` | 코드 자동완성 (LSP) |
| `/shader-signature` | 함수 시그니처 (LSP) |
| `/shader-diagnostics` | 셰이더 진단 (LSP) |

---

## 8. 파일 구성

### Unity C# Package (8개 소스 파일)

| 파일 | 역할 |
|------|------|
| `ShaderMCPServer.cs` | TcpListener 기반 WebSocket 서버 (RFC 6455) + EditorWindow UI |
| `ShaderAnalyzer.cs` | 셰이더 분석 (목록, 컴파일, 배리언트, 프로퍼티, 소스 코드) |
| `MaterialInspector.cs` | 머티리얼 정보 수집 |
| `PipelineDetector.cs` | 렌더 파이프라인 감지 (리플렉션으로 URP/HDRP 하드 의존성 회피) |
| `ShaderCompileWatcher.cs` | 셰이더 관련 콘솔 로그 필터링 |
| `MessageHandler.cs` | JSON 메시지 라우팅 |
| `JsonHelper.cs` | JsonUtility 한계 보완 (루트 배열/딕셔너리 지원) |
| `AssemblyInfo.cs` | 어셈블리 메타데이터 |

### Node.js MCP Server (16개 TypeScript 파일)

| 파일 | 역할 |
|------|------|
| `index.ts` | McpServer + StdioServerTransport 진입점 |
| `unity-bridge.ts` | Unity WebSocket 클라이언트 |
| `lsp-client.ts` | shader-ls LSP 클라이언트 (자동 설치/재시작) |
| `tools/shader-compile.ts` | compile_shader 도구 |
| `tools/shader-analyze.ts` | analyze_shader_variants, list_shaders 도구 |
| `tools/shader-variants.ts` | get_shader_code 도구 |
| `tools/shader-properties.ts` | get_shader_properties 도구 |
| `tools/material-info.ts` | get_material_info, get_shader_logs 도구 |
| `tools/lsp-hover.ts` | shader_hover 도구 |
| `tools/lsp-completion.ts` | shader_completion 도구 |
| `tools/lsp-signature.ts` | shader_signature_help 도구 |
| `tools/lsp-diagnostics.ts` | shader_diagnostics 도구 |
| `resources/pipeline-info.ts` | unity://pipeline/info 리소스 |
| `resources/shader-includes.ts` | unity://shader/includes 리소스 |
| `resources/shader-keywords.ts` | unity://shader/keywords 리소스 |
| `resources/editor-platform.ts` | unity://editor/platform 리소스 |

---

## 9. 사전 요구사항

| 요구사항 | 필요 대상 | 비고 |
|----------|----------|------|
| Unity 2021.3+ | Unity 도구 | Unity 6.0+ 권장 |
| Node.js 18+ | MCP 서버 | 전체 필수 |
| .NET 7.0+ SDK | LSP 도구만 | [다운로드](https://dotnet.microsoft.com/download) |
| Claude Code / OpenCode / Gemini CLI | AI 어시스턴트 | 택 1 |

---

## 10. 사용 방법

### 10.1 설치

**Step 1: Unity Package 설치**

Unity Editor → Window → Package Manager → `+` → **Add package from git URL**:
```
https://github.com/KULEEEE/Claude-Code-For-Unity-Shader.git?path=unity-package
```

**Step 2: Claude Code Plugin 설치**

```bash
/plugin marketplace add KULEEEE/Claude-Code-For-Unity-Shader
/plugin install unity-shader-tools@unity-shader-mcp
```

**Step 3: Unity 서버 시작**

Unity Editor → **Tools > Shader MCP > Server Window** → **Start Server**

### 10.2 사용 예시

#### 셰이더 분석
```
"프로젝트에 있는 셰이더 목록 보여줘"
"Character.shader 컴파일해서 에러 확인해줘"
"이 셰이더 배리언트가 몇 개나 되는지 분석해줘"
```

#### 최적화
```
"모바일용으로 최적화할 부분 찾아줘"
"배리언트가 너무 많은 셰이더 찾아줘"
```

#### 코드 인텔리전스 (LSP)
```
"saturate() 함수가 뭔지 알려줘"
"이 셰이더 15번째 줄에서 자동완성 보여줘"
"lerp 함수의 파라미터 정보 알려줘"
```

#### 슬래시 커맨드
```
/shader              → 대화형 도구 메뉴
/shader-compile      → 셰이더 컴파일
/shader-hover        → 심볼 정보 확인
/shader-help         → 전체 프로젝트 분석
```

---

## 11. 버전 이력

| 버전 | 주요 변경 |
|------|----------|
| 0.1.0 | 초기 릴리스 — Unity WebSocket 서버, MCP 도구 7개, 리소스 4개, 슬래시 커맨드 10개 |
| 0.1.3 | LSP 통합 — shader-ls 래핑, 코드 인텔리전스 도구 4개, 슬래시 커맨드 4개 추가 |

---

## 12. 총 산출물

| 카테고리 | 파일 수 | 설명 |
|----------|---------|------|
| Unity C# | 8 (+8 .meta) | WebSocket 서버, 셰이더 분석, 머티리얼 검사 |
| Node.js TypeScript | 16 소스 + 1 번들 | MCP 서버, Unity 도구, LSP 도구, 리소스 |
| Plugin 설정 | 3 | plugin.json, .mcp.json, marketplace.json |
| 슬래시 커맨드 | 14 | Unity 도구 + LSP 도구 + 대화형 메뉴 + 상태 |
| 스킬 | 1 | SKILL.md (셰이더 도메인 지식) |
| 문서 | 2 | README.md, REPORT.md |
| 설정 | 3 | .gitignore, package.json, tsconfig.json |
| **합계** | **~62개** | |
