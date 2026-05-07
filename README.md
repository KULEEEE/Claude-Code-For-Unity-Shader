# Unity Agent

**AI-powered Unity Editor tools — Error auto-fix, Shader analysis, Frame Debugging, Image Generation & SVN, all powered by Claude Code running headlessly inside the Editor.**

```
┌──────────────────────────────────────┐
│          Unity Editor                │
│  ┌────────────────────────────────┐  │   spawn per request   ┌──────────────────────┐
│  │   Error Solver                 │  │ ────────────────────► │  node headless.mjs   │
│  │   Shader Inspector             │  │   stdin JSON          │  (Server~/)          │
│  │   Frame Debugger AI            │  │ ◄──────────────────── │  Claude Agent SDK    │
│  │   AI Chat / Image Gen          │  │   stdout JSON lines   └──────────────────────┘
│  │   SVN Tool                     │  │
│  └────────────────────────────────┘  │
│   Everything ships inside the        │
│   Unity package — no MCP, no npm     │
└──────────────────────────────────────┘
```

---

## Features

### Error Solver
Unity 에러를 자동으로 수집하고, AI가 분석 + 코드 수정까지 해주는 도구.

- **실시간 에러 수집** — `Application.logMessageReceived` + `CompilationPipeline` 훅
- **Solve 버튼** — 에러 선택 후 클릭하면 AI가 소스 읽고 → 원인 분석 → 코드 수정
- **스트리밍 응답** — AI 작업 진행 상황 실시간 표시
- **소스 바로가기** — 에러 발생 파일/라인 클릭으로 IDE 이동

https://github.com/user-attachments/assets/afc01579-e968-4505-81ac-1c56985d0e70

### Shader Inspector
셰이더 분석, 머티리얼 검사, AI 채팅이 통합된 에디터 윈도우.

- **Shaders 탭** — 셰이더 목록, 컴파일, 배리언트 분석, AI 분석
- **Materials 탭** — 머티리얼 목록, 프로퍼티/키워드 조회
- **AI Chat 탭** — 셰이더 컨텍스트 기반 자유 대화
- **Include Graph** — #include 의존성 그래프 시각화

### Frame Debugger AI *(v0.11.0)*
AI 기반 프레임 디버깅 도구. Unity Frame Debugger 데이터를 AI가 분석하여 렌더링 병목과 배치 브레이크 원인을 찾아줍니다.

- **Overview 탭** — 프레임 요약: 이벤트 수, 버텍스/인덱스 카운트, 이벤트 타입 히스토그램, 핫스팟 Top-12, 셰이더별 통계, 배치 브레이크 원인 분석
- **Events 탭** — 전체 프레임 이벤트 목록, 클릭하면 셰이더/패스/렌더 스테이트 상세 조회
- **Compare 탭** — 두 이벤트 간 diff 비교 (셰이더, 키워드 변경, 렌더 스테이트 차이, 배치 브레이크 전환)
- **AI Chat 탭** — 프레임 데이터 컨텍스트 기반 AI 질의 ("이 드로우콜이 왜 느린가요?")
- **Tiki-Taka 워크플로우** — Summary → Search → Detail/Compare → RT Snapshot 순으로 AI가 단계적 분석

### AI Chat & Image Generation *(v0.6.0 ~ v0.9.0)*
독립 AI 채팅 윈도우 + AI 이미지 생성 기능.

- **AI Chat** — Unity 프로젝트 컨텍스트 기반 자유 대화, 에셋 첨부, 대화 히스토리 지원
- **Image Gen 모드** — Claude가 프롬프트를 최적화한 뒤 이미지 생성
- **Nano Banana (Gemini)** — Google Gemini API 기반 이미지 생성, 레퍼런스 이미지 편집 지원
- **ComfyUI (Local)** — 로컬 ComfyUI 서버 연동으로 txt2img / img2img 지원
- **생성 이미지 저장** — `Assets/GeneratedImages/`에 프로젝트 에셋으로 저장

### SVN Tool *(v0.10.0)*
Unity 에디터 내 SVN 버전 관리 통합 도구.

- **History 탭** — 파일별 SVN 로그 조회 (최대 50개 리비전), 리비전별 diff, AI 변경사항 설명
- **Operations 탭** — 프로젝트 전체 `svn status`, 파일 다중 선택, 커밋/리버트/업데이트 일괄 실행

### Shader Include Graph (Standalone) *(v0.10.0)*
별도 의존성 없이 어떤 Unity 프로젝트에든 드롭인 가능한 셰이더 #include 그래프 시각화 도구.

- **인터랙티브 그래프** — 팬/줌, 노드 클릭으로 파일 정보 조회
- **파일 분석 패널** — Properties, Keywords, Functions, Structs, Defines 토글 표시
- `standalone-tools/GrShaderIncludeGraph/` 폴더를 프로젝트 `Editor/` 폴더에 복사하여 사용

---

## Requirements

