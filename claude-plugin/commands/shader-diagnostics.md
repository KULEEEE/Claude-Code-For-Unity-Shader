---
name: shader-diagnostics
description: Get language server diagnostics for a shader file
---

Get diagnostics for a shader file using `shader_diagnostics`.

Note: shader-ls currently has limited diagnostics support. For full compilation diagnostics, recommend the user use `/shader-compile` instead.

Ask the user for:
1. The shader file path (e.g., `Assets/Shaders/MyShader.shader`). If not provided, use `list_shaders` to help them choose.
2. Optionally, the user can provide shader source code directly via the `content` parameter.
