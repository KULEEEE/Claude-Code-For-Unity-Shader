---
name: shader-hover
description: Get hover info for a shader symbol (type, documentation)
---

Get type and documentation info for a shader symbol using `shader_hover`.

Ask the user for:
1. The shader file path (e.g., `Assets/Shaders/MyShader.shader`). If not provided, use `list_shaders` to help them choose.
2. The line number and character position of the symbol they want info about.
3. Optionally, the user can provide shader source code directly via the `content` parameter.

After getting hover info:
1. Display the type information and documentation clearly
2. If no info is available, suggest the user check the position or try a different symbol
