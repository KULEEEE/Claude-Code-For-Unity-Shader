---
name: shader-status
description: Check Unity connection, pipeline, and platform info
---

Check the current Unity Editor status by reading all available resources:

1. Read `unity://pipeline/info` - Show current render pipeline (Built-in/URP/HDRP) and settings
2. Read `unity://editor/platform` - Show build target, Graphics API, Unity version
3. Read `unity://shader/keywords` - Show count of global/local shader keywords
4. Read `unity://shader/includes` - Show count of include files in the project

Present a concise status dashboard:
```
Unity Shader MCP Status
========================
Connection:  Connected
Unity:       [version]
Pipeline:    [Built-in/URP/HDRP]
Build Target:[platform]
Graphics API:[api]
Keywords:    [count] global, [count] local
Includes:    [count] files
```

If any resource fails to load, indicate the connection may be down and suggest checking the Unity WebSocket server.
