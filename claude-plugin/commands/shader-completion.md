---
name: shader-completion
description: Get code completion suggestions for shader code
---

Get code completion suggestions at a position in a shader file using `shader_completion`.

Ask the user for:
1. The shader file path (e.g., `Assets/Shaders/MyShader.shader`). If not provided, use `list_shaders` to help them choose.
2. The line number and character position where they want completions.
3. Optionally, the user can provide shader source code directly via the `content` parameter.

After getting completions:
1. Display the suggestions in a clear, organized list
2. Include detail and documentation for each item when available
3. If no completions are available, let the user know and suggest adjusting the cursor position