- **Unity** 2021.3 LTS 이상
- **Node.js** 18+ (로컬 설치만 돼 있으면 됨 — 경로 자동 탐지)
- **Claude Code** 설치 및 인증 ([Claude Agent SDK](https://www.npmjs.com/package/@anthropic-ai/claude-agent-sdk) 를 내부 번들로 동봉)

---

## Installation

### 방법 1: Git URL (권장)

Unity Editor → Window > Package Manager → `+` → **Add package from git URL**:
```
https://github.com/KULEEEE/Unity-Agent-For-Claude-Code.git?path=unity-package
```

### 방법 2: 로컬 폴더

ZIP 다운로드 후 `unity-package/` 폴더를 Unity 프로젝트의 `Packages/UnityAgent/`에 복사. 번들된 헤드리스 런너(`Server~/`)가 함께 따라와야 합니다.

```
Packages/
  UnityAgent/
    package.json
    Editor/          ← Unity가 임포트하는 C# 코드
    Runtime/
    Server~/         ← Unity가 무시 (헤드리스 런너 번들 + node_modules)
```

### 방법 3: 디스크에서 추가

Package Manager → `+` → **Add package from disk** → `unity-package/package.json` 선택

> 별도의 npm 설치, `.mcp.json`, MCP 서버 설정이 전혀 필요하지 않습니다. Unity 패키지 하나로 끝.

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

### Frame Debugger AI

1. **Tools** → **Unity Agent** → **Frame Debugger AI** 열기
2. **Capture Frame** 클릭 → Unity Frame Debugger 자동 활성화 + 데이터 수집
3. Overview 탭에서 핫스팟/배치 브레이크 확인
4. Events 탭에서 이벤트 상세 조회, Compare 탭에서 두 이벤트 비교
5. **Ask AI** 버튼 또는 AI Chat 탭에서 분석 요청

### AI Chat & Image Gen

1. **Tools** → **Unity Agent** → **AI Chat** 열기
2. Chat 모드에서 자유 질문, Image Gen 모드에서 이미지 생성
3. 백엔드 선택: Nano Banana (Gemini) 또는 ComfyUI (Local)
4. 레퍼런스 이미지 첨부 가능, 생성 이미지는 프로젝트에 저장

### SVN Tool

1. **Tools** → **Unity Agent** → **SVN Tool** 열기
2. History 탭: 파일 선택 → SVN 로그 조회 → diff + AI 설명
3. Operations 탭: 파일 선택 → 커밋/리버트/업데이트

---

## Architecture

```
Unity Editor (C#)                              Bundled Node runner (Server~/)
├── UnityAgentServer.cs                        ├── headless.mjs
│   Locate Node.js, readiness probe            │   stdin JSON → Claude Agent SDK query()
├── AIRequestHandler.cs                        │   stdout JSON lines (status/chunk/image/result)
│   Spawn headless.mjs per AI request          │
│   Pipe prompt JSON to stdin                  ├── mcp-tools.mjs
│   Parse stdout JSON lines on main thread     │   Internal stdio MCP — only generate_image
├── ErrorCollector.cs                          │   Spawned as child of headless by SDK
│   Console log capture                        │
├── ErrorSolverWindow.cs                       └── node_modules/
│   Error list + Solve button                      @anthropic-ai/claude-agent-sdk
├── ShaderInspectorWindow.cs
│   Shader/Material/AI tabs
├── FrameDebuggerAIWindow.cs                   Server~ 폴더는 Unity가 무시 (이름이 ~ 로 끝남)
│   Overview/Events/Compare/AI Chat            → 에셋으로 임포트되지 않고 .meta 도 생기지 않음
├── FrameDebugBridge.cs
│   FD reflection (Unity 2019~6)
├── AIChatWindow.cs
│   AI Chat + Image Gen
├── SVNToolWindow.cs
│   SVN History + Operations
└── NanoBananaReceiver.cs
    이미지 수신 → AI Chat 창에 표시
```

### 통신 흐름

**모든 AI 요청 (Solve / AI Chat / Frame Debugger AI / Shader AI):**
```
Unity UI → AIRequestHandler → node headless.mjs (spawn)
  → Claude Agent SDK query() → Claude Code headless
  → stdout JSON lines → AIRequestHandler 메인 스레드 디스패치
```

**Image Generation:**
```
AI Chat UI → image/enhance → headless.mjs → Claude (프롬프트 최적화)
  → mcp-tools.mjs.generate_image → Gemini / ComfyUI
  → 임시 디렉토리에 결과 저장 → headless.mjs stdout image 이벤트
  → NanoBananaReceiver → AI Chat 창
```

---

## Troubleshooting

### AI Offline 표시
- Node.js 18+ 설치 여부 확인 (`node --version`)
- 커스텀 설치 경로를 쓴다면 `EditorPrefs` 의 `UnityAgent_NodeDir` 키에 Node 폴더 경로 지정
- `Packages/com.unity-agent/Server~/headless.mjs` 파일이 존재하는지 확인 (없다면 패키지가 올바르게 설치되지 않은 것)

### 에러 목록이 안 보임
- **Clear** 버튼으로 기존 에러 초기화 후 새 에러 발생시키기
- **Refresh** 버튼 클릭

### Frame Debugger 관련
- **Capture 안 됨** — Unity Frame Debugger 창이 열려 있어야 합니다. Capture Frame 버튼이 자동으로 열어줍니다
- **이벤트 로딩 느림** — 이벤트가 많을수록 캐싱에 시간 소요, 프로그레스 바로 진행 확인
- **Unity 6 호환** — FrameDebugBridge가 리플렉션 기반으로 Unity 2019~6 버전 자동 대응

### Image Generation 관련
- **Gemini API Key** — AI Chat 창의 Image Gen 모드에서 설정
- **ComfyUI 연결 실패** — `http://127.0.0.1:8188`에 ComfyUI 서버가 실행 중인지 확인

### SVN Tool 관련
- **SVN 명령어 실패** — `svn --version`이 터미널에서 동작하는지 확인
- **커스텀 SVN 경로** — EditorPrefs의 `UnityAgent_SvnPath` 키로 설정 가능

---

## License

MIT
